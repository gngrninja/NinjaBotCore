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

// Type aliases to avoid naming conflict with Discord.Poll
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Integration tests for the Poll system including creation, voting, and expiration
    /// </summary>
    public class PollTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public PollTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"PollTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            // Add repositories
            services.AddScoped<IRepository<DbPoll>>(sp =>
                new Repository<DbPoll>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<DbPollOption>>(sp =>
                new Repository<DbPollOption>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<DbPollVote>>(sp =>
                new Repository<DbPollVote>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IUnitOfWork, UnitOfWork>();

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        #region Poll Creation Tests

        [Fact]
        public async Task CreatePoll_YesNo_CreatesWithTwoOptions()
        {
            // Arrange
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();
            var optionRepo = _serviceProvider.GetRequiredService<IRepository<DbPollOption>>();

            // Act - Create a Yes/No poll
            var poll = new DbPoll
            {
                Question = "Do you like pizza?",
                PollType = "YesNo",
                AllowVoteChange = true,
                IsAnonymous = false,
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedById = 12345,
                CreatedByName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };

            await pollRepo.AddAsync(poll);
            await pollRepo.SaveChangesAsync();

            var option1 = new DbPollOption
            {
                PollId = poll.Id,
                OptionText = "Yes",
                DisplayOrder = 0,
                Emote = "✅"
            };
            var option2 = new DbPollOption
            {
                PollId = poll.Id,
                OptionText = "No",
                DisplayOrder = 1,
                Emote = "❌"
            };

            await optionRepo.AddAsync(option1);
            await optionRepo.AddAsync(option2);
            await optionRepo.SaveChangesAsync();

            // Assert
            var savedPoll = await _context.Polls
                .Include(p => p.PollOptions)
                .FirstOrDefaultAsync(p => p.Id == poll.Id);

            Assert.NotNull(savedPoll);
            Assert.Equal("Do you like pizza?", savedPoll.Question);
            Assert.Equal("YesNo", savedPoll.PollType);
            Assert.Equal(2, savedPoll.PollOptions.Count);
            Assert.Contains(savedPoll.PollOptions, o => o.OptionText == "Yes");
            Assert.Contains(savedPoll.PollOptions, o => o.OptionText == "No");
        }

        [Fact]
        public async Task CreatePoll_SingleChoice_CreatesWithMultipleOptions()
        {
            // Arrange
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();
            var optionRepo = _serviceProvider.GetRequiredService<IRepository<DbPollOption>>();

            // Act
            var poll = new DbPoll
            {
                Question = "What's your favorite color?",
                PollType = "SingleChoice",
                AllowVoteChange = true,
                IsAnonymous = false,
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedById = 12345,
                CreatedByName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };

            await pollRepo.AddAsync(poll);
            await pollRepo.SaveChangesAsync();

            var options = new[] { "Red", "Blue", "Green", "Yellow" };
            for (int i = 0; i < options.Length; i++)
            {
                await optionRepo.AddAsync(new DbPollOption
                {
                    PollId = poll.Id,
                    OptionText = options[i],
                    DisplayOrder = i,
                    Emote = $"{i + 1}️⃣"
                });
            }
            await optionRepo.SaveChangesAsync();

            // Assert
            var savedPoll = await _context.Polls
                .Include(p => p.PollOptions)
                .FirstOrDefaultAsync(p => p.Id == poll.Id);

            Assert.NotNull(savedPoll);
            Assert.Equal(4, savedPoll.PollOptions.Count);
            Assert.Equal("SingleChoice", savedPoll.PollType);
        }

        [Fact]
        public async Task CreatePoll_WithExpiration_StoresExpirationDate()
        {
            // Arrange
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();
            var expirationDate = DateTime.UtcNow.AddHours(24);

            // Act
            var poll = new DbPoll
            {
                Question = "Expiring poll",
                PollType = "YesNo",
                AllowVoteChange = true,
                IsAnonymous = false,
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expirationDate,
                CreatedById = 12345,
                CreatedByName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };

            await pollRepo.AddAsync(poll);
            await pollRepo.SaveChangesAsync();

            // Assert
            var savedPoll = await pollRepo.FirstOrDefaultAsync(p => p.Id == poll.Id);
            Assert.NotNull(savedPoll);
            Assert.NotNull(savedPoll.ExpiresAt);
            Assert.Equal(expirationDate.Date, savedPoll.ExpiresAt.Value.Date);
        }

        #endregion

        #region Voting Tests

        [Fact]
        public async Task Vote_SingleChoice_RecordsVoteCorrectly()
        {
            // Arrange
            var (poll, options) = await CreateTestPollAsync("SingleChoice", new[] { "Option A", "Option B" });
            var voteRepo = _serviceProvider.GetRequiredService<IRepository<DbPollVote>>();

            // Act - User votes for Option A
            var vote = new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[0].Id,
                UserId = 555,
                UserName = "Voter1",
                VotedAt = DateTime.UtcNow
            };

            await voteRepo.AddAsync(vote);
            await voteRepo.SaveChangesAsync();

            // Assert
            var savedVote = await voteRepo.FirstOrDefaultAsync(v => v.PollId == poll.Id && v.UserId == 555);
            Assert.NotNull(savedVote);
            Assert.Equal(options[0].Id, savedVote.OptionId);
            Assert.Equal("Voter1", savedVote.UserName);
        }

        [Fact]
        public async Task Vote_SingleChoice_ChangeVote_RemovesOldVote()
        {
            // Arrange
            var (poll, options) = await CreateTestPollAsync("SingleChoice", new[] { "Option A", "Option B" });
            var voteRepo = _serviceProvider.GetRequiredService<IRepository<DbPollVote>>();

            // Act - User votes for Option A
            var vote1 = new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[0].Id,
                UserId = 555,
                UserName = "Voter1",
                VotedAt = DateTime.UtcNow
            };
            await voteRepo.AddAsync(vote1);
            await voteRepo.SaveChangesAsync();

            // Change vote to Option B
            voteRepo.Delete(vote1);
            var vote2 = new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[1].Id,
                UserId = 555,
                UserName = "Voter1",
                VotedAt = DateTime.UtcNow
            };
            await voteRepo.AddAsync(vote2);
            await voteRepo.SaveChangesAsync();

            // Assert - Only one vote should exist
            var votes = await voteRepo.WhereAsync(v => v.PollId == poll.Id && v.UserId == 555);
            Assert.Single(votes);
            Assert.Equal(options[1].Id, votes.First().OptionId);
        }

        [Fact]
        public async Task Vote_MultipleChoice_AllowsMultipleVotes()
        {
            // Arrange
            var (poll, options) = await CreateTestPollAsync("MultipleChoice", new[] { "Option A", "Option B", "Option C" });
            var voteRepo = _serviceProvider.GetRequiredService<IRepository<DbPollVote>>();

            // Act - User votes for Option A and Option C
            var vote1 = new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[0].Id,
                UserId = 555,
                UserName = "Voter1",
                VotedAt = DateTime.UtcNow
            };
            var vote2 = new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[2].Id,
                UserId = 555,
                UserName = "Voter1",
                VotedAt = DateTime.UtcNow
            };

            await voteRepo.AddAsync(vote1);
            await voteRepo.AddAsync(vote2);
            await voteRepo.SaveChangesAsync();

            // Assert - User should have 2 votes
            var votes = await voteRepo.WhereAsync(v => v.PollId == poll.Id && v.UserId == 555);
            Assert.Equal(2, votes.Count());
        }

        [Fact]
        public async Task Vote_ClosedPoll_ValidationCheck()
        {
            // Arrange
            var (poll, options) = await CreateTestPollAsync("SingleChoice", new[] { "Option A" });
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();

            // Close the poll
            poll.IsClosed = true;
            poll.ClosedAt = DateTime.UtcNow;
            pollRepo.Update(poll);
            await pollRepo.SaveChangesAsync();

            // Assert - Poll should be closed
            var closedPoll = await pollRepo.FirstOrDefaultAsync(p => p.Id == poll.Id);
            Assert.True(closedPoll.IsClosed);
            Assert.NotNull(closedPoll.ClosedAt);

            // Note: Vote validation is handled by PollCommands.ProcessVoteAsync
            // This test verifies the database state for closed polls
        }

        [Fact]
        public async Task Vote_ExpiredPoll_ValidationCheck()
        {
            // Arrange
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();
            var optionRepo = _serviceProvider.GetRequiredService<IRepository<DbPollOption>>();

            var poll = new DbPoll
            {
                Question = "Expired poll",
                PollType = "YesNo",
                AllowVoteChange = true,
                IsAnonymous = false,
                IsClosed = false,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddDays(-1), // Expired yesterday
                CreatedById = 12345,
                CreatedByName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };

            await pollRepo.AddAsync(poll);
            await pollRepo.SaveChangesAsync();

            // Assert - Poll should be expired
            var expiredPoll = await pollRepo.FirstOrDefaultAsync(p => p.Id == poll.Id);
            Assert.NotNull(expiredPoll.ExpiresAt);
            Assert.True(DateTime.UtcNow > expiredPoll.ExpiresAt.Value);

            // Note: Expiration handling is done by PollExpirationService
        }

        #endregion

        #region Poll Closing Tests

        [Fact]
        public async Task ClosePoll_UpdatesStatus_AndTimestamp()
        {
            // Arrange
            var (poll, _) = await CreateTestPollAsync("YesNo", new[] { "Yes", "No" });
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();

            // Act - Close the poll
            poll.IsClosed = true;
            poll.ClosedAt = DateTime.UtcNow;
            pollRepo.Update(poll);
            await pollRepo.SaveChangesAsync();

            // Assert
            var closedPoll = await pollRepo.FirstOrDefaultAsync(p => p.Id == poll.Id);
            Assert.True(closedPoll.IsClosed);
            Assert.NotNull(closedPoll.ClosedAt);
        }

        [Fact]
        public async Task ClosePoll_WithVotes_PreservesVoteData()
        {
            // Arrange
            var (poll, options) = await CreateTestPollAsync("YesNo", new[] { "Yes", "No" });
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();
            var voteRepo = _serviceProvider.GetRequiredService<IRepository<DbPollVote>>();

            // Add some votes
            await voteRepo.AddAsync(new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[0].Id,
                UserId = 100,
                UserName = "User1",
                VotedAt = DateTime.UtcNow
            });
            await voteRepo.AddAsync(new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[1].Id,
                UserId = 200,
                UserName = "User2",
                VotedAt = DateTime.UtcNow
            });
            await voteRepo.SaveChangesAsync();

            // Act - Close the poll
            poll.IsClosed = true;
            poll.ClosedAt = DateTime.UtcNow;
            pollRepo.Update(poll);
            await pollRepo.SaveChangesAsync();

            // Assert - Votes should still exist
            var closedPoll = await _context.Polls
                .Include(p => p.PollOptions)
                .Include(p => p.PollVotes)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == poll.Id);

            Assert.True(closedPoll.IsClosed);
            Assert.Equal(2, closedPoll.PollVotes.Count);
        }

        #endregion

        #region Query Tests

        [Fact]
        public async Task FindExpiredPolls_ReturnsOnlyExpiredAndOpen()
        {
            // Arrange
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();

            // Create multiple polls with different states
            var polls = new[]
            {
                new DbPoll
                {
                    Question = "Active poll",
                    PollType = "YesNo",
                    IsClosed = false,
                    ExpiresAt = DateTime.UtcNow.AddHours(1), // Expires in future
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "User1",
                    GuildId = 111,
                    ChannelId = 222,
                    MessageId = 333
                },
                new DbPoll
                {
                    Question = "Expired but open",
                    PollType = "YesNo",
                    IsClosed = false,
                    ExpiresAt = DateTime.UtcNow.AddHours(-1), // Expired
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    CreatedById = 1,
                    CreatedByName = "User1",
                    GuildId = 111,
                    ChannelId = 222,
                    MessageId = 334
                },
                new DbPoll
                {
                    Question = "Expired and closed",
                    PollType = "YesNo",
                    IsClosed = true,
                    ExpiresAt = DateTime.UtcNow.AddHours(-1), // Expired
                    ClosedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    CreatedById = 1,
                    CreatedByName = "User1",
                    GuildId = 111,
                    ChannelId = 222,
                    MessageId = 335
                }
            };

            foreach (var poll in polls)
            {
                await pollRepo.AddAsync(poll);
            }
            await pollRepo.SaveChangesAsync();

            // Act - Find expired polls that are still open (simulates PollExpirationService logic)
            var expiredPolls = await _context.Polls
                .Where(p => !p.IsClosed && p.ExpiresAt.HasValue && p.ExpiresAt.Value <= DateTime.UtcNow)
                .ToListAsync();

            // Assert - Should only return the expired but open poll
            Assert.Single(expiredPolls);
            Assert.Equal("Expired but open", expiredPolls[0].Question);
        }

        [Fact]
        public async Task GetPollsByGuild_FiltersCorrectly()
        {
            // Arrange
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();

            // Create polls in different guilds
            await pollRepo.AddAsync(new DbPoll
            {
                Question = "Guild 1 Poll 1",
                PollType = "YesNo",
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedById = 1,
                CreatedByName = "User1",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            });

            await pollRepo.AddAsync(new DbPoll
            {
                Question = "Guild 1 Poll 2",
                PollType = "YesNo",
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedById = 1,
                CreatedByName = "User1",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 334
            });

            await pollRepo.AddAsync(new DbPoll
            {
                Question = "Guild 2 Poll 1",
                PollType = "YesNo",
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedById = 1,
                CreatedByName = "User1",
                GuildId = 999,
                ChannelId = 222,
                MessageId = 335
            });

            await pollRepo.SaveChangesAsync();

            // Act - Get polls for Guild 1
            var guild1Polls = await pollRepo.WhereAsync(p => p.GuildId == 111);

            // Assert
            Assert.Equal(2, guild1Polls.Count());
            Assert.All(guild1Polls, p => Assert.Equal(111, p.GuildId));
        }

        [Fact]
        public async Task SplitQuery_MultipleIncludes_NoWarning()
        {
            // Arrange
            var (poll, options) = await CreateTestPollAsync("YesNo", new[] { "Yes", "No" });
            var voteRepo = _serviceProvider.GetRequiredService<IRepository<DbPollVote>>();

            // Add a vote
            await voteRepo.AddAsync(new DbPollVote
            {
                PollId = poll.Id,
                OptionId = options[0].Id,
                UserId = 555,
                UserName = "Voter1",
                VotedAt = DateTime.UtcNow
            });
            await voteRepo.SaveChangesAsync();

            // Act - Query with multiple includes using AsSplitQuery()
            var result = await _context.Polls
                .Include(p => p.PollOptions)
                .Include(p => p.PollVotes)
                .AsSplitQuery() // This prevents EF Core warning
                .FirstOrDefaultAsync(p => p.Id == poll.Id);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.PollOptions);
            Assert.NotEmpty(result.PollVotes);
        }

        #endregion

        #region Helper Methods

        private async Task<(DbPoll poll, List<DbPollOption> options)> CreateTestPollAsync(string pollType, string[] optionTexts)
        {
            var pollRepo = _serviceProvider.GetRequiredService<IRepository<DbPoll>>();
            var optionRepo = _serviceProvider.GetRequiredService<IRepository<DbPollOption>>();

            var poll = new DbPoll
            {
                Question = $"Test {pollType} Poll",
                PollType = pollType,
                AllowVoteChange = true,
                IsAnonymous = false,
                IsClosed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedById = 12345,
                CreatedByName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };

            await pollRepo.AddAsync(poll);
            await pollRepo.SaveChangesAsync();

            var options = new List<DbPollOption>();
            for (int i = 0; i < optionTexts.Length; i++)
            {
                var option = new DbPollOption
                {
                    PollId = poll.Id,
                    OptionText = optionTexts[i],
                    DisplayOrder = i,
                    Emote = $"{i + 1}️⃣"
                };
                await optionRepo.AddAsync(option);
                options.Add(option);
            }

            await optionRepo.SaveChangesAsync();

            // Reload poll with options
            poll = await _context.Polls
                .Include(p => p.PollOptions)
                .FirstOrDefaultAsync(p => p.Id == poll.Id);

            return (poll, options);
        }

        #endregion

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
