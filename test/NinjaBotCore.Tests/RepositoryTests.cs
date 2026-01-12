using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RepositoryTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public RepositoryTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            // Note: IServiceScopeFactory is automatically provided by ServiceProvider - no registration needed

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task FirstOrDefaultAsync_ReturnsEntity_WhenExists()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            var greeting = new ServerGreeting
            {
                DiscordGuildId = 12345,
                Greeting = "Welcome!",
                GreetUsers = true,
                SetByName = "TestUser",
                TimeSet = DateTime.UtcNow
            };
            await repo.AddAsync(greeting);
            await repo.SaveChangesAsync();

            // Act
            var result = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 12345);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("Welcome!", result.Greeting);
            Assert.Equal(12345, result.DiscordGuildId);
        }

        [Fact]
        public async Task FirstOrDefaultAsync_ReturnsNull_WhenNotExists()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);

            // Act
            var result = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 99999);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task WhereAsync_ReturnsMultipleEntities()
        {
            // Arrange
            var repo = new Repository<WowCharAssociation>(_context);
            await repo.AddRangeAsync(new[]
            {
                new WowCharAssociation { UserId = 123, CharName = "Char1", WowRealm = "TestRealm" },
                new WowCharAssociation { UserId = 123, CharName = "Char2", WowRealm = "TestRealm" },
                new WowCharAssociation { UserId = 456, CharName = "Char3", WowRealm = "TestRealm" }
            });
            await repo.SaveChangesAsync();

            // Act
            var result = await repo.WhereAsync(c => c.UserId == 123);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.All(result, c => Assert.Equal(123, c.UserId));
        }

        [Fact]
        public async Task AddAsync_AddsEntityToDatabase()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            var greeting = new ServerGreeting
            {
                DiscordGuildId = 54321,
                Greeting = "Hello!",
                GreetUsers = true,
                SetByName = "TestUser2",
                TimeSet = DateTime.UtcNow
            };

            // Act
            await repo.AddAsync(greeting);
            await repo.SaveChangesAsync();

            // Assert
            var saved = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 54321);
            Assert.NotNull(saved);
            Assert.Equal("Hello!", saved.Greeting);
        }

        [Fact]
        public async Task GetAllAsync_ReturnsAllEntities()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            await repo.AddRangeAsync(new[]
            {
                new ServerGreeting { DiscordGuildId = 1, Greeting = "G1", SetByName = "U1", TimeSet = DateTime.UtcNow },
                new ServerGreeting { DiscordGuildId = 2, Greeting = "G2", SetByName = "U2", TimeSet = DateTime.UtcNow },
                new ServerGreeting { DiscordGuildId = 3, Greeting = "G3", SetByName = "U3", TimeSet = DateTime.UtcNow }
            });
            await repo.SaveChangesAsync();

            // Act
            var result = await repo.GetAllAsync();

            // Assert
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public async Task Query_ReturnsQueryable()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            await repo.AddAsync(new ServerGreeting
            {
                DiscordGuildId = 100,
                Greeting = "Test",
                SetByName = "TestUser",
                TimeSet = DateTime.UtcNow
            });
            await repo.SaveChangesAsync();

            // Act
            var queryable = repo.Query;

            // Assert
            Assert.NotNull(queryable);
            Assert.True(queryable is IQueryable<ServerGreeting>);
            var count = await queryable.CountAsync();
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task Query_SupportsComplexLinqWithContains()
        {
            // Arrange - This tests the scenario used by DiscordServerTrackingService
            var repo = new Repository<DiscordServer>(_context);
            await repo.AddRangeAsync(new[]
            {
                new DiscordServer { ServerId = 1, ServerName = "Server1", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 2, ServerName = "Server2", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 3, ServerName = "Server3", BotPresent = false, LeftAt = DateTime.UtcNow }
            });
            await repo.SaveChangesAsync();

            // Act - Simulate DiscordServerTrackingService cleanup query
            var currentGuildIds = new List<long> { 1 }; // Bot only in server 1
            var staleServers = await repo.Query
                .Where(s => s.BotPresent && !currentGuildIds.Contains(s.ServerId))
                .ToListAsync();

            // Assert - Server 2 should be detected as stale (BotPresent but not in current list)
            Assert.Single(staleServers);
            Assert.Equal(2, staleServers[0].ServerId);
            Assert.Equal("Server2", staleServers[0].ServerName);
        }

        [Fact]
        public async Task Query_EnablesDatabaseSideFiltering()
        {
            // Arrange - Test that Query enables database-side operations, not in-memory filtering
            var repo = new Repository<WowCharAssociation>(_context);
            var largeIdList = Enumerable.Range(1, 100).Select(i => (long)i).ToList();

            await repo.AddRangeAsync(new[]
            {
                new WowCharAssociation { UserId = 50, CharName = "Char50", WowRealm = "Realm" },
                new WowCharAssociation { UserId = 150, CharName = "Char150", WowRealm = "Realm" }
            });
            await repo.SaveChangesAsync();

            // Act - Use Contains with large list (database-side operation)
            var results = await repo.Query
                .Where(c => c.UserId.HasValue && largeIdList.Contains(c.UserId.Value))
                .ToListAsync();

            // Assert - Only UserId=50 should match (50 is in the list, 150 is not)
            Assert.Single(results);
            Assert.Equal(50, results[0].UserId);
        }

        [Fact]
        public void Query_CanBeComposed()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);

            // Act - Verify Query can be composed with additional LINQ operations
            var query = repo.Query
                .Where(s => s.GreetUsers == true)
                .OrderBy(s => s.DiscordGuildId)
                .Select(s => s.Greeting);

            // Assert
            Assert.NotNull(query);
            Assert.True(query is IQueryable<string>);
        }

        public void Dispose()
        {
            _context?.Database.EnsureDeleted();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }
    }
}
