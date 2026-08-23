using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;

namespace NinjaBotCore.Modules.Interactions.Crafting
{
    /// <summary>
    /// Shared utility for craft ticket operations (DB + Discord).
    /// Used by CraftComponentHandlers, API endpoints, and CraftTicketExpirationService.
    /// </summary>
    public static class CraftTicketUpdater
    {
        /// <summary>
        /// Atomically claims an open ticket. The status predicate is part of the SQL UPDATE,
        /// so competing crafters cannot both receive a successful claim.
        /// </summary>
        public static async Task<(CraftTicket Ticket, string Error)> ClaimTicketAsync(
            NinjaBotEntities db,
            long ticketId,
            long crafterId,
            string crafterName,
            DateTime now)
        {
            var extendedExpiry = now.AddHours(72);
            var rows = await db.CraftTickets
                .Where(ticket =>
                    ticket.Id == ticketId
                    && ticket.Status == "Open"
                    && ticket.RequesterId != crafterId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(ticket => ticket.Status, "Claimed")
                    .SetProperty(ticket => ticket.CrafterId, crafterId)
                    .SetProperty(ticket => ticket.CrafterName, crafterName)
                    .SetProperty(ticket => ticket.ClaimedAt, now)
                    .SetProperty(
                        ticket => ticket.ExpiresAt,
                        ticket => ticket.ExpiresAt.HasValue ? extendedExpiry : (DateTime?)null));

            db.ChangeTracker.Clear();
            var current = await db.CraftTickets
                .AsNoTracking()
                .FirstOrDefaultAsync(ticket => ticket.Id == ticketId);
            if (rows == 1)
            {
                return (current, null);
            }

            if (current == null)
            {
                return (null, "Ticket not found.");
            }

            if (current.RequesterId == crafterId)
            {
                return (null, "You can't claim your own crafting request.");
            }

            return (null, "This ticket has already been claimed or is no longer open.");
        }

        public static async Task<(CraftTicket Ticket, string Error)> FinalizePendingProfessionAsync(
            NinjaBotEntities db,
            long ticketId,
            long requesterId,
            string profession,
            DateTime expiresAt)
        {
            var now = DateTime.UtcNow;
            var rows = await db.CraftTickets
                .Where(ticket =>
                    ticket.Id == ticketId
                    && ticket.Status == "PendingProfession"
                    && ticket.RequesterId == requesterId
                    && ticket.ExpiresAt.HasValue
                    && ticket.ExpiresAt.Value > now)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(ticket => ticket.Profession, profession)
                    .SetProperty(ticket => ticket.Status, "Open")
                    .SetProperty(ticket => ticket.ExpiresAt, expiresAt));

            db.ChangeTracker.Clear();
            var current = await db.CraftTickets.AsNoTracking()
                .FirstOrDefaultAsync(ticket => ticket.Id == ticketId);
            if (rows == 1) return (current, null);
            if (current == null) return (null, "Ticket not found.");
            if (current.RequesterId != requesterId) return (null, "This isn't your ticket.");
            if (current.Status == "PendingProfession"
                && (!current.ExpiresAt.HasValue || current.ExpiresAt.Value <= now))
            {
                return (null, "This pending crafting request has expired. Start a new request.");
            }
            return (null, "This ticket has already been processed.");
        }

        public static async Task<(CraftTicket Ticket, string Error)> MarkCraftedAsync(
            NinjaBotEntities db,
            long ticketId,
            long crafterId,
            DateTime now)
        {
            var rows = await db.CraftTickets
                .Where(ticket =>
                    ticket.Id == ticketId
                    && ticket.Status == "Claimed"
                    && ticket.CrafterId == crafterId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(ticket => ticket.Status, "Crafted")
                    .SetProperty(ticket => ticket.CraftedAt, now));

            db.ChangeTracker.Clear();
            var current = await db.CraftTickets.AsNoTracking()
                .FirstOrDefaultAsync(ticket => ticket.Id == ticketId);
            if (rows == 1) return (current, null);
            if (current == null) return (null, "Ticket not found.");
            if (current.CrafterId != crafterId) return (null, "Only the crafter can mark this as crafted.");
            return (null, "This ticket is not in a craftable state.");
        }

        public static async Task<(CraftTicket Ticket, string Error)> CompleteTicketAsync(
            NinjaBotEntities db,
            long ticketId,
            long requesterId,
            DateTime now,
            bool allowOpen)
        {
            var rows = await db.CraftTickets
                .Where(ticket =>
                    ticket.Id == ticketId
                    && ticket.RequesterId == requesterId
                    && (ticket.Status == "Claimed"
                        || ticket.Status == "Crafted"
                        || (allowOpen && ticket.Status == "Open")))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(ticket => ticket.Status, "Complete")
                    .SetProperty(ticket => ticket.CompletedAt, now));

            db.ChangeTracker.Clear();
            var current = await db.CraftTickets.AsNoTracking()
                .FirstOrDefaultAsync(ticket => ticket.Id == ticketId);
            if (rows == 1) return (current, null);
            if (current == null) return (null, "Ticket not found.");
            if (current.RequesterId != requesterId) return (null, "Only the requester can confirm trade completion.");
            return (null, "This ticket cannot be completed in its current state.");
        }

        public static async Task<CraftTicket> ExpireTicketAsync(
            NinjaBotEntities db,
            long ticketId,
            string expectedStatus,
            DateTime now,
            CancellationToken cancellationToken = default)
        {
            var rows = await db.CraftTickets
                .Where(ticket =>
                    ticket.Id == ticketId
                    && ticket.Status == expectedStatus
                    && ticket.ExpiresAt.HasValue
                    && ticket.ExpiresAt.Value <= now)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(ticket => ticket.Status, "Expired")
                    .SetProperty(ticket => ticket.CompletedAt, now),
                    cancellationToken);
            if (rows != 1) return null;

            db.ChangeTracker.Clear();
            return await db.CraftTickets.AsNoTracking()
                .FirstOrDefaultAsync(ticket => ticket.Id == ticketId, cancellationToken);
        }

        /// <summary>
        /// Cancels a craft ticket. Validates state and updates the DB.
        /// Called by both the Discord component handler and the web API endpoint.
        /// </summary>
        /// <param name="db">Database context</param>
        /// <param name="ticketId">Ticket to cancel</param>
        /// <param name="cancelledByUserId">Discord user ID of the person cancelling</param>
        /// <param name="isAdmin">If true, allows cancellation by guild admins (not just the requester)</param>
        /// <returns>The cancelled ticket and who cancelled it, or an error message</returns>
        public static async Task<(CraftTicket? Ticket, string CancelledBy, string? Error)> CancelTicketAsync(
            NinjaBotEntities db, long ticketId, long cancelledByUserId, bool isAdmin = false)
        {
            var ticket = await db.CraftTickets.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == ticketId);

            if (ticket == null)
                return (null, "", "Ticket not found.");

            var isRequester = ticket.RequesterId == cancelledByUserId;
            var isCrafter = ticket.CrafterId == cancelledByUserId;

            if (!isRequester && !isCrafter && !isAdmin)
                return (null, "", "Only the requester or crafter can cancel this ticket.");

            if (ticket.Status is "Complete" or "Cancelled" or "Expired")
                return (null, "", $"This ticket is already {ticket.Status.ToLower()}.");

            int rows;
            string cancelledBy;
            if (isCrafter && !isRequester && ticket.Status is ("Claimed" or "Crafted"))
            {
                rows = await db.CraftTickets
                    .Where(current =>
                        current.Id == ticketId
                        && current.Status == ticket.Status
                        && current.CrafterId == cancelledByUserId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(current => current.Status, "Open")
                        .SetProperty(current => current.CrafterId, (long?)null)
                        .SetProperty(current => current.CrafterName, (string)null)
                        .SetProperty(current => current.ClaimedAt, (DateTime?)null)
                        .SetProperty(current => current.CraftedAt, (DateTime?)null));
                cancelledBy = "the crafter (ticket released back to open)";
            }
            else
            {
                var completedAt = DateTime.UtcNow;
                rows = await db.CraftTickets
                    .Where(current =>
                        current.Id == ticketId
                        && current.Status == ticket.Status
                        && current.Status != "Complete"
                        && current.Status != "Cancelled"
                        && current.Status != "Expired"
                        && (isAdmin
                            || current.RequesterId == cancelledByUserId
                            || current.CrafterId == cancelledByUserId))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(current => current.Status, "Cancelled")
                        .SetProperty(current => current.CompletedAt, completedAt));
                cancelledBy = isRequester ? "the requester" : "an admin";
            }

            if (rows != 1)
                return (null, "", "This ticket changed before the cancellation could be applied. Refresh and try again.");

            db.ChangeTracker.Clear();
            var updated = await db.CraftTickets.AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == ticketId);
            return (updated, cancelledBy, null);
        }

        public static AllowedMentions BuildInitialThreadAllowedMentions(
            ulong requesterId,
            ulong? professionRoleId = null) =>
            new()
            {
                UserIds = new List<ulong> { requesterId },
                RoleIds = professionRoleId.HasValue
                    ? new List<ulong> { professionRoleId.Value }
                    : new List<ulong>()
            };

        public static AllowedMentions BuildThreadNotificationAllowedMentions(
            params ulong[] userIds) =>
            new()
            {
                UserIds = (userIds ?? Array.Empty<ulong>())
                    .Where(userId => userId > 0)
                    .Distinct()
                    .ToList(),
                RoleIds = new List<ulong>()
            };

        /// <summary>
        /// Updates a craft ticket's Discord messages.
        /// Attempts to update the authoritative channel card and the thread card.
        /// Returns true only when the authoritative channel card was refreshed.
        /// Optionally posts a text notification in the thread and/or archives the thread.
        /// </summary>
        public static async Task<bool> UpdateTicketAsync(
            DiscordShardedClient client,
            CraftTicket ticket,
            ILogger logger,
            string? threadNotification = null,
            bool archiveThread = false)
        {
            try
            {
                var guild = client.GetGuild((ulong)ticket.GuildId);
                if (guild == null) return false;

                var channel = guild.GetTextChannel((ulong)ticket.ChannelId);
                if (channel == null) return false;

                var builtCard = CraftEmbedBuilder.BuildTicketCard(ticket).Build();
                var builtThreadCard = CraftEmbedBuilder.BuildTicketCard(
                    ticket,
                    CraftEmbedBuilder.BuildThreadPreface(ticket)).Build();

                // Upgrade or refresh the channel message as a mention-safe V2 card.
                var channelUpdated = false;
                var message = await channel.GetMessageAsync((ulong)ticket.MessageId);
                if (message is IUserMessage userMessage)
                {
                    await userMessage.ModifyAsync(msg =>
                    {
                        msg.Content = string.Empty;
                        msg.Embed = null;
                        msg.Components = builtCard;
                        msg.Flags = MessageFlags.ComponentsV2;
                        msg.AllowedMentions = AllowedMentions.None;
                    });
                    channelUpdated = true;
                }

                // Thread operations — resolve thread once and reuse
                SocketThreadChannel? ticketThread = null;
                if (ticket.ThreadId.HasValue)
                {
                    ticketThread = guild.GetThreadChannel((ulong)ticket.ThreadId.Value);
                    if (ticketThread == null)
                    {
                        // Thread may not be in cache — try fetching it directly
                        try { ticketThread = client.GetChannel((ulong)ticket.ThreadId.Value) as SocketThreadChannel; }
                        catch { /* ignore */ }
                    }
                }

                // Update the in-thread message
                if (ticketThread != null && ticket.ThreadMessageId.HasValue)
                {
                    var threadMsg = await ticketThread.GetMessageAsync((ulong)ticket.ThreadMessageId.Value);
                    if (threadMsg is IUserMessage threadUserMsg)
                    {
                        await threadUserMsg.ModifyAsync(msg =>
                        {
                            msg.Content = string.Empty;
                            msg.Embed = null;
                            msg.Components = builtThreadCard;
                            msg.Flags = MessageFlags.ComponentsV2;
                            msg.AllowedMentions = AllowedMentions.None;
                        });
                    }
                }

                // Post a text notification in the thread
                if (threadNotification != null && ticketThread != null)
                {
                    await ticketThread.SendMessageAsync(
                        threadNotification,
                        allowedMentions: BuildThreadNotificationAllowedMentions(
                            (ulong)ticket.RequesterId,
                            ticket.CrafterId.HasValue ? (ulong)ticket.CrafterId.Value : 0));
                }

                // Close the thread (archive + lock)
                if (archiveThread && ticketThread != null)
                {
                    try
                    {
                        await ticketThread.ModifyAsync(t =>
                        {
                            t.Locked = true;
                            t.Archived = true;
                        });
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not close thread {ThreadId}", ticket.ThreadId); }
                }

                return channelUpdated;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating Discord message for craft ticket {TicketId}", ticket.Id);
                return false;
            }
        }
    }
}
