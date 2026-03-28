using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Crafting
{
    [Group("craft", "Crafting request commands")]
    public class CraftCommands : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<CraftCommands> _logger;

        private static string[] ActiveStatuses => CraftConstants.ActiveStatuses;

        public CraftCommands(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<CraftCommands> logger)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
        }

        [SlashCommand("request", "Request a crafted item")]
        public async Task CraftRequest(
            [Summary("item", "Name of the item you need crafted")]
            [Autocomplete(typeof(CraftableItemAutocomplete))] string itemName)
        {
            // Encode item name in modal custom ID (max 100 chars total)
            var prefix = ModalConstants.CraftRequestModalPrefix;
            var maxItemLen = 100 - prefix.Length;
            var encodedItem = itemName.Length > maxItemLen ? itemName[..maxItemLen] : itemName;

            var modal = new ModalBuilder()
                .WithTitle("Crafting Request Details")
                .WithCustomId($"{prefix}{encodedItem}")
                .AddTextInput("Quality Desired", "craft_quality", TextInputStyle.Short,
                    placeholder: "Max quality, Rank 5, Any rank, etc.", maxLength: 100, required: false)
                .AddTextInput("Materials", "craft_mats", TextInputStyle.Short,
                    placeholder: "Have all mats, Need crafter to provide some, etc.", maxLength: 100, required: false)
                .AddTextInput("Commission / Tip", "craft_commission", TextInputStyle.Short,
                    placeholder: "5k tip, Negotiable, Free for guildies, etc.", maxLength: 100, required: false)
                .AddTextInput("Note (optional)", "craft_note", TextInputStyle.Paragraph,
                    placeholder: "Embellishment prefs, stat priority, timing, etc.", maxLength: 500, required: false);

            await RespondWithModalAsync(modal.Build());
        }

        [SlashCommand("setup", "Configure the crafting request channel")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task CraftSetup(
            [Summary("channel", "Channel where crafting requests will be posted")] ITextChannel channel,
            [Summary("max_tickets", "Maximum open tickets per user (default: 3)")] int? maxTickets = null,
            [Summary("expiration_hours", "Hours before tickets expire (default: 48)")] int? expirationHours = null)
        {
            await DeferAsync(ephemeral: true);

            if (maxTickets.HasValue && (maxTickets.Value < 1 || maxTickets.Value > 25))
            {
                await FollowupAsync("Max tickets must be between 1 and 25.", ephemeral: true);
                return;
            }

            if (expirationHours.HasValue && (expirationHours.Value < 1 || expirationHours.Value > 720))
            {
                await FollowupAsync("Expiration must be between 1 and 720 hours (30 days).", ephemeral: true);
                return;
            }

            var guildId = (long)Context.Guild.Id;

            var savedSettings = await WithDbAsync(async db =>
            {
                var settings = await db.ServerCraftSettings.FindAsync(guildId);
                if (settings == null)
                {
                    settings = new ServerCraftSettings { DiscordGuildId = guildId };
                    db.ServerCraftSettings.Add(settings);
                }

                settings.CraftChannelId = (long)channel.Id;
                if (maxTickets.HasValue) settings.MaxOpenTicketsPerUser = maxTickets.Value;
                if (expirationHours.HasValue) settings.TicketExpirationHours = expirationHours.Value;
                settings.SetById = (long)Context.User.Id;
                settings.SetByName = Context.User.Username;
                settings.TimeSet = DateTime.UtcNow;

                await db.SaveChangesAsync();
                return settings;
            });

            var embed = new EmbedBuilder()
                .WithTitle("Crafting Channel Configured")
                .WithColor(Color.Green)
                .AddField("Channel", $"<#{channel.Id}>", inline: true)
                .AddField("Max Tickets Per User", savedSettings.MaxOpenTicketsPerUser.ToString(), inline: true)
                .AddField("Ticket Expiration", $"{savedSettings.TicketExpirationHours} hours", inline: true)
                .WithFooter($"Set by {Context.User.Username}")
                .WithCurrentTimestamp()
                .Build();

            await FollowupAsync(embed: embed, ephemeral: true);
        }

        [SlashCommand("cancel", "Cancel your open crafting request")]
        public async Task CraftCancel(
            [Summary("ticket", "Select the ticket to cancel")]
            [Autocomplete(typeof(CraftTicketAutocomplete))] string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId) || ticketId == 0)
            {
                await FollowupAsync("Invalid ticket selection.", ephemeral: true);
                return;
            }

            CraftTicket cancelledTicket = null;

            var result = await WithDbAsync(async db =>
            {
                var ticket = await db.CraftTickets.FirstOrDefaultAsync(t =>
                    t.Id == ticketId && t.GuildId == (long)Context.Guild.Id);

                if (ticket == null)
                    return "Ticket not found.";

                if (ticket.RequesterId != (long)Context.User.Id)
                    return "You can only cancel your own crafting requests.";

                if (ticket.Status == "Complete" || ticket.Status == "Cancelled" || ticket.Status == "Expired")
                    return $"This ticket is already {ticket.Status.ToLower()}.";

                ticket.Status = "Cancelled";
                ticket.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                cancelledTicket = ticket;
                return null;
            });

            if (result != null)
            {
                await FollowupAsync(result, ephemeral: true);
                return;
            }

            if (cancelledTicket != null)
                await UpdateTicketMessageAsync(cancelledTicket, "This crafting request has been cancelled.");

            await FollowupAsync("Your crafting request has been cancelled.", ephemeral: true);
        }

        [SlashCommand("list", "View crafting requests")]
        public async Task CraftList(
            [Summary("scope", "Whose requests to view")]
            [Choice("Mine", "mine")]
            [Choice("All Open", "all")]
            string scope = "mine")
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;
            var userId = (long)Context.User.Id;

            var tickets = await WithDbAsync(async db =>
            {
                var query = db.CraftTickets
                    .Where(t => t.GuildId == guildId && ActiveStatuses.Contains(t.Status));

                if (scope == "mine")
                    query = query.Where(t => t.RequesterId == userId);

                return await query
                    .OrderBy(t => t.CreatedAt)
                    .Take(25)
                    .ToListAsync();
            });

            if (!tickets.Any())
            {
                await FollowupAsync(scope == "mine"
                    ? "You have no active crafting requests."
                    : "No active crafting requests in this server.", ephemeral: true);
                return;
            }

            var embed = BuildTicketListEmbed(tickets, scope == "mine" ? "Your Crafting Requests" : "Open Crafting Requests");

            // Look up professions for these tickets via CraftableItems table
            var itemNames = tickets.Select(t => t.ItemName).Distinct().ToList();
            var professions = await WithDbAsync(async db =>
                await db.CraftableItems
                    .Where(c => itemNames.Contains(c.RecipeName))
                    .Select(c => c.Profession)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToListAsync());

            var components = BuildListFilterComponents(professions, (long)Context.User.Id, scope);

            await FollowupAsync(embed: embed, components: components, ephemeral: true);
        }

        [SlashCommand("board", "View all crafting requests in this server")]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task CraftBoard()
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;

            var tickets = await WithDbAsync(async db =>
                await db.CraftTickets
                    .Where(t => t.GuildId == guildId)
                    .OrderByDescending(t => t.CreatedAt)
                    .Take(10)
                    .ToListAsync());

            if (!tickets.Any())
            {
                await FollowupAsync("No crafting requests found in this server.", ephemeral: true);
                return;
            }

            var embed = BuildTicketListEmbed(tickets, "Crafting Request Board");
            var components = BuildBoardFilterComponents((long)Context.User.Id);

            await FollowupAsync(embed: embed, components: components, ephemeral: true);
        }

        internal static Embed BuildTicketListEmbed(List<CraftTicket> tickets, string title)
        {
            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(Color.Blue)
                .WithFooter($"{tickets.Count} ticket(s)")
                .WithCurrentTimestamp();

            foreach (var ticket in tickets)
            {
                var statusEmoji = ticket.Status switch
                {
                    "Open" => "\uD83D\uDFE2",
                    "Claimed" => "\uD83D\uDD35",
                    "Crafted" => "\uD83D\uDFE1",
                    "Complete" => "\u2705",
                    "Expired" => "\u23F0",
                    "Cancelled" => "\u274C",
                    _ => "\u26AA"
                };

                var createdUnix = ((DateTimeOffset)ticket.CreatedAt).ToUnixTimeSeconds();
                var details = $"Status: {ticket.Status} | Requester: <@{(ulong)ticket.RequesterId}> | <t:{createdUnix}:R>";
                if (ticket.CrafterId.HasValue)
                    details += $" | Crafter: <@{(ulong)ticket.CrafterId.Value}>";

                var fieldName = $"{statusEmoji} #{ticket.Id} — {ticket.ItemName}";
                if (fieldName.Length > 256) fieldName = fieldName[..253] + "...";

                embed.AddField(fieldName, details, inline: false);
            }

            return embed.Build();
        }

        internal static MessageComponent BuildListFilterComponents(List<string> professions, long userId, string scope)
        {
            var builder = new ComponentBuilder();

            if (professions.Count > 1)
            {
                var options = new List<SelectMenuOptionBuilder>
                {
                    new SelectMenuOptionBuilder("All Professions", "all", "Show requests for all professions", isDefault: true)
                };
                options.AddRange(professions.Select(p =>
                    new SelectMenuOptionBuilder(p, p, $"Show only {p} requests")));

                builder.WithSelectMenu(
                    $"{ModalConstants.CraftListFilterPrefix}{userId}~{scope}",
                    options,
                    "Filter by profession...",
                    row: 0);
            }

            return builder.Build();
        }

        internal static MessageComponent BuildBoardFilterComponents(long userId)
        {
            var statusOptions = new List<SelectMenuOptionBuilder>
            {
                new("All Statuses", "all", "Show all tickets", isDefault: true),
                new("Open", "open", "Waiting for a crafter"),
                new("Claimed", "claimed", "Crafter assigned, in progress"),
                new("Crafted", "crafted", "Item crafted, awaiting trade"),
                new("Complete", "complete", "Trade finished"),
                new("Expired", "expired", "Timed out without a crafter"),
                new("Cancelled", "cancelled", "Manually cancelled")
            };

            var builder = new ComponentBuilder();
            builder.WithSelectMenu(
                $"{ModalConstants.CraftBoardFilterPrefix}{userId}",
                statusOptions,
                "Filter by status...",
                row: 0);

            return builder.Build();
        }

        private async Task UpdateTicketMessageAsync(CraftTicket ticket, string threadMessage)
        {
            try
            {
                var guild = _client.GetGuild((ulong)ticket.GuildId);
                if (guild == null) return;

                var channel = guild.GetTextChannel((ulong)ticket.ChannelId);
                if (channel == null) return;

                var message = await channel.GetMessageAsync((ulong)ticket.MessageId);
                if (message is not IUserMessage userMessage) return;

                var embed = CraftEmbedBuilder.BuildTicketEmbed(ticket);
                var components = CraftEmbedBuilder.BuildComponents(ticket);

                await userMessage.ModifyAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components.Build();
                });

                if (ticket.ThreadId.HasValue)
                {
                    var thread = guild.GetThreadChannel((ulong)ticket.ThreadId.Value);
                    if (thread != null)
                    {
                        await thread.SendMessageAsync(threadMessage);
                        try { await thread.ModifyAsync(t => t.Archived = true); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Could not archive thread {ThreadId}", ticket.ThreadId); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Discord message for craft ticket {TicketId}", ticket.Id);
            }
        }
    }
}
