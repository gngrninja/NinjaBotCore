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
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
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

        private static string WithReconciliationStatus(string successMessage, bool cardUpdated) =>
            cardUpdated
                ? successMessage
                : $"{successMessage} The ticket state was saved, but I couldn't refresh its public card; reopen the crafting list before taking another action.";

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
            if (!CraftEmbedBuilder.IsValidItemName(itemName))
            {
                await RespondAsync(
                    $"Please enter an item name between 1 and {CraftEmbedBuilder.MaxItemNameLength} characters.",
                    ephemeral: true);
                return;
            }

            var trimmedName = itemName.Trim();
            await DeferAsync(ephemeral: true);

            // Check if item is from autocomplete (known profession) or freeform
            var craftableItem = await WithDbAsync(async db =>
                await db.CraftableItems.FirstOrDefaultAsync(c => c.RecipeName == trimmedName));

            if (craftableItem != null)
            {
                // Known item — create ticket immediately with profession
                await CreateCraftTicketAsync(trimmedName, craftableItem.Profession);
            }
            else
            {
                // Freeform item — ask the user to pick a profession
                var professions = await WithDbAsync(async db =>
                    await db.CraftableItems
                        .Where(c => !CraftConstants.GatheringProfessions.Contains(c.Profession))
                        .Select(c => c.Profession)
                        .Distinct()
                        .OrderBy(p => p)
                        .ToListAsync());

                if (!professions.Any())
                {
                    // No professions in DB — just create without profession
                    await CreateCraftTicketAsync(trimmedName, null);
                    return;
                }

                // Create a pending ticket so we can reference it in the select menu
                var pendingTicket = await WithDbAsync(async db =>
                {
                    var settings = await db.ServerCraftSettings
                        .FirstOrDefaultAsync(s => s.DiscordGuildId == (long)Context.Guild.Id);

                    if (settings?.CraftChannelId == null) return null;

                    var ticket = new CraftTicket
                    {
                        ItemName = trimmedName,
                        Status = "PendingProfession",
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                        RequesterId = (long)Context.User.Id,
                        RequesterName = Context.User.Username,
                        GuildId = (long)Context.Guild.Id,
                        ChannelId = settings.CraftChannelId.Value,
                        MessageId = 0
                    };
                    db.CraftTickets.Add(ticket);
                    await db.SaveChangesAsync();
                    return ticket;
                });

                if (pendingTicket == null)
                {
                    await Context.Interaction.ModifyToV2Async(
                        WowCardV2.Notice(
                            "Crafting Channel Not Configured",
                            "Ask an admin to run `/craft setup` before creating requests.",
                            Color.Orange,
                            "⚠️").Build());
                    return;
                }

                var options = professions.Select(p =>
                    new SelectMenuOptionBuilder(p, p)).ToList();

                var component = new ComponentBuilder()
                    .WithSelectMenu(
                        $"{ModalConstants.CraftProfessionSelectPrefix}{pendingTicket.Id}",
                        options,
                        "Which profession crafts this item?")
                    .Build();

                var prompt = new EmbedBuilder()
                    .WithTitle("Choose a Crafting Profession")
                    .WithDescription(
                        $"**{trimmedName}** isn't in the recipe database. " +
                        "Select the profession that crafts it to continue.")
                    .WithColor(new Color(88, 101, 242));
                await Context.Interaction.ModifyToV2Async(
                    WowCardV2.FromEmbed(prompt, component).Build());
            }
        }

        internal async Task CreateCraftTicketAsync(string itemName, string? profession = null)
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
                    Profession = profession,
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
                    if (string.IsNullOrEmpty(ticket.Profession))
                        ticket.Profession = craftableItem.Profession;

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

                // Look up profession role for one explicit auto-ping.
                long? professionRoleId = null;
                if (!string.IsNullOrEmpty(ticket.Profession))
                {
                    var roleMapping = await WithDbAsync(async db =>
                        await db.CraftProfessionRoleMappings
                            .FirstOrDefaultAsync(m => m.GuildId == (long)Context.Guild.Id
                                && m.Profession == ticket.Profession));
                    professionRoleId = roleMapping?.RoleId;
                }

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
                        dbTicket.Profession = ticket.Profession;
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

            await Context.Interaction.ModifyToV2Async(
                WowCardV2.FromEmbed(embed).Build());
        }

        [SlashCommand("roles-setup", "Auto-create roles for all crafting professions")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        [RequireBotPermission(GuildPermission.ManageRoles)]
        public async Task CraftRolesSetup()
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;

            // Get all crafting professions from the DB
            var professions = await WithDbAsync(async db =>
                await db.CraftableItems
                    .Where(c => !CraftConstants.GatheringProfessions.Contains(c.Profession))
                    .Select(c => c.Profession)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToListAsync());

            if (!professions.Any())
            {
                await FollowupAsync("No crafting professions found in the database. Run a recipe sync first.", ephemeral: true);
                return;
            }

            int created = 0, updated = 0, skipped = 0;
            var results = new List<string>();

            foreach (var profession in professions)
            {
                // Check if mapping already exists
                var existing = await WithDbAsync(async db =>
                    await db.CraftProfessionRoleMappings
                        .FirstOrDefaultAsync(m => m.GuildId == guildId && m.Profession == profession));

                if (existing != null)
                {
                    skipped++;
                    results.Add($"\u26AA {profession} — already mapped to <@&{(ulong)existing.RoleId}>");
                    continue;
                }

                // Check if a role with this name already exists in the guild
                var existingRole = Context.Guild.Roles.FirstOrDefault(r =>
                    string.Equals(r.Name, profession, StringComparison.OrdinalIgnoreCase));

                Discord.IRole role;
                if (existingRole != null)
                {
                    role = existingRole;
                }
                else
                {
                    // Create the role
                    role = await Context.Guild.CreateRoleAsync(profession, isMentionable: true);
                    created++;
                }

                // Save the mapping
                await WithDbAsync(async db =>
                {
                    db.CraftProfessionRoleMappings.Add(new CraftProfessionRoleMapping
                    {
                        GuildId = guildId,
                        Profession = profession,
                        RoleId = (long)role.Id,
                        RoleName = role.Name,
                        SetById = (long)Context.User.Id,
                        SetByName = Context.User.Username,
                        CreatedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                });
                updated++;
                results.Add($"\u2705 {profession} \u2192 <@&{role.Id}>");
            }

            var embed = new EmbedBuilder()
                .WithTitle("Crafting Profession Roles Setup")
                .WithColor(Color.Green)
                .WithDescription(string.Join("\n", results))
                .WithFooter($"{created} roles created, {updated} mappings saved, {skipped} already configured")
                .WithCurrentTimestamp()
                .Build();

            await Context.Interaction.ModifyToV2Async(
                WowCardV2.FromEmbed(embed).Build());
        }

        [SlashCommand("roles-add", "Map a crafting profession to a Discord role")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task CraftRolesAdd(
            [Summary("profession", "The crafting profession")]
            [Autocomplete(typeof(CraftProfessionAutocomplete))] string profession,
            [Summary("role", "The Discord role to ping (leave empty to auto-create)")]
            IRole role = null)
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;

            // Validate profession exists and isn't gathering
            var validProfession = await WithDbAsync(async db =>
                await db.CraftableItems
                    .AnyAsync(c => c.Profession == profession
                        && !CraftConstants.GatheringProfessions.Contains(c.Profession)));

            if (!validProfession)
            {
                await FollowupAsync($"'{profession}' is not a valid crafting profession.", ephemeral: true);
                return;
            }

            // Auto-create role if not provided
            if (role == null)
            {
                var botUser = Context.Guild.GetUser(_client.CurrentUser.Id);
                if (botUser == null || !botUser.GuildPermissions.ManageRoles)
                {
                    await FollowupAsync("I need **Manage Roles** permission to create roles. Either grant it or specify an existing role.", ephemeral: true);
                    return;
                }

                var existingRole = Context.Guild.Roles.FirstOrDefault(r =>
                    string.Equals(r.Name, profession, StringComparison.OrdinalIgnoreCase));

                role = (IRole)existingRole ?? await Context.Guild.CreateRoleAsync(profession, isMentionable: true);
            }

            // Upsert the mapping
            await WithDbAsync(async db =>
            {
                var existing = await db.CraftProfessionRoleMappings
                    .FirstOrDefaultAsync(m => m.GuildId == guildId && m.Profession == profession);

                if (existing != null)
                {
                    existing.RoleId = (long)role.Id;
                    existing.RoleName = role.Name;
                    existing.SetById = (long)Context.User.Id;
                    existing.SetByName = Context.User.Username;
                }
                else
                {
                    db.CraftProfessionRoleMappings.Add(new CraftProfessionRoleMapping
                    {
                        GuildId = guildId,
                        Profession = profession,
                        RoleId = (long)role.Id,
                        RoleName = role.Name,
                        SetById = (long)Context.User.Id,
                        SetByName = Context.User.Username,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                await db.SaveChangesAsync();
            });

            await FollowupAsync($"Mapped **{profession}** \u2192 {role.Mention}. Craft requests for {profession} items will ping this role.", ephemeral: true);
        }

        [SlashCommand("roles-remove", "Remove a profession-to-role mapping")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task CraftRolesRemove(
            [Summary("profession", "The profession to unmap")]
            [Autocomplete(typeof(CraftMappedProfessionAutocomplete))] string profession)
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;

            var removed = await WithDbAsync(async db =>
            {
                var mapping = await db.CraftProfessionRoleMappings
                    .FirstOrDefaultAsync(m => m.GuildId == guildId && m.Profession == profession);

                if (mapping == null) return false;

                db.CraftProfessionRoleMappings.Remove(mapping);
                await db.SaveChangesAsync();
                return true;
            });

            await FollowupAsync(removed
                ? $"Removed role mapping for **{profession}**."
                : $"No mapping found for '{profession}'.", ephemeral: true);
        }

        [SlashCommand("roles-list", "View current profession-to-role mappings")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task CraftRolesList()
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;

            var mappings = await WithDbAsync(async db =>
                await db.CraftProfessionRoleMappings
                    .Where(m => m.GuildId == guildId)
                    .OrderBy(m => m.Profession)
                    .ToListAsync());

            if (!mappings.Any())
            {
                await FollowupAsync("No profession-to-role mappings configured. Use `/craft roles-setup` to create them.", ephemeral: true);
                return;
            }

            var lines = mappings.Select(m => $"**{m.Profession}** \u2192 <@&{(ulong)m.RoleId}>");
            var embed = new EmbedBuilder()
                .WithTitle("Crafting Profession Role Mappings")
                .WithColor(Color.Blue)
                .WithDescription(string.Join("\n", lines))
                .WithFooter($"{mappings.Count} mapping(s)")
                .WithCurrentTimestamp()
                .Build();

            await Context.Interaction.ModifyToV2Async(
                WowCardV2.FromEmbed(embed).Build());
        }

        [SlashCommand("roster", "View the guild's crafting roster")]
        public async Task CraftRoster()
        {
            await DeferAsync(ephemeral: true);

            var guildId = (long)Context.Guild.Id;

            var mappings = await WithDbAsync(async db =>
                await db.CraftProfessionRoleMappings
                    .Where(m => m.GuildId == guildId)
                    .OrderBy(m => m.Profession)
                    .ToListAsync());

            if (!mappings.Any())
            {
                await FollowupAsync("No crafting roles have been set up. Ask an admin to run `/craft roles-setup`.", ephemeral: true);
                return;
            }

            var professionEmojis = new Dictionary<string, string>
            {
                ["Alchemy"] = "\u2697\uFE0F",
                ["Blacksmithing"] = "\uD83D\uDD28",
                ["Cooking"] = "\uD83C\uDF73",
                ["Enchanting"] = "\u2728",
                ["Engineering"] = "\u2699\uFE0F",
                ["Inscription"] = "\uD83D\uDCDC",
                ["Jewelcrafting"] = "\uD83D\uDC8E",
                ["Leatherworking"] = "\uD83E\uDDE5",
                ["Tailoring"] = "\uD83E\uDDF5"
            };

            var lines = new List<string>();
            foreach (var mapping in mappings)
            {
                var role = Context.Guild.GetRole((ulong)mapping.RoleId);
                if (role == null) continue;

                var emoji = professionEmojis.GetValueOrDefault(mapping.Profession, "\u2692\uFE0F");
                var members = Context.Guild.Users
                    .Where(u => u.Roles.Any(r => r.Id == role.Id))
                    .OrderBy(u => u.DisplayName)
                    .ToList();

                if (members.Any())
                {
                    var memberList = string.Join(", ", members.Select(m => m.Mention));
                    if (memberList.Length > 900)
                        memberList = memberList[..900] + "...";
                    lines.Add($"{emoji} **{mapping.Profession}** ({members.Count})\n{memberList}");
                }
                else
                {
                    lines.Add($"{emoji} **{mapping.Profession}**\n*No crafters yet — use `/craft roles-join` to sign up!*");
                }
            }

            var description = string.Join("\n\n", lines);
            if (description.Length > 4000)
                description = description[..4000] + "\n...";

            var embed = new EmbedBuilder()
                .WithTitle("\u2692\uFE0F Guild Crafting Roster")
                .WithColor(Color.Gold)
                .WithDescription(description)
                .WithFooter($"Use /craft roles-join to add or remove yourself")
                .WithCurrentTimestamp()
                .Build();

            await Context.Interaction.ModifyToV2Async(
                WowCardV2.FromEmbed(embed).Build());
        }

        [SlashCommand("roles-join", "Join or leave a crafting profession role")]
        public async Task CraftRolesJoin(
            [Summary("profession", "The profession to join or leave")]
            [Autocomplete(typeof(CraftMappedProfessionAutocomplete))] string profession)
        {
            await DeferAsync(ephemeral: true);

            if (string.IsNullOrWhiteSpace(profession))
            {
                await FollowupAsync("Please select a profession.", ephemeral: true);
                return;
            }

            var guildId = (long)Context.Guild.Id;

            var roleMapping = await WithDbAsync(async db =>
                await db.CraftProfessionRoleMappings
                    .FirstOrDefaultAsync(m => m.GuildId == guildId && m.Profession == profession));

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

            var guildUser = Context.User as Discord.WebSocket.SocketGuildUser;
            if (guildUser == null)
            {
                await FollowupAsync("This command only works in a server.", ephemeral: true);
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

            var isUnclaim = ticket.Status == "Open"; // CancelTicketAsync sets back to Open for crafter unclaim
            var cardUpdated = await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                threadNotification: isUnclaim
                    ? $"The crafter has released this ticket. It's open for a new crafter!"
                    : $"This crafting request has been cancelled by {cancelledBy}.",
                archiveThread: !isUnclaim);

            var successMessage = isUnclaim
                ? "You've released this crafting request. It's back open for others."
                : "Your crafting request has been cancelled.";
            await FollowupAsync(
                WithReconciliationStatus(successMessage, cardUpdated),
                ephemeral: true);
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

            var professions = CraftTicketFilters.AvailableProfessions(tickets);

            var components = BuildListFilterComponents(professions, (long)Context.User.Id, scope);
            var card = CraftEmbedBuilder.BuildTicketListCard(
                tickets,
                scope == "mine" ? "Your Crafting Requests" : "Open Crafting Requests",
                components);

            await Context.Interaction.ModifyToV2Async(card.Build());
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

            var components = BuildBoardFilterComponents((long)Context.User.Id);
            var card = CraftEmbedBuilder.BuildTicketListCard(
                tickets,
                "Crafting Request Board",
                components);

            await Context.Interaction.ModifyToV2Async(card.Build());
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
