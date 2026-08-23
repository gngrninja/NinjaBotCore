using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Crafting;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CraftTicketConcurrencyTests
    {
        [Fact]
        public async Task ClaimTicket_ConditionalUpdateAllowsOnlyFirstCrafter()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            long ticketId;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var ticket = new CraftTicket
                {
                    ItemName = "Consecrated Alloy",
                    Status = "Open",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24),
                    RequesterId = 1,
                    RequesterName = "Requester",
                    GuildId = 10,
                    ChannelId = 20,
                    MessageId = 30
                };
                seed.CraftTickets.Add(ticket);
                await seed.SaveChangesAsync();
                ticketId = ticket.Id;
            }

            await using var firstContext = new NinjaBotEntities(options);
            var first = await CraftTicketUpdater.ClaimTicketAsync(
                firstContext, ticketId, 2, "First", DateTime.UtcNow);
            await using var secondContext = new NinjaBotEntities(options);
            var second = await CraftTicketUpdater.ClaimTicketAsync(
                secondContext, ticketId, 3, "Second", DateTime.UtcNow);

            Assert.Null(first.Error);
            Assert.Equal(2, first.Ticket.CrafterId);
            Assert.NotNull(second.Error);
            Assert.Null(second.Ticket);
            await using var verify = new NinjaBotEntities(options);
            var saved = await verify.CraftTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == ticketId);
            Assert.Equal(2, saved.CrafterId);
            Assert.Equal("Claimed", saved.Status);
        }

        [Fact]
        public async Task ClaimTicket_RequesterCannotClaimOwnTicket()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            await using var context = new NinjaBotEntities(options);
            await context.Database.EnsureCreatedAsync();
            var ticket = new CraftTicket
            {
                ItemName = "Vicious Flask",
                Status = "Open",
                CreatedAt = DateTime.UtcNow,
                RequesterId = 9,
                RequesterName = "Requester",
                GuildId = 10,
                ChannelId = 20,
                MessageId = 30
            };
            context.CraftTickets.Add(ticket);
            await context.SaveChangesAsync();

            var result = await CraftTicketUpdater.ClaimTicketAsync(
                context, ticket.Id, 9, "Requester", DateTime.UtcNow);

            Assert.Null(result.Ticket);
            Assert.Contains("own", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        [Fact]
        public async Task FinalizePendingProfession_ConditionalUpdateAllowsOnlyFirstSelection()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            long ticketId;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var ticket = NewTicket("PendingProfession");
                seed.CraftTickets.Add(ticket);
                await seed.SaveChangesAsync();
                ticketId = ticket.Id;
            }

            await using var firstContext = new NinjaBotEntities(options);
            var first = await CraftTicketUpdater.FinalizePendingProfessionAsync(
                firstContext, ticketId, 1, "Alchemy", DateTime.UtcNow.AddHours(24));
            await using var secondContext = new NinjaBotEntities(options);
            var second = await CraftTicketUpdater.FinalizePendingProfessionAsync(
                secondContext, ticketId, 1, "Blacksmithing", DateTime.UtcNow.AddHours(24));

            Assert.Null(first.Error);
            Assert.Equal("Alchemy", first.Ticket.Profession);
            Assert.NotNull(second.Error);
            Assert.Null(second.Ticket);
            await using var verify = new NinjaBotEntities(options);
            var saved = await verify.CraftTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == ticketId);
            Assert.Equal("Open", saved.Status);
            Assert.Equal("Alchemy", saved.Profession);
        }

        [Fact]
        public async Task MarkCraftedCannotRegressACompletedTicket()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            long ticketId;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var ticket = NewTicket("Claimed");
                ticket.CrafterId = 2;
                seed.CraftTickets.Add(ticket);
                await seed.SaveChangesAsync();
                ticketId = ticket.Id;
            }

            await using var completeContext = new NinjaBotEntities(options);
            var completed = await CraftTicketUpdater.CompleteTicketAsync(
                completeContext, ticketId, 1, DateTime.UtcNow, allowOpen: false);
            await using var craftedContext = new NinjaBotEntities(options);
            var crafted = await CraftTicketUpdater.MarkCraftedAsync(
                craftedContext, ticketId, 2, DateTime.UtcNow);

            Assert.Null(completed.Error);
            Assert.NotNull(crafted.Error);
            Assert.Null(crafted.Ticket);
            await using var verify = new NinjaBotEntities(options);
            var saved = await verify.CraftTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == ticketId);
            Assert.Equal("Complete", saved.Status);
            Assert.NotNull(saved.CompletedAt);
        }

        [Fact]
        public async Task CompleteCannotOverwriteAnExpiredTicket()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            long ticketId;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var ticket = NewTicket("Claimed");
                ticket.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                seed.CraftTickets.Add(ticket);
                await seed.SaveChangesAsync();
                ticketId = ticket.Id;
            }

            await using var expireContext = new NinjaBotEntities(options);
            var expired = await CraftTicketUpdater.ExpireTicketAsync(
                expireContext, ticketId, "Claimed", DateTime.UtcNow);
            await using var completeContext = new NinjaBotEntities(options);
            var completed = await CraftTicketUpdater.CompleteTicketAsync(
                completeContext, ticketId, 1, DateTime.UtcNow, allowOpen: false);

            Assert.NotNull(expired);
            Assert.NotNull(completed.Error);
            await using var verify = new NinjaBotEntities(options);
            var saved = await verify.CraftTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == ticketId);
            Assert.Equal("Expired", saved.Status);
        }

        [Fact]
        public async Task CancelCannotOverwriteACompletionCommittedAfterAStaleRead()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            long ticketId;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var ticket = NewTicket("Claimed");
                seed.CraftTickets.Add(ticket);
                await seed.SaveChangesAsync();
                ticketId = ticket.Id;
            }

            await using var staleCancelContext = new NinjaBotEntities(options);
            _ = await staleCancelContext.CraftTickets.SingleAsync(ticket => ticket.Id == ticketId);
            await using var completeContext = new NinjaBotEntities(options);
            var completed = await CraftTicketUpdater.CompleteTicketAsync(
                completeContext, ticketId, 1, DateTime.UtcNow, allowOpen: false);
            var cancelled = await CraftTicketUpdater.CancelTicketAsync(
                staleCancelContext, ticketId, 1);

            Assert.Null(completed.Error);
            Assert.NotNull(cancelled.Error);
            await using var verify = new NinjaBotEntities(options);
            var saved = await verify.CraftTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == ticketId);
            Assert.Equal("Complete", saved.Status);
        }

        [Fact]
        public async Task FinalizePendingProfessionRejectsExpiredTicket()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            long ticketId;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var ticket = NewTicket("PendingProfession");
                ticket.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                seed.CraftTickets.Add(ticket);
                await seed.SaveChangesAsync();
                ticketId = ticket.Id;
            }

            await using var finalizeContext = new NinjaBotEntities(options);
            var result = await CraftTicketUpdater.FinalizePendingProfessionAsync(
                finalizeContext,
                ticketId,
                1,
                "Alchemy",
                DateTime.UtcNow.AddHours(48));

            Assert.Null(result.Ticket);
            Assert.Contains("expired", result.Error, StringComparison.OrdinalIgnoreCase);
            await using var verify = new NinjaBotEntities(options);
            var saved = await verify.CraftTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == ticketId);
            Assert.Equal("PendingProfession", saved.Status);
        }

        private static CraftTicket NewTicket(string status) => new()
        {
            ItemName = "Consecrated Alloy",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            RequesterId = 1,
            RequesterName = "Requester",
            GuildId = 10,
            ChannelId = 20,
            MessageId = 30
        };
    }
}
