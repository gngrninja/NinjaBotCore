using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Mount collection commands - shows missing mounts and provides filtering/pagination.
    /// </summary>
    public class MountCommands : NinjaBotBaseModule
    {
        private readonly ILogger<MountCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly WowCacheService _wowCache;
        private readonly WowStaticDataService _wowStaticData;
        private readonly WowUtilities _wowUtils;

        public MountCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<MountCommands> logger,
            WowApi wowApi,
            WowCacheService wowCache,
            WowStaticDataService wowStaticData,
            WowUtilities wowUtils)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _wowCache = wowCache;
            _wowStaticData = wowStaticData;
            _wowUtils = wowUtils;
        }

        #region Slash Commands

        [SlashCommand("mounts-needed", "Show mounts you still need to collect")]
        public async Task GetMissingMounts(
            [Summary("character", "Character name (leave empty to use your main character)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character = null,

            [Summary("realm", "Realm name (optional if using autocomplete)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("RU", "ru")]
            string region = null,

            [Summary("expansion", "Filter by expansion")]
            [Choice("All Expansions", "all")]
            [Choice("Midnight", "Midnight")]
            [Choice("The War Within", "The War Within")]
            [Choice("Dragonflight", "Dragonflight")]
            [Choice("Shadowlands", "Shadowlands")]
            [Choice("Battle for Azeroth", "Battle for Azeroth")]
            [Choice("Legion", "Legion")]
            [Choice("Warlords of Draenor", "Warlords of Draenor")]
            [Choice("Mists of Pandaria", "Mists of Pandaria")]
            [Choice("Cataclysm", "Cataclysm")]
            [Choice("Wrath of the Lich King", "Wrath of the Lich King")]
            [Choice("The Burning Crusade", "The Burning Crusade")]
            [Choice("Classic", "Classic")]
            string expansion = "all",

            [Summary("source", "Filter by source type")]
            [Choice("All Sources", "all")]
            [Choice("Drops", "DROP")]
            [Choice("Vendor", "VENDOR")]
            [Choice("Achievement", "ACHIEVEMENT")]
            [Choice("Profession", "PROFESSION")]
            [Choice("Quest", "QUEST")]
            string source = "all",

            [Summary("obtainable", "Filter by availability")]
            [Choice("All Mounts", "all")]
            [Choice("Obtainable Only", "obtainable")]
            [Choice("Removed/Legacy Only", "removed")]
            string obtainable = "all",

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            try
            {
                await DeferAsync(ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to defer interaction for /mounts-needed command");
                await RespondAsync("The request took too long to process. Please try again.", ephemeral: true);
                return;
            }

            string charName = null;
            string realmName = null;
            string regionName = region;
            var embed = new EmbedBuilder();

            // Get character info
            if (string.IsNullOrEmpty(character))
            {
                var charAssociation = await _wowCache.GetUserMainCharacterAsync((long)Context.User.Id);

                if (charAssociation != null)
                {
                    charName = charAssociation.CharName;
                    realmName = charAssociation.WowRealm;
                    regionName ??= charAssociation.WowRegion;
                }
                else
                {
                    embed.Title = "No Main Character Set";
                    embed.WithColor(new Color(255, 165, 0));
                    embed.Description = "You haven't set a main character yet!\n\nUse `/getchars` to manage your saved characters.";
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
            }
            else
            {
                // Handle autocomplete format: "CharName~RealmName~Region"
                var parts = character.Split('~', 3);
                charName = parts[0];

                if (string.IsNullOrEmpty(realmName) && parts.Length >= 2)
                {
                    realmName = parts[1];
                }
                else if (!string.IsNullOrEmpty(realm))
                {
                    realmName = realm;
                }

                if (string.IsNullOrEmpty(regionName) && parts.Length >= 3)
                {
                    regionName = parts[2];
                }
            }

            if (string.IsNullOrEmpty(realmName))
            {
                var guildObject = await _wowUtils.GetGuildName(Context);

                if (!string.IsNullOrEmpty(guildObject.guildName))
                {
                    // Use realmSlug for API calls, fallback to slugifying realmName
                    var effectiveRealmSlug = !string.IsNullOrEmpty(guildObject.realmSlug)
                        ? guildObject.realmSlug
                        : guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "");

                    var guildie = await _wowApi.GetCharFromGuildAsync(
                        charName,
                        effectiveRealmSlug,
                        guildObject.guildName,
                        guildObject.regionName);

                    if (!string.IsNullOrEmpty(guildie.charName))
                    {
                        realmName = guildie.realmName;
                        regionName ??= guildie.regionName;
                    }
                }

                if (string.IsNullOrEmpty(realmName))
                {
                    var chars = await _wowApi.SearchArmoryAsync(charName);
                    if (chars != null && chars.Count > 0)
                    {
                        realmName = chars[0].realmName;
                    }
                    else
                    {
                        embed.Title = "Character Not Found";
                        embed.WithColor(new Color(255, 0, 0));
                        embed.Description = $"Could not find character **{charName}**.\n\nPlease specify the realm name using the `realm` parameter.";
                        await FollowupAsync(embed: embed.Build(), ephemeral: true);
                        return;
                    }
                }
            }

            regionName ??= "us";

            // Fetch character's mount collection
            MountCollectionResponse mountCollection;
            try
            {
                mountCollection = await _wowApi.GetCharacterMountsAsync(charName, realmName, regionName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching mount collection for {Character} on {Realm}", charName, realmName);
                embed.Title = "Error Fetching Mounts";
                embed.WithColor(new Color(255, 0, 0));
                embed.Description = $"Could not load mount collection for **{charName}** on **{realmName}** ({regionName.ToUpper()}).";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            if (mountCollection?.Mounts == null)
            {
                embed.Title = "No Mount Data";
                embed.WithColor(new Color(255, 165, 0));
                embed.Description = $"No mount collection data found for **{charName}** on **{realmName}**.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            // Get collected mount IDs
            var collectedMountIds = new HashSet<long>(mountCollection.Mounts.Select(m => m.Mount.Id));

            // Get all mounts from database
            var allMounts = await _wowStaticData.GetAllMountsAsync();

            if (allMounts == null || allMounts.Count == 0)
            {
                embed.Title = "Mount Database Empty";
                embed.WithColor(new Color(255, 165, 0));
                embed.Description = "The mount database is empty. Please contact the bot administrator to import mount data.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            // Apply filters
            var filteredMounts = allMounts.AsEnumerable();

            if (expansion != "all")
            {
                filteredMounts = filteredMounts.Where(m => string.Equals(m.Expansion, expansion, StringComparison.OrdinalIgnoreCase));
            }

            if (source != "all")
            {
                filteredMounts = filteredMounts.Where(m => string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase));
            }

            if (obtainable == "obtainable")
            {
                filteredMounts = filteredMounts.Where(m => m.IsObtainable);
            }
            else if (obtainable == "removed")
            {
                filteredMounts = filteredMounts.Where(m => !m.IsObtainable);
            }

            var filteredList = filteredMounts.ToList();

            // Find missing mounts
            var missingMounts = filteredList
                .Where(m => !collectedMountIds.Contains(m.Id))
                .OrderBy(m => m.Expansion)
                .ThenBy(m => m.Source)
                .ThenBy(m => m.Name)
                .ToList();

            var collectedCount = filteredList.Count(m => collectedMountIds.Contains(m.Id));
            var totalCount = filteredList.Count;

            if (missingMounts.Count == 0)
            {
                embed.Title = $"Missing Mounts - {charName}";
                embed.WithColor(new Color(138, 43, 226));
                embed.Description = $"**Collected:** {collectedCount}/{totalCount} ({(totalCount > 0 ? (collectedCount * 100.0 / totalCount) : 0):F1}%)";
                if (expansion != "all")
                {
                    embed.Description += $"\n**Expansion:** {expansion}";
                }
                if (source != "all")
                {
                    embed.Description += $"\n**Source:** {source}";
                }
                embed.AddField("Complete!", "You have collected all mounts in this category!");
                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
                return;
            }

            // Paginated display
            int page = 0;
            int pageSize = 10;
            var pageData = await BuildMountPageAsync(missingMounts, page, pageSize, charName, realmName, regionName, collectedCount, totalCount, source, expansion);

            var pageMounts = missingMounts.Skip(page * pageSize).Take(pageSize).ToList();
            var components = BuildMountPaginationComponents(page, missingMounts.Count, pageSize, charName, realmName, regionName, source, expansion, Context.User.Id, pageMounts);

            await FollowupAsync(embed: pageData.Build(), components: components.Build(), ephemeral: !publicDisplay);
        }

        #endregion

        #region Component Handlers

        [ComponentInteraction("mount_first~*~*~*~*~*~*~*")]
        public async Task HandleMountFirst(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, 0);
        }

        [ComponentInteraction("mount_prev~*~*~*~*~*~*~*")]
        public async Task HandleMountPrevious(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, Math.Max(0, currentPage - 1));
        }

        [ComponentInteraction("mount_next~*~*~*~*~*~*~*")]
        public async Task HandleMountNext(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, currentPage + 1);
        }

        [ComponentInteraction("mount_last~*~*~*~*~*~*~*~*")]
        public async Task HandleMountLast(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr, string totalPagesStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(totalPagesStr, out var totalPages))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, totalPages - 1);
        }

        [ComponentInteraction("mount_source~*~*~*~*~*")]
        public async Task HandleMountSourceFilter(string userIdStr, string charName, string realmName, string regionName, string expansionFilter, string[] selections)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This filter belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No source selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, selections[0], expansionFilter, 0);
        }

        [ComponentInteraction("mount_expansion~*~*~*~*~*")]
        public async Task HandleMountExpansionFilter(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string[] selections)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This filter belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No expansion selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, selections[0], 0);
        }

        [ComponentInteraction("mount_details~*~*~*~*~*~*")]
        public async Task HandleMountDetails(string userIdStr, string regionName, string charName, string realmName, string sourceFilter, string expansionFilter, string[] selections)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No mount selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            try
            {
                if (!long.TryParse(selections[0], out var mountId))
                {
                    await FollowupAsync("Invalid mount ID.", ephemeral: true);
                    return;
                }

                var mount = await WithDbAsync(async db =>
                    await db.WowMounts.FirstOrDefaultAsync(m => m.Id == mountId));

                if (mount == null)
                {
                    await FollowupAsync("Mount not found in database.", ephemeral: true);
                    return;
                }

                var embed = BuildMountDetailEmbed(mount);

                // Fetch mount image
                if (mount.CreatureDisplayId.HasValue)
                {
                    try
                    {
                        var creatureMedia = await _wowApi.GetCreatureDisplayMediaAsync(mount.CreatureDisplayId.Value, regionName);
                        if (creatureMedia?.Assets != null && creatureMedia.Assets.Count > 0)
                        {
                            var mainAsset = creatureMedia.Assets.FirstOrDefault(a => a.Key == "main")
                                ?? creatureMedia.Assets[0];
                            embed.WithImageUrl(mainAsset.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch media for mount {MountId}", mount.Id);
                    }
                }

                var backButton = new ComponentBuilder()
                    .WithButton("Back to List", $"mount_back~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}", ButtonStyle.Secondary)
                    .Build();

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = backButton;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error displaying mount details");
                await FollowupAsync("An error occurred while loading mount details.", ephemeral: true);
            }
        }

        [ComponentInteraction("mount_back~*~*~*~*~*~*")]
        public async Task HandleMountBack(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, 0);
        }

        #endregion

        #region Private Helpers

        private async Task UpdateMountPage(string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, int page)
        {
            try
            {
                var mountCollection = await _wowApi.GetCharacterMountsAsync(charName, realmName, regionName);
                if (mountCollection?.Mounts == null)
                {
                    await FollowupAsync("Could not load mount collection.", ephemeral: true);
                    return;
                }

                var collectedMountIds = new HashSet<long>(mountCollection.Mounts.Select(m => m.Mount.Id));
                var allMounts = await _wowStaticData.GetAllMountsAsync();

                var filteredMounts = allMounts.AsEnumerable();
                if (expansionFilter != "all")
                {
                    filteredMounts = filteredMounts.Where(m => string.Equals(m.Expansion, expansionFilter, StringComparison.OrdinalIgnoreCase));
                }
                if (sourceFilter != "all")
                {
                    filteredMounts = filteredMounts.Where(m => string.Equals(m.Source, sourceFilter, StringComparison.OrdinalIgnoreCase));
                }

                var filteredList = filteredMounts.ToList();
                var missingMounts = filteredList
                    .Where(m => !collectedMountIds.Contains(m.Id))
                    .OrderBy(m => m.Expansion)
                    .ThenBy(m => m.Source)
                    .ThenBy(m => m.Name)
                    .ToList();

                var collectedCount = filteredList.Count(m => collectedMountIds.Contains(m.Id));
                var totalCount = filteredList.Count;

                int pageSize = 10;
                var pageData = await BuildMountPageAsync(missingMounts, page, pageSize, charName, realmName, regionName, collectedCount, totalCount, sourceFilter, expansionFilter);

                var pageMounts = missingMounts.Skip(page * pageSize).Take(pageSize).ToList();
                var components = BuildMountPaginationComponents(page, missingMounts.Count, pageSize, charName, realmName, regionName, sourceFilter, expansionFilter, Context.User.Id, pageMounts);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = pageData.Build();
                    msg.Components = components.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating mount page");
                await FollowupAsync("An error occurred while updating the page.", ephemeral: true);
            }
        }

        private async Task<EmbedBuilder> BuildMountPageAsync(List<WowMounts> mounts, int page, int pageSize, string charName, string realmName, string regionName, int collectedCount, int totalCount, string sourceFilter, string expansionFilter)
        {
            var embed = new EmbedBuilder();
            embed.Title = $"Missing Mounts - {charName}";

            var progress = totalCount > 0 ? (collectedCount * 100.0 / totalCount) : 0;
            embed.WithColor(progress switch
            {
                >= 90 => new Color(0, 255, 0),
                >= 70 => new Color(138, 43, 226),
                >= 50 => new Color(255, 165, 0),
                _ => new Color(255, 87, 51)
            });

            var filledBlocks = (int)(progress / 10);
            var progressBar = new string('█', filledBlocks) + new string('░', 10 - filledBlocks);

            var description = $"**Collected:** {collectedCount}/{totalCount} ({progress:F1}%)\n{progressBar}";
            if (expansionFilter != "all") description += $"\n**Expansion:** {expansionFilter}";
            if (sourceFilter != "all") description += $"\n**Source:** {GetFriendlySourceName(sourceFilter)}";
            embed.Description = description;

            // Try to get character thumbnail
            try
            {
                var realmSlug = realmName.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
                var armoryMedia = await _wowApi.GetArmoryMediaAsync(charName, realmSlug, regionName);
                var avatarAsset = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar");
                if (avatarAsset != null && !string.IsNullOrEmpty(avatarAsset.Value))
                {
                    embed.WithThumbnailUrl(avatarAsset.Value);
                }
            }
            catch { /* Continue without thumbnail */ }

            // Expansion breakdown
            if (expansionFilter == "all" && mounts.Count > 0)
            {
                var stats = mounts
                    .Where(m => !string.IsNullOrEmpty(m.Expansion))
                    .GroupBy(m => m.Expansion)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => $"{g.Key}: **{g.Count()}**");

                if (stats.Any())
                {
                    embed.AddField("Missing by Expansion (Top 5)", string.Join(" | ", stats), inline: false);
                }
            }

            // Mount list - compact format
            var mountList = new StringBuilder();
            foreach (var mount in mounts.Skip(page * pageSize).Take(pageSize))
            {
                var typeEmoji = GetMountTypeEmoji(mount);
                var sourceEmoji = GetSourceEmoji(mount.Source);
                mountList.AppendLine($"{typeEmoji} **{mount.Name}**");
                mountList.AppendLine($"　{sourceEmoji} {GetDisplaySource(mount)}");
                mountList.AppendLine(); // Padding between entries
            }

            if (mountList.Length > 0)
            {
                embed.AddField("Mounts", mountList.ToString().TrimEnd(), inline: false);
            }

            var totalPages = (int)Math.Ceiling(mounts.Count / (double)pageSize);
            embed.Footer = new EmbedFooterBuilder { Text = $"Page {page + 1}/{totalPages} | {mounts.Count} missing • Use dropdown below for mount details" };

            return embed;
        }

        private EmbedBuilder BuildMountDetailEmbed(WowMounts mount)
        {
            var wowheadUrl = $"https://www.wowhead.com/search?q={Uri.EscapeDataString(mount.Name)}";
            var embed = new EmbedBuilder()
                .WithTitle(mount.Name)
                .WithColor(new Color(138, 43, 226))
                .WithUrl(wowheadUrl);

            if (!string.IsNullOrEmpty(mount.Description))
            {
                embed.WithDescription($"*{mount.Description}*");
            }

            embed.AddField($"{GetSourceEmoji(mount.Source)} Source", GetDisplaySource(mount), inline: false);

            if (!string.IsNullOrEmpty(mount.DropLocation))
                embed.AddField("Location", mount.DropLocation, inline: true);
            if (!string.IsNullOrEmpty(mount.Expansion))
                embed.AddField("Expansion", mount.Expansion, inline: true);
            if (!string.IsNullOrEmpty(mount.Faction))
            {
                var emoji = mount.Faction.ToLower() == "alliance" ? "🔵" : mount.Faction.ToLower() == "horde" ? "🔴" : "⚪";
                embed.AddField("Faction", $"{emoji} {mount.Faction}", inline: true);
            }

            var types = new List<string>();
            if (mount.IsGround) types.Add("Ground");
            if (mount.IsFlying) types.Add("Flying");
            if (mount.IsAquatic) types.Add("Aquatic");
            if (types.Any()) embed.AddField("Type", string.Join(", ", types), inline: true);

            embed.WithFooter($"Mount ID: {mount.Id} | Last updated: {mount.LastUpdated:yyyy-MM-dd}");
            return embed;
        }

        private ComponentBuilder BuildMountPaginationComponents(int currentPage, int totalMounts, int pageSize, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, ulong userId, List<WowMounts> pageMounts)
        {
            var totalPages = (int)Math.Ceiling(totalMounts / (double)pageSize);
            var builder = new ComponentBuilder();

            // Expansion filter (Row 0) - uses shared WowExpansions constants
            var expansionOptions = new List<SelectMenuOptionBuilder>
            {
                new("All Expansions", "all", isDefault: expansionFilter == "all")
            };
            foreach (var expansion in Common.WowExpansions.All)
            {
                expansionOptions.Add(new(expansion, expansion, isDefault: expansionFilter == expansion));
            }
            builder.WithSelectMenu($"mount_expansion~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}", expansionOptions, "Filter by Expansion", row: 0);

            // Source filter (Row 1)
            var sourceOptions = new List<SelectMenuOptionBuilder>
            {
                new("All Sources", "all", isDefault: sourceFilter == "all"),
                new("Drops (Raids & Dungeons)", "DROP", isDefault: sourceFilter == "DROP"),
                new("Achievements", "ACHIEVEMENT", isDefault: sourceFilter == "ACHIEVEMENT"),
                new("Vendors", "VENDOR", isDefault: sourceFilter == "VENDOR"),
                new("Quests", "QUEST", isDefault: sourceFilter == "QUEST"),
                new("Professions", "PROFESSION", isDefault: sourceFilter == "PROFESSION"),
                new("World Events", "WORLD_EVENT", isDefault: sourceFilter == "WORLD_EVENT")
            };
            builder.WithSelectMenu($"mount_source~{userId}~{charName}~{realmName}~{regionName}~{expansionFilter}", sourceOptions, "Filter by Source", row: 1);

            // Mount details (Row 2)
            if (pageMounts?.Any() == true)
            {
                var mountOptions = pageMounts.Select(m => new SelectMenuOptionBuilder(
                    m.Name.Length > 100 ? m.Name[..97] + "..." : m.Name,
                    m.Id.ToString(),
                    $"{m.Expansion ?? "Unknown"} | {m.Source}"
                )).ToList();
                builder.WithSelectMenu($"mount_details~{userId}~{regionName}~{charName}~{realmName}~{sourceFilter}~{expansionFilter}", mountOptions, "View mount details...", row: 2);
            }

            // Pagination buttons (Row 3)
            builder.WithButton("First", $"mount_first~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}", ButtonStyle.Secondary, disabled: currentPage == 0, row: 3);
            builder.WithButton("Previous", $"mount_prev~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}", ButtonStyle.Primary, disabled: currentPage == 0, row: 3);
            builder.WithButton($"Page {currentPage + 1}/{totalPages}", "mount_page_info", ButtonStyle.Secondary, disabled: true, row: 3);
            builder.WithButton("Next", $"mount_next~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}", ButtonStyle.Primary, disabled: currentPage >= totalPages - 1, row: 3);
            builder.WithButton("Last", $"mount_last~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}~{totalPages}", ButtonStyle.Secondary, disabled: currentPage >= totalPages - 1, row: 3);

            return builder;
        }

        private static string GetMountTypeEmoji(WowMounts mount)
        {
            if (mount.IsFlying && mount.IsGround) return "🦅"; // Flying (can also walk)
            if (mount.IsFlying) return "🦅";
            if (mount.IsAquatic) return "🐠";
            return "🐎"; // Ground
        }

        private static string GetSourceEmoji(string source) => source?.ToUpper() switch
        {
            "DROP" => "💀",
            "ACHIEVEMENT" => "🏆",
            "VENDOR" => "💰",
            "QUEST" => "❗",
            "PROFESSION" => "🔨",
            "WORLD_EVENT" => "🎃",
            "PROMOTION" => "🎁",
            "TRADING_POST" => "🏪",
            "STORE" => "🛒",
            "PVP" => "⚔️",
            "REPUTATION" => "📜",
            "CLASS" => "🎭",
            "COVENANT" => "🔮",
            "GARRISON" => "🏰",
            _ => "📍"
        };

        private static string GetFriendlySourceName(string source) => source?.ToUpper() switch
        {
            "DROP" => "Boss Drops",
            "ACHIEVEMENT" => "Achievements",
            "VENDOR" => "Vendors",
            "QUEST" => "Quests",
            "PROFESSION" => "Crafted",
            "WORLD_EVENT" => "Holiday Events",
            "PROMOTION" => "Promotional",
            "TRADING_POST" => "Trading Post",
            "STORE" => "Blizzard Store",
            "PVP" => "PvP Rewards",
            "REPUTATION" => "Reputation",
            "CLASS" => "Class Mounts",
            "COVENANT" => "Covenant",
            "GARRISON" => "Garrison",
            _ => source ?? "Unknown"
        };

        private static string GetDisplaySource(WowMounts mount)
        {
            // For vendors: show vendor name with optional zone
            if (mount.Source?.Equals("VENDOR", StringComparison.OrdinalIgnoreCase) == true)
            {
                var vendorName = mount.SourceDetail;
                if (!string.IsNullOrEmpty(vendorName) && !vendorName.Equals("Vendor", StringComparison.OrdinalIgnoreCase))
                {
                    return !string.IsNullOrEmpty(mount.InstanceName)
                        ? $"{vendorName} ({mount.InstanceName})"
                        : vendorName;
                }
            }

            // For raid/dungeon drops: show instance + boss
            if (!string.IsNullOrEmpty(mount.InstanceName))
            {
                return !string.IsNullOrEmpty(mount.EncounterName)
                    ? $"{mount.InstanceName} - {mount.EncounterName}"
                    : mount.InstanceName;
            }

            // Fallback to SourceDetail if not generic
            var detail = mount.SourceDetail;
            var isGeneric = string.IsNullOrEmpty(detail) ||
                detail.Equals(mount.Source, StringComparison.OrdinalIgnoreCase) ||
                detail.Equals("Drop", StringComparison.OrdinalIgnoreCase) ||
                detail.Equals("Achievement", StringComparison.OrdinalIgnoreCase) ||
                detail.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ||
                detail.Equals("Quest", StringComparison.OrdinalIgnoreCase);

            if (!isGeneric)
                return detail;

            // Last fallback: DropLocation or friendly source name
            if (!string.IsNullOrEmpty(mount.DropLocation))
                return mount.DropLocation;

            return GetFriendlySourceName(mount.Source);
        }

        #endregion
    }
}
