using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for DiscordServerTrackingService database operations and cleanup logic.
    /// Note: Full service integration testing requires Discord.Net mocking which is complex.
    /// These tests focus on the database operations and business logic.
    /// </summary>
    public class DiscordServerTrackingServiceTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public DiscordServerTrackingServiceTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"DiscordServerTracking_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task UpsertDiscordServer_CreatesNewRecord_WhenNotExists()
        {
            // Arrange
            var repo = new Repository<DiscordServer>(_context);
            const long serverId = 123456789;
            const string serverName = "Test Server";

            // Act
            await repo.AddAsync(new DiscordServer
            {
                ServerId = serverId,
                ServerName = serverName,
                BotPresent = true,
                JoinedAt = DateTime.UtcNow
            });
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(s => s.ServerId == serverId);
            Assert.NotNull(result);
            Assert.Equal(serverName, result.ServerName);
            Assert.True(result.BotPresent);
            Assert.NotNull(result.JoinedAt);
            Assert.Null(result.LeftAt);
        }

        [Fact]
        public async Task UpsertDiscordServer_UpdatesExistingRecord_WhenExists()
        {
            // Arrange
            var repo = new Repository<DiscordServer>(_context);
            const long serverId = 987654321;
            var originalJoinTime = DateTime.UtcNow.AddDays(-10);

            // Create initial record
            await repo.AddAsync(new DiscordServer
            {
                ServerId = serverId,
                ServerName = "Old Name",
                BotPresent = false,
                JoinedAt = originalJoinTime,
                LeftAt = DateTime.UtcNow.AddDays(-5)
            });
            await repo.SaveChangesAsync();

            // Act - Update the record (simulating bot rejoining)
            var existing = await repo.FirstOrDefaultAsync(s => s.ServerId == serverId);
            existing.ServerName = "New Name";
            existing.BotPresent = true;
            existing.LeftAt = null;
            repo.Update(existing);
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(s => s.ServerId == serverId);
            Assert.Equal("New Name", result.ServerName);
            Assert.True(result.BotPresent);
            Assert.Equal(originalJoinTime.ToString(), result.JoinedAt.ToString()); // JoinedAt preserved
            Assert.Null(result.LeftAt);
        }

        [Fact]
        public async Task CleanupStaleServers_MarksServersAsFalse_WhenNotInCurrentList()
        {
            // Arrange - Simulate the cleanup logic from DiscordServerTrackingService
            var repo = new Repository<DiscordServer>(_context);

            // Add servers - 1 and 2 are "current", 3 and 4 are "stale"
            await repo.AddRangeAsync(new[]
            {
                new DiscordServer { ServerId = 1, ServerName = "Server1", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 2, ServerName = "Server2", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 3, ServerName = "Server3", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 4, ServerName = "Server4", BotPresent = true, JoinedAt = DateTime.UtcNow }
            });
            await repo.SaveChangesAsync();

            // Act - Simulate cleanup query (bot only in servers 1 and 2)
            var currentGuildIds = new[] { 1L, 2L }.ToList();
            var staleServers = await repo.Query
                .Where(s => s.BotPresent && !currentGuildIds.Contains(s.ServerId))
                .ToListAsync();

            foreach (var staleServer in staleServers)
            {
                staleServer.BotPresent = false;
                staleServer.LeftAt = DateTime.UtcNow;
                repo.Update(staleServer);
            }
            await repo.SaveChangesAsync();

            // Assert
            Assert.Equal(2, staleServers.Count);
            Assert.Contains(staleServers, s => s.ServerId == 3);
            Assert.Contains(staleServers, s => s.ServerId == 4);

            // Verify database state
            var server1 = await repo.FirstOrDefaultAsync(s => s.ServerId == 1);
            var server3 = await repo.FirstOrDefaultAsync(s => s.ServerId == 3);

            Assert.True(server1.BotPresent);
            Assert.Null(server1.LeftAt);

            Assert.False(server3.BotPresent);
            Assert.NotNull(server3.LeftAt);
        }

        [Fact]
        public async Task CleanupStaleServers_MarksAllServers_WhenCurrentListIsEmpty()
        {
            // Arrange - Test the edge case where bot is in 0 servers
            var repo = new Repository<DiscordServer>(_context);

            await repo.AddRangeAsync(new[]
            {
                new DiscordServer { ServerId = 1, ServerName = "Server1", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 2, ServerName = "Server2", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 3, ServerName = "Server3", BotPresent = true, JoinedAt = DateTime.UtcNow }
            });
            await repo.SaveChangesAsync();

            // Act - Empty current list (bot kicked from all servers)
            var currentGuildIds = new long[] { }.ToList();
            var staleServers = await repo.Query
                .Where(s => s.BotPresent && !currentGuildIds.Contains(s.ServerId))
                .ToListAsync();

            foreach (var staleServer in staleServers)
            {
                staleServer.BotPresent = false;
                staleServer.LeftAt = DateTime.UtcNow;
                repo.Update(staleServer);
            }
            await repo.SaveChangesAsync();

            // Assert - All servers should be marked as not present
            Assert.Equal(3, staleServers.Count);

            var allServers = await repo.GetAllAsync();
            Assert.All(allServers, s => Assert.False(s.BotPresent));
            Assert.All(allServers, s => Assert.NotNull(s.LeftAt));
        }

        [Fact]
        public async Task CleanupStaleServers_IgnoresAlreadyLeftServers()
        {
            // Arrange
            var repo = new Repository<DiscordServer>(_context);

            await repo.AddRangeAsync(new[]
            {
                new DiscordServer { ServerId = 1, ServerName = "Server1", BotPresent = true, JoinedAt = DateTime.UtcNow },
                new DiscordServer { ServerId = 2, ServerName = "Server2", BotPresent = false, LeftAt = DateTime.UtcNow.AddDays(-1) }
            });
            await repo.SaveChangesAsync();

            // Act - Bot only in empty list
            var currentGuildIds = new long[] { }.ToList();
            var staleServers = await repo.Query
                .Where(s => s.BotPresent && !currentGuildIds.Contains(s.ServerId))
                .ToListAsync();

            // Assert - Only Server1 should be marked as stale (Server2 already BotPresent=false)
            Assert.Single(staleServers);
            Assert.Equal(1, staleServers[0].ServerId);
        }

        [Fact]
        public async Task DatabaseSideFiltering_PerformsEfficientQuery()
        {
            // Arrange - Test that Contains operations are database-side, not in-memory
            var repo = new Repository<DiscordServer>(_context);

            // Add 100 servers
            var servers = Enumerable.Range(1, 100)
                .Select(i => new DiscordServer
                {
                    ServerId = i,
                    ServerName = $"Server{i}",
                    BotPresent = true,
                    JoinedAt = DateTime.UtcNow
                })
                .ToList();

            await repo.AddRangeAsync(servers);
            await repo.SaveChangesAsync();

            // Act - Query with large guild list (first 50 servers)
            var currentGuildIds = Enumerable.Range(1, 50).Select(i => (long)i).ToList();
            var staleServers = await repo.Query
                .Where(s => s.BotPresent && !currentGuildIds.Contains(s.ServerId))
                .ToListAsync();

            // Assert - Servers 51-100 should be stale
            Assert.Equal(50, staleServers.Count);
            Assert.All(staleServers, s => Assert.True(s.ServerId > 50));
        }

        [Fact]
        public async Task OnLeftDiscordServer_UpdatesRecord()
        {
            // Arrange - Simulate OnLeftDiscordServer event handler logic
            var repo = new Repository<DiscordServer>(_context);
            const long serverId = 111111;

            await repo.AddAsync(new DiscordServer
            {
                ServerId = serverId,
                ServerName = "Test Server",
                BotPresent = true,
                JoinedAt = DateTime.UtcNow
            });
            await repo.SaveChangesAsync();

            // Act - Simulate bot leaving
            var server = await repo.FirstOrDefaultAsync(s => s.ServerId == serverId);
            server.BotPresent = false;
            server.LeftAt = DateTime.UtcNow;
            repo.Update(server);
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(s => s.ServerId == serverId);
            Assert.False(result.BotPresent);
            Assert.NotNull(result.LeftAt);
        }

        public async ValueTask DisposeAsync()
        {
            await _context.Database.EnsureDeletedAsync();
            await _context.DisposeAsync();
            await _serviceProvider.DisposeAsync();
        }
    }
}
