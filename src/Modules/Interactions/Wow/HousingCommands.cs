using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Housing;
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
    /// Housing-related commands for WoW's housing feature.
    /// </summary>
    public class HousingCommands : NinjaBotBaseModule
    {
        private readonly ILogger<HousingCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly WowCacheService _wowCache;
        private readonly WowStaticDataService _wowStaticData;

        private const int PageSize = 10;

        public HousingCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<HousingCommands> logger,
            WowApi wowApi,
            WowCacheService wowCache,
            WowStaticDataService wowStaticData)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _wowCache = wowCache;
            _wowStaticData = wowStaticData;
        }

        [SlashCommand("housing-collection", "View your housing decor collection progress")]
        public async Task GetDecorCollection(
            [Summary("character", "Character name (leave empty to use your main character)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character = null,

            [Summary("realm", "Realm name (optional if using autocomplete)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = null,

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

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

            regionName ??= "us";

            if (string.IsNullOrEmpty(realmName))
            {
                embed.Title = "Realm Required";
                embed.WithColor(Color.Red);
                embed.Description = "Please specify a realm for the character.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            try
            {
                // Fetch character's decor collection
                var decorCollection = await _wowApi.GetCharacterDecorAsync(charName, realmName, regionName);

                if (decorCollection?.Decor == null)
                {
                    embed.Title = "No Decor Collection Found";
                    embed.WithColor(Color.Orange);
                    embed.Description = $"Could not find decor collection for **{charName}** on **{realmName}**.\n\nThis could mean:\n- The character doesn't exist\n- The character's profile is private\n- Housing hasn't been unlocked yet";
                    await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
                    return;
                }

                // Get total decor count from database
                int totalDecor = await _wowStaticData.GetHousingDecorCountAsync();
                if (totalDecor == 0)
                {
                    embed.Title = "Static Data Not Loaded";
                    embed.WithColor(Color.Orange);
                    embed.Description = $"Housing decor static data hasn't been synced yet.\n\nAn admin can run `/sync trigger` with type **Housing Decor** to import the data.";
                    await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
                    return;
                }

                int collectedCount = decorCollection.Decor.Count;
                int missingCount = totalDecor - collectedCount;
                int totalQuantity = decorCollection.Decor.Sum(d => d.Quantity);

                // Build progress bar
                double percentage = totalDecor > 0 ? (double)collectedCount / totalDecor * 100 : 0;
                var progressBar = GetProgressBar(percentage);

                embed.WithTitle($"🏠 Housing Collection: {charName}");
                embed.WithColor(GetCollectionColor(percentage));

                var sb = new StringBuilder();
                sb.AppendLine($"**{progressBar}**");
                sb.AppendLine();
                sb.AppendLine($"📦 **Unique Decor:** {collectedCount:N0} / {totalDecor:N0} ({percentage:F1}%)");
                sb.AppendLine($"❌ **Missing:** {missingCount:N0} items");
                sb.AppendLine($"🎁 **Total Items:** {totalQuantity:N0} (including duplicates)");

                // Show some recent/notable items if we have them
                if (decorCollection.Decor.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("**Top Collected Items:**");

                    var topItems = decorCollection.Decor
                        .OrderByDescending(d => d.Quantity)
                        .Take(5);

                    foreach (var item in topItems)
                    {
                        var name = item.DecorRef?.Name ?? "Unknown";
                        var qty = item.Quantity > 1 ? $" (x{item.Quantity})" : "";
                        sb.AppendLine($"  • {name}{qty}");
                    }
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"{realmName} ({regionName.ToUpper()})");

                // Build components - only show browse button if there are missing items
                ComponentBuilder components = null;
                if (missingCount > 0)
                {
                    components = new ComponentBuilder()
                        .WithButton("Browse Missing",
                            $"housing_browse~{Context.User.Id}~{charName}~{realmName}~{regionName}~0",
                            ButtonStyle.Primary,
                            emote: new Emoji("🔍"));
                }

                await FollowupAsync(embed: embed.Build(), components: components?.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching decor collection for {Character}-{Realm}", charName, realmName);
                embed.Title = "Error";
                embed.WithColor(Color.Red);
                embed.Description = "An error occurred while fetching your decor collection. The character may not exist or have housing unlocked.";
                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
        }

        #region Component Handlers

        [ComponentInteraction("housing_browse~*~*~*~*~*")]
        public async Task HandleBrowseMissing(string userIdStr, string charName, string realm, string region, string pageStr)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This button belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, int.Parse(pageStr), "");
        }

        // Pagination handlers - each button type has unique prefix and calculates target page
        // Pattern: housing_{action}~{userId}~{charName}~{realm}~{region}~{search}~{currentPage}[~{totalPages}]
        // Note: search uses "_" as placeholder for empty to avoid ~~ in custom IDs
        [ComponentInteraction("housing_first~*~*~*~*~*~*")]
        public async Task HandleFirst(string userIdStr, string charName, string realm, string region, string search, string currentPageStr)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, 0, DecodeSearch(search));
        }

        [ComponentInteraction("housing_prev~*~*~*~*~*~*")]
        public async Task HandlePrev(string userIdStr, string charName, string realm, string region, string search, string currentPageStr)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, Math.Max(0, currentPage - 1), DecodeSearch(search));
        }

        [ComponentInteraction("housing_next~*~*~*~*~*~*")]
        public async Task HandleNext(string userIdStr, string charName, string realm, string region, string search, string currentPageStr)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, currentPage + 1, DecodeSearch(search));
        }

        [ComponentInteraction("housing_last~*~*~*~*~*~*~*")]
        public async Task HandleLast(string userIdStr, string charName, string realm, string region, string search, string currentPageStr, string totalPagesStr)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(totalPagesStr, out var totalPages))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, totalPages - 1, DecodeSearch(search));
        }

        /// <summary>
        /// Decode search param from custom ID ("_" = empty string)
        /// </summary>
        private static string DecodeSearch(string search) => search == "_" ? "" : search;

        [ComponentInteraction("housing_details~*~*~*~*~*~*")]
        public async Task HandleDetails(string userIdStr, string charName, string realm, string region, string search, string currentPageStr, string[] selections)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No item selected.", ephemeral: true);
                return;
            }

            await DeferAsync();

            try
            {
                if (!long.TryParse(selections[0], out var decorId))
                {
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = new EmbedBuilder()
                            .WithTitle("Error")
                            .WithDescription("Invalid decor ID.")
                            .WithColor(Color.Red)
                            .Build();
                    });
                    return;
                }

                var decor = await WithDbAsync(async db =>
                    await db.HousingDecor.FirstOrDefaultAsync(d => d.Id == decorId));

                if (decor == null)
                {
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = new EmbedBuilder()
                            .WithTitle("Not Found")
                            .WithDescription("Decor item not found in database.")
                            .WithColor(Color.Orange)
                            .Build();
                    });
                    return;
                }

                var embed = BuildDecorDetailEmbed(decor);
                var components = BuildDecorDetailComponents(charName, realm, region, DecodeSearch(search), currentPageStr);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = components;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error displaying decor details for {DecorId}", selections[0]);
                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = new EmbedBuilder()
                        .WithTitle("Error")
                        .WithDescription("An error occurred while loading decor details.")
                        .WithColor(Color.Red)
                        .Build();
                });
            }
        }

        [ComponentInteraction("housing_detail_back~*~*~*~*~*~*")]
        public async Task HandleDetailBack(string userIdStr, string charName, string realm, string region, string search, string pageStr)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(pageStr, out var page))
            {
                page = 0;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, page, DecodeSearch(search));
        }

        [ComponentInteraction("housing_search~*~*~*~*")]
        public async Task HandleSearchButton(string userIdStr, string charName, string realm, string region)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This button belongs to another user.", ephemeral: true);
                return;
            }

            var modal = new ModalBuilder()
                .WithTitle("Search Missing Decor")
                .WithCustomId($"housing_search_modal~{userIdStr}~{charName}~{realm}~{region}")
                .AddTextInput("Decor Name", "search_term", TextInputStyle.Short,
                    placeholder: "Enter part of the decor name...", maxLength: 100, required: true);

            await RespondWithModalAsync(modal.Build());
        }

        [ModalInteraction("housing_search_modal~*~*~*~*")]
        public async Task HandleSearchModal(string userIdStr, string charName, string realm, string region, HousingSearchModal modal)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This modal belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, 0, modal.SearchTerm?.Trim() ?? "");
        }

        [ComponentInteraction("housing_clear~*~*~*~*")]
        public async Task HandleClearSearch(string userIdStr, string charName, string realm, string region)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This button belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();
            await UpdateMissingDecorPage(charName, realm, region, 0, "");
        }

        [ComponentInteraction("housing_back~*~*~*~*")]
        public async Task HandleBackToSummary(string userIdStr, string charName, string realm, string region)
        {
            if (!ValidateUser(userIdStr))
            {
                await RespondAsync("This button belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();

            try
            {
                // Re-fetch and display the summary
                var decorCollection = await _wowApi.GetCharacterDecorAsync(charName, realm, region);
                int totalDecor = await _wowStaticData.GetHousingDecorCountAsync();

                if (decorCollection?.Decor == null || totalDecor == 0)
                {
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = new EmbedBuilder()
                            .WithTitle("Error")
                            .WithDescription("Could not reload collection data.")
                            .WithColor(Color.Red)
                            .Build();
                        msg.Components = null;
                    });
                    return;
                }

                int collectedCount = decorCollection.Decor.Count;
                int missingCount = totalDecor - collectedCount;
                int totalQuantity = decorCollection.Decor.Sum(d => d.Quantity);
                double percentage = (double)collectedCount / totalDecor * 100;
                var progressBar = GetProgressBar(percentage);

                var embed = new EmbedBuilder()
                    .WithTitle($"🏠 Housing Collection: {charName}")
                    .WithColor(GetCollectionColor(percentage));

                var sb = new StringBuilder();
                sb.AppendLine($"**{progressBar}**");
                sb.AppendLine();
                sb.AppendLine($"📦 **Unique Decor:** {collectedCount:N0} / {totalDecor:N0} ({percentage:F1}%)");
                sb.AppendLine($"❌ **Missing:** {missingCount:N0} items");
                sb.AppendLine($"🎁 **Total Items:** {totalQuantity:N0} (including duplicates)");

                if (decorCollection.Decor.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("**Top Collected Items:**");
                    foreach (var item in decorCollection.Decor.OrderByDescending(d => d.Quantity).Take(5))
                    {
                        var name = item.DecorRef?.Name ?? "Unknown";
                        var qty = item.Quantity > 1 ? $" (x{item.Quantity})" : "";
                        sb.AppendLine($"  • {name}{qty}");
                    }
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"{realm} ({region.ToUpper()})");

                ComponentBuilder components = null;
                if (missingCount > 0)
                {
                    components = new ComponentBuilder()
                        .WithButton("Browse Missing",
                            $"housing_browse~{Context.User.Id}~{charName}~{realm}~{region}~0",
                            ButtonStyle.Primary,
                            emote: new Emoji("🔍"));
                }

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components?.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error returning to summary");
                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = new EmbedBuilder()
                        .WithTitle("Error")
                        .WithDescription("An error occurred while loading the summary.")
                        .WithColor(Color.Red)
                        .Build();
                    msg.Components = null;
                });
            }
        }

        #endregion

        #region Helper Methods

        private async Task UpdateMissingDecorPage(string charName, string realm, string region, int page, string search)
        {
            try
            {
                // Fetch character's collected decor
                var decorCollection = await _wowApi.GetCharacterDecorAsync(charName, realm, region);
                if (decorCollection?.Decor == null)
                {
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = new EmbedBuilder()
                            .WithTitle("Error")
                            .WithDescription("Could not fetch character decor collection.")
                            .WithColor(Color.Red)
                            .Build();
                        msg.Components = null;
                    });
                    return;
                }

                // Get collected IDs
                var collectedIds = decorCollection.Decor
                    .Where(d => d.DecorRef != null)
                    .Select(d => d.DecorRef.Id)
                    .ToHashSet();

                // Get missing decor from database
                var missingDecor = await _wowStaticData.GetMissingDecorAsync(collectedIds, search);

                if (missingDecor.Count == 0)
                {
                    var noResultsEmbed = new EmbedBuilder()
                        .WithTitle($"🏠 Missing Decor: {charName}")
                        .WithColor(Color.Green);

                    if (!string.IsNullOrEmpty(search))
                    {
                        noResultsEmbed.WithDescription($"No missing decor matches your search: **{search}**");
                    }
                    else
                    {
                        noResultsEmbed.WithDescription("Congratulations! You have collected all available housing decor!");
                    }

                    var backComponents = new ComponentBuilder()
                        .WithButton("Back to Summary",
                            $"housing_back~{Context.User.Id}~{charName}~{realm}~{region}",
                            ButtonStyle.Secondary,
                            emote: new Emoji("◀"));

                    if (!string.IsNullOrEmpty(search))
                    {
                        backComponents.WithButton("Clear Search",
                            $"housing_clear~{Context.User.Id}~{charName}~{realm}~{region}",
                            ButtonStyle.Secondary);
                    }

                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = noResultsEmbed.Build();
                        msg.Components = backComponents.Build();
                    });
                    return;
                }

                // Calculate pagination
                int totalPages = (int)Math.Ceiling((double)missingDecor.Count / PageSize);
                page = Math.Clamp(page, 0, totalPages - 1);

                var pageItems = missingDecor
                    .Skip(page * PageSize)
                    .Take(PageSize)
                    .ToList();

                // Build embed
                var embed = BuildMissingDecorEmbed(charName, pageItems, missingDecor.Count, page, totalPages, search);
                var components = BuildMissingDecorComponents(charName, realm, region, page, totalPages, search, pageItems);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = components;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating missing decor page");
                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = new EmbedBuilder()
                        .WithTitle("Error")
                        .WithDescription("An error occurred while loading missing decor.")
                        .WithColor(Color.Red)
                        .Build();
                    msg.Components = null;
                });
            }
        }

        private Embed BuildMissingDecorEmbed(string charName, List<HousingDecor> items, int totalMissing, int page, int totalPages, string search)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"🏠 Missing Decor: {charName}")
                .WithColor(new Color(0, 176, 240));

            var sb = new StringBuilder();

            // Show search filter if active
            if (!string.IsNullOrEmpty(search))
            {
                sb.AppendLine($"🔍 **Filter:** {search}");
                sb.AppendLine();
            }

            sb.AppendLine($"Showing {page * PageSize + 1}-{Math.Min((page + 1) * PageSize, totalMissing)} of **{totalMissing}** missing items");
            sb.AppendLine();

            foreach (var item in items)
            {
                if (item.LinkedItemId.HasValue)
                {
                    var wowheadUrl = $"https://www.wowhead.com/item={item.LinkedItemId}";
                    sb.AppendLine($"• **{item.Name}** - [Wowhead]({wowheadUrl})");
                }
                else
                {
                    sb.AppendLine($"• **{item.Name}**");
                }
            }

            embed.WithDescription(sb.ToString());
            embed.WithFooter($"Page {page + 1}/{totalPages} | Use buttons to navigate");

            return embed.Build();
        }

        private MessageComponent BuildMissingDecorComponents(string charName, string realm, string region, int page, int totalPages, string search, List<HousingDecor> pageItems)
        {
            var userId = Context.User.Id;
            // Use "_" as placeholder for empty search to avoid ~~ in custom IDs
            var searchParam = string.IsNullOrEmpty(search) ? "_" : search;

            var builder = new ComponentBuilder();

            // Row 0 - Search buttons
            builder.WithButton("Search",
                $"housing_search~{userId}~{charName}~{realm}~{region}",
                ButtonStyle.Secondary,
                emote: new Emoji("🔍"),
                row: 0);

            if (!string.IsNullOrEmpty(search))
            {
                builder.WithButton("Clear Search",
                    $"housing_clear~{userId}~{charName}~{realm}~{region}",
                    ButtonStyle.Secondary,
                    row: 0);
            }

            // Row 1 - Item details dropdown
            if (pageItems?.Any() == true)
            {
                var options = pageItems.Select(d => new SelectMenuOptionBuilder(
                    d.Name.Length > 100 ? d.Name[..97] + "..." : d.Name,
                    d.Id.ToString(),
                    d.LinkedItemId.HasValue ? "Has Wowhead link" : "No item link"
                )).ToList();

                builder.WithSelectMenu(
                    $"housing_details~{userId}~{charName}~{realm}~{region}~{searchParam}~{page}",
                    options,
                    "View decor details...",
                    row: 1);
            }

            // Row 2 - Pagination
            // Pattern: housing_{action}~{userId}~{charName}~{realm}~{region}~{search}~{currentPage}
            // Each button has unique prefix to avoid duplicate custom IDs
            // Handlers calculate target page from currentPage
            builder.WithButton("First",
                $"housing_first~{userId}~{charName}~{realm}~{region}~{searchParam}~{page}",
                ButtonStyle.Primary,
                disabled: page == 0,
                row: 2);

            builder.WithButton("Prev",
                $"housing_prev~{userId}~{charName}~{realm}~{region}~{searchParam}~{page}",
                ButtonStyle.Primary,
                disabled: page == 0,
                row: 2);

            builder.WithButton($"{page + 1}/{totalPages}",
                "housing_page_info",
                ButtonStyle.Secondary,
                disabled: true,
                row: 2);

            builder.WithButton("Next",
                $"housing_next~{userId}~{charName}~{realm}~{region}~{searchParam}~{page}",
                ButtonStyle.Primary,
                disabled: page >= totalPages - 1,
                row: 2);

            // Last button includes totalPages since handler needs it to calculate target
            builder.WithButton("Last",
                $"housing_last~{userId}~{charName}~{realm}~{region}~{searchParam}~{page}~{totalPages}",
                ButtonStyle.Primary,
                disabled: page >= totalPages - 1,
                row: 2);

            // Row 3 - Back button
            builder.WithButton("Back to Summary",
                $"housing_back~{userId}~{charName}~{realm}~{region}",
                ButtonStyle.Secondary,
                emote: new Emoji("◀"),
                row: 3);

            return builder.Build();
        }

        private bool ValidateUser(string userIdStr)
        {
            return ulong.TryParse(userIdStr, out var userId) && Context.User.Id == userId;
        }

        private Embed BuildDecorDetailEmbed(HousingDecor decor)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"🏠 {decor.Name}")
                .WithColor(new Color(0, 176, 240));

            var sb = new StringBuilder();

            sb.AppendLine($"**Decor ID:** {decor.Id}");

            if (decor.LinkedItemId.HasValue)
            {
                var wowheadUrl = $"https://www.wowhead.com/item={decor.LinkedItemId}";
                sb.AppendLine();
                sb.AppendLine($"🔗 **[View on Wowhead]({wowheadUrl})**");
            }

            embed.WithDescription(sb.ToString());

            // Set icon as thumbnail if available
            if (!string.IsNullOrEmpty(decor.IconUrl))
            {
                embed.WithThumbnailUrl(decor.IconUrl);
            }

            return embed.Build();
        }

        private MessageComponent BuildDecorDetailComponents(string charName, string realm, string region, string search, string pageStr)
        {
            var userId = Context.User.Id;
            var searchParam = string.IsNullOrEmpty(search) ? "_" : search;

            var builder = new ComponentBuilder()
                .WithButton("Back to List",
                    $"housing_detail_back~{userId}~{charName}~{realm}~{region}~{searchParam}~{pageStr}",
                    ButtonStyle.Secondary,
                    emote: new Emoji("◀"));

            return builder.Build();
        }

        private static Color GetCollectionColor(double percentage)
        {
            return percentage switch
            {
                >= 90 => Color.Green,
                >= 70 => Color.Purple,
                >= 50 => Color.Orange,
                _ => new Color(0, 176, 240) // Blue for < 50%
            };
        }

        private static string GetProgressBar(double percentage)
        {
            const int barLength = 20;
            int filled = (int)(percentage / 100 * barLength);
            int empty = barLength - filled;

            return $"[{new string('█', filled)}{new string('░', empty)}] {percentage:F1}%";
        }

        #endregion

        #region Search Modal

        public class HousingSearchModal : IModal
        {
            public string Title => "Search Missing Decor";

            [InputLabel("Decor Name")]
            [ModalTextInput("search_term", TextInputStyle.Short, placeholder: "Enter part of the decor name...", maxLength: 100)]
            public string SearchTerm { get; set; }
        }

        #endregion

        [SlashCommand("housing-search-decor", "Search for housing decor items")]
        public async Task SearchDecor(
            [Summary("name", "Decor item name to search for")]
            string name,
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var encodedName = Uri.EscapeDataString(name);
                var url = $"/data/wow/search/decor?namespace=static-us&name.en_US={encodedName}&_pageSize=10";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var decorSearch = JsonConvert.DeserializeObject<DecorSearchResponse>(response);

                if (decorSearch?.Results == null || decorSearch.Results.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Results Found")
                        .WithDescription($"No decor items found matching '{name}'")
                        .WithColor(Color.Orange)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"🔍 Decor Search: {name}")
                    .WithColor(new Color(0, 176, 240));

                var sb = new StringBuilder();
                foreach (var result in decorSearch.Results.Take(10))
                {
                    sb.Append($"🏠 **{result.Data.Name.EnUS}** (ID: {result.Data.Id})");
                    if (result.Data.Item != null)
                    {
                        var wowheadUrl = $"https://www.wowhead.com/item={result.Data.Item.Id}";
                        sb.Append($" — [Item: {result.Data.Item.Name?.EnUS ?? "Unknown"}]({wowheadUrl})");
                    }
                    sb.AppendLine();
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Showing {Math.Min(10, decorSearch.Results.Count)} of {decorSearch.Results.Count} results");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching decor");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while searching for decor items.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }
    }
}
