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
    [RequireContext(ContextType.Guild)]
    [Group("craft", "Crafting request commands")]
    public class CraftCommands : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<CraftCommands> _logger;
        private readonly IWowApi _wowApi;
        private readonly WowCacheService _wowCache;

        private static string[] ActiveStatuses => CraftConstants.ActiveStatuses;

        public CraftCommands(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<CraftCommands> logger,
            IWowApi wowApi,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
            _wowApi = wowApi;
            _wowCache = wowCache;
        }

        [SlashCommand("request", "Request a crafted item from your guild")]
        public async Task CraftRequest(
            [Summary("item", "Start typing to search, or enter any item name")]
            [Autocomplete(typeof(CraftableItemAutocomplete))] string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
            {
                await RespondAsync("Please type an item name in the autocomplete field first.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await CreateCraftTicketAsync(itemName.Trim());
        }

        private async Task CreateCraftTicketAsync(string itemName)
        {
            // Pre-check: verify craft channel exists before creating ticket
            var preCheckSettings = await WithDbAsync(async db =>
                await db.ServerCraftSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == (long)Context.Guild.Id));

            if (preCheckSettings?.CraftChannelId == null)
            {
                await FollowupAsync("A crafting channel has not been configured. Ask an admin to run `/craft setup`.", ephemeral: true);
                return;
            }

            var preCheckChannel = _client.GetGuild(Context.Guild.Id)?.GetTextChannel((ulong)preCheckSettings.CraftChannelId.Value);
            if (preCheckChannel == null)
            {
                await FollowupAsync("The configured crafting channel could not be found. Ask an admin to run `/craft setup` again.", ephemeral: true);
                return;
            }

            // Settings check + ticket limit + creation
            var (ticket, craftChannelId, error) = await WithDbAsync(async db =>
            {
                var settings = await db.ServerCraftSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == (long)Context.Guild.Id);

                if (settings?.CraftChannelId == null)
                    return (null, 0L, "A crafting channel has not been configured. Ask an admin to run `/craft setup`.");

                var openCount = await db.CraftTickets.CountAsync(t =>
                    t.GuildId == (long)Context.Guild.Id
                    && t.RequesterId == (long)Context.User.Id
                    && ActiveStatuses.Contains(t.Status));

                if (openCount >= settings.MaxOpenTicketsPerUser)
                    return (null, 0L, $"You already have {openCount} open crafting requests (max {settings.MaxOpenTicketsPerUser}). Complete or cancel existing requests first.");

                var newTicket = new CraftTicket
                {
                    ItemName = itemName,
                    Status = "Open",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(settings.TicketExpirationHours),
                    RequesterId = (long)Context.User.Id,
                    RequesterName = Context.User.Username,
                    GuildId = (long)Context.Guild.Id,
                    ChannelId = settings.CraftChannelId.Value,
                    MessageId = 0
                };

                db.CraftTickets.Add(newTicket);
                await db.SaveChangesAsync();

                return (newTicket, settings.CraftChannelId.Value, (string)null);
            });

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            // Best-effort item lookup via recipe detail API
            try
            {
                var craftableItem = await WithDbAsync(async db =>
                    await db.CraftableItems
                        .FirstOrDefaultAsync(c => c.RecipeName == itemName));

                if (craftableItem != null)
                {
                    ticket.BlizzardItemId = craftableItem.CraftedItemId ?? craftableItem.Id;

                    var recipe = await _wowApi.GetRecipeAsync(craftableItem.Id);
                    if (recipe?.CraftedItem != null)
                    {
                        ticket.BlizzardItemId = recipe.CraftedItem.Id;
                        var resolvedName = recipe.CraftedItem.Name?.EnUS;
                        if (!string.IsNullOrEmpty(resolvedName))
                            ticket.ItemName = resolvedName;

                        var media = await _wowApi.GetItemMediaAsync(recipe.CraftedItem.Id);
                        ticket.ItemIconUrl = media?.Assets?.FirstOrDefault(a => a.Key == "icon")?.Value;
                    }

                    if (string.IsNullOrEmpty(ticket.ItemIconUrl))
                    {
                        var localItem = await WithDbAsync(async db =>
                            await db.WowItems
                                .FirstOrDefaultAsync(i => EF.Functions.ILike(i.Name, itemName)));
                        if (localItem != null)
                        {
                            ticket.BlizzardItemId = localItem.Id;
                            if (!string.IsNullOrEmpty(localItem.MediaUrl))
                                ticket.ItemIconUrl = localItem.MediaUrl;
                        }
                    }

                    if (string.IsNullOrEmpty(ticket.ItemIconUrl))
                    {
                        var recipeMedia = await _wowApi.GetRecipeMediaAsync(craftableItem.Id);
                        ticket.ItemIconUrl = recipeMedia?.Assets?.FirstOrDefault(a => a.Key == "icon")?.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Item lookup failed for '{ItemName}'", itemName);
            }

            // Best-effort connected realm lookup
            try
            {
                var mainChar = await _wowCache.GetUserMainCharacterAsync((long)Context.User.Id);
                if (mainChar?.LocalRealmSlug != null)
                {
                    ticket.RequesterRealm = mainChar.WowRealm;
                    var region = mainChar.WowRegion ?? "us";

                    var singleRealm = await _wowApi.GetSingleRealmInfoAsync(mainChar.LocalRealmSlug, region);
                    if (singleRealm?.ConnectedRealm?.Href != null)
                    {
                        var connectedRealm = await _wowApi.GetConnectedRealmInfoAsync(
                            singleRealm.ConnectedRealm.Href.ToString(), region);

                        if (connectedRealm?.Realms?.Length > 0)
                        {
                            var realmNames = connectedRealm.Realms
                                .Select(r => r.Name)
                                .Where(n => !string.IsNullOrEmpty(n))
                                .OrderBy(n => n);
                            var joined = string.Join(", ", realmNames);
                            ticket.ConnectedRealms = joined.Length > 2000 ? joined[..2000] : joined;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connected realm lookup failed for user {UserId}", Context.User.Id);
            }

            // Send embed to craft channel
            var channel = _client.GetGuild(Context.Guild.Id)?.GetTextChannel((ulong)craftChannelId);
            if (channel == null)
            {
                await WithDbAsync(async db =>
                {
                    var orphan = await db.CraftTickets.FindAsync(ticket.Id);
                    if (orphan != null) { db.CraftTickets.Remove(orphan); await db.SaveChangesAsync(); }
                });
                await FollowupAsync("The configured crafting channel could not be found. Ask an admin to run `/craft setup` again.", ephemeral: true);
                return;
            }

            IUserMessage message;
            try
            {
                var embed = CraftEmbedBuilder.BuildTicketEmbed(ticket);
                var components = CraftEmbedBuilder.BuildComponents(ticket);
                message = await channel.SendMessageAsync(embed: embed.Build(), components: components.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send craft ticket embed to channel {ChannelId}", craftChannelId);
                await WithDbAsync(async db =>
                {
                    var orphan = await db.CraftTickets.FindAsync(ticket.Id);
                    if (orphan != null) { db.CraftTickets.Remove(orphan); await db.SaveChangesAsync(); }
                });
                await FollowupAsync("Failed to post the crafting request. Please try again.", ephemeral: true);
                return;
            }

            ticket.MessageId = (long)message.Id;

            // Create thread
            try
            {
                var threadName = $"{ticket.ItemName} — {Context.User.GlobalName ?? Context.User.Username}";
                if (threadName.Length > 97) threadName = threadName[..97] + "...";

                var thread = await channel.CreateThreadAsync(
                    threadName, ThreadType.PublicThread,
                    autoArchiveDuration: ThreadArchiveDuration.OneDay, message: message);

                ticket.ThreadId = (long)thread.Id;

                var threadComponents = CraftEmbedBuilder.BuildComponents(ticket);
                var threadMessage = await thread.SendMessageAsync(
                    $"<@{Context.User.Id}> is looking for a crafter for **{ticket.ItemName}**!",
                    components: threadComponents.Build());
                ticket.ThreadMessageId = (long)threadMessage.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create thread for craft ticket {TicketId}", ticket.Id);
            }

            // Save enrichment data
            try
            {
                await WithDbAsync(async db =>
                {
                    var dbTicket = await db.CraftTickets.FindAsync(ticket.Id);
                    if (dbTicket != null)
                    {
                        dbTicket.MessageId = ticket.MessageId;
                        dbTicket.ThreadId = ticket.ThreadId;
                        dbTicket.ThreadMessageId = ticket.ThreadMessageId;
                        dbTicket.ItemName = ticket.ItemName;
                        dbTicket.BlizzardItemId = ticket.BlizzardItemId;
                        dbTicket.ItemIconUrl = ticket.ItemIconUrl;
                        dbTicket.RequesterRealm = ticket.RequesterRealm;
                        dbTicket.ConnectedRealms = ticket.ConnectedRealms;
                        await db.SaveChangesAsync();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save enrichment data for craft ticket {TicketId}", ticket.Id);
            }

            await FollowupAsync($"Your crafting request has been posted! Check <#{craftChannelId}>.", ephemeral: true);
        }

        [SlashCommand("setup", "Set up the crafting channel and settings")]
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
            [Summary("ticket", "Start typing to search your tickets")]
            [Autocomplete(typeof(CraftTicketAutocomplete))] string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId) || ticketId == 0)
            {
                await FollowupAsync("Invalid ticket selection.", ephemeral: true);
                return;
            }

            var (ticket, cancelledBy, error) = await WithDbAsync(async db =>
                await CraftTicketUpdater.CancelTicketAsync(db, ticketId, (long)Context.User.Id));

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: $"This crafting request has been cancelled by {cancelledBy}.",
                archiveThread: true);

            await FollowupAsync("Your crafting request has been cancelled.", ephemeral: true);
        }

        [SlashCommand("list", "View your active crafting requests")]
        public async Task CraftList(
            [Summary("scope", "View your requests or all open ones")]
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
                    .Take(24)
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

        [SlashCommand("board", "Admin view of all crafting requests")]
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

    }
}
