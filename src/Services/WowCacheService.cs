using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Provides caching for frequently accessed WoW-related database queries
    /// </summary>
    public class WowCacheService
    {
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;

        // Cache expiration times
        private static readonly TimeSpan MainCharacterExpiration = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan LogMonitoringExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan WowResourcesExpiration = TimeSpan.FromHours(1);
        private static readonly TimeSpan SearchHistoryExpiration = TimeSpan.FromMinutes(5);

        public WowCacheService(IMemoryCache cache, IServiceScopeFactory scopeFactory)
        {
            _cache = cache;
            _scopeFactory = scopeFactory;
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

            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var characters = await db.WowCharAssociation
                .Where(c => c.UserId == userId)
                .ToListAsync();

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

            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var resources = await db.WowResources
                .Where(r => r.ResourceDescription == resourceDescription)
                .ToListAsync();

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
    }
}
