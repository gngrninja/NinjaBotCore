using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Repositories;
using NinjaBotCore.Models.Wow;
using Newtonsoft.Json;

namespace NinjaBotCore.Services
{
    public class WowStaticDataService : IDisposable, IAsyncDisposable
    {
        private readonly ILogger _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfigurationRoot _config;
        private readonly WowApi _wowApi;
        private readonly WowTokenService _tokenService;
        private CancellationTokenSource _updateCancellation;
        private Task _updateTask;
        private bool _disposed;

        // Configuration keys with defaults
        private const string DEFAULT_MOUNT_UPDATE_INTERVAL_DAYS = "7"; // Update mounts every 7 days
        private const string DEFAULT_STATIC_DATA_UPDATE_INTERVAL_DAYS = "30"; // Update realms/classes/races monthly

        public WowStaticDataService(
            IServiceScopeFactory scopeFactory,
            ILogger<WowStaticDataService> logger,
            IConfigurationRoot config,
            WowApi wowApi,
            WowTokenService tokenService)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
            _wowApi = wowApi;
            _tokenService = tokenService;

            // Start background update tasks
            InitializeUpdateLoop();
        }

        private void InitializeUpdateLoop()
        {
            _updateCancellation = new CancellationTokenSource();
            _updateTask = RunUpdateLoopAsync(_updateCancellation.Token);
            _logger.LogInformation("WowStaticDataService update loop initialized");
        }

        /// <summary>
        /// Maps WoW item quality type string to numeric value
        /// </summary>
        private static int ParseQualityType(string qualityType)
        {
            if (string.IsNullOrEmpty(qualityType))
                return 0;

            if (int.TryParse(qualityType, out int parsedQuality))
                return parsedQuality;

            return qualityType.ToUpper() switch
            {
                "POOR" => 0,
                "COMMON" => 1,
                "UNCOMMON" => 2,
                "RARE" => 3,
                "EPIC" => 4,
                "LEGENDARY" => 5,
                "ARTIFACT" => 6,
                "HEIRLOOM" => 7,
                _ => 0
            };
        }

        private string TryGetFileName(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            try
            {
                var uri = new Uri(url);
                var fileName = Path.GetFileName(uri.AbsolutePath);
                return string.IsNullOrEmpty(fileName) ? null : fileName;
            }
            catch
            {
                return null;
            }
        }

        private string BuildPublicIconUrl(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            var normalized = fileName.ToLowerInvariant();
            return $"https://wow.zamimg.com/images/wow/icons/large/{normalized}";
        }

        private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
        {
            // Get update intervals from config or use defaults
            // NOTE: Mount updates have been moved to NinjaBotHelpers (StaticDataSyncWorker)
            var tokenPriceInterval = _tokenService.GetUpdateInterval();

            _logger.LogInformation("Token price update interval: {TokenMinutes}m", tokenPriceInterval.TotalMinutes);

            // Wait for WoW API to complete initialization
            _logger.LogInformation("Waiting for WoW API initialization...");
            var initialized = await _wowApi.WaitForInitializationAsync(cancellationToken);
            if (!initialized)
            {
                _logger.LogWarning("WoW API initialization did not complete successfully. Some features may not work correctly.");
            }
            else
            {
                _logger.LogInformation("WoW API initialized successfully");
            }

            // Check if databases are empty and perform initial imports if needed
            // NOTE: Achievement, Pet, Mount, and Item sync has been moved to NinjaBotHelpers (StaticDataSyncWorker)
            // to avoid blocking bot startup. Only lightweight data (realms, classes, races) is synced here.
            using (var scope = _scopeFactory.CreateScope())
            {
                // Check if realm/class/race data is empty (fast, low overhead)
                var realmRepo = new Repository<WowRealms>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var realmCount = (await realmRepo.GetAllAsync()).Count();

                if (realmCount == 0)
                {
                    _logger.LogInformation("Realm database is empty. Starting initial realm import...");
                    await ImportAllRealmsAsync(cancellationToken);
                }

                var classRepo = new Repository<WowPlayableClass>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var classCount = (await classRepo.GetAllAsync()).Count();

                if (classCount == 0)
                {
                    _logger.LogInformation("Class database is empty. Starting initial class import...");
                    await ImportAllClassesAsync(cancellationToken);
                }

                var raceRepo = new Repository<WowRaces>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var raceCount = (await raceRepo.GetAllAsync()).Count();

                if (raceCount == 0)
                {
                    _logger.LogInformation("Race database is empty. Starting initial race import...");
                    await ImportAllRacesAsync(cancellationToken);
                }
            }

            // Perform initial token price update for all regions
            await _tokenService.UpdateAllRegionsAsync(cancellationToken);

            // Start periodic token price updates
            // NOTE: Mount updates have been moved to NinjaBotHelpers (StaticDataSyncWorker)
            using var tokenPriceTimer = new PeriodicTimer(tokenPriceInterval);

            try
            {
                await _tokenService.RunPriceUpdatesAsync(tokenPriceTimer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("WowStaticDataService update loop cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in WowStaticDataService update loop");
            }
        }

        // NOTE: RunMountUpdatesAsync has been removed - mount updates are now handled by
        // NinjaBotHelpers StaticDataSyncWorker on a 30-day schedule.

        /// <summary>
        /// Import a single item by ID
        /// </summary>
        public async Task<WowItems> ImportItemAsync(long itemId, string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Importing item {ItemId}", itemId);

                var url = $"/data/wow/item/{itemId}?namespace=static-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var itemData = JsonConvert.DeserializeObject<dynamic>(response);

                if (itemData == null)
                {
                    _logger.LogWarning("Item {ItemId} returned null data", itemId);
                    return null;
                }

                // Extract name - handle both string and localized object formats
                string itemName = "Unknown";
                try
                {
                    if (itemData.name != null)
                    {
                        // Try as localized object first
                        if (itemData.name.en_US != null)
                        {
                            itemName = itemData.name.en_US.ToString();
                        }
                        // Fallback to direct string
                        else
                        {
                            itemName = itemData.name.ToString();
                        }
                    }
                }
                catch
                {
                    // If all else fails, try direct toString
                    itemName = itemData.name?.ToString() ?? "Unknown";
                }

                // Extract quality - handle both int and string formats
                int quality = 0;
                string qualityName = "Common";
                try
                {
                    if (itemData.quality?.type != null)
                    {
                        quality = ParseQualityType(itemData.quality.type.ToString());
                    }

                    if (itemData.quality?.name != null)
                    {
                        qualityName = itemData.quality.name.ToString();
                    }
                }
                catch
                {
                    // Use defaults
                }

                var item = new WowItems
                {
                    Id = itemId,
                    Name = itemName,
                    Quality = quality,
                    QualityName = qualityName,
                    ItemLevel = itemData.level ?? 0,
                    InventoryType = itemData.inventory_type?.name?.ToString(),
                    ItemClass = itemData.item_class?.name?.ToString(),
                    ItemSubclass = itemData.item_subclass?.name?.ToString(),
                    IsEquippable = itemData.is_equippable ?? false,
                    RequiredLevel = itemData.required_level ?? 0,
                    LastUpdated = DateTime.UtcNow
                };

                // Try to get media
                try
                {
                    var mediaUrl = $"/data/wow/media/item/{itemId}?namespace=static-{region}";
                    var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrl, "en_US", region, cancellationToken);
                    var mediaData = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                    if (mediaData?.assets != null && mediaData.assets.Count > 0)
                    {
                        string iconUrl = null;
                        foreach (var asset in mediaData.assets)
                        {
                            var key = asset?.key?.ToString();
                            var value = asset?.value?.ToString();
                            if (string.IsNullOrEmpty(value))
                            {
                                continue;
                            }

                            // Prefer the icon asset if present; otherwise first asset as fallback
                            if (string.Equals(key, "icon", StringComparison.OrdinalIgnoreCase))
                            {
                                iconUrl = value;
                                break;
                            }

                            iconUrl ??= value;
                        }

                        if (!string.IsNullOrEmpty(iconUrl))
                        {
                            var fileName = TryGetFileName(iconUrl);
                            item.MediaUrl = !string.IsNullOrEmpty(fileName)
                                ? BuildPublicIconUrl(fileName)
                                : iconUrl;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Media fetch failed for item {ItemId}, continuing without icon", itemId);
                }

                // Extract extended item details
                WowItemDetails itemDetails = null;
                try
                {
                    long? setId = null;
                    string setName = null;
                    string setEffectsJson = null;
                    string baseStatsJson = null;
                    string spellEffectsJson = null;
                    int socketCount = 0;

                    // Extract set information
                    if (itemData.preview_item?.set != null)
                    {
                        setId = itemData.preview_item.set.item_set?.id;
                        setName = itemData.preview_item.set.item_set?.name?.ToString();

                        if (itemData.preview_item.set.effects != null)
                        {
                            var effects = new List<object>();
                            foreach (var effect in itemData.preview_item.set.effects)
                            {
                                effects.Add(new
                                {
                                    display_string = effect.display_string?.ToString(),
                                    required_count = (int)(effect.required_count ?? 0),
                                    is_active = (bool)(effect.is_active ?? false)
                                });
                            }
                            setEffectsJson = JsonConvert.SerializeObject(effects);
                        }
                    }

                    // Extract base stats
                    if (itemData.preview_item?.stats != null && itemData.preview_item.stats.Count > 0)
                    {
                        var stats = new Dictionary<string, int>();
                        foreach (var stat in itemData.preview_item.stats)
                        {
                            var statType = stat.type?.type?.ToString() ?? stat.type?.name?.ToString();
                            var statValue = (int)(stat.value ?? 0);
                            if (!string.IsNullOrEmpty(statType) && statValue > 0)
                            {
                                stats[statType] = statValue;
                            }
                        }
                        if (stats.Count > 0)
                        {
                            baseStatsJson = JsonConvert.SerializeObject(stats);
                        }
                    }

                    // Extract spell effects
                    if (itemData.preview_item?.spells != null && itemData.preview_item.spells.Count > 0)
                    {
                        var spells = new List<object>();
                        foreach (var spell in itemData.preview_item.spells)
                        {
                            var description = spell.description?.ToString() ?? spell.spell?.name?.ToString();
                            if (!string.IsNullOrEmpty(description))
                            {
                                spells.Add(new
                                {
                                    description = description,
                                    spell_id = (long)(spell.spell?.id ?? 0)
                                });
                            }
                        }
                        if (spells.Count > 0)
                        {
                            spellEffectsJson = JsonConvert.SerializeObject(spells);
                        }
                    }

                    // Extract socket count
                    if (itemData.preview_item?.sockets != null && itemData.preview_item.sockets.Count > 0)
                    {
                        socketCount = itemData.preview_item.sockets.Count;
                    }

                    // Create details object if we have any extended data
                    if (setId.HasValue || !string.IsNullOrEmpty(baseStatsJson) ||
                        !string.IsNullOrEmpty(spellEffectsJson) || socketCount > 0)
                    {
                        itemDetails = new WowItemDetails
                        {
                            ItemId = itemId,
                            SetId = setId,
                            SetName = setName,
                            SetEffects = setEffectsJson,
                            BaseStats = baseStatsJson,
                            SpellEffects = spellEffectsJson,
                            SocketCount = socketCount,
                            LastUpdated = DateTime.UtcNow
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract extended details for item {ItemId}", itemId);
                }

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowItems>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                await repo.UpsertAsync(
                    findPredicate: i => i.Id == itemId,
                    updateAction: existing =>
                    {
                        existing.Name = item.Name;
                        existing.Quality = item.Quality;
                        existing.QualityName = item.QualityName;
                        existing.ItemLevel = item.ItemLevel;
                        existing.InventoryType = item.InventoryType;
                        existing.ItemClass = item.ItemClass;
                        existing.ItemSubclass = item.ItemSubclass;
                        existing.MediaUrl = item.MediaUrl;
                        existing.IsEquippable = item.IsEquippable;
                        existing.RequiredLevel = item.RequiredLevel;
                        existing.LastUpdated = DateTime.UtcNow;
                    },
                    createFactory: () => item);

                await repo.SaveChangesAsync();

                // Upsert item details if we have extended data
                if (itemDetails != null)
                {
                    var detailsRepo = new Repository<WowItemDetails>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
#pragma warning disable CA2016 // Forward the 'cancellationToken' parameter
                    await detailsRepo.UpsertAsync(
                        findPredicate: d => d.ItemId == itemId,
                        updateAction: existing =>
                        {
                            existing.SetId = itemDetails.SetId;
                            existing.SetName = itemDetails.SetName;
                            existing.SetEffects = itemDetails.SetEffects;
                            existing.BaseStats = itemDetails.BaseStats;
                            existing.SpellEffects = itemDetails.SpellEffects;
                            existing.SocketCount = itemDetails.SocketCount;
                            existing.LastUpdated = DateTime.UtcNow;
                        },
                        createFactory: () => itemDetails);
#pragma warning restore CA2016
                    await detailsRepo.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Item details for {ItemId} saved successfully", itemId);
                }

                _logger.LogInformation("Item {ItemId} ({Name}) imported successfully", itemId, item.Name);
                return item;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogDebug("Item {ItemId} does not exist (404)", itemId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing item {ItemId}", itemId);
                throw;
            }
        }

        // NOTE: ImportAllItemsAsync has been removed - bulk item imports are now handled by
        // NinjaBotHelpers StaticDataSyncWorker. Use the sync API to trigger item imports:
        // POST /api/sync/trigger with { "syncType": "items" }

        /// <summary>
        /// Search for an item by name in the database (with caching)
        /// </summary>
        public async Task<WowItems> SearchItemAsync(string itemName)
        {
            var itemNameLower = itemName.ToLower();
            var cacheKey = $"item_search_{itemNameLower}";

            // Check cache first - Note: WowCacheService uses IMemoryCache internally
            // For now, skip caching and search database directly
            // TODO: Add generic caching method to WowCacheService

            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowItems>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

            // Try exact match first
            var exactMatch = await repo.FirstOrDefaultAsync(i => i.Name.ToLower() == itemNameLower);

            if (exactMatch != null)
            {
                return exactMatch;
            }

            // Try partial match - get all items and filter in memory
            var allItems = await repo.GetAllAsync();
            var partialMatch = allItems.FirstOrDefault(i => i.Name.ToLower().Contains(itemNameLower));

            return partialMatch;
        }

        /// <summary>
        /// Import all mounts from the WoW API
        /// </summary>
        public async Task ImportAllMountsAsync(string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting mount import for region {Region}", region);

                // Get the mount index which lists all mounts
                var url = $"/data/wow/mount/index?namespace=static-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var mountIndex = JsonConvert.DeserializeObject<MountIndexResponse>(response);

                if (mountIndex?.Mounts == null || mountIndex.Mounts.Count == 0)
                {
                    _logger.LogWarning("No mounts found in index");
                    return;
                }

                _logger.LogInformation("Found {Count} mounts to import", mountIndex.Mounts.Count);

                int imported = 0;
                int failed = 0;

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                foreach (var mountEntry in mountIndex.Mounts)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var mount = await ImportMountAsync(mountEntry.Id, region, cancellationToken);
                        if (mount != null)
                        {
                            imported++;
                            if (imported % 50 == 0)
                            {
                                _logger.LogInformation("Mount import progress: {Imported}/{Total}", imported, mountIndex.Mounts.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import mount {MountId}", mountEntry.Id);
                        failed++;
                    }

                    // Rate limiting
                    await Task.Delay(100, cancellationToken);
                }

                _logger.LogInformation("Mount import completed: {Imported} imported, {Failed} failed", imported, failed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during mount import");
                throw;
            }
        }

        /// <summary>
        /// Detect mount types from description and name
        /// </summary>
        private static (bool isGround, bool isFlying, bool isAquatic) DetectMountTypes(string name, string description)
        {
            var nameLower = (name ?? "").ToLower();
            var descLower = (description ?? "").ToLower();
            var combined = $"{nameLower} {descLower}";

            // Check for aquatic mounts
            bool isAquatic = combined.Contains("water") ||
                           combined.Contains("aquatic") ||
                           combined.Contains("swim") ||
                           combined.Contains("sea") ||
                           combined.Contains("ocean") ||
                           combined.Contains("underwater") ||
                           combined.Contains("turtle") ||
                           combined.Contains("seahorse") ||
                           combined.Contains("ray"); // manta ray, etc.

            // Check for flying mounts
            bool isFlying = combined.Contains("fly") ||
                          combined.Contains("flies") ||
                          combined.Contains("flying") ||
                          combined.Contains("soar") ||
                          combined.Contains("glide") ||
                          combined.Contains("wings") ||
                          combined.Contains("dragon") ||
                          combined.Contains("drake") ||
                          combined.Contains("bird") ||
                          combined.Contains("gryphon") ||
                          combined.Contains("hippogryph") ||
                          combined.Contains("wyvern") ||
                          combined.Contains("phoenix") ||
                          combined.Contains("cloud serpent") ||
                          combined.Contains("skyterror") ||
                          combined.Contains("raven");

            // Ground is default, but explicitly check if it's ONLY ground (not flying/aquatic)
            bool isGround = true; // All mounts can be used on ground

            // Special case: If it's ONLY aquatic (can't be used on land), mark as non-ground
            if (isAquatic && (combined.Contains("only") || combined.Contains("underwater only")))
            {
                isGround = false;
            }

            return (isGround, isFlying, isAquatic);
        }

        /// <summary>
        /// Normalize source detail to standardized raid/dungeon names
        /// </summary>
        private static string NormalizeSourceDetail(string sourceDetail)
        {
            if (string.IsNullOrWhiteSpace(sourceDetail))
                return sourceDetail;

            // Common patterns to normalize
            var normalized = sourceDetail
                .Replace("The ", "") // "The Nighthold" -> "Nighthold"
                .Replace(" (Raid Finder)", "")
                .Replace(" (Normal)", "")
                .Replace(" (Heroic)", "")
                .Replace(" (Mythic)", "");

            // Specific raid/dungeon name mappings
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // The War Within
                { "Nerub-ar Palace", "Nerub-ar Palace" },

                // Dragonflight
                { "Vault of the Incarnates", "Vault of Incarnates" },
                { "Aberrus, the Shadowed Crucible", "Aberrus" },
                { "Amirdrassil, the Dream's Hope", "Amirdrassil" },

                // Shadowlands
                { "Castle Nathria", "Nathria" },
                { "Sanctum of Domination", "Sanctum" },
                { "Sepulcher of the First Ones", "Sepulcher" },

                // Battle for Azeroth
                { "Battle of Dazar'alor", "Dazar'alor" },
                { "Crucible of Storms", "Crucible of Storms" },
                { "Eternal Palace", "Eternal Palace" },
                { "Ny'alotha, the Waking City", "Ny'alotha" },

                // Legion
                { "Emerald Nightmare", "Emerald Nightmare" },
                { "Trial of Valor", "Trial of Valor" },
                { "Nighthold", "Nighthold" },
                { "Tomb of Sargeras", "Tomb of Sargeras" },
                { "Antorus, the Burning Throne", "Antorus" }
            };

            // Check if we have a specific mapping
            foreach (var mapping in mappings)
            {
                if (normalized.Contains(mapping.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return mapping.Value;
                }
            }

            return normalized.Trim();
        }

        /// <summary>
        /// Determine expansion from mount ID based on ID ranges.
        /// This is a fallback - prefer using DetermineExpansion() which checks description/category first.
        /// Uses shared WowExpansions.MountIdRanges - update that when new expansions launch.
        /// </summary>
        private static string GetExpansionFromMountId(long mountId)
        {
            // Delegate to shared constants to avoid duplication
            return Common.WowExpansions.GetExpansionFromMountId(mountId);
        }

        /// <summary>
        /// Determine expansion from mount description text.
        /// This is the most reliable method for special events like "Mists of Pandaria: Remix".
        /// </summary>
        private static string GetExpansionFromDescription(string description)
        {
            if (string.IsNullOrEmpty(description))
                return null;

            var desc = description.ToLowerInvariant();

            // Check for remix/timewalking events first (these override ID-based detection)
            if (desc.Contains("mists of pandaria") || desc.Contains("mop remix") || desc.Contains("pandaria remix"))
                return "Mists of Pandaria";
            if (desc.Contains("cataclysm remix") || desc.Contains("cataclysm:"))
                return "Cataclysm";
            if (desc.Contains("wrath of the lich king") || desc.Contains("wotlk") || desc.Contains("northrend"))
                return "Wrath of the Lich King";
            if (desc.Contains("burning crusade") || desc.Contains("outland"))
                return "The Burning Crusade";

            // Check for expansion-specific keywords in descriptions
            // Midnight (12.x) - check first since it's newest
            if (desc.Contains("midnight"))
                return "Midnight";
            if (desc.Contains("isle of dorn") || desc.Contains("khaz algar") || desc.Contains("war within") ||
                desc.Contains("earthen") || desc.Contains("hallowfall") || desc.Contains("azj-kahet") ||
                desc.Contains("ringing deeps"))
                return "The War Within";
            if (desc.Contains("dragon isles") || desc.Contains("valdrakken") || desc.Contains("thaldraszus") ||
                desc.Contains("ohn'ahran") || desc.Contains("waking shores") || desc.Contains("azure span") ||
                desc.Contains("forbidden reach") || desc.Contains("zaralek") || desc.Contains("emerald dream"))
                return "Dragonflight";
            if (desc.Contains("shadowlands") || desc.Contains("oribos") || desc.Contains("maldraxxus") ||
                desc.Contains("bastion") || desc.Contains("ardenweald") || desc.Contains("revendreth") ||
                desc.Contains("covenant") || desc.Contains("zereth mortis") || desc.Contains("korthia"))
                return "Shadowlands";
            if (desc.Contains("battle for azeroth") || desc.Contains("kul tiras") || desc.Contains("zandalar") ||
                desc.Contains("nazjatar") || desc.Contains("mechagon") || desc.Contains("vol'dun") ||
                desc.Contains("tiragarde") || desc.Contains("drustvar") || desc.Contains("stormsong") ||
                desc.Contains("n'zoth"))
                return "Battle for Azeroth";
            if (desc.Contains("legion") || desc.Contains("broken isles") || desc.Contains("argus") ||
                desc.Contains("class hall") || desc.Contains("suramar") || desc.Contains("val'sharah") ||
                desc.Contains("highmountain") || desc.Contains("stormheim") || desc.Contains("azsuna"))
                return "Legion";
            if (desc.Contains("draenor") || desc.Contains("garrison") || desc.Contains("tanaan") ||
                desc.Contains("frostfire") || desc.Contains("shadowmoon valley") || desc.Contains("gorgrond") ||
                desc.Contains("talador") || desc.Contains("spires of arak") || desc.Contains("nagrand"))
                return "Warlords of Draenor";
            if (desc.Contains("pandaria") || desc.Contains("sha of") || desc.Contains("mogu") ||
                desc.Contains("isle of thunder") || desc.Contains("timeless isle") || desc.Contains("vale of eternal"))
                return "Mists of Pandaria";
            if (desc.Contains("deathwing") || desc.Contains("twilight highlands") || desc.Contains("deepholm") ||
                desc.Contains("vashj'ir") || desc.Contains("uldum") || desc.Contains("mount hyjal") ||
                desc.Contains("molten front") || desc.Contains("firelands"))
                return "Cataclysm";
            if (desc.Contains("arthas") || desc.Contains("icecrown") || desc.Contains("dalaran") ||
                desc.Contains("ulduar") || desc.Contains("wintergrasp") || desc.Contains("grizzly hills") ||
                desc.Contains("borean tundra") || desc.Contains("dragonblight") || desc.Contains("howling fjord"))
                return "Wrath of the Lich King";
            if (desc.Contains("illidan") || desc.Contains("black temple") || desc.Contains("shattrath") ||
                desc.Contains("tempest keep") || desc.Contains("hellfire") || desc.Contains("nagrand") ||
                desc.Contains("terokkar") || desc.Contains("netherstorm") || desc.Contains("shadowmoon"))
                return "The Burning Crusade";

            return null;
        }

        /// <summary>
        /// Determine expansion from zone name.
        /// Maps zone names to their expansion.
        /// </summary>
        private static string GetExpansionFromZone(string zone)
        {
            if (string.IsNullOrEmpty(zone))
                return null;

            var z = zone.ToLowerInvariant();

            // The War Within zones
            if (z.Contains("isle of dorn") || z.Contains("ringing deeps") || z.Contains("hallowfall") ||
                z.Contains("azj-kahet") || z.Contains("city of threads") || z.Contains("khaz algar"))
                return "The War Within";

            // Dragonflight zones
            if (z.Contains("dragon isles") || z.Contains("valdrakken") || z.Contains("thaldraszus") ||
                z.Contains("ohn'ahran") || z.Contains("waking shores") || z.Contains("azure span") ||
                z.Contains("forbidden reach") || z.Contains("zaralek") || z.Contains("emerald dream") ||
                z.Contains("amirdrassil"))
                return "Dragonflight";

            // Shadowlands zones
            if (z.Contains("oribos") || z.Contains("maldraxxus") || z.Contains("bastion") ||
                z.Contains("ardenweald") || z.Contains("revendreth") || z.Contains("the maw") ||
                z.Contains("zereth mortis") || z.Contains("korthia") || z.Contains("torghast") ||
                z.Contains("sanctum of domination") || z.Contains("sepulcher"))
                return "Shadowlands";

            // Battle for Azeroth zones
            if (z.Contains("kul tiras") || z.Contains("zandalar") || z.Contains("nazjatar") ||
                z.Contains("mechagon") || z.Contains("vol'dun") || z.Contains("tiragarde") ||
                z.Contains("drustvar") || z.Contains("stormsong") || z.Contains("ny'alotha") ||
                z.Contains("uldir") || z.Contains("dazar'alor") || z.Contains("boralus"))
                return "Battle for Azeroth";

            // Legion zones
            if (z.Contains("broken isles") || z.Contains("argus") || z.Contains("suramar") ||
                z.Contains("val'sharah") || z.Contains("highmountain") || z.Contains("stormheim") ||
                z.Contains("azsuna") || z.Contains("tomb of sargeras") || z.Contains("antorus") ||
                z.Contains("nighthold") || z.Contains("emerald nightmare"))
                return "Legion";

            // Warlords of Draenor zones
            if (z.Contains("draenor") || z.Contains("tanaan") || z.Contains("frostfire") ||
                z.Contains("gorgrond") || z.Contains("talador") || z.Contains("spires of arak") ||
                z.Contains("hellfire citadel") || z.Contains("blackrock foundry"))
                return "Warlords of Draenor";

            // Mists of Pandaria zones
            if (z.Contains("pandaria") || z.Contains("isle of thunder") || z.Contains("timeless isle") ||
                z.Contains("vale of eternal") || z.Contains("kun-lai") || z.Contains("jade forest") ||
                z.Contains("townlong") || z.Contains("dread wastes") || z.Contains("krasarang") ||
                z.Contains("valley of the four winds") || z.Contains("siege of orgrimmar") ||
                z.Contains("throne of thunder") || z.Contains("mogu'shan") || z.Contains("heart of fear"))
                return "Mists of Pandaria";

            // Cataclysm zones
            if (z.Contains("twilight highlands") || z.Contains("deepholm") || z.Contains("vashj'ir") ||
                z.Contains("uldum") || z.Contains("mount hyjal") || z.Contains("molten front") ||
                z.Contains("firelands") || z.Contains("dragon soul") || z.Contains("bastion of twilight") ||
                z.Contains("blackwing descent") || z.Contains("throne of the four winds"))
                return "Cataclysm";

            // Wrath of the Lich King zones
            if (z.Contains("icecrown") || z.Contains("dalaran") || z.Contains("ulduar") ||
                z.Contains("wintergrasp") || z.Contains("grizzly hills") || z.Contains("borean tundra") ||
                z.Contains("dragonblight") || z.Contains("howling fjord") || z.Contains("zul'drak") ||
                z.Contains("sholazar") || z.Contains("storm peaks") || z.Contains("crystalsong") ||
                z.Contains("naxxramas") || z.Contains("trial of the crusader") || z.Contains("obsidian sanctum"))
                return "Wrath of the Lich King";

            // The Burning Crusade zones
            if (z.Contains("outland") || z.Contains("shattrath") || z.Contains("hellfire") ||
                z.Contains("zangarmarsh") || z.Contains("terokkar") || z.Contains("nagrand") ||
                z.Contains("blade's edge") || z.Contains("netherstorm") || z.Contains("shadowmoon") ||
                z.Contains("black temple") || z.Contains("tempest keep") || z.Contains("karazhan") ||
                z.Contains("sunwell") || z.Contains("isle of quel'danas") || z.Contains("gruul"))
                return "The Burning Crusade";

            // Classic zones and raids are usually handled by low mount IDs
            if (z.Contains("molten core") || z.Contains("blackwing lair") || z.Contains("ahn'qiraj") ||
                z.Contains("naxxramas") || z.Contains("zul'gurub") || z.Contains("stratholme") ||
                z.Contains("scholomance"))
                return "Classic";

            return null;
        }

        /// <summary>
        /// Determine expansion from scraped category field.
        /// The category often contains expansion or source info.
        /// </summary>
        private static string GetExpansionFromCategory(string category)
        {
            if (string.IsNullOrEmpty(category))
                return null;

            var cat = category.ToLowerInvariant();

            // Check for expansion names in category
            if (cat.Contains("midnight"))
                return "Midnight";
            if (cat.Contains("war within") || cat.Contains("tww"))
                return "The War Within";
            if (cat.Contains("dragonflight") || cat.Contains("df"))
                return "Dragonflight";
            if (cat.Contains("shadowlands") || cat.Contains("sl"))
                return "Shadowlands";
            if (cat.Contains("battle for azeroth") || cat.Contains("bfa"))
                return "Battle for Azeroth";
            if (cat.Contains("legion"))
                return "Legion";
            if (cat.Contains("warlords") || cat.Contains("draenor") || cat.Contains("wod"))
                return "Warlords of Draenor";
            if (cat.Contains("mists") || cat.Contains("pandaria") || cat.Contains("mop"))
                return "Mists of Pandaria";
            if (cat.Contains("cataclysm") || cat.Contains("cata"))
                return "Cataclysm";
            if (cat.Contains("wrath") || cat.Contains("lich king") || cat.Contains("wotlk"))
                return "Wrath of the Lich King";
            if (cat.Contains("burning crusade") || cat.Contains("tbc") || cat.Contains("outland"))
                return "The Burning Crusade";
            if (cat.Contains("classic") || cat.Contains("vanilla"))
                return "Classic";

            return null;
        }

        /// <summary>
        /// Determine expansion using multiple detection methods with fallback.
        /// Priority: Description > Zone > Category > SourceText > ID-based
        /// Public so API endpoints can use it for mount imports.
        /// </summary>
        public static string DetermineExpansion(long mountId, string description, string zone, string category, string sourceText = null)
        {
            // Try description first (most accurate for special events like MoP Remix)
            var expansion = GetExpansionFromDescription(description);
            if (!string.IsNullOrEmpty(expansion))
                return expansion;

            // Try zone-based detection
            expansion = GetExpansionFromZone(zone);
            if (!string.IsNullOrEmpty(expansion))
                return expansion;

            // Try category from scraped data
            expansion = GetExpansionFromCategory(category);
            if (!string.IsNullOrEmpty(expansion))
                return expansion;

            // Try source text (achievement, clean text, etc.)
            if (!string.IsNullOrEmpty(sourceText))
            {
                expansion = GetExpansionFromCategory(sourceText); // Reuse category logic for keyword detection
                if (!string.IsNullOrEmpty(expansion))
                    return expansion;
            }

            // Fallback to ID-based detection
            return GetExpansionFromMountId(mountId);
        }

        /// <summary>
        /// Import a single mount by ID
        /// </summary>
        public async Task<WowMounts> ImportMountAsync(long mountId, string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Importing mount {MountId}", mountId);

                var url = $"/data/wow/mount/{mountId}?namespace=static-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var mountData = JsonConvert.DeserializeObject<MountDetailsResponse>(response);

                if (mountData == null)
                {
                    _logger.LogWarning("Mount {MountId} returned null data", mountId);
                    return null;
                }

                // Detect mount types from description
                var (isGround, isFlying, isAquatic) = DetectMountTypes(mountData.Name, mountData.Description);

                // Note: Source details (drop location, vendor, etc.) come from mounts.json via MergeScrapedMountDataAsync
                // The Blizzard API only provides generic source types like "DROP" without specific locations

                var mount = new WowMounts
                {
                    Id = mountData.Id,
                    Name = mountData.Name ?? "Unknown",
                    Description = mountData.Description,
                    Source = mountData.Source?.Type ?? "Unknown",
                    SourceDetail = NormalizeSourceDetail(mountData.Source?.Name),
                    Faction = mountData.Faction?.Type ?? "Both",
                    IsGround = isGround,
                    IsFlying = isFlying,
                    IsAquatic = isAquatic,
                    CreatureDisplayId = mountData.CreatureDisplays?.FirstOrDefault()?.Id,
                    // Use smart expansion detection - description takes priority over ID-based fallback
                    Expansion = DetermineExpansion(mountData.Id, mountData.Description, null, null),
                    LastUpdated = DateTime.UtcNow
                };

                // Note: We don't fetch media during import to save time and API calls
                // Media is fetched on-demand when users view mount details
                if (mount.CreatureDisplayId.HasValue)
                {
                    _logger.LogDebug("Mount {MountId} ({Name}): Stored creature display ID {DisplayId}",
                        mountId, mount.Name, mount.CreatureDisplayId.Value);
                }
                else
                {
                    _logger.LogWarning("Mount {MountId} ({Name}) has no creature display ID", mountId, mount.Name);
                }

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                await repo.UpsertAsync(
                    findPredicate: m => m.Id == mountId,
                    updateAction: existing =>
                    {
                        // Update basic metadata from API
                        existing.Name = mount.Name;
                        existing.Description = mount.Description;
                        existing.IsGround = mount.IsGround;
                        existing.IsFlying = mount.IsFlying;
                        existing.IsAquatic = mount.IsAquatic;
                        existing.CreatureDisplayId = mount.CreatureDisplayId;

                        // Recalculate expansion using smart detection (preserves zone/category from scraped data)
                        existing.Expansion = DetermineExpansion(mount.Id, mount.Description, existing.InstanceName, null);

                        // Only update source fields if not already populated from mounts.json
                        // This allows scraped data to take priority over generic API data
                        if (string.IsNullOrEmpty(existing.InstanceName))
                        {
                            existing.Source = mount.Source;
                            existing.SourceDetail = mount.SourceDetail;
                            existing.Faction = mount.Faction;
                        }

                        existing.LastUpdated = DateTime.UtcNow;
                    },
                    createFactory: () => mount);

                await repo.SaveChangesAsync();

                _logger.LogDebug("Mount {MountId} ({Name}) imported successfully", mountId, mount.Name);
                return mount;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogDebug("Mount {MountId} does not exist (404)", mountId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing mount {MountId}", mountId);
                throw;
            }
        }

        /// <summary>
        /// Get all mounts from the database
        /// </summary>
        public async Task<List<WowMounts>> GetAllMountsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Search for mounts by name
        /// </summary>
        public async Task<List<WowMounts>> SearchMountsAsync(string mountName)
        {
            var mountNameLower = mountName.ToLower();

            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

            var allMounts = await repo.GetAllAsync();
            return allMounts.Where(m => m.Name.ToLower().Contains(mountNameLower)).ToList();
        }

        #region Housing Decor

        // Note: Housing decor import is handled by NinjaBotHelpers via StaticDataSyncRequest
        // Use /sync trigger or /import-housing-decor to queue an import

        /// <summary>
        /// Get all housing decor from the database
        /// </summary>
        public async Task<List<HousingDecor>> GetAllHousingDecorAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<HousingDecor>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Get housing decor count from the database
        /// </summary>
        public async Task<int> GetHousingDecorCountAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            return await Task.FromResult(db.HousingDecor.Count());
        }

        /// <summary>
        /// Search for housing decor by name
        /// </summary>
        public async Task<List<HousingDecor>> SearchHousingDecorAsync(string decorName)
        {
            var decorNameLower = decorName.ToLower();

            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<HousingDecor>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

            var allDecor = await repo.GetAllAsync();
            return allDecor.Where(d => d.Name.ToLower().Contains(decorNameLower)).ToList();
        }

        /// <summary>
        /// Get missing housing decor items (items in database but not in collected set)
        /// </summary>
        /// <param name="collectedIds">Set of decor IDs the character has collected</param>
        /// <param name="searchFilter">Optional name filter</param>
        /// <returns>List of missing decor items, ordered by name</returns>
        public async Task<List<HousingDecor>> GetMissingDecorAsync(HashSet<long> collectedIds, string searchFilter = null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var query = db.HousingDecor.AsQueryable();

            // Filter to missing items (not in collected set)
            query = query.Where(d => !collectedIds.Contains(d.Id));

            // Apply search filter if provided
            if (!string.IsNullOrWhiteSpace(searchFilter))
            {
                var search = searchFilter.ToLower();
                query = query.Where(d => d.Name.ToLower().Contains(search));
            }

            return await query.OrderBy(d => d.Name).ToListAsync();
        }

        #endregion

        /// <summary>
        /// Merge source data from scraped mounts.json file into database
        /// </summary>
        public async Task<string> MergeScrapedMountDataAsync(string jsonFilePath = "mounts.json", CancellationToken cancellationToken = default)
        {
            var updated = 0;
            var notFound = 0;
            var failed = 0;

            try
            {
                _logger.LogInformation("Starting mount data merge from {FilePath}", jsonFilePath);

                // Load the JSON file
                if (!File.Exists(jsonFilePath))
                {
                    return $"Error: File not found: {jsonFilePath}";
                }

                var jsonContent = await File.ReadAllTextAsync(jsonFilePath, cancellationToken);
                var scrapedData = JsonConvert.DeserializeObject<ScrapedMountData>(jsonContent);

                if (scrapedData?.Mounts == null || scrapedData.Mounts.Count == 0)
                {
                    return "Error: No mount data found in JSON file";
                }

                _logger.LogInformation("Loaded {Count} mounts from JSON (scanned: {Timestamp})",
                    scrapedData.Mounts.Count, scrapedData.Metadata?.ScanTimestamp);

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                // Get all mounts from database
                var dbMounts = await repo.GetAllAsync();
                var dbMountDict = dbMounts.ToDictionary(m => m.Id);

                _logger.LogInformation("Found {Count} mounts in database to update", dbMounts.Count);

                foreach (var kvp in scrapedData.Mounts)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var scraped = kvp.Value;

                    try
                    {
                        if (!dbMountDict.TryGetValue(scraped.MountId, out var dbMount))
                        {
                            notFound++;
                            continue;
                        }

                        // Get primary source type and detail
                        var (sourceType, sourceDetail) = scraped.Source?.GetPrimarySource() ?? ("UNKNOWN", null);

                        // Update the mount with scraped data
                        dbMount.Source = sourceType;
                        dbMount.SourceDetail = sourceDetail;
                        dbMount.InstanceName = scraped.Source?.Zone;
                        dbMount.DropLocation = scraped.Source?.Zone;
                        dbMount.EncounterName = scraped.Source?.Drop;

                        // Recalculate expansion using smart detection with all available data
                        // Priority: Description > Zone > Category > SourceText > ID-based fallback
                        dbMount.Expansion = DetermineExpansion(
                            dbMount.Id,
                            dbMount.Description,
                            scraped.Source?.Zone,
                            scraped.Source?.Category,
                            scraped.Source?.Clean ?? scraped.Source?.Achievement
                        );

                        // Set obtainability based on legacy status
                        if (scraped.Source?.IsLegacy() == true)
                        {
                            dbMount.IsObtainable = false;
                        }

                        // Update faction if available
                        if (scraped.IsFactionSpecific && scraped.Faction.HasValue)
                        {
                            dbMount.Faction = scraped.Faction.Value == 0 ? "Horde" : "Alliance";
                        }

                        dbMount.LastUpdated = DateTime.UtcNow;
                        updated++;

                        if (updated % 100 == 0)
                        {
                            _logger.LogInformation("Merge progress: {Updated} updated", updated);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed to merge mount {MountId} ({Name})", scraped.MountId, scraped.Name);
                    }
                }

                await repo.SaveChangesAsync();

                var resultMessage = $"Mount data merge complete. Updated: {updated}, Not in DB: {notFound}, Failed: {failed}";
                _logger.LogInformation(resultMessage);
                return resultMessage;
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error during merge: {ex.Message}";
                _logger.LogError(ex, "Error during mount data merge");
                return errorMessage;
            }
        }

        /// <summary>
        /// Recalculate expansion tags for all mounts in the database.
        /// Uses smart detection: Description > Zone > Category > ID-based fallback.
        /// Call this after fixing expansion detection logic to update existing data.
        /// </summary>
        public async Task<string> RecalculateMountExpansionsAsync(CancellationToken cancellationToken = default)
        {
            var updated = 0;
            var failed = 0;
            var changes = new Dictionary<string, int>(); // Track expansion changes for reporting

            try
            {
                _logger.LogInformation("Starting mount expansion recalculation");

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                var allMounts = await repo.GetAllAsync();
                _logger.LogInformation("Recalculating expansions for {Count} mounts", allMounts.Count);

                foreach (var mount in allMounts)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var oldExpansion = mount.Expansion ?? "Unknown";
                        var newExpansion = DetermineExpansion(
                            mount.Id,
                            mount.Description,
                            mount.InstanceName,  // Zone from scraped data
                            null                  // Category not stored in DB, would need merge
                        );

                        if (oldExpansion != newExpansion)
                        {
                            var changeKey = $"{oldExpansion} -> {newExpansion}";
                            changes[changeKey] = changes.GetValueOrDefault(changeKey) + 1;

                            mount.Expansion = newExpansion;
                            mount.LastUpdated = DateTime.UtcNow;
                            updated++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed to recalculate expansion for mount {MountId} ({Name})",
                            mount.Id, mount.Name);
                    }
                }

                await repo.SaveChangesAsync();

                // Build result message with change summary
                var changesSummary = changes.Any()
                    ? "\n" + string.Join("\n", changes.OrderByDescending(c => c.Value).Select(c => $"  {c.Key}: {c.Value}"))
                    : " (no changes needed)";

                var resultMessage = $"Expansion recalculation complete. Updated: {updated}, Failed: {failed}{changesSummary}";
                _logger.LogInformation("Mount expansion recalculation complete. Updated: {Updated}, Failed: {Failed}",
                    updated, failed);

                return resultMessage;
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error during expansion recalculation: {ex.Message}";
                _logger.LogError(ex, "Error during mount expansion recalculation");
                return errorMessage;
            }
        }

        #region Realms Import/Retrieval

        /// <summary>
        /// Import all realms from the WoW API for all regions
        /// </summary>
        public async Task ImportAllRealmsAsync(CancellationToken cancellationToken = default)
        {
            var regions = new[] { "us", "eu", "kr", "tw" };

            foreach (var region in regions)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await ImportRealmsForRegionAsync(region, cancellationToken);
                    _logger.LogInformation("Realm import completed for region {Region}", region.ToUpper());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error importing realms for region {Region}", region.ToUpper());
                }
            }
        }

        /// <summary>
        /// Import realms for a specific region
        /// </summary>
        public async Task ImportRealmsForRegionAsync(string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting realm import for region {Region}", region);

                var localeName = region switch
                {
                    "us" => "en_US",
                    "eu" => "en_GB",
                    "kr" => "ko_KR",
                    "tw" => "zh_TW",
                    _ => "en_US"
                };

                var url = $"/data/wow/realm/index?namespace=dynamic-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, localeName, region, cancellationToken);
                var realmData = JsonConvert.DeserializeObject<WowRealm>(response);

                if (realmData?.realms == null || realmData.realms.Length == 0)
                {
                    _logger.LogWarning("No realms found for region {Region}", region);
                    return;
                }

                _logger.LogInformation("Found {Count} realms for region {Region}", realmData.realms.Length, region);

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowRealms>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                int imported = 0;
                foreach (var realm in realmData.realms)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var dbRealm = new WowRealms
                        {
                            Id = realm.id,
                            Name = realm.name,
                            Slug = realm.slug,
                            Region = region.ToUpper(),
                            Timezone = realm.timezone,
                            Type = realm.type,
                            Population = realm.population,
                            Locale = realm.locale,
                            IsTournament = false,
                            LastUpdated = DateTime.UtcNow
                        };

                        await repo.UpsertAsync(
                            findPredicate: r => r.Id == realm.id,
                            updateAction: existing =>
                            {
                                existing.Name = dbRealm.Name;
                                existing.Slug = dbRealm.Slug;
                                existing.Region = dbRealm.Region;
                                existing.Timezone = dbRealm.Timezone;
                                existing.Type = dbRealm.Type;
                                existing.Population = dbRealm.Population;
                                existing.Locale = dbRealm.Locale;
                                existing.LastUpdated = DateTime.UtcNow;
                            },
                            createFactory: () => dbRealm);

                        imported++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import realm {RealmId} ({Name})", realm.id, realm.name);
                    }
                }

                await repo.SaveChangesAsync();
                _logger.LogInformation("Imported {Count} realms for region {Region}", imported, region);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during realm import for region {Region}", region);
                throw;
            }
        }

        /// <summary>
        /// Get all realms from the database
        /// </summary>
        public async Task<List<WowRealms>> GetAllRealmsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRealms>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Get realms for a specific region
        /// </summary>
        public async Task<List<WowRealms>> GetRealmsByRegionAsync(string region)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRealms>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allRealms = await repo.GetAllAsync();
            return allRealms.Where(r => r.Region.Equals(region, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Search realms by name
        /// </summary>
        public async Task<List<WowRealms>> SearchRealmsAsync(string query, string region = null)
        {
            var queryLower = query.ToLower();

            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRealms>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allRealms = await repo.GetAllAsync();

            var results = allRealms.Where(r => r.Name.ToLower().Contains(queryLower) || r.Slug.ToLower().Contains(queryLower));

            if (!string.IsNullOrEmpty(region))
            {
                results = results.Where(r => r.Region.Equals(region, StringComparison.OrdinalIgnoreCase));
            }

            return results.ToList();
        }

        /// <summary>
        /// Get a realm by slug
        /// </summary>
        public async Task<WowRealms> GetRealmBySlugAsync(string slug, string region = null)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRealms>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allRealms = await repo.GetAllAsync();

            var query = allRealms.Where(r => r.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(region))
            {
                query = query.Where(r => r.Region.Equals(region, StringComparison.OrdinalIgnoreCase));
            }

            return query.FirstOrDefault();
        }

        #endregion

        #region Classes Import/Retrieval

        /// <summary>
        /// Import all playable classes from the WoW API
        /// </summary>
        public async Task ImportAllClassesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting playable class import");

                var url = "/data/wow/playable-class/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
                var classData = JsonConvert.DeserializeObject<WowClasses>(response);

                if (classData?.classes == null || classData.classes.Length == 0)
                {
                    _logger.LogWarning("No playable classes found");
                    return;
                }

                _logger.LogInformation("Found {Count} playable classes", classData.classes.Length);

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowPlayableClass>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                foreach (var wowClass in classData.classes)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var dbClass = new WowPlayableClass
                        {
                            Id = wowClass.id,
                            Name = wowClass.name,
                            PowerType = wowClass.powerType,
                            LastUpdated = DateTime.UtcNow
                        };

                        await repo.UpsertAsync(
                            findPredicate: c => c.Id == wowClass.id,
                            updateAction: existing =>
                            {
                                existing.Name = dbClass.Name;
                                existing.PowerType = dbClass.PowerType;
                                existing.LastUpdated = DateTime.UtcNow;
                            },
                            createFactory: () => dbClass);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import class {ClassId} ({Name})", wowClass.id, wowClass.name);
                    }
                }

                await repo.SaveChangesAsync();
                _logger.LogInformation("Playable class import completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during playable class import");
                throw;
            }
        }

        /// <summary>
        /// Get all playable classes from the database
        /// </summary>
        public async Task<List<WowPlayableClass>> GetAllClassesAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPlayableClass>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Get a class by ID
        /// </summary>
        public async Task<WowPlayableClass> GetClassByIdAsync(long classId)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPlayableClass>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.FirstOrDefaultAsync(c => c.Id == classId);
        }

        /// <summary>
        /// Get a class by name
        /// </summary>
        public async Task<WowPlayableClass> GetClassByNameAsync(string name)
        {
            var nameLower = name.ToLower();
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPlayableClass>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allClasses = await repo.GetAllAsync();
            return allClasses.FirstOrDefault(c => c.Name.ToLower() == nameLower);
        }

        #endregion

        #region Races Import/Retrieval

        /// <summary>
        /// Import all playable races from the WoW API
        /// </summary>
        public async Task ImportAllRacesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting playable race import");

                var url = "/data/wow/playable-race/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
                var raceData = JsonConvert.DeserializeObject<Race>(response);

                if (raceData?.races == null || raceData.races.Length == 0)
                {
                    _logger.LogWarning("No playable races found");
                    return;
                }

                _logger.LogInformation("Found {Count} playable races", raceData.races.Length);

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowRaces>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                foreach (var race in raceData.races)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        // Determine faction from side property
                        var faction = race.side?.ToLower() switch
                        {
                            "alliance" => "Alliance",
                            "horde" => "Horde",
                            _ => "Neutral"
                        };

                        var dbRace = new WowRaces
                        {
                            Id = race.id,
                            Name = race.name,
                            Faction = faction,
                            IsPlayable = true,
                            IsAlliedRace = race.id > 30, // Allied races have higher IDs
                            LastUpdated = DateTime.UtcNow
                        };

                        await repo.UpsertAsync(
                            findPredicate: r => r.Id == race.id,
                            updateAction: existing =>
                            {
                                existing.Name = dbRace.Name;
                                existing.Faction = dbRace.Faction;
                                existing.IsPlayable = dbRace.IsPlayable;
                                existing.IsAlliedRace = dbRace.IsAlliedRace;
                                existing.LastUpdated = DateTime.UtcNow;
                            },
                            createFactory: () => dbRace);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import race {RaceId} ({Name})", race.id, race.name);
                    }
                }

                await repo.SaveChangesAsync();
                _logger.LogInformation("Playable race import completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during playable race import");
                throw;
            }
        }

        /// <summary>
        /// Get all playable races from the database
        /// </summary>
        public async Task<List<WowRaces>> GetAllRacesAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRaces>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Get races by faction
        /// </summary>
        public async Task<List<WowRaces>> GetRacesByFactionAsync(string faction)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRaces>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allRaces = await repo.GetAllAsync();
            return allRaces.Where(r => r.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Get a race by ID
        /// </summary>
        public async Task<WowRaces> GetRaceByIdAsync(long raceId)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRaces>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.FirstOrDefaultAsync(r => r.Id == raceId);
        }

        /// <summary>
        /// Get a race by name
        /// </summary>
        public async Task<WowRaces> GetRaceByNameAsync(string name)
        {
            var nameLower = name.ToLower();
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowRaces>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allRaces = await repo.GetAllAsync();
            return allRaces.FirstOrDefault(r => r.Name.ToLower() == nameLower);
        }

        #endregion

        #region Achievements Import/Retrieval

        /// <summary>
        /// Import all achievements from the WoW API
        /// </summary>
        public async Task ImportAllAchievementsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting achievement import");

                var url = "/data/wow/achievement/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
                var achievementData = JsonConvert.DeserializeObject<dynamic>(response);

                if (achievementData?.achievements == null)
                {
                    _logger.LogWarning("No achievements found in index");
                    return;
                }

                int totalCount = achievementData.achievements.Count;
                _logger.LogInformation("Found {Count} achievements to import", totalCount);

                // Load existing achievement IDs to skip API calls for already-imported achievements
                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowAchievements>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var existingAchievements = await repo.GetAllAsync();
                var existingIds = new HashSet<long>(existingAchievements.Select(a => a.Id));
                _logger.LogInformation("Found {Count} existing achievements in database, will skip those", existingIds.Count);

                int imported = 0;
                int skipped = 0;
                int failed = 0;

                foreach (var achievementEntry in achievementData.achievements)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        long achievementId = achievementEntry.id;

                        // Skip if already in database
                        if (existingIds.Contains(achievementId))
                        {
                            skipped++;
                            continue;
                        }

                        await ImportAchievementAsync(achievementId, "us", cancellationToken);
                        imported++;

                        if ((imported + skipped) % 100 == 0)
                        {
                            _logger.LogInformation("Achievement import progress: {Imported} imported, {Skipped} skipped, {Processed}/{Total} processed",
                                imported, skipped, imported + skipped, totalCount);
                        }

                        // Rate limiting - 100ms to stay safely under Blizzard's 10 req/sec limit
                        // Each achievement makes 2 API calls (details + media)
                        await Task.Delay(100, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import achievement {AchievementId}", (long)achievementEntry.id);
                        failed++;
                    }
                }

                _logger.LogInformation("Achievement import completed: {Imported} imported, {Skipped} skipped, {Failed} failed", imported, skipped, failed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during achievement import");
                throw;
            }
        }

        /// <summary>
        /// Import a single achievement by ID
        /// </summary>
        public async Task<WowAchievements> ImportAchievementAsync(long achievementId, string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Importing achievement {AchievementId}", achievementId);

                var url = $"/data/wow/achievement/{achievementId}?namespace=static-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var achievementData = JsonConvert.DeserializeObject<dynamic>(response);

                if (achievementData == null)
                {
                    _logger.LogWarning("Achievement {AchievementId} returned null data", achievementId);
                    return null;
                }

                // Extract name
                string name = achievementData.name?.ToString() ?? "Unknown";

                // Extract description
                string description = achievementData.description?.ToString();

                // Extract points
                int points = achievementData.points ?? 0;

                // Extract category
                string category = achievementData.category?.name?.ToString();
                long? categoryId = achievementData.category?.id;

                // Extract parent category
                string parentCategory = achievementData.category?.parent_category?.name?.ToString();

                // Check if account-wide
                bool isAccountWide = achievementData.is_account_wide ?? false;

                // Extract reward info
                string rewardDescription = achievementData.reward_description?.ToString();

                // Extract faction
                string faction = "Both";
                if (achievementData.faction != null)
                {
                    string factionType = achievementData.faction.type?.ToString();
                    faction = factionType?.ToUpper() switch
                    {
                        "ALLIANCE" => "Alliance",
                        "HORDE" => "Horde",
                        _ => "Both"
                    };
                }

                // Extract media URL
                string mediaUrl = null;
                try
                {
                    var mediaUrlPath = $"/data/wow/media/achievement/{achievementId}?namespace=static-{region}";
                    var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrlPath, "en_US", region, cancellationToken);
                    var mediaData = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                    if (mediaData?.assets != null && mediaData.assets.Count > 0)
                    {
                        foreach (var asset in mediaData.assets)
                        {
                            if (asset?.key?.ToString() == "icon")
                            {
                                string iconUrl = asset?.value?.ToString();
                                if (!string.IsNullOrEmpty(iconUrl))
                                {
                                    var fileName = TryGetFileName(iconUrl);
                                    mediaUrl = !string.IsNullOrEmpty(fileName)
                                        ? BuildPublicIconUrl(fileName)
                                        : iconUrl;
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Media fetch failed for achievement {AchievementId}, continuing without icon", achievementId);
                }

                var achievement = new WowAchievements
                {
                    Id = achievementId,
                    Name = name,
                    Description = description,
                    Points = points,
                    Category = category,
                    CategoryId = categoryId,
                    ParentCategory = parentCategory,
                    IsAccountWide = isAccountWide,
                    RewardDescription = rewardDescription,
                    Faction = faction,
                    MediaUrl = mediaUrl,
                    DisplayOrder = achievementData.display_order ?? 0,
                    LastUpdated = DateTime.UtcNow
                };

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowAchievements>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                await repo.UpsertAsync(
                    findPredicate: a => a.Id == achievementId,
                    updateAction: existing =>
                    {
                        existing.Name = achievement.Name;
                        existing.Description = achievement.Description;
                        existing.Points = achievement.Points;
                        existing.Category = achievement.Category;
                        existing.CategoryId = achievement.CategoryId;
                        existing.ParentCategory = achievement.ParentCategory;
                        existing.IsAccountWide = achievement.IsAccountWide;
                        existing.RewardDescription = achievement.RewardDescription;
                        existing.Faction = achievement.Faction;
                        existing.MediaUrl = achievement.MediaUrl;
                        existing.DisplayOrder = achievement.DisplayOrder;
                        existing.LastUpdated = DateTime.UtcNow;
                    },
                    createFactory: () => achievement);

                await repo.SaveChangesAsync();

                // Import criteria if available
                if (achievementData.criteria != null)
                {
                    await ImportAchievementCriteriaAsync(achievementId, achievementData.criteria, cancellationToken);
                }

                _logger.LogDebug("Achievement {AchievementId} ({Name}) imported successfully", achievementId, achievement.Name);
                return achievement;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogDebug("Achievement {AchievementId} does not exist (404)", achievementId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing achievement {AchievementId}", achievementId);
                throw;
            }
        }

        /// <summary>
        /// Import achievement criteria
        /// </summary>
        private async Task ImportAchievementCriteriaAsync(long achievementId, dynamic criteriaData, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowAchievementCriteria>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                // Handle single criterion or child_criteria array
                if (criteriaData.child_criteria != null)
                {
                    int orderIndex = 0;
                    foreach (var criterion in criteriaData.child_criteria)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            long criterionId = criterion.id ?? 0;
                            if (criterionId == 0) continue;

                            var criteria = new WowAchievementCriteria
                            {
                                Id = criterionId,
                                AchievementId = achievementId,
                                Description = criterion.description?.ToString(),
                                OrderIndex = orderIndex++,
                                Amount = criterion.amount ?? 1,
                                IsCompleted = false,
                                LastUpdated = DateTime.UtcNow
                            };

                            await repo.UpsertAsync(
                                findPredicate: c => c.Id == criterionId,
                                updateAction: existing =>
                                {
                                    existing.AchievementId = achievementId;
                                    existing.Description = criteria.Description;
                                    existing.OrderIndex = criteria.OrderIndex;
                                    existing.Amount = criteria.Amount;
                                    existing.LastUpdated = DateTime.UtcNow;
                                },
                                createFactory: () => criteria);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to import criterion for achievement {AchievementId}", achievementId);
                        }
                    }
                }
                else
                {
                    // Single criterion
                    long criterionId = criteriaData.id ?? 0;
                    if (criterionId > 0)
                    {
                        var criteria = new WowAchievementCriteria
                        {
                            Id = criterionId,
                            AchievementId = achievementId,
                            Description = criteriaData.description?.ToString(),
                            OrderIndex = 0,
                            Amount = criteriaData.amount ?? 1,
                            IsCompleted = false,
                            LastUpdated = DateTime.UtcNow
                        };

                        await repo.UpsertAsync(
                            findPredicate: c => c.Id == criterionId,
                            updateAction: existing =>
                            {
                                existing.AchievementId = achievementId;
                                existing.Description = criteria.Description;
                                existing.Amount = criteria.Amount;
                                existing.LastUpdated = DateTime.UtcNow;
                            },
                            createFactory: () => criteria);
                    }
                }

                await repo.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error importing criteria for achievement {AchievementId}", achievementId);
            }
        }

        /// <summary>
        /// Get all achievements from the database
        /// </summary>
        public async Task<List<WowAchievements>> GetAllAchievementsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowAchievements>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Get an achievement by ID
        /// </summary>
        public async Task<WowAchievements> GetAchievementByIdAsync(long achievementId)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowAchievements>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.FirstOrDefaultAsync(a => a.Id == achievementId);
        }

        /// <summary>
        /// Search achievements by name
        /// </summary>
        public async Task<List<WowAchievements>> SearchAchievementsAsync(string query)
        {
            var queryLower = query.ToLower();

            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowAchievements>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allAchievements = await repo.GetAllAsync();

            return allAchievements.Where(a => a.Name.ToLower().Contains(queryLower)).ToList();
        }

        /// <summary>
        /// Get achievements by category
        /// </summary>
        public async Task<List<WowAchievements>> GetAchievementsByCategoryAsync(string category)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowAchievements>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allAchievements = await repo.GetAllAsync();

            return allAchievements.Where(a => a.Category != null && a.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Get criteria for an achievement
        /// </summary>
        public async Task<List<WowAchievementCriteria>> GetAchievementCriteriaAsync(long achievementId)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowAchievementCriteria>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allCriteria = await repo.GetAllAsync();

            return allCriteria.Where(c => c.AchievementId == achievementId).OrderBy(c => c.OrderIndex).ToList();
        }

        #endregion

        #region Pets Import/Retrieval

        /// <summary>
        /// Import all pets from the WoW API
        /// </summary>
        public async Task ImportAllPetsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting pet import");

                var url = "/data/wow/pet/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
                var petData = JsonConvert.DeserializeObject<dynamic>(response);

                if (petData?.pets == null)
                {
                    _logger.LogWarning("No pets found in index");
                    return;
                }

                int totalCount = petData.pets.Count;
                _logger.LogInformation("Found {Count} pets to import", totalCount);

                // Load existing pet IDs to skip API calls for already-imported pets
                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var existingPets = await repo.GetAllAsync();
                var existingIds = new HashSet<long>(existingPets.Select(p => p.Id));
                _logger.LogInformation("Found {Count} existing pets in database, will skip those", existingIds.Count);

                int imported = 0;
                int skipped = 0;
                int failed = 0;

                foreach (var petEntry in petData.pets)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        long petId = petEntry.id;

                        // Skip if already in database
                        if (existingIds.Contains(petId))
                        {
                            skipped++;
                            continue;
                        }

                        await ImportPetAsync(petId, "us", cancellationToken);
                        imported++;

                        if ((imported + skipped) % 100 == 0)
                        {
                            _logger.LogInformation("Pet import progress: {Imported} imported, {Skipped} skipped, {Processed}/{Total} processed",
                                imported, skipped, imported + skipped, totalCount);
                        }

                        // Rate limiting - 100ms to stay safely under Blizzard's 10 req/sec limit
                        // Each pet makes 2 API calls (details + media)
                        await Task.Delay(100, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import pet {PetId}", (long)petEntry.id);
                        failed++;
                    }
                }

                _logger.LogInformation("Pet import completed: {Imported} imported, {Skipped} skipped, {Failed} failed", imported, skipped, failed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during pet import");
                throw;
            }
        }

        /// <summary>
        /// Import a single pet by ID
        /// </summary>
        public async Task<WowPets> ImportPetAsync(long petId, string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Importing pet {PetId}", petId);

                var url = $"/data/wow/pet/{petId}?namespace=static-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var petData = JsonConvert.DeserializeObject<dynamic>(response);

                if (petData == null)
                {
                    _logger.LogWarning("Pet {PetId} returned null data", petId);
                    return null;
                }

                // Extract name
                string name = petData.name?.ToString() ?? "Unknown";

                // Extract description
                string description = petData.description?.ToString();

                // Extract pet type
                string petType = petData.battle_pet_type?.name?.ToString();

                // Extract source info
                string source = petData.source?.type?.ToString() ?? "Unknown";
                string sourceDetail = petData.source?.name?.ToString();

                // Check if capturable (wild pet)
                bool isCapturable = petData.is_capturable ?? false;

                // Check if tradable
                bool isTradable = petData.is_tradable ?? false;

                // Check if battle pet
                bool isBattlePet = petData.is_battlepet ?? true;

                // Extract creature info
                long? creatureId = petData.creature?.id;
                long? speciesId = petData.id;

                // Extract media URL
                string mediaUrl = null;
                string iconUrl = null;
                try
                {
                    if (petData.icon != null)
                    {
                        iconUrl = petData.icon?.ToString();
                    }

                    var mediaUrlPath = $"/data/wow/media/pet/{petId}?namespace=static-{region}";
                    var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrlPath, "en_US", region, cancellationToken);
                    var mediaDataResponse = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                    if (mediaDataResponse?.assets != null && mediaDataResponse.assets.Count > 0)
                    {
                        foreach (var asset in mediaDataResponse.assets)
                        {
                            string assetKey = asset?.key?.ToString();
                            string assetValue = asset?.value?.ToString();

                            if (assetKey == "icon" && !string.IsNullOrEmpty(assetValue))
                            {
                                var fileName = TryGetFileName(assetValue);
                                iconUrl = !string.IsNullOrEmpty(fileName)
                                    ? BuildPublicIconUrl(fileName)
                                    : assetValue;
                            }
                            else if (!string.IsNullOrEmpty(assetValue))
                            {
                                mediaUrl = assetValue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Media fetch failed for pet {PetId}, continuing without icon", petId);
                }

                var pet = new WowPets
                {
                    Id = petId,
                    SpeciesId = speciesId,
                    CreatureId = creatureId,
                    Name = name,
                    Description = description,
                    PetType = petType,
                    Source = source,
                    SourceDetail = sourceDetail,
                    IsCapturable = isCapturable,
                    IsTradable = isTradable,
                    IsBattlePet = isBattlePet,
                    Faction = "Both", // Pets are generally faction-neutral
                    MediaUrl = mediaUrl,
                    IconUrl = iconUrl,
                    LastUpdated = DateTime.UtcNow
                };

                using var scope = _scopeFactory.CreateScope();
                var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                await repo.UpsertAsync(
                    findPredicate: p => p.Id == petId,
                    updateAction: existing =>
                    {
                        existing.SpeciesId = pet.SpeciesId;
                        existing.CreatureId = pet.CreatureId;
                        existing.Name = pet.Name;
                        existing.Description = pet.Description;
                        existing.PetType = pet.PetType;
                        existing.Source = pet.Source;
                        existing.SourceDetail = pet.SourceDetail;
                        existing.IsCapturable = pet.IsCapturable;
                        existing.IsTradable = pet.IsTradable;
                        existing.IsBattlePet = pet.IsBattlePet;
                        existing.MediaUrl = pet.MediaUrl;
                        existing.IconUrl = pet.IconUrl;
                        existing.LastUpdated = DateTime.UtcNow;
                    },
                    createFactory: () => pet);

                await repo.SaveChangesAsync();

                _logger.LogDebug("Pet {PetId} ({Name}) imported successfully", petId, pet.Name);
                return pet;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogDebug("Pet {PetId} does not exist (404)", petId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing pet {PetId}", petId);
                throw;
            }
        }

        /// <summary>
        /// Get all pets from the database
        /// </summary>
        public async Task<List<WowPets>> GetAllPetsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.GetAllAsync();
        }

        /// <summary>
        /// Get a pet by ID
        /// </summary>
        public async Task<WowPets> GetPetByIdAsync(long petId)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            return await repo.FirstOrDefaultAsync(p => p.Id == petId);
        }

        /// <summary>
        /// Search pets by name
        /// </summary>
        public async Task<List<WowPets>> SearchPetsAsync(string query)
        {
            var queryLower = query.ToLower();

            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allPets = await repo.GetAllAsync();

            return allPets.Where(p => p.Name.ToLower().Contains(queryLower)).ToList();
        }

        /// <summary>
        /// Get pets by type
        /// </summary>
        public async Task<List<WowPets>> GetPetsByTypeAsync(string petType)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allPets = await repo.GetAllAsync();

            return allPets.Where(p => p.PetType != null && p.PetType.Equals(petType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Get capturable (wild) pets
        /// </summary>
        public async Task<List<WowPets>> GetCapturablePetsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowPets>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
            var allPets = await repo.GetAllAsync();

            return allPets.Where(p => p.IsCapturable).ToList();
        }

        #endregion

        #region Manual Refresh Methods

        /// <summary>
        /// Manually refresh all static data (realms, classes, races)
        /// </summary>
        public async Task<string> RefreshAllStaticDataAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<string>();

            try
            {
                await ImportAllRealmsAsync(cancellationToken);
                results.Add("Realms: Success");
            }
            catch (Exception ex)
            {
                results.Add($"Realms: Failed - {ex.Message}");
            }

            try
            {
                await ImportAllClassesAsync(cancellationToken);
                results.Add("Classes: Success");
            }
            catch (Exception ex)
            {
                results.Add($"Classes: Failed - {ex.Message}");
            }

            try
            {
                await ImportAllRacesAsync(cancellationToken);
                results.Add("Races: Success");
            }
            catch (Exception ex)
            {
                results.Add($"Races: Failed - {ex.Message}");
            }

            return string.Join("\n", results);
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                _updateCancellation?.Cancel();
                if (_updateTask != null)
                {
                    await _updateTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                _updateCancellation?.Dispose();

                _logger.LogInformation("WowStaticDataService disposed async");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("WowStaticDataService update task did not complete within timeout");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing WowStaticDataService");
            }
            finally
            {
                _disposed = true;
            }
        }

        public void Dispose()
        {
            // No-op: Host calls DisposeAsync() directly
            // Avoiding sync-over-async which can deadlock
        }
    }
}
