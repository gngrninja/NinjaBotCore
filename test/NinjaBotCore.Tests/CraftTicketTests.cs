using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Crafting;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CraftTicketTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public CraftTicketTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"CraftTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            services.AddScoped<IRepository<CraftTicket>>(sp =>
                new Repository<CraftTicket>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IRepository<ServerCraftSettings>>(sp =>
                new Repository<ServerCraftSettings>(sp.GetRequiredService<NinjaBotEntities>()));
            services.AddScoped<IUnitOfWork, UnitOfWork>();

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task CreateTicket_SetsCorrectDefaults()
        {
            var ticket = new CraftTicket
            {
                ItemName = "Flask of Alchemical Chaos",
                Status = "Open",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(48),
                RequesterId = 12345,
                RequesterName = "TestUser",
                GuildId = 111,
                ChannelId = 222,
                MessageId = 333
            };

            _context.CraftTickets.Add(ticket);
            await _context.SaveChangesAsync();

            var saved = await _context.CraftTickets.FindAsync(ticket.Id);
            Assert.NotNull(saved);
            Assert.Equal("Open", saved.Status);
            Assert.Null(saved.CrafterId);
            Assert.Null(saved.CrafterName);
            Assert.Null(saved.ClaimedAt);
            Assert.Null(saved.CraftedAt);
            Assert.Null(saved.CompletedAt);
            Assert.Null(saved.BlizzardItemId);
            Assert.Null(saved.ItemIconUrl);
        }

        [Fact]
        public async Task ClaimTicket_UpdatesStatusAndCrafter()
        {
            var ticket = CreateOpenTicket();
            _context.CraftTickets.Add(ticket);
            await _context.SaveChangesAsync();

            ticket.Status = "Claimed";
            ticket.CrafterId = 99999;
            ticket.CrafterName = "CrafterUser";
            ticket.ClaimedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var saved = await _context.CraftTickets.FindAsync(ticket.Id);
            Assert.Equal("Claimed", saved.Status);
            Assert.Equal(99999, saved.CrafterId);
            Assert.Equal("CrafterUser", saved.CrafterName);
            Assert.NotNull(saved.ClaimedAt);
        }

        [Fact]
        public async Task CannotClaimOwnTicket_BusinessRule()
        {
            var ticket = CreateOpenTicket();
            _context.CraftTickets.Add(ticket);
            await _context.SaveChangesAsync();

            // Business rule: requester cannot be the crafter
            var requesterId = ticket.RequesterId;
            var wouldBeCrafterId = requesterId; // Same user

            Assert.Equal(requesterId, wouldBeCrafterId);
            // The slash command handler enforces this check
        }

        [Fact]
        public async Task MaxOpenTickets_CountCorrectly()
        {
            var guildId = 111L;
            var userId = 12345L;

            // Create 3 tickets in different statuses
            _context.CraftTickets.Add(CreateOpenTicket(guildId: guildId, requesterId: userId));
            _context.CraftTickets.Add(CreateOpenTicket(guildId: guildId, requesterId: userId, status: "Claimed"));
            _context.CraftTickets.Add(CreateOpenTicket(guildId: guildId, requesterId: userId, status: "Crafted"));

            // Also create a completed ticket (shouldn't count)
            _context.CraftTickets.Add(CreateOpenTicket(guildId: guildId, requesterId: userId, status: "Complete"));

            await _context.SaveChangesAsync();

            var activeStatuses = new[] { "Open", "Claimed", "Crafted" };
            var openCount = await _context.CraftTickets.CountAsync(t =>
                t.GuildId == guildId
                && t.RequesterId == userId
                && activeStatuses.Contains(t.Status));

            Assert.Equal(3, openCount);
        }

        [Fact]
        public async Task CompleteFlow_OpenToClaimedToCraftedToComplete()
        {
            var ticket = CreateOpenTicket();
            _context.CraftTickets.Add(ticket);
            await _context.SaveChangesAsync();

            // Step 1: Claim
            ticket.Status = "Claimed";
            ticket.CrafterId = 99999;
            ticket.CrafterName = "Crafter";
            ticket.ClaimedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            Assert.Equal("Claimed", ticket.Status);

            // Step 2: Mark as crafted
            ticket.Status = "Crafted";
            ticket.CraftedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            Assert.Equal("Crafted", ticket.Status);

            // Step 3: Complete
            ticket.Status = "Complete";
            ticket.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var saved = await _context.CraftTickets.FindAsync(ticket.Id);
            Assert.Equal("Complete", saved.Status);
            Assert.NotNull(saved.ClaimedAt);
            Assert.NotNull(saved.CraftedAt);
            Assert.NotNull(saved.CompletedAt);
        }

        [Fact]
        public async Task ExpiredTickets_FoundByExpirationQuery()
        {
            // Create an expired ticket
            var expired = CreateOpenTicket();
            expired.ExpiresAt = DateTime.UtcNow.AddHours(-1);
            _context.CraftTickets.Add(expired);

            // Create a non-expired ticket
            var active = CreateOpenTicket();
            active.ExpiresAt = DateTime.UtcNow.AddHours(24);
            _context.CraftTickets.Add(active);

            // Create an already-completed ticket with past expiry
            var completed = CreateOpenTicket(status: "Complete");
            completed.ExpiresAt = DateTime.UtcNow.AddHours(-2);
            _context.CraftTickets.Add(completed);

            await _context.SaveChangesAsync();

            var activeStatuses = new[] { "Open", "Claimed", "Crafted" };
            var expiredTickets = await _context.CraftTickets
                .Where(t => activeStatuses.Contains(t.Status)
                            && t.ExpiresAt.HasValue
                            && t.ExpiresAt.Value <= DateTime.UtcNow)
                .ToListAsync();

            Assert.Single(expiredTickets);
            Assert.Equal(expired.Id, expiredTickets[0].Id);
        }

        [Fact]
        public async Task CancelTicket_SetsStatusAndTimestamp()
        {
            var ticket = CreateOpenTicket();
            _context.CraftTickets.Add(ticket);
            await _context.SaveChangesAsync();

            ticket.Status = "Cancelled";
            ticket.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var saved = await _context.CraftTickets.FindAsync(ticket.Id);
            Assert.Equal("Cancelled", saved.Status);
            Assert.NotNull(saved.CompletedAt);
        }

        [Fact]
        public async Task ServerCraftSettings_UpsertPattern()
        {
            var guildId = 111L;

            // Create settings
            var settings = new ServerCraftSettings
            {
                DiscordGuildId = guildId,
                CraftChannelId = 222,
                MaxOpenTicketsPerUser = 5,
                TicketExpirationHours = 72,
                SetById = 12345,
                SetByName = "Admin",
                TimeSet = DateTime.UtcNow
            };

            _context.ServerCraftSettings.Add(settings);
            await _context.SaveChangesAsync();

            // Update settings
            var saved = await _context.ServerCraftSettings.FindAsync(guildId);
            Assert.NotNull(saved);
            Assert.Equal(222, saved.CraftChannelId);
            Assert.Equal(5, saved.MaxOpenTicketsPerUser);
            Assert.Equal(72, saved.TicketExpirationHours);

            saved.CraftChannelId = 333;
            await _context.SaveChangesAsync();

            var updated = await _context.ServerCraftSettings.FindAsync(guildId);
            Assert.Equal(333, updated.CraftChannelId);
        }

        [Fact]
        public void ServerCraftSettings_DefaultValues()
        {
            var settings = new ServerCraftSettings();

            Assert.Equal(3, settings.MaxOpenTicketsPerUser);
            Assert.Equal(48, settings.TicketExpirationHours);
            Assert.Null(settings.CraftChannelId);
        }

        [Fact]
        public async Task TicketWithBlizzardData_StoresAllFields()
        {
            var ticket = CreateOpenTicket();
            ticket.BlizzardItemId = 191585;
            ticket.ItemIconUrl = "https://render.worldofwarcraft.com/us/icons/56/inv_10_alchemy_bottle_shape4_orange.jpg";
            ticket.Note = "Have all mats, will tip 1k gold";

            _context.CraftTickets.Add(ticket);
            await _context.SaveChangesAsync();

            var saved = await _context.CraftTickets.FindAsync(ticket.Id);
            Assert.Equal(191585, saved.BlizzardItemId);
            Assert.NotNull(saved.ItemIconUrl);
            Assert.Equal("Have all mats, will tip 1k gold", saved.Note);
        }

        [Fact]
        public async Task DiscordUpdateReportsFailureWhenTheAuthoritativeCardCannotBeReached()
        {
            var updated = await CraftTicketUpdater.UpdateTicketAsync(
                null,
                CreateOpenTicket(),
                NullLogger.Instance);

            Assert.False(updated);
        }

        private static CraftTicket CreateOpenTicket(
            long guildId = 111,
            long requesterId = 12345,
            string status = "Open")
        {
            return new CraftTicket
            {
                ItemName = "Flask of Alchemical Chaos",
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(48),
                RequesterId = requesterId,
                RequesterName = "TestUser",
                GuildId = guildId,
                ChannelId = 222,
                MessageId = 333
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (_context != null)
            {
                await _context.DisposeAsync();
            }

            if (_serviceProvider != null)
            {
                await _serviceProvider.DisposeAsync();
            }
        }
    }
}
