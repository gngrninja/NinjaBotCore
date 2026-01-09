using System;
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
    /// <summary>
    /// Integration tests for database operations including transactions, concurrency, and complex scenarios
    /// </summary>
    public class DatabaseIntegrationTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public DatabaseIntegrationTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"IntegrationTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task MultiEntityTransaction_SavesAllChanges_WhenSuccessful()
        {
            // Arrange
            var greetingRepo = new Repository<ServerGreeting>(_context);
            var noteRepo = new Repository<Note>(_context);

            var greeting = new ServerGreeting
            {
                DiscordGuildId = 111,
                Greeting = "Welcome!",
                GreetUsers = true,
                SetByName = "TestUser",
                TimeSet = DateTime.UtcNow
            };

            var note = new Note
            {
                ServerId = 111,
                ServerName = "Test Server",
                Note1 = "Test Note",
                SetBy = "TestUser",
                SetById = 123,
                TimeSet = DateTime.UtcNow
            };

            // Act - Add both entities in the same transaction
            await greetingRepo.AddAsync(greeting);
            await noteRepo.AddAsync(note);
            await _context.SaveChangesAsync(); // Single transaction

            // Assert
            var savedGreeting = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == 111);
            var savedNote = await noteRepo.FirstOrDefaultAsync(n => n.ServerId == 111);

            Assert.NotNull(savedGreeting);
            Assert.NotNull(savedNote);
            Assert.Equal("Welcome!", savedGreeting.Greeting);
            Assert.Equal("Test Note", savedNote.Note1);
        }

        [Fact]
        public async Task UpsertAsync_HandlesMultipleUpdates_InSequence()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            long guildId = 222;

            // Act - Create initial entry
            await repo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: g => g.Greeting = "Should not be called",
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = "Version 1",
                    SetByName = "User1",
                    TimeSet = DateTime.UtcNow
                });
            await repo.SaveChangesAsync();

            // Update it multiple times
            for (int i = 2; i <= 5; i++)
            {
                await repo.UpsertAsync(
                    findPredicate: g => g.DiscordGuildId == guildId,
                    updateAction: g =>
                    {
                        g.Greeting = $"Version {i}";
                        g.SetByName = $"User{i}";
                    },
                    createFactory: () => throw new InvalidOperationException("Should not create"));
                await repo.SaveChangesAsync();
            }

            // Assert - Should have one entry with final version
            var all = await repo.WhereAsync(g => g.DiscordGuildId == guildId);
            Assert.Single(all);

            var final = all.First();
            Assert.Equal("Version 5", final.Greeting);
            Assert.Equal("User5", final.SetByName);
        }

        [Fact]
        public async Task Repository_SupportsComplexQueries_WithMultiplePredicates()
        {
            // Arrange
            var repo = new Repository<WowCharAssociation>(_context);

            // Create test data
            await repo.AddRangeAsync(new[]
            {
                new WowCharAssociation { UserId = 100, CharName = "WarriorA", WowRealm = "Realm1" },
                new WowCharAssociation { UserId = 100, CharName = "MageA", WowRealm = "Realm1" },
                new WowCharAssociation { UserId = 100, CharName = "RogueA", WowRealm = "Realm2" },
                new WowCharAssociation { UserId = 200, CharName = "PriestB", WowRealm = "Realm1" },
                new WowCharAssociation { UserId = 200, CharName = "HunterB", WowRealm = "Realm2" }
            });
            await repo.SaveChangesAsync();

            // Act - Query with multiple conditions
            var realm1Chars = await repo.WhereAsync(c => c.WowRealm == "Realm1");
            var user100Chars = await repo.WhereAsync(c => c.UserId == 100);
            var user100Realm1 = await repo.WhereAsync(c => c.UserId == 100 && c.WowRealm == "Realm1");

            // Assert
            Assert.Equal(3, realm1Chars.Count); // 2 from user 100, 1 from user 200
            Assert.Equal(3, user100Chars.Count); // All 3 chars of user 100
            Assert.Equal(2, user100Realm1.Count); // Only user 100's chars in Realm1
        }

        [Fact]
        public async Task AddRangeAsync_HandlesBulkOperations_Efficiently()
        {
            // Arrange
            var repo = new Repository<AwaySystem>(_context);
            var bulkData = Enumerable.Range(1, 100)
                .Select(i => new AwaySystem
                {
                    UserName = $"User{i}",
                    Status = i % 2 == 0,
                    Message = $"Message {i}",
                    TimeAway = DateTime.UtcNow
                })
                .ToList();

            // Act
            await repo.AddRangeAsync(bulkData);
            await repo.SaveChangesAsync();

            // Assert
            var all = await repo.GetAllAsync();
            Assert.Equal(100, all.Count);

            var activeUsers = await repo.WhereAsync(a => a.Status == true);
            Assert.Equal(50, activeUsers.Count);
        }

        [Fact]
        public async Task Repository_HandlesEmptyResults_Gracefully()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);

            // Act
            var notFound = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 99999);
            var emptyList = await repo.WhereAsync(g => g.DiscordGuildId == 99999);
            var all = await repo.GetAllAsync();

            // Assert
            Assert.Null(notFound);
            Assert.Empty(emptyList);
            Assert.Empty(all);
        }

        [Fact]
        public async Task UpsertAsync_PreservesUnmodifiedFields_OnUpdate()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            long guildId = 333;

            // Create initial entity with multiple fields
            await repo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: _ => { },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = "Hello",
                    PartingMessage = "Goodbye",
                    GreetUsers = true,
                    GreetingChannelId = 1000,
                    PartingChannelId = 2000,
                    SetByName = "Creator",
                    SetById = 100,
                    TimeSet = DateTime.UtcNow.AddDays(-1)
                });
            await repo.SaveChangesAsync();

            // Act - Update only specific fields
            await repo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.Greeting = "Updated Hello";
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => throw new InvalidOperationException("Should not create"));
            await repo.SaveChangesAsync();

            // Assert - Verify only specified fields changed
            var result = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal("Updated Hello", result.Greeting); // Changed
            Assert.Equal("Goodbye", result.PartingMessage); // Unchanged
            Assert.True(result.GreetUsers); // Unchanged
            Assert.Equal(1000, result.GreetingChannelId); // Unchanged
            Assert.Equal(2000, result.PartingChannelId); // Unchanged
            Assert.Equal("Creator", result.SetByName); // Unchanged
            Assert.Equal(100, result.SetById); // Unchanged
        }

        [Fact]
        public async Task Repository_HandlesNullableFields_Correctly()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);

            var greetingWithNulls = new ServerGreeting
            {
                DiscordGuildId = 444,
                Greeting = null,
                PartingMessage = null,
                GreetUsers = false,
                GreetingChannelId = null,
                PartingChannelId = null,
                SetByName = "TestUser",
                TimeSet = DateTime.UtcNow
            };

            // Act
            await repo.AddAsync(greetingWithNulls);
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 444);
            Assert.NotNull(result);
            Assert.Null(result.Greeting);
            Assert.Null(result.PartingMessage);
            Assert.Null(result.GreetingChannelId);
            Assert.Null(result.PartingChannelId);
        }

        public void Dispose()
        {
            _context?.Database.EnsureDeleted();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }
    }
}
