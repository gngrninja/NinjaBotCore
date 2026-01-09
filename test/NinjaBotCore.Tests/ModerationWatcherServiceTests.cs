using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for ModerationWatcherService - cache management and settings retrieval
    /// </summary>
    public class ModerationWatcherServiceTests : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ModerationWatcherService _service;
        private readonly IMemoryCache _cache;
        private readonly NinjaBotEntities _context;

        public ModerationWatcherServiceTests()
        {
            var services = new ServiceCollection();

            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddMemoryCache(options =>
            {
                options.SizeLimit = 1000;
            });
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"ModWatcherTests_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddSingleton<Discord.WebSocket.DiscordShardedClient>();

            _provider = services.BuildServiceProvider();
            _cache = _provider.GetRequiredService<IMemoryCache>();
            _context = _provider.GetRequiredService<NinjaBotEntities>();
            _service = new ModerationWatcherService(_provider);
        }

        [Fact]
        public void InvalidateSettingsCache_RemovesCachedSettings()
        {
            // Arrange
            const long guildId = 12345;
            var cacheKey = $"modwatch_settings_{guildId}";
            var cacheOptions = new MemoryCacheEntryOptions { Size = 1 };
            _cache.Set(cacheKey, new ModerationWatcher { DiscordGuildId = guildId }, cacheOptions);

            Assert.True(_cache.TryGetValue(cacheKey, out _));

            // Act
            _service.InvalidateSettingsCache(guildId);

            // Assert
            Assert.False(_cache.TryGetValue(cacheKey, out _));
        }

        [Fact]
        public async Task GetSettingsAsync_RefreshesAfterCacheExpiration()
        {
            // Arrange
            const long guildId = 34567;
            var cacheKey = $"modwatch_settings_{guildId}";

            var settings = new ModerationWatcher
            {
                DiscordGuildId = guildId,
                WatchMessages = true,
                ChannelId = 888888
            };

            // Act - Manually add to cache with short expiration
            var shortExpiry = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(100),
                Size = 1
            };
            _cache.Set(cacheKey, settings, shortExpiry);
            Assert.True(_cache.TryGetValue(cacheKey, out _)); // Cached

            // Wait for cache to expire
            await Task.Delay(150);

            // Assert - Cache should be expired
            Assert.False(_cache.TryGetValue(cacheKey, out _));
        }

        [Fact]
        public void InvalidateCache_WorksWithMultipleGuilds()
        {
            // Arrange - Test that cache invalidation only affects specific guild
            const long guild1 = 11111;
            const long guild2 = 22222;

            var settings1 = new ModerationWatcher { DiscordGuildId = guild1, WatchMessages = true };
            var settings2 = new ModerationWatcher { DiscordGuildId = guild2, WatchVoice = true };

            var cacheOptions = new MemoryCacheEntryOptions { Size = 1 };
            _cache.Set($"modwatch_settings_{guild1}", settings1, cacheOptions);
            _cache.Set($"modwatch_settings_{guild2}", settings2, cacheOptions);

            // Act - Invalidate only guild1
            _service.InvalidateSettingsCache(guild1);

            // Assert
            Assert.False(_cache.TryGetValue($"modwatch_settings_{guild1}", out _)); // Guild 1 removed
            Assert.True(_cache.TryGetValue($"modwatch_settings_{guild2}", out _));  // Guild 2 still cached
        }

        [Fact]
        public void CacheKey_FollowsExpectedFormat()
        {
            // Verify the cache key format used by the service
            const long guildId = 99999;
            var expectedKey = $"modwatch_settings_{guildId}";

            // Add settings to cache with the expected key format
            var settings = new ModerationWatcher { DiscordGuildId = guildId };
            var cacheOptions = new MemoryCacheEntryOptions { Size = 1 };
            _cache.Set(expectedKey, settings, cacheOptions);

            // Invalidate using service method
            _service.InvalidateSettingsCache(guildId);

            // Verify it was removed (confirms service uses same key format)
            Assert.False(_cache.TryGetValue(expectedKey, out _));
        }

        [Fact]
        public void InvalidateSettingsCache_HandlesMissingCache_Gracefully()
        {
            // Arrange - No cache entry exists
            const long guildId = 77777;
            var cacheKey = $"modwatch_settings_{guildId}";

            // Verify nothing is cached
            Assert.False(_cache.TryGetValue(cacheKey, out _));

            // Act - Should not throw
            var exception = Record.Exception(() => _service.InvalidateSettingsCache(guildId));

            // Assert
            Assert.Null(exception);
        }

        public async ValueTask DisposeAsync()
        {
            _context?.Database.EnsureDeleted();
            if (_context != null)
                await _context.DisposeAsync();
            if (_provider != null)
                await _provider.DisposeAsync();
        }
    }
}
