using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    public class WowStaticDataService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfigurationRoot _config;
        private readonly WowApi _wowApi;
        private CancellationTokenSource _updateCancellation;
        private Task _updateTask;
        private bool _disposed;

        // Configuration keys with defaults
        private const string DEFAULT_TOKEN_UPDATE_INTERVAL_MINUTES = "15"; // Update token price every 15 minutes
        private const string DEFAULT_MOUNT_UPDATE_INTERVAL_DAYS = "7"; // Update mounts every 7 days

        public WowStaticDataService(
            IServiceScopeFactory scopeFactory,
            ILogger<WowStaticDataService> logger,
            IConfigurationRoot config,
            WowApi wowApi)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
            _wowApi = wowApi;

            // Start background update tasks
            InitializeUpdateLoop();
        }

        private void InitializeUpdateLoop()
        {
            _updateCancellation = new CancellationTokenSource();
            _updateTask = RunUpdateLoopAsync(_updateCancellation.Token);
            _logger.LogInformation("WowStaticDataService update loop initialized");
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
            var tokenPriceInterval = TimeSpan.FromMinutes(
                int.Parse(_config["WowTokenPriceUpdateIntervalMinutes"] ?? DEFAULT_TOKEN_UPDATE_INTERVAL_MINUTES));
            var mountUpdateInterval = TimeSpan.FromDays(
                int.Parse(_config["WowMountUpdateIntervalDays"] ?? DEFAULT_MOUNT_UPDATE_INTERVAL_DAYS));

            _logger.LogInformation("Token price update interval: {TokenMinutes}m", tokenPriceInterval.TotalMinutes);
            _logger.LogInformation("Mount update interval: {MountDays}d", mountUpdateInterval.TotalDays);

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
            using (var scope = _scopeFactory.CreateScope())
            {
                var itemRepo = new Repository<WowItems>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var itemCount = (await itemRepo.GetAllAsync()).Count();

                if (itemCount == 0)
                {
                    _logger.LogInformation("Item database is empty. Starting initial bulk item import...");
                    await ImportAllItemsAsync(cancellationToken);
                }

                var mountRepo = new Repository<WowMounts>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                var mountCount = (await mountRepo.GetAllAsync()).Count();

                if (mountCount == 0)
                {
                    _logger.LogInformation("Mount database is empty. Starting initial mount import...");
                    await ImportAllMountsAsync("us", cancellationToken);
                }
            }

            // Perform initial token price update for all regions
            await UpdateAllRegionTokenPricesAsync(cancellationToken);

            // Start periodic updates in parallel
            var tokenPriceTimer = new PeriodicTimer(tokenPriceInterval);
            var mountUpdateTimer = new PeriodicTimer(mountUpdateInterval);

            try
            {
                var tokenTask = RunTokenPriceUpdatesAsync(tokenPriceTimer, cancellationToken);
                var mountTask = RunMountUpdatesAsync(mountUpdateTimer, cancellationToken);

                await Task.WhenAll(tokenTask, mountTask);
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

        private async Task RunTokenPriceUpdatesAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await UpdateAllRegionTokenPricesAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when service is disposed
            }
        }

        private async Task RunMountUpdatesAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    _logger.LogInformation("Starting periodic mount update");
                    try
                    {
                        await ImportAllMountsAsync("us", cancellationToken);
                        _logger.LogInformation("Periodic mount update completed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during periodic mount update");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when service is disposed
            }
        }

        /// <summary>
        /// Update token prices for all regions
        /// </summary>
        private async Task UpdateAllRegionTokenPricesAsync(CancellationToken cancellationToken)
        {
            var regions = new[] { "us", "eu", "kr", "tw" };

            foreach (var region in regions)
            {
                try
                {
                    await UpdateTokenPricesAsync(region, cancellationToken);
                    _logger.LogInformation("Token price update completed for {Region}", region.ToUpper());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating token prices for {Region}", region.ToUpper());
                }
            }
        }

        /// <summary>
        /// Update WoW token prices for a specific region
        /// </summary>
        public async Task UpdateTokenPricesAsync(string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Fetching WoW token price for region: {Region}", region);

                var url = $"/data/wow/token/index?namespace=dynamic-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var tokenData = JsonConvert.DeserializeObject<dynamic>(response);

                if (tokenData?.price != null)
                {
                    long price = tokenData.price;

                    using var scope = _scopeFactory.CreateScope();
                    var repo = new Repository<WowTokenPrices>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                    var tokenPrice = new WowTokenPrices
                    {
                        Region = region,
                        Price = price,
                        Timestamp = DateTime.UtcNow
                    };

                    await repo.AddAsync(tokenPrice);
                    await repo.SaveChangesAsync();

                    _logger.LogInformation(
                        "Token price updated: {Region}={Price}g",
                        region,
                        price / 10000); // Convert copper to gold
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating token price for region {Region}", region);
                throw;
            }
        }

        /// <summary>
        /// Get current WoW token price for a region
        /// </summary>
        public async Task<WowTokenPrices> GetCurrentTokenPriceAsync(string region = "us")
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowTokenPrices>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

            // Get all prices for this region and sort in memory
            var allPrices = await repo.WhereAsync(t => t.Region == region);
            var recentPrice = allPrices.OrderByDescending(t => t.Timestamp).FirstOrDefault();

            return recentPrice;
        }

        /// <summary>
        /// Get token price trend (24h change)
        /// </summary>
        public async Task<long?> GetTokenPriceTrendAsync(string region = "us")
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = new Repository<WowTokenPrices>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

            var dayAgo = DateTime.UtcNow.AddHours(-24);

            // Get all prices for this region
            var allPrices = await repo.WhereAsync(t => t.Region == region);

            // Get current (most recent) price
            var current = allPrices.OrderByDescending(t => t.Timestamp).FirstOrDefault();

            // Get price from ~24h ago
            var previous = allPrices
                .Where(t => t.Timestamp <= dayAgo)
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            if (current != null && previous != null)
            {
                return current.Price - previous.Price;
            }

            return null;
        }

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
                        string qualityType = itemData.quality.type.ToString();
                        if (int.TryParse(qualityType, out int parsedQuality))
                        {
                            quality = parsedQuality;
                        }
                        else
                        {
                            // Map string quality names to numeric values
                            quality = qualityType.ToUpper() switch
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
                catch
                {
                    // Media fetch failed, continue without it
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

        /// <summary>
        /// Bulk import all items from the WoW API using search with ID filtering
        /// Uses the Blizzard-approved approach of filtering by minimum item ID to bypass pagination limits
        /// Reference: https://us.forums.blizzard.com/en/blizzard/t/get-itemsubclasses-out-of-the-auction-house-api/12065
        /// </summary>
        public async Task ImportAllItemsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting bulk item import using ID-filtered search (Blizzard-approved method)");
                long minItemId = 1;
                int pageSize = 1000;
                int totalImported = 0;
                int totalSkipped = 0;
                int batchNumber = 1;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Use ID filtering to bypass the 1000-item pagination limit
                        // This is the official Blizzard-approved approach for bulk importing
                        var url = $"/data/wow/search/item?namespace=static-us&orderby=id&id=[{minItemId},]&_page=1&_pageSize={pageSize}";
                        var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
                        var searchResult = JsonConvert.DeserializeObject<dynamic>(response);

                        int resultCount = searchResult?.results?.Count ?? 0;

                        // Log batch progress
                        _logger.LogInformation(
                            "Batch {BatchNumber}: minItemId={MinId}, resultCount={ResultCount}",
                            batchNumber,
                            minItemId,
                            resultCount);

                        if (searchResult?.results == null || searchResult.results.Count == 0)
                        {
                            _logger.LogInformation("No more items found starting from ID {MinId}. Bulk import completed.", minItemId);
                            break;
                        }

                        // Batch insert items from this page
                        using var scope = _scopeFactory.CreateScope();
                        var repo = new Repository<WowItems>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());

                        // Track processed IDs to avoid duplicates in same batch
                        var processedIds = new HashSet<long>();
                        long lastItemId = minItemId;

                        foreach (var result in searchResult.results)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            try
                            {
                                long itemId = result.data.id;

                                // Track the highest item ID we've seen
                                if (itemId > lastItemId)
                                {
                                    lastItemId = itemId;
                                }

                                // Skip duplicates in same batch
                                if (processedIds.Contains(itemId))
                                {
                                    totalSkipped++;
                                    continue;
                                }

                                string itemName = result.data.name?.en_US?.ToString();

                                if (string.IsNullOrEmpty(itemName))
                                {
                                    totalSkipped++;
                                    continue;
                                }

                                processedIds.Add(itemId);

                                // Extract quality - handle both int and string formats
                                int quality = 0;
                                string qualityName = "Common";
                                try
                                {
                                    if (result.data.quality?.type != null)
                                    {
                                        string qualityType = result.data.quality.type.ToString();
                                        if (int.TryParse(qualityType, out int parsedQuality))
                                        {
                                            quality = parsedQuality;
                                        }
                                        else
                                        {
                                            // Map string quality names to numeric values
                                            quality = qualityType.ToUpper() switch
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
                                    }
                                    // Extract localized quality name
                                    if (result.data.quality?.name != null)
                                    {
                                        try
                                        {
                                            qualityName = result.data.quality.name.en_US?.ToString() ?? result.data.quality.name.ToString();
                                        }
                                        catch
                                        {
                                            qualityName = result.data.quality.name.ToString();
                                        }
                                    }
                                }
                                catch
                                {
                                    // Use defaults
                                }

                                // Extract localized inventory type name
                                string inventoryType = null;
                                try
                                {
                                    if (result.data.inventory_type?.name != null)
                                    {
                                        inventoryType = result.data.inventory_type.name.en_US?.ToString() ?? result.data.inventory_type.name.ToString();
                                    }
                                }
                                catch
                                {
                                    inventoryType = result.data.inventory_type?.name?.ToString();
                                }

                                // Truncate to match database constraints: varchar(50) fields
                                if (inventoryType?.Length > 50)
                                {
                                    _logger.LogWarning("Truncating InventoryType for item {ItemId}: '{Value}' (length {Length})", itemId, inventoryType, inventoryType.Length);
                                    inventoryType = inventoryType.Substring(0, 50);
                                }

                                if (qualityName?.Length > 50)
                                {
                                    _logger.LogWarning("Truncating QualityName for item {ItemId}: '{Value}' (length {Length})", itemId, qualityName, qualityName.Length);
                                    qualityName = qualityName.Substring(0, 50);
                                }

                                // Extract localized item class and subclass names
                                string itemClass = null;
                                try
                                {
                                    if (result.data.item_class?.name != null)
                                    {
                                        itemClass = result.data.item_class.name.en_US?.ToString() ?? result.data.item_class.name.ToString();
                                    }
                                }
                                catch
                                {
                                    itemClass = result.data.item_class?.name?.ToString();
                                }

                                string itemSubclass = null;
                                try
                                {
                                    if (result.data.item_subclass?.name != null)
                                    {
                                        itemSubclass = result.data.item_subclass.name.en_US?.ToString() ?? result.data.item_subclass.name.ToString();
                                    }
                                }
                                catch
                                {
                                    itemSubclass = result.data.item_subclass?.name?.ToString();
                                }

                                // Note: MediaUrl is not fetched during bulk import to avoid excessive API calls
                                // Items will have their media URLs populated on-demand when users search for them via /item command

                                var item = new WowItems
                                {
                                    Id = itemId,
                                    Name = itemName,
                                    Quality = quality,
                                    QualityName = qualityName,
                                    ItemLevel = (int)(result.data.level ?? 0),
                                    InventoryType = inventoryType,
                                    ItemClass = itemClass,
                                    ItemSubclass = itemSubclass,
                                    IsEquippable = result.data.is_equippable ?? false,
                                    RequiredLevel = (int)(result.data.required_level ?? 0),
                                    MediaUrl = null, // Will be populated on-demand via ImportItemAsync
                                    LastUpdated = DateTime.UtcNow
                                };

                                // Upsert item into database
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

                                totalImported++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to import item from search result");
                                totalSkipped++;
                            }
                        }

                        // Save all items from this batch
                        await repo.SaveChangesAsync();

                        _logger.LogInformation(
                            "Bulk import progress: Batch {BatchNumber} complete (IDs {MinId}-{MaxId}) - {Imported} total imported, {Skipped} total skipped",
                            batchNumber, minItemId, lastItemId, totalImported, totalSkipped);

                        // Set the minimum ID for the next batch to one more than the highest ID we saw
                        minItemId = lastItemId + 1;
                        batchNumber++;

                        // Rate limiting between batches (conservative: 0.5 req/sec vs limit of 100 req/sec)
                        await Task.Delay(2000, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing batch {BatchNumber} (starting at ID {MinId}) during bulk import", batchNumber, minItemId);
                        // Skip this batch and try the next one
                        minItemId += pageSize;
                        batchNumber++;
                        await Task.Delay(5000, cancellationToken); // Wait longer on error
                    }
                }

                _logger.LogInformation("Bulk item import completed: {Total} items imported, {Skipped} skipped", totalImported, totalSkipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bulk item import");
                throw;
            }
        }

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
        /// Determine expansion from mount ID based on ID ranges
        /// Note: These ranges are approximate. Update this mapping when new expansions launch.
        /// </summary>
        private static string GetExpansionFromMountId(long mountId)
        {
            // Mount ID ranges are approximate and based on when they were added to the game
            // TODO: Update ID threshold for Midnight expansion when it launches (estimate: >= 1800)
            return mountId switch
            {
                >= 1800 => "Midnight",              // Future expansion - adjust this threshold when Midnight launches
                >= 1700 => "The War Within",        // Current expansion
                >= 1500 => "Dragonflight",
                >= 1200 => "Shadowlands",
                >= 900 => "Battle for Azeroth",
                >= 700 => "Legion",
                >= 600 => "Warlords of Draenor",
                >= 500 => "Mists of Pandaria",
                >= 400 => "Cataclysm",
                >= 300 => "Wrath of the Lich King",
                >= 200 => "The Burning Crusade",
                >= 1 => "Classic",
                _ => "Unknown"
            };
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
                    Expansion = GetExpansionFromMountId(mountData.Id),
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
                        existing.Expansion = mount.Expansion;

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

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _updateCancellation?.Cancel();
                _updateTask?.Wait(TimeSpan.FromSeconds(5));
                _updateCancellation?.Dispose();

                _logger.LogInformation("WowStaticDataService disposed");
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
    }
}
