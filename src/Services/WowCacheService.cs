using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Provides caching for frequently accessed WoW-related database queries
    /// </summary>
    public class WowCacheService
    {
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WowCacheService> _logger;

        // Cache expiration times
        private static readonly TimeSpan MainCharacterExpiration = TimeSpan.FromMinutes(15);

        // Limits
        private const int MaxSearchHistoryEntries = 30;
        private static readonly TimeSpan LogMonitoringExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan WowResourcesExpiration = TimeSpan.FromHours(1);
        private static readonly TimeSpan SearchHistoryExpiration = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan GreetingExpiration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ArmoryEquipmentExpiration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Top10RankingsExpiration = TimeSpan.FromHours(10);
        private static readonly TimeSpan CharacterEncounterRankingsExpiration = TimeSpan.FromHours(10);
        private static readonly TimeSpan ZoneRankingsExpiration = TimeSpan.FromHours(10);
        private static readonly TimeSpan GuildReportsExpiration = TimeSpan.FromHours(10);

        public WowCacheService(IMemoryCache cache, IServiceScopeFactory scopeFactory, ILogger<WowCacheService> logger)
        {
            _cache = cache;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// Gets a user's main character with caching
        /// </summary>
        public async Task<WowCharAssociation> GetUserMainCharacterAsync(long userId)
        {
            var cacheKey = $"wow_main_char_{userId}";

            if (_cache.TryGetValue<WowCharAssociation>(cacheKey, out var cachedChar))
            {
                return cachedChar;
            }

            await using var repo = new Repository<WowCharAssociation>(_scopeFactory);
            var character = await repo.FirstOrDefaultAsync(c => c.UserId == userId && c.IsMain);

            if (character != null)
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = MainCharacterExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, character, cacheOptions);
            }

            return character;
        }

        /// <summary>
        /// Gets all characters for a user with caching
        /// </summary>
        public async Task<List<WowCharAssociation>> GetUserCharactersAsync(long userId)
        {
            var cacheKey = $"wow_user_chars_{userId}";

            if (_cache.TryGetValue<List<WowCharAssociation>>(cacheKey, out var cachedChars))
            {
                return cachedChars;
            }

            await using var repo = new Repository<WowCharAssociation>(_scopeFactory);
            var characters = await repo.WhereAsync(c => c.UserId == userId);

            if (characters != null && characters.Any())
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = MainCharacterExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, characters, cacheOptions);
            }

            return characters;
        }

        /// <summary>
        /// Gets log monitoring settings for a guild with caching
        /// </summary>
        public async Task<LogMonitoring> GetLogMonitoringAsync(long serverId)
        {
            var cacheKey = $"log_monitoring_{serverId}";

            if (_cache.TryGetValue<LogMonitoring>(cacheKey, out var cachedSettings))
            {
                return cachedSettings;
            }

            await using var repo = new Repository<LogMonitoring>(_scopeFactory);
            var settings = await repo.FirstOrDefaultAsync(l => l.ServerId == serverId);

            if (settings != null)
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = LogMonitoringExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, settings, cacheOptions);
            }

            return settings;
        }

        /// <summary>
        /// Gets WoW resources by description with caching
        /// </summary>
        public async Task<List<WowResources>> GetWowResourcesAsync(string resourceDescription)
        {
            var cacheKey = $"wow_resources_{resourceDescription}";

            if (_cache.TryGetValue<List<WowResources>>(cacheKey, out var cachedResources))
            {
                return cachedResources;
            }

            await using var repo = new Repository<WowResources>(_scopeFactory);
            var resources = await repo.WhereAsync(r => r.ResourceDescription == resourceDescription);

            if (resources != null && resources.Any())
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = WowResourcesExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, resources, cacheOptions);
            }

            return resources;
        }

        /// <summary>
        /// Invalidates the cached main character for a user
        /// </summary>
        public void InvalidateUserMainCharacter(long userId)
        {
            _cache.Remove($"wow_main_char_{userId}");
        }

        /// <summary>
        /// Invalidates all cached characters for a user
        /// </summary>
        public void InvalidateUserCharacters(long userId)
        {
            _cache.Remove($"wow_user_chars_{userId}");
            _cache.Remove($"wow_main_char_{userId}");
        }

        /// <summary>
        /// Invalidates the cached log monitoring settings for a guild
        /// </summary>
        public void InvalidateLogMonitoring(long serverId)
        {
            _cache.Remove($"log_monitoring_{serverId}");
        }

        /// <summary>
        /// Invalidates cached WoW resources
        /// </summary>
        public void InvalidateWowResources(string resourceDescription)
        {
            _cache.Remove($"wow_resources_{resourceDescription}");
        }

        /// <summary>
        /// Gets user's RIO search history with caching (for autocomplete)
        /// </summary>
        public async Task<List<RioSearchHistory>> GetRioSearchHistoryAsync(long userId)
        {
            var cacheKey = $"rio_search_history_{userId}";

            if (_cache.TryGetValue<List<RioSearchHistory>>(cacheKey, out var cachedHistory))
            {
                return cachedHistory;
            }

            // Uses direct DbContext instead of Repository because query requires
            // OrderByDescending, ThenByDescending, and Take operations
            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var history = await db.RioSearchHistory
                .Where(h => h.DiscordUserId == userId && h.GameVersion == null)
                .OrderByDescending(h => h.SearchCount)
                .ThenByDescending(h => h.LastSearched)
                .Take(30) // Match the limit from SaveRioSearchHistory
                .ToListAsync();

            if (history != null && history.Any())
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = SearchHistoryExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, history, cacheOptions);
            }

            return history ?? new List<RioSearchHistory>();
        }

        /// <summary>
        /// Invalidates the cached RIO search history for a user
        /// </summary>
        public void InvalidateRioSearchHistory(long userId)
        {
            _cache.Remove($"rio_search_history_{userId}");
        }

        /// <summary>
        /// Records a character search to the user's search history.
        /// Updates existing entry or creates new one. Maintains max 30 entries per user.
        /// </summary>
        public async Task RecordSearchHistoryAsync(long userId, string characterName, string realmName, string region)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Check for existing retail entry (GameVersion == null means retail)
                var existing = await db.RioSearchHistory
                    .FirstOrDefaultAsync(h =>
                        h.DiscordUserId == userId &&
                        h.GameVersion == null &&
                        h.CharacterName.ToLower() == characterName.ToLower() &&
                        h.RealmName.ToLower() == realmName.ToLower() &&
                        h.Region.ToLower() == region.ToLower());

                if (existing != null)
                {
                    // Update existing entry
                    existing.SearchCount++;
                    existing.LastSearched = DateTime.UtcNow;
                }
                else
                {
                    // Add new entry
                    db.RioSearchHistory.Add(new RioSearchHistory
                    {
                        DiscordUserId = userId,
                        CharacterName = characterName,
                        RealmName = realmName,
                        Region = region,
                        LastSearched = DateTime.UtcNow,
                        SearchCount = 1
                    });

                    // Enforce max entries per user (retail only) - delete oldest/least used if over limit
                    var count = await db.RioSearchHistory.CountAsync(
                        h => h.DiscordUserId == userId && h.GameVersion == null);
                    if (count >= MaxSearchHistoryEntries)
                    {
                        var oldest = await db.RioSearchHistory
                            .Where(h => h.DiscordUserId == userId && h.GameVersion == null)
                            .OrderBy(h => h.SearchCount)
                            .ThenBy(h => h.LastSearched)
                            .FirstOrDefaultAsync();

                        if (oldest != null)
                        {
                            db.RioSearchHistory.Remove(oldest);
                        }
                    }
                }

                await db.SaveChangesAsync();

                // Invalidate cache so next autocomplete fetch gets fresh data
                InvalidateRioSearchHistory(userId);
            }
            catch (Exception ex)
            {
                // Non-critical - log at debug level for diagnostics
                _logger.LogDebug(ex, "Failed to record search history for user {UserId}", userId);
            }
        }

        #region Classic Search History

        /// <summary>
        /// Gets Classic character search history for a user, sorted by frequency then recency
        /// </summary>
        public async Task<List<RioSearchHistory>> GetClassicSearchHistoryAsync(long userId)
        {
            var cacheKey = $"classic_search_history_{userId}";

            if (_cache.TryGetValue<List<RioSearchHistory>>(cacheKey, out var cachedHistory))
            {
                return cachedHistory;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var history = await db.RioSearchHistory
                .Where(h => h.DiscordUserId == userId && h.GameVersion == "Classic")
                .OrderByDescending(h => h.SearchCount)
                .ThenByDescending(h => h.LastSearched)
                .Take(30)
                .ToListAsync();

            if (history != null && history.Any())
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = SearchHistoryExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, history, cacheOptions);
            }

            return history ?? new List<RioSearchHistory>();
        }

        /// <summary>
        /// Records a Classic character search to the user's search history
        /// </summary>
        public async Task RecordClassicSearchHistoryAsync(long userId, string characterName, string realmName, string region)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var existing = await db.RioSearchHistory
                    .FirstOrDefaultAsync(h =>
                        h.DiscordUserId == userId &&
                        h.GameVersion == "Classic" &&
                        h.CharacterName.ToLower() == characterName.ToLower() &&
                        h.RealmName.ToLower() == realmName.ToLower() &&
                        h.Region.ToLower() == region.ToLower());

                if (existing != null)
                {
                    existing.SearchCount++;
                    existing.LastSearched = DateTime.UtcNow;
                }
                else
                {
                    db.RioSearchHistory.Add(new RioSearchHistory
                    {
                        DiscordUserId = userId,
                        CharacterName = characterName,
                        RealmName = realmName,
                        Region = region,
                        LastSearched = DateTime.UtcNow,
                        SearchCount = 1,
                        GameVersion = "Classic"
                    });

                    // Enforce max entries per user (Classic only)
                    var count = await db.RioSearchHistory.CountAsync(
                        h => h.DiscordUserId == userId && h.GameVersion == "Classic");
                    if (count >= MaxSearchHistoryEntries)
                    {
                        var oldest = await db.RioSearchHistory
                            .Where(h => h.DiscordUserId == userId && h.GameVersion == "Classic")
                            .OrderBy(h => h.SearchCount)
                            .ThenBy(h => h.LastSearched)
                            .FirstOrDefaultAsync();

                        if (oldest != null)
                        {
                            db.RioSearchHistory.Remove(oldest);
                        }
                    }
                }

                await db.SaveChangesAsync();
                _cache.Remove($"classic_search_history_{userId}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to record Classic search history for user {UserId}", userId);
            }
        }

        #endregion

        /// <summary>
        /// Gets server greeting settings for a guild with caching
        /// </summary>
        public async Task<ServerGreeting> GetServerGreetingAsync(long guildId)
        {
            var cacheKey = $"server_greeting_{guildId}";

            if (_cache.TryGetValue<ServerGreeting>(cacheKey, out var cachedGreeting))
            {
                return cachedGreeting;
            }

            await using var repo = new Repository<ServerGreeting>(_scopeFactory);
            var greeting = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);

            if (greeting != null)
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = GreetingExpiration,
                    Size = 1
                };
                _cache.Set(cacheKey, greeting, cacheOptions);
            }

            return greeting;
        }

        /// <summary>
        /// Invalidates the cached server greeting for a guild
        /// </summary>
        public void InvalidateServerGreeting(long guildId)
        {
            _cache.Remove($"server_greeting_{guildId}");
        }

        /// <summary>
        /// Gets cached armory equipment data for a character
        /// </summary>
        public (ArmoryEquipment Equipment, ArmoryMedia Media)? GetCachedArmoryEquipment(string characterName, string realmSlug, string region)
        {
            var cacheKey = $"armory_equipment_{characterName.ToLower()}_{realmSlug.ToLower()}_{region.ToLower()}";

            if (_cache.TryGetValue<(ArmoryEquipment, ArmoryMedia)>(cacheKey, out var cached))
            {
                return cached;
            }

            return null;
        }

        /// <summary>
        /// Caches armory equipment data for a character
        /// </summary>
        public void SetCachedArmoryEquipment(string characterName, string realmSlug, string region, ArmoryEquipment equipment, ArmoryMedia media)
        {
            var cacheKey = $"armory_equipment_{characterName.ToLower()}_{realmSlug.ToLower()}_{region.ToLower()}";

            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ArmoryEquipmentExpiration,
                Size = 1
            };

            _cache.Set(cacheKey, (equipment, media), cacheOptions);
        }

        /// <summary>
        /// Invalidates cached armory equipment for a character
        /// </summary>
        public void InvalidateArmoryEquipment(string characterName, string realmSlug, string region)
        {
            var cacheKey = $"armory_equipment_{characterName.ToLower()}_{realmSlug.ToLower()}_{region.ToLower()}";
            _cache.Remove(cacheKey);
        }

        // ===== Top 10 Rankings Cache =====

        /// <summary>
        /// Generates cache key for top10 rankings
        /// </summary>
        private string GetTop10CacheKey(string scope, string serverSlug, string region, int encounterId, string metric, string difficulty, string guildName = null)
        {
            var baseKey = $"top10_{scope}_{serverSlug.ToLower()}_{region.ToLower()}_{encounterId}_{metric.ToLower()}_{difficulty.ToLower()}";
            return scope == "guild" && !string.IsNullOrEmpty(guildName)
                ? $"{baseKey}_{guildName.ToLower().Replace(" ", "_")}"
                : baseKey;
        }

        /// <summary>
        /// Gets cached top10 rankings if available
        /// </summary>
        public List<WclV2CharacterRanking> GetCachedTop10Rankings(string scope, string serverSlug, string region, int encounterId, string metric, string difficulty, string guildName = null)
        {
            var cacheKey = GetTop10CacheKey(scope, serverSlug, region, encounterId, metric, difficulty, guildName);

            if (_cache.TryGetValue<List<WclV2CharacterRanking>>(cacheKey, out var cachedRankings))
            {
                _logger.LogDebug("[Top10 Cache] HIT: {CacheKey}", cacheKey);
                return cachedRankings;
            }

            _logger.LogDebug("[Top10 Cache] MISS: {CacheKey}", cacheKey);
            return null;
        }

        /// <summary>
        /// Caches top10 rankings
        /// </summary>
        public void SetCachedTop10Rankings(string scope, string serverSlug, string region, int encounterId, string metric, string difficulty, List<WclV2CharacterRanking> rankings, string guildName = null)
        {
            var cacheKey = GetTop10CacheKey(scope, serverSlug, region, encounterId, metric, difficulty, guildName);

            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Top10RankingsExpiration,
                Size = 1
            };

            _cache.Set(cacheKey, rankings, cacheOptions);

            // Track the key for bulk invalidation when new logs are detected
            TrackTop10CacheKey(cacheKey);

            _logger.LogDebug("[Top10 Cache] SET: {CacheKey} ({Count} rankings)", cacheKey, rankings?.Count ?? 0);
        }

        /// <summary>
        /// Invalidates all top10 cache entries for a specific realm
        /// Note: IMemoryCache doesn't support wildcard removal, so we track keys
        /// </summary>
        public void InvalidateTop10Rankings(string serverSlug, string region)
        {
            // IMemoryCache doesn't support prefix-based removal
            // For now, we rely on TTL expiration
            // A full implementation would require tracking cache keys in a separate collection
            _logger.LogInformation("[Top10 Cache] Invalidation requested for {ServerSlug}-{Region} (will expire via TTL)", serverSlug, region);
        }

        /// <summary>
        /// Invalidates a specific top10 cache entry
        /// </summary>
        public void InvalidateTop10Rankings(string scope, string serverSlug, string region, int encounterId, string metric, string difficulty, string guildName = null)
        {
            var cacheKey = GetTop10CacheKey(scope, serverSlug, region, encounterId, metric, difficulty, guildName);
            _cache.Remove(cacheKey);
            _logger.LogInformation("[Top10 Cache] Invalidated: {CacheKey}", cacheKey);
        }

        // ===== Character Encounter Rankings Cache (individual parses for /char logs) =====

        /// <summary>
        /// Generates cache key for character encounter rankings (individual parses)
        /// </summary>
        private string GetCharEncounterCacheKey(string characterName, string realmSlug, string region, int encounterId, int? difficulty)
        {
            var diffStr = difficulty?.ToString() ?? "all";
            return $"char_encounter_{characterName.ToLower()}_{realmSlug.ToLower()}_{region.ToLower()}_{encounterId}_{diffStr}";
        }

        /// <summary>
        /// Gets cached character encounter rankings if available
        /// </summary>
        public WclV2EncounterRankingsData GetCachedCharacterEncounterRankings(
            string characterName,
            string realmSlug,
            string region,
            int encounterId,
            int? difficulty)
        {
            var cacheKey = GetCharEncounterCacheKey(characterName, realmSlug, region, encounterId, difficulty);

            if (_cache.TryGetValue<WclV2EncounterRankingsData>(cacheKey, out var cachedRankings))
            {
                _logger.LogDebug("[CharEncounter Cache] HIT: {CacheKey}", cacheKey);
                return cachedRankings;
            }

            _logger.LogDebug("[CharEncounter Cache] MISS: {CacheKey}", cacheKey);
            return null;
        }

        /// <summary>
        /// Caches character encounter rankings
        /// </summary>
        public void SetCachedCharacterEncounterRankings(
            string characterName,
            string realmSlug,
            string region,
            int encounterId,
            int? difficulty,
            WclV2EncounterRankingsData rankings)
        {
            var cacheKey = GetCharEncounterCacheKey(characterName, realmSlug, region, encounterId, difficulty);

            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CharacterEncounterRankingsExpiration,
                Size = 1
            };

            _cache.Set(cacheKey, rankings, cacheOptions);
            _logger.LogDebug("[CharEncounter Cache] SET: {CacheKey} ({Count} parses)", cacheKey, rankings?.Ranks?.Count ?? 0);
        }

        /// <summary>
        /// Invalidates cached character encounter rankings for a specific encounter
        /// </summary>
        public void InvalidateCharacterEncounterRankings(string characterName, string realmSlug, string region, int encounterId, int? difficulty)
        {
            var cacheKey = GetCharEncounterCacheKey(characterName, realmSlug, region, encounterId, difficulty);
            _cache.Remove(cacheKey);
            _logger.LogDebug("[CharEncounter Cache] Invalidated: {CacheKey}", cacheKey);
        }

        // ===== Zone Rankings Cache (per-boss aggregate stats for /char logs overview) =====

        /// <summary>
        /// Generates cache key for character zone rankings
        /// </summary>
        private string GetZoneRankingsCacheKey(string characterName, string realmSlug, string region, int zoneId, int? difficulty)
        {
            var diffStr = difficulty?.ToString() ?? "all";
            return $"zone_rankings_{characterName.ToLower()}_{realmSlug.ToLower()}_{region.ToLower()}_{zoneId}_{diffStr}";
        }

        /// <summary>
        /// Gets cached zone rankings if available
        /// </summary>
        public WclV2ZoneRankingsData GetCachedZoneRankings(
            string characterName,
            string realmSlug,
            string region,
            int zoneId,
            int? difficulty)
        {
            var cacheKey = GetZoneRankingsCacheKey(characterName, realmSlug, region, zoneId, difficulty);

            if (_cache.TryGetValue<WclV2ZoneRankingsData>(cacheKey, out var cachedRankings))
            {
                _logger.LogDebug("[ZoneRankings Cache] HIT: {CacheKey}", cacheKey);
                return cachedRankings;
            }

            _logger.LogDebug("[ZoneRankings Cache] MISS: {CacheKey}", cacheKey);
            return null;
        }

        /// <summary>
        /// Caches zone rankings
        /// </summary>
        public void SetCachedZoneRankings(
            string characterName,
            string realmSlug,
            string region,
            int zoneId,
            int? difficulty,
            WclV2ZoneRankingsData rankings)
        {
            var cacheKey = GetZoneRankingsCacheKey(characterName, realmSlug, region, zoneId, difficulty);

            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ZoneRankingsExpiration,
                Size = 1
            };

            _cache.Set(cacheKey, rankings, cacheOptions);
            _logger.LogDebug("[ZoneRankings Cache] SET: {CacheKey} ({Count} bosses)", cacheKey, rankings?.Rankings?.Count ?? 0);
        }

        /// <summary>
        /// Invalidates cached zone rankings for a character
        /// </summary>
        public void InvalidateZoneRankings(string characterName, string realmSlug, string region, int zoneId, int? difficulty)
        {
            var cacheKey = GetZoneRankingsCacheKey(characterName, realmSlug, region, zoneId, difficulty);
            _cache.Remove(cacheKey);
            _logger.LogDebug("[ZoneRankings Cache] Invalidated: {CacheKey}", cacheKey);
        }

        // ===== Guild Reports Cache (for /logs command) =====

        /// <summary>
        /// Generates cache key for guild reports
        /// </summary>
        private string GetGuildReportsCacheKey(string guildName, string realmSlug, string region)
        {
            return $"guild_reports_{guildName.ToLower()}_{realmSlug.ToLower()}_{region.ToLower()}";
        }

        /// <summary>
        /// Gets cached guild reports if available
        /// </summary>
        public List<WclV2Report> GetCachedGuildReports(string guildName, string realmSlug, string region)
        {
            var cacheKey = GetGuildReportsCacheKey(guildName, realmSlug, region);

            if (_cache.TryGetValue<List<WclV2Report>>(cacheKey, out var cachedReports))
            {
                _logger.LogDebug("[GuildReports Cache] HIT: {CacheKey}", cacheKey);
                return cachedReports;
            }

            _logger.LogDebug("[GuildReports Cache] MISS: {CacheKey}", cacheKey);
            return null;
        }

        /// <summary>
        /// Caches guild reports
        /// </summary>
        public void SetCachedGuildReports(string guildName, string realmSlug, string region, List<WclV2Report> reports)
        {
            var cacheKey = GetGuildReportsCacheKey(guildName, realmSlug, region);

            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = GuildReportsExpiration,
                Size = 1
            };

            _cache.Set(cacheKey, reports, cacheOptions);
            _logger.LogDebug("[GuildReports Cache] SET: {CacheKey} ({Count} reports)", cacheKey, reports?.Count ?? 0);
        }

        /// <summary>
        /// Invalidates cached guild reports
        /// </summary>
        public void InvalidateGuildReports(string guildName, string realmSlug, string region)
        {
            var cacheKey = GetGuildReportsCacheKey(guildName, realmSlug, region);
            _cache.Remove(cacheKey);
            _logger.LogDebug("[GuildReports Cache] Invalidated: {CacheKey}", cacheKey);
        }

        // ===== Bulk Invalidation for New Log Detection =====

        // Track cache keys for bulk invalidation (IMemoryCache doesn't support prefix removal)
        private static readonly HashSet<string> _top10CacheKeys = new();
        private static readonly object _top10KeysLock = new();

        /// <summary>
        /// Registers a top10 cache key for later bulk invalidation
        /// </summary>
        private void TrackTop10CacheKey(string cacheKey)
        {
            lock (_top10KeysLock)
            {
                _top10CacheKeys.Add(cacheKey);
            }
        }

        /// <summary>
        /// Invalidates all WCL caches for a guild when a new log is detected.
        /// Called by the log watcher service via API when posting new logs.
        /// </summary>
        /// <param name="guildName">Guild name</param>
        /// <param name="realmSlug">Realm slug</param>
        /// <param name="region">Region (us, eu, etc.)</param>
        /// <returns>Number of cache entries invalidated</returns>
        public int InvalidateGuildWclCaches(string guildName, string realmSlug, string region)
        {
            int invalidated = 0;

            // 1. Invalidate guild reports
            var guildReportsKey = GetGuildReportsCacheKey(guildName, realmSlug, region);
            _cache.Remove(guildReportsKey);
            invalidated++;

            // 2. Invalidate top10 rankings for this realm
            List<string> keysToRemove;
            lock (_top10KeysLock)
            {
                var realmPattern = $"_{realmSlug.ToLower()}_{region.ToLower()}_";
                keysToRemove = _top10CacheKeys
                    .Where(k => k.Contains(realmPattern))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _top10CacheKeys.Remove(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _cache.Remove(key);
                invalidated++;
            }

            _logger.LogInformation("[WCL Cache] Invalidated {Count} entries for guild {Guild} on {Realm}-{Region} (new log detected)",
                invalidated, guildName, realmSlug, region);

            return invalidated;
        }
    }
}
