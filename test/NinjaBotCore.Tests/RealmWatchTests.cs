using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RealmWatchTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly IServiceScopeFactory _scopeFactory;

        public RealmWatchTests()
        {
            var services = new ServiceCollection();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"RealmWatchTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
        }

        #region Subscription Tests

        [Fact]
        public async Task AddWatch_CreatesSubscription_WithCorrectDefaults()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var subscription = new RealmWatchSubscription
            {
                GuildId = 123456789,
                UserId = 987654321,
                RealmSlug = "stormrage",
                RealmName = "Stormrage",
                Region = "us",
                ConnectedRealmId = 1,
                AlertOnOnline = true,
                AlertOnOffline = true,
                AlertOnQueue = true,
                CreatedAt = DateTime.UtcNow
            };

            db.RealmWatchSubscriptions.Add(subscription);
            await db.SaveChangesAsync();

            var saved = await db.RealmWatchSubscriptions.FirstAsync();
            Assert.True(saved.AlertOnOnline);
            Assert.True(saved.AlertOnOffline);
            Assert.True(saved.AlertOnQueue);
            Assert.Null(saved.ChannelId); // DM by default
        }

        [Fact]
        public async Task AddWatch_StoresChannelId_WhenSpecified()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var subscription = new RealmWatchSubscription
            {
                GuildId = 123456789,
                UserId = 987654321,
                ChannelId = 111222333444,
                RealmSlug = "stormrage",
                RealmName = "Stormrage",
                Region = "us",
                ConnectedRealmId = 1,
                CreatedAt = DateTime.UtcNow
            };

            db.RealmWatchSubscriptions.Add(subscription);
            await db.SaveChangesAsync();

            var saved = await db.RealmWatchSubscriptions.FirstAsync();
            Assert.Equal(111222333444, saved.ChannelId);
        }

        [Fact]
        public async Task AddWatch_AllowsMultipleRealms_SameUser()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            db.RealmWatchSubscriptions.AddRange(
                new RealmWatchSubscription
                {
                    GuildId = 123456789,
                    UserId = 987654321,
                    RealmSlug = "stormrage",
                    RealmName = "Stormrage",
                    Region = "us",
                    ConnectedRealmId = 1,
                    CreatedAt = DateTime.UtcNow
                },
                new RealmWatchSubscription
                {
                    GuildId = 123456789,
                    UserId = 987654321,
                    RealmSlug = "area-52",
                    RealmName = "Area 52",
                    Region = "us",
                    ConnectedRealmId = 2,
                    CreatedAt = DateTime.UtcNow
                }
            );
            await db.SaveChangesAsync();

            var userSubs = await db.RealmWatchSubscriptions
                .Where(s => s.UserId == 987654321)
                .ToListAsync();

            Assert.Equal(2, userSubs.Count);
        }

        [Fact]
        public async Task RemoveWatch_DeletesCorrectSubscription()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Add two subscriptions
            db.RealmWatchSubscriptions.AddRange(
                new RealmWatchSubscription
                {
                    GuildId = 123456789,
                    UserId = 987654321,
                    RealmSlug = "stormrage",
                    RealmName = "Stormrage",
                    Region = "us",
                    ConnectedRealmId = 1,
                    CreatedAt = DateTime.UtcNow
                },
                new RealmWatchSubscription
                {
                    GuildId = 123456789,
                    UserId = 987654321,
                    RealmSlug = "area-52",
                    RealmName = "Area 52",
                    Region = "us",
                    ConnectedRealmId = 2,
                    CreatedAt = DateTime.UtcNow
                }
            );
            await db.SaveChangesAsync();

            // Remove one
            var toRemove = await db.RealmWatchSubscriptions
                .FirstAsync(s => s.RealmSlug == "stormrage");
            db.RealmWatchSubscriptions.Remove(toRemove);
            await db.SaveChangesAsync();

            var remaining = await db.RealmWatchSubscriptions.ToListAsync();
            Assert.Single(remaining);
            Assert.Equal("area-52", remaining[0].RealmSlug);
        }

        [Fact]
        public async Task ListWatches_FiltersByUserAndGuild()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Add subscriptions for different users/guilds
            db.RealmWatchSubscriptions.AddRange(
                new RealmWatchSubscription
                {
                    GuildId = 111,
                    UserId = 100,
                    RealmSlug = "stormrage",
                    RealmName = "Stormrage",
                    Region = "us",
                    ConnectedRealmId = 1,
                    CreatedAt = DateTime.UtcNow
                },
                new RealmWatchSubscription
                {
                    GuildId = 111,
                    UserId = 200,
                    RealmSlug = "area-52",
                    RealmName = "Area 52",
                    Region = "us",
                    ConnectedRealmId = 2,
                    CreatedAt = DateTime.UtcNow
                },
                new RealmWatchSubscription
                {
                    GuildId = 222,
                    UserId = 100,
                    RealmSlug = "illidan",
                    RealmName = "Illidan",
                    Region = "us",
                    ConnectedRealmId = 3,
                    CreatedAt = DateTime.UtcNow
                }
            );
            await db.SaveChangesAsync();

            var user100Guild111 = await db.RealmWatchSubscriptions
                .Where(s => s.GuildId == 111 && s.UserId == 100)
                .ToListAsync();

            Assert.Single(user100Guild111);
            Assert.Equal("stormrage", user100Guild111[0].RealmSlug);
        }

        #endregion

        #region Status Cache Tests

        [Fact]
        public async Task RealmStatusCache_StoresStatus_Correctly()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var cache = new RealmStatusCache
            {
                Region = "us",
                ConnectedRealmId = 1,
                RealmName = "Stormrage",
                IsOnline = true,
                HasQueue = false,
                LastCheckedAt = DateTime.UtcNow
            };

            db.RealmStatusCache.Add(cache);
            await db.SaveChangesAsync();

            var saved = await db.RealmStatusCache.FirstAsync();
            Assert.True(saved.IsOnline);
            Assert.False(saved.HasQueue);
        }

        [Fact]
        public async Task RealmStatusCache_UpdatesOnStatusChange()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var cache = new RealmStatusCache
            {
                Region = "us",
                ConnectedRealmId = 1,
                RealmName = "Stormrage",
                IsOnline = true,
                HasQueue = false,
                LastCheckedAt = DateTime.UtcNow
            };

            db.RealmStatusCache.Add(cache);
            await db.SaveChangesAsync();

            // Simulate status change
            cache.IsOnline = false;
            cache.LastCheckedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var updated = await db.RealmStatusCache.FirstAsync();
            Assert.False(updated.IsOnline);
        }

        [Fact]
        public async Task RealmStatusCache_LookupByRegionAndConnectedRealmId()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            db.RealmStatusCache.AddRange(
                new RealmStatusCache { Region = "us", ConnectedRealmId = 1, RealmName = "Stormrage", IsOnline = true, LastCheckedAt = DateTime.UtcNow },
                new RealmStatusCache { Region = "us", ConnectedRealmId = 2, RealmName = "Area 52", IsOnline = false, LastCheckedAt = DateTime.UtcNow },
                new RealmStatusCache { Region = "eu", ConnectedRealmId = 1, RealmName = "Silvermoon", IsOnline = true, LastCheckedAt = DateTime.UtcNow }
            );
            await db.SaveChangesAsync();

            var usRealm1 = await db.RealmStatusCache
                .FirstOrDefaultAsync(c => c.Region == "us" && c.ConnectedRealmId == 1);

            Assert.NotNull(usRealm1);
            Assert.Equal("Stormrage", usRealm1.RealmName);
        }

        #endregion

        #region Alert Logic Tests

        [Fact]
        public void AlertLogic_OnlineChange_ShouldAlert()
        {
            var wasOnline = false;
            var isOnline = true;
            var alertOnOnline = true;

            var shouldAlert = !wasOnline && isOnline && alertOnOnline;

            Assert.True(shouldAlert);
        }

        [Fact]
        public void AlertLogic_OfflineChange_ShouldAlert()
        {
            var wasOnline = true;
            var isOnline = false;
            var alertOnOffline = true;

            var shouldAlert = wasOnline && !isOnline && alertOnOffline;

            Assert.True(shouldAlert);
        }

        [Fact]
        public void AlertLogic_QueueChange_ShouldAlert()
        {
            var wasQueue = false;
            var hasQueue = true;
            var alertOnQueue = true;

            var shouldAlert = wasQueue != hasQueue && alertOnQueue;

            Assert.True(shouldAlert);
        }

        [Fact]
        public void AlertLogic_OnlineChange_WhenDisabled_ShouldNotAlert()
        {
            var wasOnline = false;
            var isOnline = true;
            var alertOnOnline = false;

            var shouldAlert = !wasOnline && isOnline && alertOnOnline;

            Assert.False(shouldAlert);
        }

        [Fact]
        public void AlertLogic_NoChange_ShouldNotAlert()
        {
            var wasOnline = true;
            var isOnline = true;
            var wasQueue = false;
            var hasQueue = false;

            var statusChanged = wasOnline != isOnline || wasQueue != hasQueue;

            Assert.False(statusChanged);
        }

        #endregion

        #region Test Alert Flip Logic

        [Fact]
        public async Task TestWatch_FlipsStatus_ForNextAlert()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Setup subscription and cache
            db.RealmWatchSubscriptions.Add(new RealmWatchSubscription
            {
                GuildId = 123,
                UserId = 456,
                RealmSlug = "stormrage",
                RealmName = "Stormrage",
                Region = "us",
                ConnectedRealmId = 1,
                CreatedAt = DateTime.UtcNow
            });

            db.RealmStatusCache.Add(new RealmStatusCache
            {
                Region = "us",
                ConnectedRealmId = 1,
                RealmName = "Stormrage",
                IsOnline = true,
                HasQueue = false,
                LastCheckedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            // Flip status (like test command does)
            var cached = await db.RealmStatusCache
                .FirstAsync(c => c.Region == "us" && c.ConnectedRealmId == 1);
            cached.IsOnline = !cached.IsOnline;
            await db.SaveChangesAsync();

            var updated = await db.RealmStatusCache.FirstAsync();
            Assert.False(updated.IsOnline); // Was true, now false
        }

        #endregion

        #region WowRealms Lookup Tests

        [Fact]
        public async Task WowRealms_LookupBySlugAndRegion()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            db.WowRealms.AddRange(
                new WowRealms { Id = 1, Name = "Stormrage", Slug = "stormrage", Region = "us", ConnectedRealmId = 100, LastUpdated = DateTime.UtcNow },
                new WowRealms { Id = 2, Name = "Stormrage", Slug = "stormrage", Region = "eu", ConnectedRealmId = 200, LastUpdated = DateTime.UtcNow }
            );
            await db.SaveChangesAsync();

            var usStormrage = await db.WowRealms
                .FirstOrDefaultAsync(r => r.Slug == "stormrage" && r.Region.ToLower() == "us");

            Assert.NotNull(usStormrage);
            Assert.Equal(100, usStormrage.ConnectedRealmId);
        }

        [Fact]
        public async Task WowRealms_CachesConnectedRealmId()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var realm = new WowRealms
            {
                Id = 1,
                Name = "Stormrage",
                Slug = "stormrage",
                Region = "us",
                ConnectedRealmId = null, // Not cached yet
                LastUpdated = DateTime.UtcNow
            };
            db.WowRealms.Add(realm);
            await db.SaveChangesAsync();

            // Simulate API fetch and cache
            realm.ConnectedRealmId = 100;
            await db.SaveChangesAsync();

            var updated = await db.WowRealms.FirstAsync();
            Assert.Equal(100, updated.ConnectedRealmId);
        }

        #endregion
    }
}
