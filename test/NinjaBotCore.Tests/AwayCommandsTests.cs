using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Integration tests for away system functionality
    /// Tests the database operations that AwayCommands performs
    /// </summary>
    public class AwayCommandsTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public AwayCommandsTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"AwayTestDb_{Guid.NewGuid()}"));

            // Add repositories
            services.AddScoped<IRepository<AwaySystem>>(sp =>
                new Repository<AwaySystem>(sp.GetRequiredService<NinjaBotEntities>()));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task SetAway_CreatesNewAwayEntry_WhenUserNotAway()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 12345;
            string userName = "TestUser";
            string message = "Gone fishing";

            // Act - Simulate /away command
            await awayRepo.UpsertAsync(
                findPredicate: a => a.UserId == userId,
                updateAction: away =>
                {
                    away.Status = true;
                    away.Message = message;
                    away.TimeAway = DateTime.UtcNow;
                    away.UserName = userName;
                },
                createFactory: () => new AwaySystem
                {
                    UserId = userId,
                    UserName = userName,
                    Status = true,
                    Message = message,
                    TimeAway = DateTime.UtcNow
                });
            await awayRepo.SaveChangesAsync();

            // Assert
            var result = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);
            Assert.NotNull(result);
            Assert.True(result.Status);
            Assert.Equal("Gone fishing", result.Message);
            Assert.Equal("TestUser", result.UserName);
            Assert.NotNull(result.TimeAway);
        }

        [Fact]
        public async Task SetAway_WithExistingAway_ReturnsExistingStatus()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 23456;

            // Create existing away entry
            await awayRepo.AddAsync(new AwaySystem
            {
                UserId = userId,
                UserName = "ExistingUser",
                Status = true,
                Message = "Already away",
                TimeAway = DateTime.UtcNow.AddHours(-1)
            });
            await awayRepo.SaveChangesAsync();

            // Act - Check if user is already away (what the command does before allowing new away)
            var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);

            // Assert - Should find existing away status
            Assert.NotNull(existing);
            Assert.True(existing.Status);
            Assert.Equal("Already away", existing.Message);
        }

        [Fact]
        public async Task SetAway_WithEmptyMessage_UsesDefaultMessage()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 34567;
            string userName = "TestUser";
            string input = "";
            string message = string.IsNullOrEmpty(input) ? "No message set!" : input;

            // Act
            await awayRepo.UpsertAsync(
                findPredicate: a => a.UserId == userId,
                updateAction: away =>
                {
                    away.Status = true;
                    away.Message = message;
                    away.TimeAway = DateTime.UtcNow;
                    away.UserName = userName;
                },
                createFactory: () => new AwaySystem
                {
                    UserId = userId,
                    UserName = userName,
                    Status = true,
                    Message = message,
                    TimeAway = DateTime.UtcNow
                });
            await awayRepo.SaveChangesAsync();

            // Assert
            var result = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);
            Assert.NotNull(result);
            Assert.Equal("No message set!", result.Message);
        }

        [Fact]
        public async Task SetBack_CalculatesAwayDuration_Correctly()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 45678;
            var awayTime = DateTime.UtcNow.AddDays(-2).AddHours(-3).AddMinutes(-15);

            // Create away entry
            await awayRepo.AddAsync(new AwaySystem
            {
                UserId = userId,
                UserName = "DurationUser",
                Status = true,
                Message = "Away message",
                TimeAway = awayTime
            });
            await awayRepo.SaveChangesAsync();

            // Act - Get existing record and calculate duration
            var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);
            TimeSpan? duration = null;
            if (existing != null && existing.TimeAway.HasValue)
            {
                duration = DateTime.UtcNow - existing.TimeAway;
            }

            // Assert
            Assert.NotNull(duration);
            Assert.True(duration.Value.Days >= 2);
            Assert.True(duration.Value.TotalHours >= 51); // 2 days + 3 hours
        }

        [Fact]
        public async Task SetBack_SetsStatusToFalse_AndClearsMessage()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 56789;
            string userName = "BackUser";

            // Create away entry
            await awayRepo.AddAsync(new AwaySystem
            {
                UserId = userId,
                UserName = userName,
                Status = true,
                Message = "I'm away",
                TimeAway = DateTime.UtcNow.AddHours(-5)
            });
            await awayRepo.SaveChangesAsync();

            // Act - Simulate /back command
            await awayRepo.UpsertAsync(
                findPredicate: a => a.UserId == userId,
                updateAction: away =>
                {
                    away.Status = false;
                    away.Message = string.Empty;
                    away.UserName = userName;
                },
                createFactory: () => new AwaySystem
                {
                    UserId = userId,
                    UserName = userName,
                    Status = false,
                    Message = string.Empty
                });
            await awayRepo.SaveChangesAsync();

            // Assert
            var result = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);
            Assert.NotNull(result);
            Assert.False(result.Status);
            Assert.Equal(string.Empty, result.Message);
        }

        [Fact]
        public async Task SetBack_WhenNotAway_ReturnsNotAwayStatus()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 67890;

            // Create user entry with status = false (not away)
            await awayRepo.AddAsync(new AwaySystem
            {
                UserId = userId,
                UserName = "NotAwayUser",
                Status = false,
                Message = string.Empty
            });
            await awayRepo.SaveChangesAsync();

            // Act - Check status before allowing /back
            var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);

            // Assert - Should show not away
            Assert.NotNull(existing);
            Assert.False(existing.Status);
        }

        [Fact]
        public async Task SetBack_WhenNoRecord_ReturnsNull()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 78901;

            // Act - Check for non-existent user
            var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);

            // Assert
            Assert.Null(existing);
        }

        [Fact]
        public async Task SetBackForced_UpdatesAnotherUsersStatus()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong targetUserId = 89012;
            string targetUserName = "ForcedBackUser";

            // Create away entry for target user
            await awayRepo.AddAsync(new AwaySystem
            {
                UserId = targetUserId,
                UserName = targetUserName,
                Status = true,
                Message = "Away for a while",
                TimeAway = DateTime.UtcNow.AddDays(-1)
            });
            await awayRepo.SaveChangesAsync();

            // Act - Simulate admin forcing user back via /set-back-forced
            await awayRepo.UpsertAsync(
                findPredicate: a => a.UserId == targetUserId,
                updateAction: away =>
                {
                    away.Status = false;
                    away.Message = string.Empty;
                    away.UserName = targetUserName;
                },
                createFactory: () => new AwaySystem
                {
                    UserId = targetUserId,
                    UserName = targetUserName,
                    Status = false,
                    Message = string.Empty
                });
            await awayRepo.SaveChangesAsync();

            // Assert
            var result = await awayRepo.FirstOrDefaultAsync(a => a.UserId == targetUserId);
            Assert.NotNull(result);
            Assert.False(result.Status);
        }

        [Fact]
        public async Task AwaySystem_HandlesMultipleUsers_Independently()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();

            // Create multiple away users
            var users = new[]
            {
                new AwaySystem { UserId = 100001, UserName = "User1", Status = true, Message = "Away 1", TimeAway = DateTime.UtcNow },
                new AwaySystem { UserId = 100002, UserName = "User2", Status = false, Message = string.Empty },
                new AwaySystem { UserId = 100003, UserName = "User3", Status = true, Message = "Away 3", TimeAway = DateTime.UtcNow }
            };

            foreach (var user in users)
            {
                await awayRepo.AddAsync(user);
            }
            await awayRepo.SaveChangesAsync();

            // Act - Query away users only
            var awayUsers = await awayRepo.WhereAsync(a => a.Status == true);

            // Assert
            Assert.Equal(2, awayUsers.Count());
            Assert.All(awayUsers, u => Assert.True(u.Status));
            Assert.Contains(awayUsers, u => u.UserId == 100001);
            Assert.Contains(awayUsers, u => u.UserId == 100003);
            Assert.DoesNotContain(awayUsers, u => u.UserId == 100002);
        }

        [Fact]
        public async Task AwaySystem_UpdatesUsername_OnStatusChange()
        {
            // Arrange
            var awayRepo = _serviceProvider.GetRequiredService<IRepository<AwaySystem>>();
            ulong userId = 200001;

            // Create entry with old username
            await awayRepo.AddAsync(new AwaySystem
            {
                UserId = userId,
                UserName = "OldUsername",
                Status = true,
                Message = "Away",
                TimeAway = DateTime.UtcNow
            });
            await awayRepo.SaveChangesAsync();

            // Act - User changed their username, then goes back (updates username)
            string newUsername = "NewUsername";
            await awayRepo.UpsertAsync(
                findPredicate: a => a.UserId == userId,
                updateAction: away =>
                {
                    away.Status = false;
                    away.Message = string.Empty;
                    away.UserName = newUsername; // Update to current username
                },
                createFactory: () => throw new InvalidOperationException("Should not create"));
            await awayRepo.SaveChangesAsync();

            // Assert
            var result = await awayRepo.FirstOrDefaultAsync(a => a.UserId == userId);
            Assert.NotNull(result);
            Assert.Equal("NewUsername", result.UserName);
        }

        public async ValueTask DisposeAsync()
        {
            _context?.Database.EnsureDeleted();
            if (_context != null)
                await _context.DisposeAsync();
            if (_serviceProvider != null)
                await _serviceProvider.DisposeAsync();
        }
    }
}
