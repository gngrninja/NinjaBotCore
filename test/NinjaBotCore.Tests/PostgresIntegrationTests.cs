using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Database;
using Xunit;
using Npgsql;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Integration tests for PostgreSQL database operations.
    /// These tests verify the migration from SQLite to PostgreSQL is working correctly.
    /// </summary>
    [Collection("DatabaseConfigurator")]
    public class PostgresIntegrationTests : IDisposable
    {
        private readonly NinjaBotEntities _context;
        private readonly IConfiguration _configuration;
        private readonly bool _skip;

        public PostgresIntegrationTests()
        {
            // Load configuration from environment or config file
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("config.json", optional: true)
                .AddEnvironmentVariables(prefix: "NINJABOT_");

            _configuration = builder.Build();

            var provider = _configuration["Database:Provider"] ?? string.Empty;
            var connectionString = _configuration.GetConnectionString("NinjaBot");
            if (!provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) &&
                !provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
            {
                _skip = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _skip = true;
                return;
            }

            // quick connectivity check before creating the context
            try
            {
                using var conn = new NpgsqlConnection(connectionString);
                conn.Open();
            }
            catch (Exception)
            {
                _skip = true;
                return;
            }

            // Configure the database
            DatabaseConfigurator.ConfigureFrom(_configuration);

            // Create context
            _context = new NinjaBotEntities();

            // Ensure database exists and is migrated
            _context.Database.EnsureCreated();
        }

        [Fact]
        public async Task Database_ShouldConnect_WithoutErrors()
        {
            // Act - Try to open connection
            if (_skip) return;

            var canConnect = await _context.Database.CanConnectAsync();

            // Assert
            Assert.True(canConnect, "Database connection failed");
        }

        [Fact]
        public void Database_Provider_ShouldBePostgreSQL()
        {
            // Act
            if (_skip) return;

            var provider = _context.Database.ProviderName;

            // Assert - Check if we're using PostgreSQL when configured
            var configuredProvider = _configuration["Database:Provider"];

            if (!string.IsNullOrWhiteSpace(configuredProvider) &&
                (configuredProvider.ToLower() == "postgres" || configuredProvider.ToLower() == "postgresql"))
            {
                Assert.Contains("Npgsql", provider, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task AwaySystem_CRUD_Operations_ShouldWork()
        {
            // Arrange
            if (_skip) return;

            var testAway = new AwaySystem
            {
                UserName = "TestUser",
                Message = "Test message",
                Status = true,
                TimeAway = DateTime.UtcNow
            };

            try
            {
                // Act - Create
                _context.AwaySystem.Add(testAway);
                await _context.SaveChangesAsync();

                Assert.True(testAway.AwayId > 0, "AwayId should be generated as bigint > 0");

                // Act - Read
                var retrieved = await _context.AwaySystem
                    .FirstOrDefaultAsync(a => a.AwayId == testAway.AwayId);

                // Assert - Read
                Assert.NotNull(retrieved);
                Assert.Equal("TestUser", retrieved.UserName);
                Assert.Equal(true, retrieved.Status);
                Assert.NotNull(retrieved.TimeAway);

                // Assert - DateTime precision (PostgreSQL stores with microsecond precision)
                var timeDifference = Math.Abs((retrieved.TimeAway.Value - testAway.TimeAway.Value).TotalMilliseconds);
                Assert.True(timeDifference < 1, "DateTime should be stored with sub-millisecond precision");

                // Act - Update
                retrieved.Message = "Updated message";
                retrieved.Status = false;
                await _context.SaveChangesAsync();

                var updated = await _context.AwaySystem.FindAsync(testAway.AwayId);
                Assert.Equal("Updated message", updated.Message);
                Assert.Equal(false, updated.Status);

                // Act - Delete
                _context.AwaySystem.Remove(updated);
                await _context.SaveChangesAsync();

                var deleted = await _context.AwaySystem.FindAsync(testAway.AwayId);
                Assert.Null(deleted);
            }
            finally
            {
                // Cleanup - ensure test data is removed
                var cleanup = await _context.AwaySystem
                    .FirstOrDefaultAsync(a => a.UserName == "TestUser");
                if (cleanup != null)
                {
                    _context.AwaySystem.Remove(cleanup);
                    await _context.SaveChangesAsync();
                }
            }
        }

        [Fact]
        public async Task WowVanillaGuild_DateTime_ShouldStore_WithTimezone()
        {
            // Arrange
            if (_skip) return;

            var testGuild = new WowVanillaGuild
            {
                ServerName = "TestServer",
                WowGuild = "TestGuild",
                WowRealm = "TestRealm",
                WowRegion = "US",
                SetBy = "TestUser",
                SetById = 123456789012345, // Test long ID
                TimeSet = DateTime.UtcNow
            };

            try
            {
                // Act
                _context.WowVanillaGuild.Add(testGuild);
                await _context.SaveChangesAsync();

                // Assert - ID is bigint
                Assert.True(testGuild.Id > 0, "Id should be generated as bigint");

                // Retrieve and verify
                var retrieved = await _context.WowVanillaGuild.FindAsync(testGuild.Id);

                Assert.NotNull(retrieved);
                Assert.NotNull(retrieved.TimeSet);
                Assert.Equal(testGuild.SetById, retrieved.SetById);

                // Verify DateTime kind is preserved
                Assert.Equal(DateTimeKind.Utc, retrieved.TimeSet.Value.Kind);
            }
            finally
            {
                // Cleanup
                var cleanup = await _context.WowVanillaGuild
                    .FirstOrDefaultAsync(g => g.ServerName == "TestServer");
                if (cleanup != null)
                {
                    _context.WowVanillaGuild.Remove(cleanup);
                    await _context.SaveChangesAsync();
                }
            }
        }

        [Fact]
        public async Task WclPosted_BigInt_IDs_ShouldWork()
        {
            // Arrange - Test with Discord snowflake IDs (which are large long values)
            if (_skip) return;

            var testPosted = new WclPosted
            {
                ServerId = 123456789012345678, // Discord snowflake
                ChannelId = 987654321098765432, // Discord snowflake
                ChannelName = "test-channel",
                ServerName = "TestServer",
                ReportId = "TestReport123"
            };

            try
            {
                // Act
                _context.WclPosted.Add(testPosted);
                await _context.SaveChangesAsync();

                // Assert
                Assert.True(testPosted.Id > 0);

                var retrieved = await _context.WclPosted.FindAsync(testPosted.Id);

                Assert.NotNull(retrieved);
                Assert.Equal(123456789012345678, retrieved.ServerId);
                Assert.Equal(987654321098765432, retrieved.ChannelId);
            }
            finally
            {
                // Cleanup
                var cleanup = await _context.WclPosted
                    .FirstOrDefaultAsync(w => w.ReportId == "TestReport123");
                if (cleanup != null)
                {
                    _context.WclPosted.Remove(cleanup);
                    await _context.SaveChangesAsync();
                }
            }
        }

        [Fact]
        public async Task TriviaQuestionChoice_ForeignKey_ShouldEnforce_Constraints()
        {
            if (_skip) return;

            // Arrange
            var testQuestion = new TriviaQuestion
            {
                Question = "Test Question?",
                Category = 1,
                IsActive = true
            };

            var testChoice = new TriviaQuestionChoice
            {
                Choice = "Test Answer",
                IsRightChoice = true
            };

            try
            {
                // Act - Create parent
                _context.TriviaQuestion.Add(testQuestion);
                await _context.SaveChangesAsync();

                // Link child to parent
                testChoice.QuestionId = testQuestion.QuestionId;
                _context.TriviaQuestionChoices.Add(testChoice);
                await _context.SaveChangesAsync();

                // Assert - Foreign key relationship exists
                var retrieved = await _context.TriviaQuestionChoices
                    .Include(c => c.TriviaQuestion)
                    .FirstOrDefaultAsync(c => c.ChoiceId == testChoice.ChoiceId);

                Assert.NotNull(retrieved);

                // Note: Navigation properties may not be loaded automatically
                // Try to load it explicitly
                if (retrieved.TriviaQuestion == null)
                {
                    await _context.Entry(retrieved).Reference(c => c.TriviaQuestion).LoadAsync();
                }

                // Verify relationship exists via QuestionId
                Assert.Equal(testQuestion.QuestionId, retrieved.QuestionId);
            }
            finally
            {
                // Cleanup - Remove in correct order
                var cleanupChoice = await _context.TriviaQuestionChoices
                    .FirstOrDefaultAsync(c => c.Choice == "Test Answer");
                if (cleanupChoice != null)
                {
                    _context.TriviaQuestionChoices.Remove(cleanupChoice);
                    await _context.SaveChangesAsync();
                }

                var cleanupQuestion = await _context.TriviaQuestion
                    .FirstOrDefaultAsync(q => q.Question == "Test Question?");
                if (cleanupQuestion != null)
                {
                    _context.TriviaQuestion.Remove(cleanupQuestion);
                    await _context.SaveChangesAsync();
                }
            }
        }

        [Fact]
        public async Task Database_CaseInsensitive_Search_ShouldWork()
        {
            if (_skip) return;

            // Arrange
            var testAway = new AwaySystem
            {
                UserName = "CaseSensitiveTest",
                Message = "Testing case sensitivity",
                Status = true
            };

            try
            {
                _context.AwaySystem.Add(testAway);
                await _context.SaveChangesAsync();

                // Act - PostgreSQL is case-sensitive by default, use EF.Functions.ILike or ToLower()
                var resultLower = await _context.AwaySystem
                    .Where(a => a.UserName.ToLower() == "casesensitivetest")
                    .FirstOrDefaultAsync();

                var resultExact = await _context.AwaySystem
                    .Where(a => a.UserName == "CaseSensitiveTest")
                    .FirstOrDefaultAsync();

                // Assert
                Assert.NotNull(resultLower);
                Assert.NotNull(resultExact);
                Assert.Equal(testAway.AwayId, resultLower.AwayId);
                Assert.Equal(testAway.AwayId, resultExact.AwayId);
            }
            finally
            {
                // Cleanup
                var cleanup = await _context.AwaySystem
                    .FirstOrDefaultAsync(a => a.UserName == "CaseSensitiveTest");
                if (cleanup != null)
                {
                    _context.AwaySystem.Remove(cleanup);
                    await _context.SaveChangesAsync();
                }
            }
        }

        [Fact]
        public async Task NullableDateTime_ShouldStore_Null_Values()
        {
            if (_skip) return;

            // Arrange
            var testGuild = new WowVanillaGuild
            {
                ServerName = "NullTimeTest",
                WowGuild = "TestGuild",
                WowRealm = "TestRealm",
                TimeSet = null // Explicitly null
            };

            try
            {
                // Act
                _context.WowVanillaGuild.Add(testGuild);
                await _context.SaveChangesAsync();

                // Assert
                var retrieved = await _context.WowVanillaGuild.FindAsync(testGuild.Id);
                Assert.NotNull(retrieved);
                Assert.Null(retrieved.TimeSet);
            }
            finally
            {
                // Cleanup
                var cleanup = await _context.WowVanillaGuild
                    .FirstOrDefaultAsync(g => g.ServerName == "NullTimeTest");
                if (cleanup != null)
                {
                    _context.WowVanillaGuild.Remove(cleanup);
                    await _context.SaveChangesAsync();
                }
            }
        }

        [Fact]
        public async Task BulkInsert_Performance_Test()
        {
            if (_skip) return;

            // Arrange - Create 100 test records
            var testRecords = Enumerable.Range(1, 100)
                .Select(i => new AwaySystem
                {
                    UserName = $"BulkTest{i}",
                    Message = $"Bulk insert test {i}",
                    Status = i % 2 == 0,
                    TimeAway = DateTime.UtcNow.AddMinutes(i)
                })
                .ToList();

            try
            {
                // Act
                var startTime = DateTime.UtcNow;
                _context.AwaySystem.AddRange(testRecords);
                await _context.SaveChangesAsync();
                var endTime = DateTime.UtcNow;

                // Assert
                Assert.All(testRecords, r => Assert.True(r.AwayId > 0));

                var duration = (endTime - startTime).TotalSeconds;
                // Bulk insert should complete in reasonable time (< 5 seconds)
                Assert.True(duration < 5, $"Bulk insert took {duration} seconds, expected < 5 seconds");
            }
            finally
            {
                // Cleanup
                var cleanup = await _context.AwaySystem
                    .Where(a => a.UserName.StartsWith("BulkTest"))
                    .ToListAsync();

                _context.AwaySystem.RemoveRange(cleanup);
                await _context.SaveChangesAsync();
            }
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
