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
        private static readonly TimeSpan LogMonitoringExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan WowResourcesExpiration = TimeSpan.FromHours(1);
        private static readonly TimeSpan SearchHistoryExpiration = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan GreetingExpiration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ArmoryEquipmentExpiration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Top10RankingsExpiration = TimeSpan.FromHours(1);

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
                .Where(h => h.DiscordUserId == userId)
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

                // Check for existing entry
                var existing = await db.RioSearchHistory
                    .FirstOrDefaultAsync(h =>
                        h.DiscordUserId == userId &&
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

                    // Enforce max 30 entries per user - delete oldest/least used if over limit
                    var count = await db.RioSearchHistory.CountAsync(h => h.DiscordUserId == userId);
                    if (count >= 30)
                    {
                        var oldest = await db.RioSearchHistory
                            .Where(h => h.DiscordUserId == userId)
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
    }
}
