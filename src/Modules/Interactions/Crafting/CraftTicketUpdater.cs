using System;
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
            var ticket = await db.CraftTickets.FirstOrDefaultAsync(x => x.Id == ticketId);

            if (ticket == null)
                return (null, "", "Ticket not found.");

            var isRequester = ticket.RequesterId == cancelledByUserId;

            if (!isRequester && !isAdmin)
                return (null, "", "Only the requester can cancel this ticket.");

            if (ticket.Status is "Complete" or "Cancelled" or "Expired")
                return (null, "", $"This ticket is already {ticket.Status.ToLower()}.");

            ticket.Status = "Cancelled";
            ticket.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var cancelledBy = isRequester ? "the requester" : "an admin";
            return (ticket, cancelledBy, null);
        }

        /// <summary>
        /// Updates a craft ticket's Discord messages.
        /// Always updates the channel embed+buttons and in-thread embed+buttons.
        /// Optionally posts a text notification in the thread and/or archives the thread.
        /// </summary>
        public static async Task UpdateTicketAsync(
            DiscordShardedClient client,
            CraftTicket ticket,
            ILogger logger,
            string? threadNotification = null,
            bool archiveThread = false)
        {
            try
            {
                var guild = client.GetGuild((ulong)ticket.GuildId);
                if (guild == null) return;

                var channel = guild.GetTextChannel((ulong)ticket.ChannelId);
                if (channel == null) return;

                var embed = CraftEmbedBuilder.BuildTicketEmbed(ticket);
                var components = CraftEmbedBuilder.BuildComponents(ticket);
                var builtEmbed = embed.Build();
                var builtComponents = components.Build();

                // Update the channel message
                var message = await channel.GetMessageAsync((ulong)ticket.MessageId);
                if (message is IUserMessage userMessage)
                {
                    await userMessage.ModifyAsync(msg =>
                    {
                        msg.Embed = builtEmbed;
                        msg.Components = builtComponents;
                    });
                }

                // Update the in-thread message
                if (ticket.ThreadId.HasValue && ticket.ThreadMessageId.HasValue)
                {
                    var thread = guild.GetThreadChannel((ulong)ticket.ThreadId.Value);
                    if (thread != null)
                    {
                        var threadMsg = await thread.GetMessageAsync((ulong)ticket.ThreadMessageId.Value);
                        if (threadMsg is IUserMessage threadUserMsg)
                        {
                            await threadUserMsg.ModifyAsync(msg =>
                            {
                                msg.Embed = builtEmbed;
                                msg.Components = builtComponents;
                            });
                        }
                    }
                }

                // Post a text notification in the thread
                if (threadNotification != null && ticket.ThreadId.HasValue)
                {
                    var thread = guild.GetThreadChannel((ulong)ticket.ThreadId.Value);
                    if (thread != null)
                    {
                        await thread.SendMessageAsync(threadNotification);
                    }
                }

                // Close the thread (archive + lock)
                if (archiveThread && ticket.ThreadId.HasValue)
                {
                    var thread = guild.GetThreadChannel((ulong)ticket.ThreadId.Value);
                    if (thread != null)
                    {
                        try
                        {
                            await thread.ModifyAsync(t =>
                            {
                                t.Locked = true;
                                t.Archived = true;
                            });
                        }
                        catch (Exception ex) { logger.LogDebug(ex, "Could not close thread {ThreadId}", ticket.ThreadId); }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating Discord message for craft ticket {TicketId}", ticket.Id);
            }
        }
    }
}
