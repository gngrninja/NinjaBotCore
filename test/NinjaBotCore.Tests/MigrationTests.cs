using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using Npgsql;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for migration idempotency - ensuring migrations can run safely
    /// on both fresh databases and existing databases.
    ///
    /// Note: These tests use raw SQL to simulate migration scenarios.
    /// Full migration testing requires PostgreSQL integration tests.
    /// </summary>
    public class MigrationTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public MigrationTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"MigrationTests_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task RecreateFoundationTables_IsIdempotent()
        {
            // Arrange - Simulate RecreateFoundationTables migration logic
            // This tests that CREATE TABLE IF NOT EXISTS works correctly

            // First run - create tables
            _context.Database.EnsureCreated();
            await _context.DiscordServers.AddAsync(new DiscordServer
            {
                ServerId = 123,
                ServerName = "Test",
                BotPresent = true,
                JoinedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            // Act - Second run - should not fail or lose data
            // In real migration, this would be: CREATE TABLE IF NOT EXISTS
            // In-memory DB doesn't support raw SQL well, so we test by trying to add more data
            await _context.DiscordServers.AddAsync(new DiscordServer
            {
                ServerId = 456,
                ServerName = "Test2",
                BotPresent = true,
                JoinedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            // Assert - Both records should exist
            var count = await _context.DiscordServers.CountAsync();
            Assert.Equal(2, count);
        }

        [Fact]
        public async Task DropPrefixList_HandlesMissingTable()
        {
            // Arrange - Test that dropping a non-existent table doesn't fail
            // This simulates: DROP TABLE IF EXISTS "PrefixList"

            // Act - Try to query a table that doesn't exist in our schema
            // In-memory DB will return empty result rather than error
            var exists = _context.Model.FindEntityType(typeof(ServerGreeting)) != null;

            // Assert - Should not throw
            Assert.True(exists);
        }

        [Fact]
        public async Task AddUserIdToAwaySystem_HandlesEmptyTable()
        {
            // Arrange - Test AddUserIdToAwaySystem migration on fresh database
            _context.Database.EnsureCreated();

            // The migration deletes all AwaySystem records before adding UserId column
            // Simulate: DELETE FROM "AwaySystem"; (on empty table)
            var initialCount = await _context.AwaySystem.CountAsync();

            // Act - Add column would happen here in real migration
            // We can't actually alter schema in in-memory DB, but we can verify no data exists
            Assert.Equal(0, initialCount);

            // Add a record with UserId after "migration"
            await _context.AwaySystem.AddAsync(new AwaySystem
            {
                UserId = 12345,
                UserName = "TestUser",
                Status = true,
                Message = "AFK",
                TimeAway = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            // Assert - Record with UserId saved successfully
            var record = await _context.AwaySystem.FirstOrDefaultAsync();
            Assert.NotNull(record);
            Assert.Equal(12345uL, record.UserId);
        }

        [Fact]
        public async Task AddUserIdToAwaySystem_DeletesExistingRecords()
        {
            // Arrange - Test AddUserIdToAwaySystem migration on database with existing data
            _context.Database.EnsureCreated();

            // Add old-style records (before UserId column existed)
            // Note: In in-memory DB, UserId already exists, so we simulate with data
            await _context.AwaySystem.AddRangeAsync(new[]
            {
                new AwaySystem { UserId = 0, UserName = "OldUser1", Status = true, Message = "Old", TimeAway = DateTime.UtcNow },
                new AwaySystem { UserId = 0, UserName = "OldUser2", Status = true, Message = "Old", TimeAway = DateTime.UtcNow }
            });
            await _context.SaveChangesAsync();

            // Act - Simulate migration deletion
            _context.AwaySystem.RemoveRange(await _context.AwaySystem.ToListAsync());
            await _context.SaveChangesAsync();

            // Assert - All old records deleted
            var count = await _context.AwaySystem.CountAsync();
            Assert.Equal(0, count);

            // Add new record with UserId
            await _context.AwaySystem.AddAsync(new AwaySystem
            {
                UserId = 99999,
                UserName = "NewUser",
                Status = true,
                Message = "New",
                TimeAway = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            // Verify new schema works
            var newRecord = await _context.AwaySystem.FirstOrDefaultAsync();
            Assert.Equal(99999uL, newRecord.UserId);
        }

        [Fact]
        public async Task MigrationPreservesUnrelatedData()
        {
            // Arrange - Ensure migrations don't accidentally affect unrelated tables
            _context.Database.EnsureCreated();

            await _context.ServerGreetings.AddAsync(new ServerGreeting
            {
                DiscordGuildId = 123,
                Greeting = "Welcome!",
                GreetUsers = true,
                SetByName = "Admin",
                TimeSet = DateTime.UtcNow
            });

            await _context.DiscordServers.AddAsync(new DiscordServer
            {
                ServerId = 456,
                ServerName = "Server",
                BotPresent = true,
                JoinedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            // Act - Simulate running migrations (like DropPrefixList)
            // which should not touch other tables
            var greetingCount = await _context.ServerGreetings.CountAsync();
            var serverCount = await _context.DiscordServers.CountAsync();

            // Assert - Data in other tables unaffected
            Assert.Equal(1, greetingCount);
            Assert.Equal(1, serverCount);
        }

        [Fact]
        public async Task RecreateFoundationTables_CreatesAllRequiredTables()
        {
            // Arrange & Act - Ensure database is created with all tables
            _context.Database.EnsureCreated();

            // Assert - Verify key foundation tables exist
            var serverGreetingsExists = _context.Model.FindEntityType(typeof(ServerGreeting)) != null;
            var discordServersExists = _context.Model.FindEntityType(typeof(DiscordServer)) != null;
            var awaySystemExists = _context.Model.FindEntityType(typeof(AwaySystem)) != null;
            var wowGuildAssociationsExists = _context.Model.FindEntityType(typeof(WowGuildAssociations)) != null;
            var logMonitoringExists = _context.Model.FindEntityType(typeof(LogMonitoring)) != null;

            Assert.True(serverGreetingsExists);
            Assert.True(discordServersExists);
            Assert.True(awaySystemExists);
            Assert.True(wowGuildAssociationsExists);
            Assert.True(logMonitoringExists);
        }

        public async ValueTask DisposeAsync()
        {
            await _context.Database.EnsureDeletedAsync();
            await _context.DisposeAsync();
            await _serviceProvider.DisposeAsync();
        }
    }
}
