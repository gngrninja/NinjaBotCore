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
    public class UpsertAsyncTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public UpsertAsyncTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"UpsertTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task UpsertAsync_CreatesNewEntity_WhenNotExists()
        {
            // Arrange
            var repo = new Repository<AwaySystem>(_context);

            // Act
            await repo.UpsertAsync(
                findPredicate: a => a.UserName == "TestUser",
                updateAction: away =>
                {
                    away.Status = true;
                    away.Message = "Away message";
                },
                createFactory: () => new AwaySystem
                {
                    UserName = "TestUser",
                    Status = true,
                    Message = "Away message",
                    TimeAway = DateTime.UtcNow
                });
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(a => a.UserName == "TestUser");
            Assert.NotNull(result);
            Assert.Equal("TestUser", result.UserName);
            Assert.True(result.Status);
            Assert.Equal("Away message", result.Message);
        }

        [Fact]
        public async Task UpsertAsync_UpdatesExistingEntity_WhenExists()
        {
            // Arrange
            var repo = new Repository<AwaySystem>(_context);

            // Create initial entity
            var existingAway = new AwaySystem
            {
                UserName = "ExistingUser",
                Status = false,
                Message = "Old message",
                TimeAway = DateTime.UtcNow.AddDays(-1)
            };
            await repo.AddAsync(existingAway);
            await repo.SaveChangesAsync();

            // Act
            await repo.UpsertAsync(
                findPredicate: a => a.UserName == "ExistingUser",
                updateAction: away =>
                {
                    away.Status = true;
                    away.Message = "New message";
                    away.TimeAway = DateTime.UtcNow;
                },
                createFactory: () => new AwaySystem
                {
                    UserName = "ExistingUser",
                    Status = true,
                    Message = "This should not be used",
                    TimeAway = DateTime.UtcNow
                });
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(a => a.UserName == "ExistingUser");
            Assert.NotNull(result);
            Assert.True(result.Status); // Updated
            Assert.Equal("New message", result.Message); // Updated
            Assert.NotEqual("This should not be used", result.Message); // createFactory not used
        }

        [Fact]
        public async Task UpsertAsync_DoesNotCreateDuplicates()
        {
            // Arrange
            var repo = new Repository<AwaySystem>(_context);

            // Create initial entity
            await repo.AddAsync(new AwaySystem
            {
                UserName = "UniqueUser",
                Status = false,
                Message = "Initial"
            });
            await repo.SaveChangesAsync();

            // Act - Update the same entity twice
            await repo.UpsertAsync(
                findPredicate: a => a.UserName == "UniqueUser",
                updateAction: away => away.Message = "First update",
                createFactory: () => new AwaySystem { UserName = "UniqueUser", Message = "Should not create" });
            await repo.SaveChangesAsync();

            await repo.UpsertAsync(
                findPredicate: a => a.UserName == "UniqueUser",
                updateAction: away => away.Message = "Second update",
                createFactory: () => new AwaySystem { UserName = "UniqueUser", Message = "Should not create" });
            await repo.SaveChangesAsync();

            // Assert
            var all = await repo.WhereAsync(a => a.UserName == "UniqueUser");
            Assert.Equal(1, all.Count); // Should only have one entity

            var result = await repo.FirstOrDefaultAsync(a => a.UserName == "UniqueUser");
            Assert.Equal("Second update", result.Message);
        }

        [Fact]
        public async Task UpsertAsync_HandlesComplexEntity_ServerGreeting()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            long guildId = 12345L;

            // Act - Create
            await repo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.Greeting = "Updated greeting";
                    greeting.SetByName = "Updater";
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = "Welcome!",
                    PartingMessage = "Goodbye!",
                    GreetUsers = true,
                    SetById = 100,
                    SetByName = "Creator",
                    TimeSet = DateTime.UtcNow
                });
            await repo.SaveChangesAsync();

            var created = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(created);
            Assert.Equal("Welcome!", created.Greeting);
            Assert.Equal("Creator", created.SetByName);

            // Act - Update
            await repo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.PartingMessage = "See ya!";
                    greeting.SetById = 200;
                    greeting.SetByName = "Updater";
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = "This won't be used",
                    SetByName = "This won't be used"
                });
            await repo.SaveChangesAsync();

            // Assert
            var updated = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(updated);
            Assert.Equal("Welcome!", updated.Greeting); // Unchanged
            Assert.Equal("See ya!", updated.PartingMessage); // Updated
            Assert.Equal(200, updated.SetById); // Updated
            Assert.Equal("Updater", updated.SetByName); // Updated
        }

        public void Dispose()
        {
            _context?.Database.EnsureDeleted();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }
    }
}
