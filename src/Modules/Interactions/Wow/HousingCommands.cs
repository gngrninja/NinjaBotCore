using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow.Housing;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
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

        public HousingCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<HousingCommands> logger,
            WowApi wowApi,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _wowCache = wowCache;
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

                // Get total decor count from the index
                int totalDecor = 0;
                try
                {
                    var indexUrl = "/data/wow/decor/index?namespace=static-us";
                    var indexResponse = await _wowApi.GetAPIRequestAsync(indexUrl, "en_US", "us");
                    var decorIndex = JsonConvert.DeserializeObject<DecorIndexResponse>(indexResponse);
                    totalDecor = decorIndex?.DecorItems?.Count ?? 0;
                }
                catch
                {
                    // If we can't get total, we'll just show collected count
                }

                int collectedCount = decorCollection.Decor.Count;
                int totalQuantity = decorCollection.Decor.Sum(d => d.Quantity);

                // Build progress bar
                double percentage = totalDecor > 0 ? (double)collectedCount / totalDecor * 100 : 0;
                var progressBar = GetProgressBar(percentage);

                embed.WithTitle($"🏠 Housing Collection: {charName}");
                embed.WithColor(new Color(0, 176, 240));

                var sb = new StringBuilder();
                sb.AppendLine($"**{progressBar}**");
                sb.AppendLine();

                if (totalDecor > 0)
                {
                    sb.AppendLine($"📦 **Unique Decor:** {collectedCount:N0} / {totalDecor:N0} ({percentage:F1}%)");
                }
                else
                {
                    sb.AppendLine($"📦 **Unique Decor:** {collectedCount:N0}");
                }

                sb.AppendLine($"🎁 **Total Items:** {totalQuantity:N0} (including duplicates)");

                // Show some recent/notable items if we have them
                if (decorCollection.Decor.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("**Recent Decor Items:**");

                    // Show up to 5 items with highest quantity or just first 5
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
                embed.WithFooter($"{realmName} ({regionName.ToUpper()}) • Use /housing-search-decor to find specific items");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
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

        private static string GetProgressBar(double percentage)
        {
            const int barLength = 20;
            int filled = (int)(percentage / 100 * barLength);
            int empty = barLength - filled;

            return $"[{'█'.ToString().PadRight(filled, '█')}{'░'.ToString().PadRight(empty, '░')}] {percentage:F1}%";
        }
    }
}
