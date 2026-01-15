using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Polls;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for the PollResultsBuilder and ServerPollSettings
    /// </summary>
    public class PollResultsTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public PollResultsTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"PollResultsTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        #region PollResultsBuilder Tests

        [Fact]
        public void BuildResultsEmbed_ReturnsEmbed_WithCorrectTitle()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var poll = CreateTestPoll("What is your favorite color?");
            var options = CreateTestOptions(poll.Id, new[] { "Red", "Blue", "Green" });
            var votes = new List<PollVote>();

            // Act
            var embed = builder.BuildResultsEmbed(poll, options, votes);

            // Assert
            Assert.NotNull(embed);
            Assert.Equal("📊 Poll Results", embed.Title);
        }

        [Fact]
        public void BuildResultsEmbed_ShowsWinner_WithTrophyEmoji()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var poll = CreateTestPoll("What is your favorite color?");
            var options = CreateTestOptions(poll.Id, new[] { "Red", "Blue", "Green" });

            // Create votes - Red gets 5, Blue gets 2, Green gets 1
            var votes = new List<PollVote>();
            for (int i = 0; i < 5; i++)
            {
                votes.Add(new PollVote { PollId = poll.Id, OptionId = options[0].Id, UserId = 100 + i, UserName = $"User{i}" });
            }
            for (int i = 0; i < 2; i++)
            {
                votes.Add(new PollVote { PollId = poll.Id, OptionId = options[1].Id, UserId = 200 + i, UserName = $"User{5+i}" });
            }
            votes.Add(new PollVote { PollId = poll.Id, OptionId = options[2].Id, UserId = 300, UserName = "User7" });

            // Act
            var embed = builder.BuildResultsEmbed(poll, options, votes);

            // Assert
            Assert.True(embed.Fields.Length > 0);
            var firstField = embed.Fields.First();
            Assert.Contains("🏆", firstField.Name); // Winner should have trophy
        }

        [Fact]
        public void BuildResultsEmbed_ShowsPercentages_Correctly()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var poll = CreateTestPoll("Yes or No?");
            var options = CreateTestOptions(poll.Id, new[] { "Yes", "No" });

            // Create 3 yes votes and 1 no vote (75% / 25%)
            var votes = new List<PollVote>
            {
                new() { PollId = poll.Id, OptionId = options[0].Id, UserId = 1, UserName = "User1" },
                new() { PollId = poll.Id, OptionId = options[0].Id, UserId = 2, UserName = "User2" },
                new() { PollId = poll.Id, OptionId = options[0].Id, UserId = 3, UserName = "User3" },
                new() { PollId = poll.Id, OptionId = options[1].Id, UserId = 4, UserName = "User4" }
            };

            // Act
            var embed = builder.BuildResultsEmbed(poll, options, votes);

            // Assert
            Assert.True(embed.Fields.Length > 0);
            var yesField = embed.Fields.First();
            Assert.Contains("75.0%", yesField.Value); // Yes should show 75%
            Assert.Contains("3 votes", yesField.Value);
        }

        [Fact]
        public void BuildResultsEmbed_ShowsClosedBy_WhenProvided()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var poll = CreateTestPoll("Test poll");
            var options = CreateTestOptions(poll.Id, new[] { "A", "B" });
            var votes = new List<PollVote>();

            // Act
            var embed = builder.BuildResultsEmbed(poll, options, votes, closedBy: "TestModerator", wasExpired: false);

            // Assert
            Assert.NotNull(embed.Footer);
            Assert.Contains("Closed by TestModerator", embed.Footer.Value.Text);
        }

        [Fact]
        public void BuildResultsEmbed_ShowsAutoExpired_WhenExpired()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var poll = CreateTestPoll("Test poll");
            var options = CreateTestOptions(poll.Id, new[] { "A", "B" });
            var votes = new List<PollVote>();

            // Act
            var embed = builder.BuildResultsEmbed(poll, options, votes, closedBy: null, wasExpired: true);

            // Assert
            Assert.NotNull(embed.Footer);
            Assert.Contains("Auto-expired", embed.Footer.Value.Text);
        }

        [Fact]
        public void BuildResultsEmbed_ShowsJumpLink_WhenMessageIdExists()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var poll = CreateTestPoll("Test poll");
            poll.GuildId = 111;
            poll.ChannelId = 222;
            poll.MessageId = 333;
            var options = CreateTestOptions(poll.Id, new[] { "A", "B" });
            var votes = new List<PollVote>();

            // Act
            var embed = builder.BuildResultsEmbed(poll, options, votes);

            // Assert
            var jumpField = embed.Fields.FirstOrDefault(f => f.Name == "📎 Original Poll");
            Assert.NotNull(jumpField.Name); // Name is non-null when field exists
            Assert.Contains("discord.com/channels/111/222/333", jumpField.Value);
        }

        [Fact]
        public void BuildVoterMentions_ReturnsEmpty_WhenAnonymous()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var votes = new List<PollVote>
            {
                new() { UserId = 123, UserName = "User1" },
                new() { UserId = 456, UserName = "User2" }
            };

            // Act
            var result = builder.BuildVoterMentions(votes, isAnonymous: true);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void BuildVoterMentions_ReturnsMentions_WhenNotAnonymous()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var votes = new List<PollVote>
            {
                new() { UserId = 123, UserName = "User1" },
                new() { UserId = 456, UserName = "User2" }
            };

            // Act
            var result = builder.BuildVoterMentions(votes, isAnonymous: false);

            // Assert
            Assert.Contains("<@123>", result);
            Assert.Contains("<@456>", result);
            Assert.Contains("**Voters:**", result);
        }

        [Fact]
        public void BuildVoterMentions_TruncatesLongLists()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var votes = new List<PollVote>();
            for (int i = 0; i < 15; i++)
            {
                votes.Add(new PollVote { UserId = 100 + i, UserName = $"User{i}" });
            }

            // Act
            var result = builder.BuildVoterMentions(votes, isAnonymous: false, maxMentions: 10);

            // Assert
            Assert.Contains("(+5 more)", result);
        }

        [Fact]
        public void BuildVoterMentions_ReturnsEmpty_WhenNoVotes()
        {
            // Arrange
            var builder = new PollResultsBuilder();
            var votes = new List<PollVote>();

            // Act
            var result = builder.BuildVoterMentions(votes, isAnonymous: false);

            // Assert
            Assert.Empty(result);
        }

        #endregion

        #region ServerPollSettings Tests

        [Fact]
        public async Task ServerPollSettings_CanBeCreated_WithDefaults()
        {
            // Arrange & Act
            var settings = new ServerPollSettings
            {
                DiscordGuildId = 123456789,
                MentionVotersOnClose = false,
                ResultsChannelId = null
            };

            _context.ServerPollSettings.Add(settings);
            await _context.SaveChangesAsync();

            // Assert
            var retrieved = await _context.ServerPollSettings.FindAsync(123456789L);
            Assert.NotNull(retrieved);
            Assert.False(retrieved.MentionVotersOnClose);
            Assert.Null(retrieved.ResultsChannelId);
        }

        [Fact]
        public async Task ServerPollSettings_CanBeUpdated()
        {
            // Arrange
            var settings = new ServerPollSettings
            {
                DiscordGuildId = 987654321,
                MentionVotersOnClose = false,
                ResultsChannelId = null
            };

            _context.ServerPollSettings.Add(settings);
            await _context.SaveChangesAsync();

            // Act
            settings.MentionVotersOnClose = true;
            settings.ResultsChannelId = 111222333;
            settings.SetById = 999;
            settings.SetByName = "Admin";
            settings.TimeSet = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Assert
            var retrieved = await _context.ServerPollSettings.FindAsync(987654321L);
            Assert.NotNull(retrieved);
            Assert.True(retrieved.MentionVotersOnClose);
            Assert.Equal(111222333, retrieved.ResultsChannelId);
            Assert.Equal("Admin", retrieved.SetByName);
        }

        [Fact]
        public async Task ServerPollSettings_GuildIdIsPrimaryKey()
        {
            // Arrange
            var settings1 = new ServerPollSettings
            {
                DiscordGuildId = 111111111,
                MentionVotersOnClose = true
            };

            _context.ServerPollSettings.Add(settings1);
            await _context.SaveChangesAsync();

            // Act - Try to add duplicate
            var settings2 = new ServerPollSettings
            {
                DiscordGuildId = 111111111, // Same ID
                MentionVotersOnClose = false
            };

            // Assert - Should throw on Add due to duplicate key tracking
            Assert.Throws<InvalidOperationException>(() => _context.ServerPollSettings.Add(settings2));
        }

        #endregion

        #region Helper Methods

        private Poll CreateTestPoll(string question)
        {
            return new Poll
            {
                Id = new Random().Next(1, 100000),
                Question = question,
                PollType = "SingleChoice",
                AllowVoteChange = true,
                IsAnonymous = false,
                IsClosed = true,
                CreatedAt = DateTime.UtcNow.AddHours(-1),
                ClosedAt = DateTime.UtcNow,
                CreatedById = 12345,
                CreatedByName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };
        }

        private List<PollOption> CreateTestOptions(long pollId, string[] optionTexts)
        {
            var options = new List<PollOption>();
            for (int i = 0; i < optionTexts.Length; i++)
            {
                options.Add(new PollOption
                {
                    Id = pollId * 100 + i,
                    PollId = pollId,
                    OptionText = optionTexts[i],
                    DisplayOrder = i,
                    Emote = $"{i + 1}️⃣"
                });
            }
            return options;
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _serviceProvider.DisposeAsync();
        }
    }
}
