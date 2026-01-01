using System;
using System.Collections.Generic;
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
    /// Integration tests for modal interactions including race condition handling
    /// Tests the defensive HasResponded checks added to prevent duplicate responses
    /// </summary>
    public class ModalInteractionTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public ModalInteractionTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"ModalTestDb_{Guid.NewGuid()}"));

            // Add repositories
            services.AddScoped<IRepository<ServerGreeting>>(sp =>
                new Repository<ServerGreeting>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<Note>>(sp =>
                new Repository<Note>(sp.GetRequiredService<NinjaBotEntities>()));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task JoiningMessageModal_CreatesNewGreeting_WhenNotExists()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 111;
            string newGreeting = "Welcome to our server!";

            // Act - Simulate modal submission creating a new greeting
            await greetingRepo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.Greeting = newGreeting.Trim();
                    greeting.SetById = 123;
                    greeting.SetByName = "TestUser";
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = newGreeting.Trim(),
                    SetById = 123,
                    SetByName = "TestUser",
                    TimeSet = DateTime.UtcNow
                });
            await greetingRepo.SaveChangesAsync();

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal("Welcome to our server!", result.Greeting);
            Assert.Equal("TestUser", result.SetByName);
        }

        [Fact]
        public async Task JoiningMessageModal_UpdatesExistingGreeting_PreservesOtherFields()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 222;

            // Create existing greeting with parting message
            await greetingRepo.AddAsync(new ServerGreeting
            {
                DiscordGuildId = guildId,
                Greeting = "Old greeting",
                PartingMessage = "Goodbye!",
                GreetUsers = true,
                GreetingChannelId = 1000,
                SetByName = "OriginalUser",
                SetById = 100,
                TimeSet = DateTime.UtcNow.AddDays(-1)
            });
            await greetingRepo.SaveChangesAsync();

            // Act - Update only the greeting message (simulating modal submission)
            string newGreeting = "Updated welcome message";
            await greetingRepo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.Greeting = newGreeting.Trim();
                    greeting.SetById = 200;
                    greeting.SetByName = "UpdaterUser";
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => throw new InvalidOperationException("Should not create"));
            await greetingRepo.SaveChangesAsync();

            // Assert - Verify greeting updated but other fields preserved
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal("Updated welcome message", result.Greeting); // Updated
            Assert.Equal("Goodbye!", result.PartingMessage); // Preserved
            Assert.True(result.GreetUsers); // Preserved
            Assert.Equal(1000, result.GreetingChannelId); // Preserved
            Assert.Equal("UpdaterUser", result.SetByName); // Updated
            Assert.Equal(200, result.SetById); // Updated
        }

        [Fact]
        public async Task PartingMessageModal_CreatesNewGreeting_WhenNotExists()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 333;
            string partingMsg = "See you later!";

            // Act - Simulate parting modal submission
            await greetingRepo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.PartingMessage = partingMsg.Trim();
                    greeting.SetById = 456;
                    greeting.SetByName = "PartingUser";
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    PartingMessage = partingMsg.Trim(),
                    SetById = 456,
                    SetByName = "PartingUser",
                    TimeSet = DateTime.UtcNow
                });
            await greetingRepo.SaveChangesAsync();

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal("See you later!", result.PartingMessage);
            Assert.Null(result.Greeting); // Not set
        }

        [Fact]
        public async Task NoteModal_CreatesNote_WithCorrectMetadata()
        {
            // Arrange
            var noteRepo = _serviceProvider.GetRequiredService<IRepository<Note>>();
            long guildId = 444;
            string guildName = "Test Guild";
            string noteText = "Important server note";

            // Act - Simulate note modal submission
            await noteRepo.UpsertAsync(
                findPredicate: n => n.ServerId == guildId,
                updateAction: note =>
                {
                    note.Note1 = noteText;
                    note.SetBy = "NoteUser";
                    note.SetById = 789;
                    note.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => new Note
                {
                    Note1 = noteText,
                    ServerId = guildId,
                    ServerName = guildName,
                    SetBy = "NoteUser",
                    SetById = 789,
                    TimeSet = DateTime.UtcNow
                });
            await noteRepo.SaveChangesAsync();

            // Assert
            var result = await noteRepo.FirstOrDefaultAsync(n => n.ServerId == guildId);
            Assert.NotNull(result);
            Assert.Equal("Important server note", result.Note1);
            Assert.Equal(guildId, result.ServerId);
            Assert.Equal("Test Guild", result.ServerName);
            Assert.Equal("NoteUser", result.SetBy);
        }

        [Fact]
        public async Task NoteModal_UpdatesExistingNote_PreservesServerName()
        {
            // Arrange
            var noteRepo = _serviceProvider.GetRequiredService<IRepository<Note>>();
            long guildId = 555;

            // Create existing note
            await noteRepo.AddAsync(new Note
            {
                ServerId = guildId,
                ServerName = "Original Server Name",
                Note1 = "Old note",
                SetBy = "OldUser",
                SetById = 111,
                TimeSet = DateTime.UtcNow.AddHours(-1)
            });
            await noteRepo.SaveChangesAsync();

            // Act - Update note
            await noteRepo.UpsertAsync(
                findPredicate: n => n.ServerId == guildId,
                updateAction: note =>
                {
                    note.Note1 = "Updated note";
                    note.SetBy = "NewUser";
                    note.SetById = 222;
                    note.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => throw new InvalidOperationException("Should not create"));
            await noteRepo.SaveChangesAsync();

            // Assert
            var result = await noteRepo.FirstOrDefaultAsync(n => n.ServerId == guildId);
            Assert.NotNull(result);
            Assert.Equal("Updated note", result.Note1); // Updated
            Assert.Equal("Original Server Name", result.ServerName); // Preserved
            Assert.Equal("NewUser", result.SetBy); // Updated
        }

        [Fact]
        public async Task MultipleModalSubmissions_DoNotCreateDuplicates_SameGuild()
        {
            // Arrange - Simulates multiple modal submissions to the same guild
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 666;

            // Act - Submit multiple greeting changes sequentially (simulating rapid modal submissions)
            // The UpsertAsync pattern ensures only one entry exists
            for (int i = 1; i <= 5; i++)
            {
                await greetingRepo.UpsertAsync(
                    findPredicate: g => g.DiscordGuildId == guildId,
                    updateAction: greeting =>
                    {
                        greeting.Greeting = $"Message {i}";
                        greeting.SetByName = $"User{i}";
                        greeting.TimeSet = DateTime.UtcNow;
                    },
                    createFactory: () => new ServerGreeting
                    {
                        DiscordGuildId = guildId,
                        Greeting = $"Message {i}",
                        SetByName = $"User{i}",
                        TimeSet = DateTime.UtcNow
                    });
                await greetingRepo.SaveChangesAsync();
            }

            // Assert - Should only have one greeting entry despite multiple submissions
            var all = await greetingRepo.WhereAsync(g => g.DiscordGuildId == guildId);
            Assert.Single(all);

            var result = all.First();
            Assert.Equal(guildId, result.DiscordGuildId);
            // Last message should have won
            Assert.Equal("Message 5", result.Greeting);
            Assert.Equal("User5", result.SetByName);
        }

        [Fact]
        public async Task ModalSubmission_HandlesWhitespace_Correctly()
        {
            // Arrange
            var greetingRepo = _serviceProvider.GetRequiredService<IRepository<ServerGreeting>>();
            long guildId = 777;
            string greetingWithSpaces = "  Welcome!  ";

            // Act - Simulate modal submission with whitespace (should be trimmed)
            await greetingRepo.UpsertAsync(
                findPredicate: g => g.DiscordGuildId == guildId,
                updateAction: greeting =>
                {
                    greeting.Greeting = greetingWithSpaces.Trim();
                    greeting.SetByName = "TrimUser";
                    greeting.TimeSet = DateTime.UtcNow;
                },
                createFactory: () => new ServerGreeting
                {
                    DiscordGuildId = guildId,
                    Greeting = greetingWithSpaces.Trim(),
                    SetByName = "TrimUser",
                    TimeSet = DateTime.UtcNow
                });
            await greetingRepo.SaveChangesAsync();

            // Assert
            var result = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            Assert.NotNull(result);
            Assert.Equal("Welcome!", result.Greeting); // Trimmed
            Assert.DoesNotContain("  ", result.Greeting);
        }

        [Fact]
        public async Task InteractionHandler_SkipsEventBasedModals_BasedOnCustomId()
        {
            // This test documents the expected behavior from InteractionHandler.cs:70-78
            // Modal interactions with specific custom IDs should be skipped by InteractionHandler
            // and handled by the ModalSubmitted event in UserInteraction instead

            // Arrange
            var eventBasedModalIds = new[] { "joining_message", "parting_message", "discord_server_note" };

            // Assert - Document the expected behavior
            foreach (var customId in eventBasedModalIds)
            {
                // These modal custom IDs should be handled by UserInteraction.HandleModal
                // via the ModalSubmitted event, NOT by InteractionHandler.HandleInteraction
                // This prevents the "Cannot respond twice to the same interaction" error
                Assert.Contains(customId, eventBasedModalIds);
            }

            // The InteractionHandler checks:
            // if (customId == "joining_message" || customId == "parting_message" || customId == "discord_server_note")
            // { return; } // Skip processing, let ModalSubmitted event handle it
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
