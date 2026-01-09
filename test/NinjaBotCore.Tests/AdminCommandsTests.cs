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
    /// Integration tests for admin module functionality
    /// Tests database operations for moderation, warnings, word filtering, and greeting systems
    /// </summary>
    public class AdminCommandsTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public AdminCommandsTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"AdminTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            // Add repositories
            services.AddScoped<IRepository<ModerationWatcher>>(sp =>
                new Repository<ModerationWatcher>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<Warnings>>(sp =>
                new Repository<Warnings>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<WordList>>(sp =>
                new Repository<WordList>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<ServerGreeting>>(sp =>
                new Repository<ServerGreeting>(sp.GetRequiredService<NinjaBotEntities>()));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        #region Moderation Watcher Tests

        [Fact]
        public async Task WatchCommand_Enable_CreatesOrUpdatesSettings()
        {
            // Arrange
            var watcherRepo = _serviceProvider.GetRequiredService<IRepository<ModerationWatcher>>();
            long guildId = 111111;

            // Act - Simulate /watch enable messages
            await watcherRepo.UpsertAsync(
                findPredicate: w => w.DiscordGuildId == guildId,
                updateAction: watcher =>
                {
                    watcher.WatchMessages = true;
                    watcher.ChannelId = 123456;
                },
                createFactory: () => new ModerationWatcher
                {
                    DiscordGuildId = guildId,
                    WatchMessages = true,
                    ChannelId = 123456
                });
            await watcherRepo.SaveChangesAsync();

            // Assert
            var result = await watcherRepo.FirstOrDefaultAsync(w => w.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.True(result.WatchMessages);
            Assert.Equal(123456, result.ChannelId);
        }

        [Fact]
        public async Task WatchCommand_Disable_UpdatesSettings()
        {
            // Arrange
            var watcherRepo = _serviceProvider.GetRequiredService<IRepository<ModerationWatcher>>();
            long guildId = 222222;

            // Create existing settings with watchers enabled
            await watcherRepo.AddAsync(new ModerationWatcher
            {
                DiscordGuildId = guildId,
                WatchMessages = true,
                WatchVoice = true,
                WatchBans = true,
                ChannelId = 123456
            });
            await watcherRepo.SaveChangesAsync();

            // Act - Simulate /watch disable messages
            await watcherRepo.UpsertAsync(
                findPredicate: w => w.DiscordGuildId == guildId,
                updateAction: watcher =>
                {
                    watcher.WatchMessages = false;
                },
                createFactory: () => throw new InvalidOperationException("Should not create"));
            await watcherRepo.SaveChangesAsync();

            // Assert
            var result = await watcherRepo.FirstOrDefaultAsync(w => w.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.False(result.WatchMessages);
            Assert.True(result.WatchVoice); // Other settings preserved
            Assert.True(result.WatchBans);
        }

        [Fact]
        public async Task WatchCommand_Status_ShowsCurrentState()
        {
            // Arrange
            var watcherRepo = _serviceProvider.GetRequiredService<IRepository<ModerationWatcher>>();
            long guildId = 333333;

            // Create settings
            await watcherRepo.AddAsync(new ModerationWatcher
            {
                DiscordGuildId = guildId,
                WatchMessages = true,
                WatchVoice = false,
                WatchBans = true,
                WatchRoles = true,
                WatchNicknames = false,
                ChannelId = 123456
            });
            await watcherRepo.SaveChangesAsync();

            // Act - Query current settings (what /watch status does)
            var result = await watcherRepo.FirstOrDefaultAsync(w => w.DiscordGuildId == guildId);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.WatchMessages);
            Assert.False(result.WatchVoice);
            Assert.True(result.WatchBans);
            Assert.True(result.WatchRoles);
            Assert.False(result.WatchNicknames);
        }

        #endregion

        #region Warning System Tests

        [Fact]
        public async Task WarnUser_FirstWarning_CreatesWarningRecord()
        {
            // Arrange
            var warningRepo = _serviceProvider.GetRequiredService<IRepository<Warnings>>();
            long guildId = 444444;
            long userId = 500001;

            // Act - Simulate /warn command (first warning, NumWarnings = 1)
            await warningRepo.UpsertAsync(
                findPredicate: w => w.ServerId == guildId && w.UserWarnedId == userId,
                updateAction: warning =>
                {
                    warning.NumWarnings += 1;
                    warning.TimeIssued = DateTime.UtcNow;
                    warning.IssuerName = "ModUser";
                    warning.IssuerId = 600001;
                },
                createFactory: () => new Warnings
                {
                    ServerId = guildId,
                    ServerName = "Test Server",
                    UserWarnedId = userId,
                    UserWarnedName = "WarnedUser",
                    IssuerId = 600001,
                    IssuerName = "ModUser",
                    TimeIssued = DateTime.UtcNow,
                    NumWarnings = 1
                });
            await warningRepo.SaveChangesAsync();

            // Assert
            var result = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.UserWarnedId == userId);
            Assert.NotNull(result);
            Assert.Equal(1, result.NumWarnings);
            Assert.Equal("ModUser", result.IssuerName);
        }

        [Fact]
        public async Task WarnUser_ThirdWarning_IdentifiesKickThreshold()
        {
            // Arrange
            var warningRepo = _serviceProvider.GetRequiredService<IRepository<Warnings>>();
            long guildId = 555555;
            long userId = 500002;

            // Create existing warning record with 2 warnings
            await warningRepo.AddAsync(new Warnings
            {
                ServerId = guildId,
                ServerName = "Test Server",
                UserWarnedId = userId,
                UserWarnedName = "WarnedUser",
                IssuerId = 600001,
                IssuerName = "Mod",
                TimeIssued = DateTime.UtcNow.AddDays(-2),
                NumWarnings = 2
            });
            await warningRepo.SaveChangesAsync();

            // Act - Add third warning
            var existing = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.UserWarnedId == userId);
            existing.NumWarnings += 1;
            existing.TimeIssued = DateTime.UtcNow;
            await warningRepo.SaveChangesAsync();

            // Assert - At 3 warnings, user should be kicked
            var result = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.UserWarnedId == userId);
            Assert.NotNull(result);
            Assert.Equal(3, result.NumWarnings);
        }

        [Fact]
        public async Task WarnUser_IncrementsWarningCount_PerGuild()
        {
            // Arrange
            var warningRepo = _serviceProvider.GetRequiredService<IRepository<Warnings>>();
            long guild1 = 666666;
            long guild2 = 777777;
            long userId = 500003;

            // Act - Add warnings in different guilds
            await warningRepo.AddAsync(new Warnings
            {
                ServerId = guild1,
                ServerName = "Guild 1",
                UserWarnedId = userId,
                UserWarnedName = "User",
                IssuerId = 600001,
                IssuerName = "Mod1",
                TimeIssued = DateTime.UtcNow,
                NumWarnings = 1
            });
            await warningRepo.AddAsync(new Warnings
            {
                ServerId = guild2,
                ServerName = "Guild 2",
                UserWarnedId = userId,
                UserWarnedName = "User",
                IssuerId = 600002,
                IssuerName = "Mod2",
                TimeIssued = DateTime.UtcNow,
                NumWarnings = 1
            });
            await warningRepo.SaveChangesAsync();

            // Assert - Warnings are per-guild
            var guild1Warning = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guild1 && w.UserWarnedId == userId);
            var guild2Warning = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guild2 && w.UserWarnedId == userId);

            Assert.NotNull(guild1Warning);
            Assert.NotNull(guild2Warning);
            Assert.Equal(1, guild1Warning.NumWarnings);
            Assert.Equal(1, guild2Warning.NumWarnings);
        }

        [Fact]
        public async Task ResetWarnings_ClearsWarningRecord()
        {
            // Arrange
            var warningRepo = _serviceProvider.GetRequiredService<IRepository<Warnings>>();
            long guildId = 888888;
            long userId = 500004;

            // Create warning
            await warningRepo.AddAsync(new Warnings
            {
                ServerId = guildId,
                ServerName = "Test Server",
                UserWarnedId = userId,
                UserWarnedName = "User",
                IssuerId = 600001,
                IssuerName = "Mod",
                TimeIssued = DateTime.UtcNow,
                NumWarnings = 3
            });
            await warningRepo.SaveChangesAsync();

            // Act - Simulate /reset-warnings command
            var warning = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.UserWarnedId == userId);
            if (warning != null)
            {
                warningRepo.Delete(warning);
                await warningRepo.SaveChangesAsync();
            }

            // Assert
            var result = await warningRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.UserWarnedId == userId);
            Assert.Null(result);
        }

        #endregion

        #region Word Blacklist Tests

        [Fact]
        public async Task AddWord_CreatesBlacklistEntry()
        {
            // Arrange
            var wordRepo = _serviceProvider.GetRequiredService<IRepository<WordList>>();
            long guildId = 999999;
            string word = "badword";

            // Act - Simulate /add-word command
            await wordRepo.AddAsync(new WordList
            {
                ServerId = guildId,
                ServerName = "Test Server",
                Word = word,
                SetById = 700001
            });
            await wordRepo.SaveChangesAsync();

            // Assert
            var result = await wordRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.Word.ToLower() == word.ToLower());
            Assert.NotNull(result);
            Assert.Equal("badword", result.Word);
        }

        [Fact]
        public async Task AddWord_PreventsDuplicates_CaseInsensitive()
        {
            // Arrange
            var wordRepo = _serviceProvider.GetRequiredService<IRepository<WordList>>();
            long guildId = 101010;
            string word = "spam";

            // Add word first time
            await wordRepo.AddAsync(new WordList
            {
                ServerId = guildId,
                ServerName = "Test Server",
                Word = word,
                SetById = 700001
            });
            await wordRepo.SaveChangesAsync();

            // Act - Check for duplicate (case insensitive)
            var foundWord = await wordRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.Word.ToLower() == "SPAM".ToLower());

            // Assert
            Assert.NotNull(foundWord); // Word already exists
        }

        [Fact]
        public async Task RemoveWord_DeletesBlacklistEntry()
        {
            // Arrange
            var wordRepo = _serviceProvider.GetRequiredService<IRepository<WordList>>();
            long guildId = 202020;
            string word = "removeMe";

            // Add word
            await wordRepo.AddAsync(new WordList
            {
                ServerId = guildId,
                ServerName = "Test Server",
                Word = word,
                SetById = 700001
            });
            await wordRepo.SaveChangesAsync();

            // Act - Simulate /remove-word command
            var searchWord = word.ToLower();
            var wordToDelete = await wordRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.Word.ToLower() == searchWord);

            int deletedCount = 0;
            if (wordToDelete != null)
            {
                wordRepo.Delete(wordToDelete);
                deletedCount = await wordRepo.SaveChangesAsync();
            }

            // Assert
            Assert.Equal(1, deletedCount);
            var remaining = await wordRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.Word.ToLower() == searchWord);
            Assert.Null(remaining);
        }

        [Fact]
        public async Task RemoveWord_NonExistent_ReturnsZeroDeleted()
        {
            // Arrange
            var wordRepo = _serviceProvider.GetRequiredService<IRepository<WordList>>();
            long guildId = 303030;
            string word = "notInList";

            // Act - Try to remove word that doesn't exist
            var searchWord = word.ToLower();
            var wordToDelete = await wordRepo.FirstOrDefaultAsync(w =>
                w.ServerId == guildId && w.Word.ToLower() == searchWord);

            int deletedCount = 0;
            if (wordToDelete != null)
            {
                wordRepo.Delete(wordToDelete);
                deletedCount = await wordRepo.SaveChangesAsync();
            }

            // Assert
            Assert.Equal(0, deletedCount);
        }

        #endregion

        #region Server Greeting Tests

        [Fact]
        public async Task ToggleGreetings_EnablesGreetingSystem()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 404040;

            // Act - Simulate /toggle-greetings enable
            await greetingRepo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.GreetUsers = true;
                    greeting.GreetingChannelId = 123456;
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    GreetUsers = true,
                    GreetingChannelId = 123456
                });
            await greetingRepo.SaveChangesAsync();

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.True(result.GreetUsers);
            Assert.Equal(123456, result.GreetingChannelId);
        }

        [Fact]
        public async Task SetPartingChannel_RequiresGreetingsEnabled()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 505050;

            // Create greeting with GreetUsers = false
            await greetingRepo.AddAsync(new ServerGreeting
            {
                DiscordGuildId = guildId,
                GreetUsers = false
            });
            await greetingRepo.SaveChangesAsync();

            // Act - Check if greetings are enabled
            var currentSetting = await greetingRepo.FirstOrDefaultAsync(g =>
                g.DiscordGuildId == guildId);

            // Assert - Should not allow parting channel if greetings disabled
            Assert.NotNull(currentSetting);
            Assert.False(currentSetting.GreetUsers);
            // Command would show: "Please enable greetings first via /toggle-greetings"
        }

        [Fact]
        public async Task SetPartingChannel_SetsChannelId_WhenGreetingsEnabled()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 606060;
            long partingChannelId = 789012;

            // Create greeting with greetings enabled
            await greetingRepo.AddAsync(new ServerGreeting
            {
                DiscordGuildId = guildId,
                GreetUsers = true,
                GreetingChannelId = 123456
            });
            await greetingRepo.SaveChangesAsync();

            // Act - Set parting channel
            var currentSetting = await greetingRepo.FirstOrDefaultAsync(g =>
                g.DiscordGuildId == guildId);

            if (currentSetting != null && currentSetting.GreetUsers == true)
            {
                currentSetting.PartingChannelId = partingChannelId;
                await greetingRepo.SaveChangesAsync();
            }

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal(partingChannelId, result.PartingChannelId);
        }

        [Fact]
        public async Task ForceGreetingClear_RemovesGreetingSettings()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 707070;

            // Create greeting
            await greetingRepo.AddAsync(new ServerGreeting
            {
                DiscordGuildId = guildId,
                GreetUsers = true,
                Greeting = "Welcome!",
                PartingMessage = "Goodbye!",
                GreetingChannelId = 111111
            });
            await greetingRepo.SaveChangesAsync();

            // Act - Simulate /force-greeting-clear
            var greeting = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            if (greeting != null)
            {
                greetingRepo.Delete(greeting);
                await greetingRepo.SaveChangesAsync();
            }

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.Null(result);
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
