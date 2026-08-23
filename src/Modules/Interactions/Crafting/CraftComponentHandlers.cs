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
    public class CraftRequestModal : IModal
    {
        public string Title => "Crafting Request Details";

        [InputLabel("Quality Desired")]
        [ModalTextInput("craft_quality", TextInputStyle.Short,
            placeholder: "Max quality, Rank 5, Any rank, etc.", maxLength: 100)]
        [RequiredInput(false)]
        public string QualityDesired { get; set; }

        [InputLabel("Materials")]
        [ModalTextInput("craft_mats", TextInputStyle.Short,
            placeholder: "Have all mats, Need crafter to provide some, etc.", maxLength: 100)]
        [RequiredInput(false)]
        public string MaterialsStatus { get; set; }

        [InputLabel("Commission / Tip")]
        [ModalTextInput("craft_commission", TextInputStyle.Short,
            placeholder: "5k tip, Negotiable, Free for guildies, etc.", maxLength: 100)]
        [RequiredInput(false)]
        public string Commission { get; set; }

        [InputLabel("Note (optional)")]
        [ModalTextInput("craft_note", TextInputStyle.Paragraph,
            placeholder: "Embellishment prefs, stat priority, timing, etc.", maxLength: 500)]
        [RequiredInput(false)]
        public string Note { get; set; }
    }

    /// <summary>
    /// Attribute-based handlers for CraftLink button, modal, and select menu interactions.
    /// NOTE: No [Group] attribute — component/modal handlers don't work inside grouped classes.
    /// </summary>
    [RequireContext(ContextType.Guild)]
    public class CraftComponentHandlers : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<CraftComponentHandlers> _logger;
        private readonly IWowApi _wowApi;
        private readonly WowCacheService _wowCache;

        private static string[] ActiveStatuses => CraftConstants.ActiveStatuses;

        private static string WithReconciliationStatus(string successMessage, bool cardUpdated) =>
            cardUpdated
                ? successMessage
                : $"{successMessage} The ticket state was saved, but I couldn't refresh its public card; reopen the crafting list before taking another action.";

        public CraftComponentHandlers(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<CraftComponentHandlers> logger,
            IWowApi wowApi,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
            _wowApi = wowApi;
            _wowCache = wowCache;
        }

        [ComponentInteraction("craft_claim~*")]
        public async Task HandleCraftClaim(string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId))
            {
                await FollowupAsync("Invalid ticket data.", ephemeral: true);
                return;
            }

            var (ticket, error) = await WithDbAsync(async db =>
                await CraftTicketUpdater.ClaimTicketAsync(
                    db,
                    ticketId,
                    (long)Context.User.Id,
                    Context.User.Username,
                    DateTime.UtcNow));

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            // Discord operations: best-effort, outside DB scope
            var cardUpdated = await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: $"<@{Context.User.Id}> has claimed this crafting request! <@{(ulong)ticket.RequesterId}>, coordinate the trade here.\n\n" +
                $"**Crafter:** Use the **Mark as Crafted** button when the item is ready.\n" +
                $"**Requester:** Use **Item Received** once you've received the item.");

            await FollowupAsync(
                WithReconciliationStatus("You've claimed this crafting request! Head to the thread to coordinate.", cardUpdated),
                ephemeral: true);
        }

        [ComponentInteraction("craft_crafted~*")]
        public async Task HandleCraftCrafted(string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId))
            {
                await FollowupAsync("Invalid ticket data.", ephemeral: true);
                return;
            }

            var (ticket, error) = await WithDbAsync(async db =>
                await CraftTicketUpdater.MarkCraftedAsync(
                    db,
                    ticketId,
                    (long)Context.User.Id,
                    DateTime.UtcNow));

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var cardUpdated = await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: $"<@{(ulong)ticket.RequesterId}> — the item has been crafted! Click **Item Received** once you've received it.");

            await FollowupAsync(
                WithReconciliationStatus("Marked as crafted! Waiting for the requester to confirm the trade.", cardUpdated),
                ephemeral: true);
        }

        [ComponentInteraction("craft_complete~*")]
        public async Task HandleCraftComplete(string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId))
            {
                await FollowupAsync("Invalid ticket data.", ephemeral: true);
                return;
            }

            var (ticket, error) = await WithDbAsync(async db =>
                await CraftTicketUpdater.CompleteTicketAsync(
                    db,
                    ticketId,
                    (long)Context.User.Id,
                    DateTime.UtcNow,
                    allowOpen: false));

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var crafterMention = ticket.CrafterId.HasValue
                ? $" Thanks to <@{(ulong)ticket.CrafterId.Value}> for crafting **{ticket.ItemName}**."
                : "";
            var cardUpdated = await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: $"Item received!{crafterMention} This ticket is now closed.",
                archiveThread: true);

            await FollowupAsync(
                WithReconciliationStatus("Item received! The ticket has been closed.", cardUpdated),
                ephemeral: true);
        }

        [ComponentInteraction("craft_gotit~*")]
        public async Task HandleGotIt(string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId))
            {
                await FollowupAsync("Invalid ticket data.", ephemeral: true);
                return;
            }

            var (ticket, error) = await WithDbAsync(async db =>
                await CraftTicketUpdater.CompleteTicketAsync(
                    db,
                    ticketId,
                    (long)Context.User.Id,
                    DateTime.UtcNow,
                    allowOpen: true));

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var cardUpdated = await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: "Item received! This ticket is now closed.",
                archiveThread: true);

            await FollowupAsync(
                WithReconciliationStatus("Got it! The ticket has been closed.", cardUpdated),
                ephemeral: true);
        }

        [ComponentInteraction("craft_cancel~*")]
        public async Task HandleCraftCancel(string ticketIdStr)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId))
            {
                await FollowupAsync("Invalid ticket data.", ephemeral: true);
                return;
            }

            var (ticket, cancelledBy, error) = await WithDbAsync(async db =>
                await CraftTicketUpdater.CancelTicketAsync(db, ticketId, (long)Context.User.Id));

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var isUnclaim = ticket.Status == "Open"; // CancelTicketAsync sets back to Open for crafter unclaim
            var cardUpdated = await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: isUnclaim
                    ? $"The crafter has released this ticket. It's open for a new crafter!"
                    : $"This crafting request has been cancelled by {cancelledBy}.",
                archiveThread: !isUnclaim);

            var successMessage = isUnclaim
                ? "You've released this crafting request. It's back open for others."
                : "The crafting request has been cancelled.";
            await FollowupAsync(
                WithReconciliationStatus(successMessage, cardUpdated),
                ephemeral: true);
        }

        #region Join Role Handler

        [ComponentInteraction("craft_join_role~*")]
        public async Task HandleJoinRole(string profession)
        {
            await DeferAsync(ephemeral: true);

            var guildUser = Context.User as Discord.WebSocket.SocketGuildUser;
            if (guildUser == null)
            {
                await FollowupAsync("This command only works in a server.", ephemeral: true);
                return;
            }

            var roleMapping = await WithDbAsync(async db =>
                await db.CraftProfessionRoleMappings
                    .FirstOrDefaultAsync(m => m.GuildId == (long)Context.Guild.Id && m.Profession == profession));

            if (roleMapping == null)
            {
                await FollowupAsync($"No role is configured for {profession}. Ask an admin to run `/craft roles-setup`.", ephemeral: true);
                return;
            }

            var role = Context.Guild.GetRole((ulong)roleMapping.RoleId);
            if (role == null)
            {
                await FollowupAsync($"The {profession} role no longer exists. Ask an admin to reconfigure it.", ephemeral: true);
                return;
            }

            try
            {
                if (guildUser.Roles.Any(r => r.Id == role.Id))
                {
                    await guildUser.RemoveRoleAsync(role);
                    await FollowupAsync($"**{profession}** role removed. You won't be pinged for {profession} requests.", ephemeral: true);
                }
                else
                {
                    await guildUser.AddRoleAsync(role);
                    await FollowupAsync($"**{profession}** role added! You'll be pinged for {profession} requests.\nClick the button again or use `/craft roles-join` to remove it.", ephemeral: true);
                }
            }
            catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
            {
                await FollowupAsync($"I don't have permission to manage the **{profession}** role. Make sure my role is above it in the server settings.", ephemeral: true);
            }
        }

        #endregion

        #region Profession Select Handler

        [ComponentInteraction("craft_prof_select~*")]
        public async Task HandleProfessionSelect(string ticketIdStr, string[] selections)
        {
            await DeferAsync(ephemeral: true);

            if (!long.TryParse(ticketIdStr, out var ticketId)
                || selections == null
                || selections.Length != 1)
            {
                await FollowupAsync("Invalid selection.", ephemeral: true);
                return;
            }

            var selectedProfession = selections[0];

            // Finalize the pending ticket
            var (ticket, craftChannelId, error) = await WithDbAsync(async db =>
            {
                var validProfession = await db.CraftableItems.AnyAsync(item =>
                    item.Profession == selectedProfession
                    && !CraftConstants.GatheringProfessions.Contains(item.Profession));
                if (!validProfession)
                    return (null, 0L, "That crafting profession is no longer available.");

                var t = await db.CraftTickets.FirstOrDefaultAsync(x => x.Id == ticketId);

                if (t == null)
                    return (null, 0L, "Ticket not found.");

                if (t.Status != "PendingProfession")
                    return (null, 0L, "This ticket has already been processed.");

                if (t.RequesterId != (long)Context.User.Id)
                    return (null, 0L, "This isn't your ticket.");

                // Check ticket limit before transitioning to Open
                var settings = await db.ServerCraftSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == t.GuildId);
                var openCount = await db.CraftTickets.CountAsync(x =>
                    x.GuildId == t.GuildId
                    && x.RequesterId == t.RequesterId
                    && ActiveStatuses.Contains(x.Status));

                if (settings != null && openCount >= settings.MaxOpenTicketsPerUser)
                {
                    var deleted = await db.CraftTickets
                        .Where(ticket =>
                            ticket.Id == ticketId
                            && ticket.Status == "PendingProfession"
                            && ticket.RequesterId == (long)Context.User.Id)
                        .ExecuteDeleteAsync();
                    return deleted == 1
                        ? (null, 0L, $"You already have {openCount} open crafting requests (max {settings.MaxOpenTicketsPerUser}). Complete or cancel existing requests first.")
                        : (null, 0L, "This ticket has already been processed.");
                }

                var finalized = await CraftTicketUpdater.FinalizePendingProfessionAsync(
                    db,
                    ticketId,
                    (long)Context.User.Id,
                    selectedProfession,
                    DateTime.UtcNow.AddHours(settings?.TicketExpirationHours ?? 48));
                return finalized.Error == null
                    ? (finalized.Ticket, finalized.Ticket.ChannelId, (string?)null)
                    : (null, 0L, finalized.Error);
            });

            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            // Best-effort realm enrichment
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

            // Post the ticket to the craft channel
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
                var ticketCard = CraftEmbedBuilder.BuildTicketCard(ticket);
                message = await channel.SendMessageAsync(
                    components: ticketCard.Build(),
                    flags: MessageFlags.ComponentsV2,
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send craft ticket embed");
                await WithDbAsync(async db =>
                {
                    var orphan = await db.CraftTickets.FindAsync(ticket.Id);
                    if (orphan != null) { db.CraftTickets.Remove(orphan); await db.SaveChangesAsync(); }
                });
                await FollowupAsync("Failed to post the crafting request. Please try again.", ephemeral: true);
                return;
            }

            ticket.MessageId = (long)message.Id;

            // Create thread with role ping
            try
            {
                var threadName = $"{ticket.ItemName} — {Context.User.GlobalName ?? Context.User.Username}";
                if (threadName.Length > 97) threadName = threadName[..97] + "...";

                var thread = await channel.CreateThreadAsync(
                    threadName, ThreadType.PublicThread,
                    autoArchiveDuration: ThreadArchiveDuration.OneDay, message: message);

                ticket.ThreadId = (long)thread.Id;

                var roleMapping = await WithDbAsync(async db =>
                    await db.CraftProfessionRoleMappings
                        .FirstOrDefaultAsync(m => m.GuildId == (long)Context.Guild.Id
                            && m.Profession == selectedProfession));
                long? professionRoleId = roleMapping?.RoleId;

                var threadCard = CraftEmbedBuilder.BuildTicketCard(
                    ticket,
                    CraftEmbedBuilder.BuildThreadPreface(ticket, professionRoleId));
                var threadMessage = await thread.SendMessageAsync(
                    components: threadCard.Build(),
                    flags: MessageFlags.ComponentsV2,
                    allowedMentions: CraftTicketUpdater.BuildInitialThreadAllowedMentions(
                        Context.User.Id,
                        professionRoleId.HasValue ? (ulong)professionRoleId.Value : null));
                ticket.ThreadMessageId = (long)threadMessage.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create thread for craft ticket {TicketId}", ticket.Id);
            }

            // Save enrichment data
            await WithDbAsync(async db =>
            {
                var dbTicket = await db.CraftTickets.FindAsync(ticket.Id);
                if (dbTicket != null)
                {
                    dbTicket.MessageId = ticket.MessageId;
                    dbTicket.ThreadId = ticket.ThreadId;
                    dbTicket.ThreadMessageId = ticket.ThreadMessageId;
                    dbTicket.RequesterRealm = ticket.RequesterRealm;
                    dbTicket.ConnectedRealms = ticket.ConnectedRealms;
                    await db.SaveChangesAsync();
                }
            });

            await FollowupAsync($"Your crafting request has been posted! Check <#{craftChannelId}>.", ephemeral: true);
        }

        #endregion

        #region Ticket Creation (kept for modal handler compatibility)

        [ModalInteraction("craft_req~*")]
        public async Task HandleCraftRequestModal(string itemNameFromId, CraftRequestModal modal)
        {
            // Modal still works if triggered, but the primary flow is now the slash command
            await DeferAsync(ephemeral: true);

            var itemName = itemNameFromId?.Trim();
            if (string.IsNullOrEmpty(itemName))
            {
                await FollowupAsync("Item name is required.", ephemeral: true);
                return;
            }

            await CreateCraftTicketAsync(itemName,
                modal.Note?.Trim(), modal.QualityDesired?.Trim(),
                modal.MaterialsStatus?.Trim(), modal.Commission?.Trim());
        }

        private async Task CreateCraftTicketAsync(
            string itemName,
            string? note = null,
            string? qualityDesired = null,
            string? materialsStatus = null,
            string? commission = null)
        {
            // Pre-check: verify craft channel exists before creating ticket
            long? preCheckChannelId = null;
            var preCheckSettings = await WithDbAsync(async db =>
                await db.ServerCraftSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == (long)Context.Guild.Id));

            if (preCheckSettings?.CraftChannelId == null)
            {
                await FollowupAsync("A crafting channel has not been configured. Ask an admin to run `/craft setup`.", ephemeral: true);
                return;
            }

            preCheckChannelId = preCheckSettings.CraftChannelId.Value;
            var preCheckChannel = _client.GetGuild(Context.Guild.Id)?.GetTextChannel((ulong)preCheckChannelId.Value);
            if (preCheckChannel == null)
            {
                await FollowupAsync("The configured crafting channel could not be found. Ask an admin to run `/craft setup` again.", ephemeral: true);
                return;
            }

            // Settings check + ticket limit + creation in one scope
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
                    Note = string.IsNullOrEmpty(note) ? null : note,
                    QualityDesired = string.IsNullOrEmpty(qualityDesired) ? null : qualityDesired,
                    MaterialsStatus = string.IsNullOrEmpty(materialsStatus) ? null : materialsStatus,
                    Commission = string.IsNullOrEmpty(commission) ? null : commission,
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
                    // Item is from the professions database — mark as verified using recipe ID
                    ticket.BlizzardItemId = craftableItem.CraftedItemId ?? craftableItem.Id;

                    // Try to get the crafted item details from recipe API
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

                    // Fallback: match by name in local WowItems database
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

                    // Fallback: use recipe media (profession icon)
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
                var ticketCard = CraftEmbedBuilder.BuildTicketCard(ticket);
                message = await channel.SendMessageAsync(
                    components: ticketCard.Build(),
                    flags: MessageFlags.ComponentsV2,
                    allowedMentions: AllowedMentions.None);
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

                // Post a mention-safe V2 coordination card inside the thread.
                var threadCard = CraftEmbedBuilder.BuildTicketCard(
                    ticket,
                    CraftEmbedBuilder.BuildThreadPreface(ticket));
                var threadMessage = await thread.SendMessageAsync(
                    components: threadCard.Build(),
                    flags: MessageFlags.ComponentsV2,
                    allowedMentions: CraftTicketUpdater.BuildInitialThreadAllowedMentions(
                        Context.User.Id));
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

        #endregion

        #region List Filter Handler

        [ComponentInteraction("craft_list_filter~*~*")]
        public async Task HandleListFilter(string userIdStr, string scope, string[] selections)
        {
            if (!long.TryParse(userIdStr, out var userId) || userId != (long)Context.User.Id)
            {
                await RespondAsync("This menu belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length != 1
                || scope is not ("mine" or "all"))
            {
                await RespondAsync("Invalid crafting filter.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var professionFilter = selections[0];
            var guildId = (long)Context.Guild.Id;

            var (tickets, professions, validFilter) = await WithDbAsync(async db =>
            {
                var query = db.CraftTickets
                    .Where(t => t.GuildId == guildId && ActiveStatuses.Contains(t.Status));
                if (scope == "mine")
                    query = query.Where(t => t.RequesterId == userId);

                var available = await query
                    .Where(ticket => ticket.Profession != null && ticket.Profession != "")
                    .Select(ticket => ticket.Profession)
                    .Distinct()
                    .OrderBy(profession => profession)
                    .ToListAsync();
                var isValid = professionFilter == "all"
                    || available.Any(profession => string.Equals(
                        profession,
                        professionFilter,
                        StringComparison.OrdinalIgnoreCase));
                if (!isValid)
                {
                    return (new List<CraftTicket>(), available, false);
                }

                if (professionFilter != "all")
                {
                    query = query.Where(ticket => ticket.Profession == professionFilter);
                }

                var filtered = await query
                    .OrderBy(ticket => ticket.CreatedAt)
                    .Take(25)
                    .ToListAsync();
                return (filtered, available, true);
            });

            if (!validFilter)
            {
                await FollowupAsync("That profession is no longer available in this ticket list.", ephemeral: true);
                return;
            }

            var title = professionFilter == "all"
                ? (scope == "mine" ? "Your Crafting Requests" : "Open Crafting Requests")
                : $"Crafting Requests — {professionFilter}";

            var components = CraftCommands.BuildListFilterComponents(professions, userId, scope);
            var card = CraftEmbedBuilder.BuildTicketListCard(tickets, title, components);

            await Context.Interaction.ModifyToV2Async(card.Build());
        }

        #endregion

        #region Board Filter Handler

        [ComponentInteraction("craft_board_filter~*")]
        public async Task HandleBoardFilter(string userIdStr, string[] selections)
        {
            if (!long.TryParse(userIdStr, out var userId) || userId != (long)Context.User.Id)
            {
                await RespondAsync("This menu belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length != 1
                || selections[0] is not ("all" or "open" or "claimed" or "crafted" or "complete" or "expired" or "cancelled"))
            {
                await RespondAsync("Invalid status filter.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var statusFilter = selections[0];
            var guildId = (long)Context.Guild.Id;

            var tickets = await WithDbAsync(async db =>
            {
                var query = db.CraftTickets.Where(t => t.GuildId == guildId);

                if (statusFilter != "all")
                {
                    var normalizedStatus = statusFilter switch
                    {
                        "open" => "Open",
                        "claimed" => "Claimed",
                        "crafted" => "Crafted",
                        "complete" => "Complete",
                        "expired" => "Expired",
                        "cancelled" => "Cancelled",
                        _ => statusFilter
                    };
                    query = query.Where(t => t.Status == normalizedStatus);
                }

                return await query
                    .OrderByDescending(t => t.CreatedAt)
                    .Take(10)
                    .ToListAsync();
            });

            var title = statusFilter == "all"
                ? "Crafting Request Board"
                : $"Crafting Requests — {char.ToUpper(statusFilter[0])}{statusFilter[1..]}";

            var components = CraftCommands.BuildBoardFilterComponents(userId);
            var card = CraftEmbedBuilder.BuildTicketListCard(tickets, title, components);

            await Context.Interaction.ModifyToV2Async(card.Build());
        }

        #endregion
    }
}
