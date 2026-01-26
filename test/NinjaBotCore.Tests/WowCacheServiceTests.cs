using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class WowCacheServiceTests : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WowCacheService _cacheService;

        public WowCacheServiceTests()
        {
            var services = new ServiceCollection();

            // Ensure all contexts share the same in-memory database instance
            var dbRoot = new InMemoryDatabaseRoot();

            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddMemoryCache();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase("WowCacheTests", dbRoot)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _provider = services.BuildServiceProvider();
            _cacheService = new WowCacheService(
                _provider.GetRequiredService<IMemoryCache>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<WowCacheService>>());
        }

        [Fact]
        public async Task GetUserMainCharacterAsync_UsesCacheUntilInvalidated()
        {
            const long userId = 42;
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowCharAssociation.Add(new WowCharAssociation
                {
                    UserId = userId,
                    CharName = "CachedMain",
                    IsMain = true,
                    WowRealm = "realm",
                    WowRegion = "us"
                });
                await db.SaveChangesAsync();
            }

            var first = await _cacheService.GetUserMainCharacterAsync(userId);
            Assert.NotNull(first);
            Assert.Equal("CachedMain", first.CharName);

            // Update underlying data but keep cache populated
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var existing = await db.WowCharAssociation.FirstAsync(c => c.UserId == userId);
                existing.CharName = "UpdatedMain";
                await db.SaveChangesAsync();
            }

            // Should still return cached value before invalidation
            var cached = await _cacheService.GetUserMainCharacterAsync(userId);
            Assert.Equal("CachedMain", cached.CharName);

            // After invalidation, cache should refresh from database
            _cacheService.InvalidateUserCharacters(userId);
            var refreshed = await _cacheService.GetUserMainCharacterAsync(userId);
            Assert.Equal("UpdatedMain", refreshed.CharName);
        }

        [Fact]
        public async Task GetRioSearchHistoryAsync_RespectsCacheAndInvalidation()
        {
            const long userId = 99;
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.RioSearchHistory.AddRange(
                    new RioSearchHistory
                    {
                        DiscordUserId = userId,
                        CharacterName = "Alpha",
                        RealmName = "A",
                        Region = "us",
                        SearchCount = 1,
                        LastSearched = DateTime.UtcNow.AddMinutes(-10)
                    },
                    new RioSearchHistory
                    {
                        DiscordUserId = userId,
                        CharacterName = "Bravo",
                        RealmName = "B",
                        Region = "us",
                        SearchCount = 5,
                        LastSearched = DateTime.UtcNow.AddMinutes(-5)
                    });
                await db.SaveChangesAsync();
            }

            var initial = await _cacheService.GetRioSearchHistoryAsync(userId);
            Assert.Equal(2, initial.Count);
            Assert.Equal("Bravo", initial.First().CharacterName); // ordered by SearchCount desc

            // Add a higher-priority entry; cache should still return old ordering until invalidated
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.RioSearchHistory.Add(new RioSearchHistory
                {
                    DiscordUserId = userId,
                    CharacterName = "Charlie",
                    RealmName = "C",
                    Region = "us",
                    SearchCount = 10,
                    LastSearched = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var cached = await _cacheService.GetRioSearchHistoryAsync(userId);
            Assert.Equal(2, cached.Count);
            Assert.DoesNotContain(cached, h => h.CharacterName == "Charlie");

            _cacheService.InvalidateRioSearchHistory(userId);
            var refreshed = await _cacheService.GetRioSearchHistoryAsync(userId);
            Assert.Equal(3, refreshed.Count);
            Assert.Equal("Charlie", refreshed.First().CharacterName);
        }

        [Fact]
        public async Task GetWowResourcesAsync_UsesCacheUntilInvalidated()
        {
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowResources.Add(new WowResources
                {
                    ResourceDescription = "raidvid",
                    Resource = "Old"
                });
                await db.SaveChangesAsync();
            }

            var first = await _cacheService.GetWowResourcesAsync("raidvid");
            Assert.Single(first);
            Assert.Equal("Old", first[0].Resource);

            // Update underlying data
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var existing = await db.WowResources.FirstAsync(r => r.ResourceDescription == "raidvid");
                existing.Resource = "New";
                await db.SaveChangesAsync();
            }

            // Cached result should still be returned
            var cached = await _cacheService.GetWowResourcesAsync("raidvid");
            Assert.Equal("Old", cached[0].Resource);

            // Invalidate and confirm refresh
            _cacheService.InvalidateWowResources("raidvid");
            var refreshed = await _cacheService.GetWowResourcesAsync("raidvid");
            Assert.Equal("New", refreshed[0].Resource);
        }

        [Fact]
        public async Task GetLogMonitoringAsync_UsesCacheUntilInvalidated()
        {
            const long guildId = 777;
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.LogMonitoring.Add(new LogMonitoring
                {
                    ServerId = guildId,
                    ServerName = "OldGuild",
                    ChannelId = 1,
                    ChannelName = "logs"
                });
                await db.SaveChangesAsync();
            }

            var first = await _cacheService.GetLogMonitoringAsync(guildId);
            Assert.NotNull(first);
            Assert.Equal("OldGuild", first.ServerName);

            // Update underlying data
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var existing = await db.LogMonitoring.FirstAsync(l => l.ServerId == guildId);
                existing.ServerName = "NewGuild";
                await db.SaveChangesAsync();
            }

            // Cached value persists until invalidation
            var cached = await _cacheService.GetLogMonitoringAsync(guildId);
            Assert.Equal("OldGuild", cached.ServerName);

            _cacheService.InvalidateLogMonitoring(guildId);
            var refreshed = await _cacheService.GetLogMonitoringAsync(guildId);
            Assert.Equal("NewGuild", refreshed.ServerName);
        }

        public void Dispose()
        {
            _provider?.Dispose();
        }
    }
}
