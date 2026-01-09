using System;
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
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for greeting system functionality via WowCacheService
    /// Tests greeting message storage, caching, and database integration
    /// </summary>
    public class UserJoinedHandlerTests : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly NinjaBotEntities _context;
        private readonly IMemoryCache _cache;

        public UserJoinedHandlerTests()
        {
            var services = new ServiceCollection();

            // Ensure all contexts share the same in-memory database instance
            var dbRoot = new InMemoryDatabaseRoot();

            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddMemoryCache();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase("UserJoinedHandlerTests", dbRoot)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _provider = services.BuildServiceProvider();
            _context = _provider.GetRequiredService<NinjaBotEntities>();
            _cache = _provider.GetRequiredService<IMemoryCache>();
        }

        [Fact]
        public async Task HandleGreeting_WithGreetingEnabled_LoadsFromDatabase()
        {
            // Arrange
            const long guildId = 123456;
            const long channelId = 789012;
            const string greetingMessage = "Welcome to the server!";

            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.Add(new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = true,
                    Greeting = greetingMessage,
                    GreetingChannelId = channelId,
                    SetById = 11111,
                    SetByName = "Admin",
                    TimeSet = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // Act
            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());
            var result = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.GreetUsers);
            Assert.Equal(greetingMessage, result.Greeting);
            Assert.Equal(channelId, result.GreetingChannelId);
        }

        [Fact]
        public async Task HandleGreeting_WithGreetingDisabled_ReturnsSettings()
        {
            // Arrange
            const long guildId = 234567;

            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.Add(new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = false,
                    Greeting = "This message should not be sent",
                    GreetingChannelId = 888888
                });
                await db.SaveChangesAsync();
            }

            // Act
            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());
            var result = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.NotNull(result);
            Assert.False(result.GreetUsers);
        }

        [Fact]
        public async Task HandleGreeting_WithNoSettings_ReturnsNull()
        {
            // Arrange
            const long guildId = 345678;
            // No greeting settings in database

            // Act
            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());
            var result = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task HandleGreeting_WithDefaultMessage_UsesDefaultText()
        {
            // Arrange - Greeting enabled but message is null/empty
            const long guildId = 456789;

            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.Add(new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = true,
                    Greeting = null,
                    GreetingChannelId = 999999
                });
                await db.SaveChangesAsync();
            }

            // Act
            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());
            var result = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.GreetUsers);
            Assert.Null(result.Greeting); // Will use default message in handler
        }

        [Fact]
        public async Task HandleGreeting_WithCustomChannel_UsesSpecifiedChannel()
        {
            // Arrange
            const long guildId = 567890;
            const long customChannelId = 111222;

            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.Add(new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = true,
                    Greeting = "Custom greeting",
                    GreetingChannelId = customChannelId
                });
                await db.SaveChangesAsync();
            }

            // Act
            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());
            var result = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(customChannelId, result.GreetingChannelId);
        }

        [Fact]
        public async Task HandleGreeting_UsesCaching_ForPerformance()
        {
            // Arrange
            const long guildId = 678901;

            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.Add(new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = true,
                    Greeting = "Cached greeting"
                });
                await db.SaveChangesAsync();
            }

            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());

            // Act - First call hits database and caches
            var result1 = await cacheService.GetServerGreetingAsync(guildId);

            // Modify database (should not affect cached result)
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var greeting = await db.ServerGreetings.FirstAsync(g => g.DiscordGuildId == guildId);
                greeting.Greeting = "Modified greeting";
                await db.SaveChangesAsync();
            }

            // Second call uses cache
            var result2 = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.Equal("Cached greeting", result1.Greeting);
            Assert.Equal("Cached greeting", result2.Greeting); // Still using cached version
        }

        [Fact]
        public async Task HandleGreeting_CacheInvalidation_RefreshesData()
        {
            // Arrange
            const long guildId = 789012;

            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.Add(new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = true,
                    Greeting = "Original greeting"
                });
                await db.SaveChangesAsync();
            }

            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());

            // Act - First call caches the greeting
            var result1 = await cacheService.GetServerGreetingAsync(guildId);

            // Invalidate cache
            cacheService.InvalidateServerGreeting(guildId);

            // Modify database
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var greeting = await db.ServerGreetings.FirstAsync(g => g.DiscordGuildId == guildId);
                greeting.Greeting = "Updated greeting";
                await db.SaveChangesAsync();
            }

            // Next call should fetch fresh data
            var result2 = await cacheService.GetServerGreetingAsync(guildId);

            // Assert
            Assert.Equal("Original greeting", result1.Greeting);
            Assert.Equal("Updated greeting", result2.Greeting);
        }

        [Fact]
        public async Task HandleGreeting_MultipleGuilds_IndependentSettings()
        {
            // Arrange
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.ServerGreetings.AddRange(
                    new ServerGreeting
                    {
                        DiscordGuildId = 111111,
                        GreetUsers = true,
                        Greeting = "Guild 1 greeting",
                        GreetingChannelId = 100100
                    },
                    new ServerGreeting
                    {
                        DiscordGuildId = 222222,
                        GreetUsers = false,
                        Greeting = "Guild 2 greeting (disabled)",
                        GreetingChannelId = 200200
                    });
                await db.SaveChangesAsync();
            }

            var cacheService = new WowCacheService(_cache, _provider.GetRequiredService<IServiceScopeFactory>());

            // Act
            var result1 = await cacheService.GetServerGreetingAsync(111111);
            var result2 = await cacheService.GetServerGreetingAsync(222222);

            // Assert
            Assert.True(result1.GreetUsers);
            Assert.Equal("Guild 1 greeting", result1.Greeting);

            Assert.False(result2.GreetUsers);
            Assert.Equal("Guild 2 greeting (disabled)", result2.Greeting);
        }

        [Fact]
        public async Task HandleGreeting_AdminChangesSettings_UpdatesDatabase()
        {
            // Arrange - Simulate admin using /set-joining-message modal
            const long guildId = 890123;
            const long adminId = 555555;
            const string adminName = "ServerAdmin";
            const string newGreeting = "Welcome! Please read the rules.";

            // Act - Simulate modal submission
            await using var greetingRepo = new Repository<ServerGreeting>(_provider.GetRequiredService<NinjaBotEntities>());
            await greetingRepo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.Greeting = newGreeting.Trim();
                    greeting.SetById = adminId;
                    greeting.SetByName = adminName;
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = newGreeting.Trim(),
                    SetById = adminId,
                    SetByName = adminName,
                    TimeSet = DateTime.UtcNow
                });
            await greetingRepo.SaveChangesAsync();

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal(newGreeting, result.Greeting);
            Assert.Equal(adminId, result.SetById);
            Assert.Equal(adminName, result.SetByName);
            Assert.NotNull(result.TimeSet);
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
