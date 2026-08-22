using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// WoW utility commands that are relatively standalone.
    /// Includes: /affixes, /wowdiscord, /realminfo, /item, /token
    /// </summary>
    public class WowUtilityCommands : NinjaBotBaseModule
    {
        private readonly ILogger<WowUtilityCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly RaiderIOApi _rioApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;
        private readonly WowStaticDataService _wowStaticData;
        private readonly WowTokenService _tokenService;

        public WowUtilityCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<WowUtilityCommands> logger,
            WowApi wowApi,
            RaiderIOApi rioApi,
            WowUtilities wowUtils,
            WowCacheService wowCache,
            WowStaticDataService wowStaticData,
            WowTokenService tokenService)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _rioApi = rioApi;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
            _wowStaticData = wowStaticData;
            _tokenService = tokenService;
        }

        [SlashCommand("affixes", "Get current m+ affixes")]
        public async Task GetAffixes()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            var guildInfo = Context.Guild;

            string title = string.Empty;
            string discordGuildName = string.Empty;
            string thumbUrl = string.Empty;
            string region = string.Empty;

            if (guildInfo == null)
            {
                discordGuildName = Context.User.Username;
                thumbUrl = Context.User.GetAvatarUrl();
            }
            else
            {
                discordGuildName = Context.Guild.Name;
                thumbUrl = Context.Guild.IconUrl;
            }

            var guildObject = await _wowUtils.GetGuildName(Context);
            RaiderIOModels.Affix affixes = null;

            switch (guildObject.regionName.ToLower())
            {
                case "us":
                    {
                        region = "us";
                        break;
                    }
                case "eu":
                    {
                        region = "eu";
                        break;
                    }
                default:
                    {
                        region = "us";
                        break;
                    }
            }

            affixes = await _rioApi.GetCurrentAffixAsync(region: region);

            title = $"Current M+ Affixes ({region})";

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            embed.WithColor(new Color(0, 255, 0));
            string affixLevel = string.Empty;
            foreach (var detail in affixes.AffixDetails)
            {
                sb.AppendLine($"[{detail.Name}]({detail.WowheadUrl})");
                sb.AppendLine($"\t*{detail.Description}*");
                sb.AppendLine();
            }

            sb.AppendLine($"[Leaderboard]({affixes.LeaderboardUrl.AbsoluteUri})");
            embed.Description = sb.ToString();

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("wowdiscord", "list class discord servers")]
        public async Task ListWowDiscordServers()
        {
            try
            {
                var resourceList = await _wowCache.GetWowResourcesAsync("Discord");

                if (resourceList != null && resourceList.Any())
                {
                    var embed = new EmbedBuilder();
                    embed.Title = $"WoW Class Discord List";
                    foreach (var resource in resourceList)
                    {
                        embed.AddField(new EmbedFieldBuilder
                        {
                            Name = $"{resource.ClassName}",
                            Value = $"{resource.Resource}",
                            IsInline = true
                        });
                    }
                    embed.WithColor(new Color(0, 255, 0));
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error listing channels: [{ex.Message}]");
                await RespondAsync($"Sorry, {Context.User.Username}, something went wrong :(");
            }
        }

        [SlashCommand("realminfo", "Get detailed information about a WoW realm")]
        public async Task GetRealmInfo(
            [Summary("realm", "Realm name (use autocomplete to select)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to your guild's region or US)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("RU", "ru")]
            string region = null)
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            var guildInfo = await _wowUtils.GetGuildName(Context);

            // Determine region
            string regionName = region?.ToLower() ?? guildInfo.regionName?.ToLower() ?? "us";

            // Determine realm slug
            string realmSlug = realm;
            if (string.IsNullOrEmpty(realmSlug))
            {
                // If no realm specified, use guild's realm
                if (!string.IsNullOrEmpty(guildInfo.realmSlug))
                {
                    realmSlug = guildInfo.realmSlug;
                }
                else
                {
                    embed.Title = "No Realm Specified";
                    embed.WithColor(new Color(255, 165, 0));
                    embed.Description = "Please specify a realm using the `realm` parameter or set a guild association with `/setguild`.";
                    await Context.Interaction.ModifyToV2Async(WowCardV2.FromEmbed(embed).Build());
                    return;
                }
            }

            try
            {
                // Look up realm in static data service first
                var staticRealm = await _wowStaticData.GetRealmBySlugAsync(realmSlug, regionName.ToUpper());

                // Fetch detailed realm info from API
                var singleRealmInfo = await _wowApi.GetSingleRealmInfoAsync(realmSlug, regionName);
                var connectedRealmInfo = await _wowApi.GetConnectedRealmInfoAsync(singleRealmInfo.ConnectedRealm.Href.ToString(), regionName);

                // Use the official name and metadata from the connected-realm response.
                var realmData = connectedRealmInfo.Realms[0];
                var realmName = realmData.Name;
                var isUp = string.Equals(connectedRealmInfo.Status.Name, "Up", StringComparison.OrdinalIgnoreCase);
                var statusEmoji = isUp ? "🟢" : "🔴";

                embed.Title = $"{statusEmoji} {realmName}";
                embed.WithColor(isUp ? new Color(46, 204, 113) : new Color(231, 76, 60));

                var sb = new StringBuilder();
                sb.AppendLine($"**{regionName.ToUpper()}** • **{realmData.Type.Name}** realm");
                sb.AppendLine();
                sb.AppendLine("## Realm Status");
                sb.AppendLine($"**{connectedRealmInfo.Status.Name}** • Population **{connectedRealmInfo.Population.Name}** • Queue **{(connectedRealmInfo.HasQueue ? "Active" : "None")}**");
                sb.AppendLine($"`Locale` {realmData.Locale}   `Timezone` {realmData.Timezone}");

                if (connectedRealmInfo.Realms.Length > 1)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## Connected Realms — {connectedRealmInfo.Realms.Length}");
                    sb.AppendLine(string.Join(" • ", connectedRealmInfo.Realms
                        .OrderBy(connectedRealm => connectedRealm.Name)
                        .Select(connectedRealm => connectedRealm.Name)));
                }

                embed.Description = sb.ToString();
                embed.WithFooter($"Realm ID {connectedRealmInfo.Id} • Live Blizzard realm data");

                await Context.Interaction.ModifyToV2Async(WowCardV2.FromEmbed(embed).Build());
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error fetching realm info for {RealmSlug} in {Region}", realmSlug, regionName);
                embed.Title = "Error Fetching Realm Data";
                embed.WithColor(new Color(255, 0, 0));
                embed.Description = $"Unable to find realm **{realmSlug}** in region **{regionName.ToUpper()}**.\n\n" +
                    "**Possible reasons:**\n" +
                    "• Realm name is incorrect\n" +
                    "• Wrong region selected\n" +
                    "• Blizzard API is temporarily unavailable\n\n" +
                    "Use autocomplete to select a valid realm.";
                await Context.Interaction.ModifyToV2Async(WowCardV2.FromEmbed(embed).Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching realm info for {RealmSlug} in {Region}", realmSlug, regionName);
                embed.Title = "Error";
                embed.WithColor(new Color(255, 0, 0));
                embed.Description = "An error occurred while fetching realm information. Please try again later.";
                await Context.Interaction.ModifyToV2Async(WowCardV2.FromEmbed(embed).Build());
            }
        }

        [SlashCommand("item", "Look up a WoW item")]
        public async Task ItemLookup(
            [Summary("name", "Item name to search for")]
            string name,

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                // Try to search for the item in the database
                var item = await _wowStaticData.SearchItemAsync(name);

                // If not found, try to import by ID if the name is a number
                if (item == null && long.TryParse(name, out long itemId))
                {
                    _logger.LogInformation("Item not found by name, attempting to import by ID: {ItemId}", itemId);
                    item = await _wowStaticData.ImportItemAsync(itemId);
                }
                // If found but missing media URL, fetch it from the API
                else if (item != null && string.IsNullOrEmpty(item.MediaUrl))
                {
                    _logger.LogInformation("Item {ItemId} found but missing media URL, fetching from API", item.Id);
                    var refreshedItem = await _wowStaticData.ImportItemAsync(item.Id);
                    if (refreshedItem != null)
                    {
                        item = refreshedItem;
                    }
                }

                if (item == null)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle($"Item Not Found")
                        .WithDescription($"No item found matching '{name}'. Try searching by item ID if you know it.")
                        .WithColor(Color.Orange)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                // Fetch extended item details
                WowItemDetails itemDetails = null;
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    await using var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                    itemDetails = await db.WowItemDetails.FirstOrDefaultAsync(d => d.ItemId == item.Id);

                    // If details don't exist, trigger ImportItemAsync to fetch them
                    if (itemDetails == null)
                    {
                        _logger.LogInformation("Item details not found for {ItemId}, fetching from API", item.Id);
                        await _wowStaticData.ImportItemAsync(item.Id);
                        // Re-fetch details after import
                        itemDetails = await db.WowItemDetails.FirstOrDefaultAsync(d => d.ItemId == item.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch item details for {ItemId}", item.Id);
                }

                // Get quality emoji and color
                var qualityEmoji = GetQualityEmojiByName(item.QualityName ?? "Common");
                var qualityColor = GetQualityColor(item.Quality);

                var embed = new EmbedBuilder()
                    .WithTitle($"{qualityEmoji} {item.Name}")
                    .WithColor(qualityColor)
                    .WithUrl($"https://www.wowhead.com/item={item.Id}")
                    .AddField("Item Level", item.ItemLevel, inline: true)
                    .AddField("Quality", $"{qualityEmoji} {item.QualityName}", inline: true);

                if (item.RequiredLevel > 0)
                {
                    embed.AddField("Required Level", item.RequiredLevel, inline: true);
                }

                if (!string.IsNullOrEmpty(item.InventoryType))
                {
                    embed.AddField("Slot", item.InventoryType, inline: true);
                }

                if (!string.IsNullOrEmpty(item.ItemClass))
                {
                    embed.AddField("Type", item.ItemClass, inline: true);
                }

                if (!string.IsNullOrEmpty(item.ItemSubclass))
                {
                    embed.AddField("Subtype", item.ItemSubclass, inline: true);
                }

                if (item.IsEquippable)
                {
                    embed.AddField("Equippable", "Yes", inline: true);
                }

                // Display extended item details
                if (itemDetails != null)
                {
                    // Display base stats
                    if (!string.IsNullOrEmpty(itemDetails.BaseStats))
                    {
                        try
                        {
                            var stats = JsonConvert.DeserializeObject<Dictionary<string, int>>(itemDetails.BaseStats);
                            if (stats != null && stats.Count > 0)
                            {
                                var statText = string.Join("\n", stats.Select(s => $"{FormatStatName(s.Key)}: +{s.Value}"));
                                embed.AddField("Stats", statText, inline: false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse base stats for item {ItemId}", item.Id);
                        }
                    }

                    // Display spell effects
                    if (!string.IsNullOrEmpty(itemDetails.SpellEffects))
                    {
                        try
                        {
                            var spells = JsonConvert.DeserializeObject<List<dynamic>>(itemDetails.SpellEffects);
                            if (spells != null && spells.Count > 0)
                            {
                                var spellText = string.Join("\n", spells.Select(s => $"{s.description}"));
                                embed.AddField("Effects", spellText, inline: false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse spell effects for item {ItemId}", item.Id);
                        }
                    }

                    // Display socket count
                    if (itemDetails.SocketCount > 0)
                    {
                        embed.AddField("Sockets", itemDetails.SocketCount.ToString(), inline: true);
                    }

                    // Display set information
                    if (!string.IsNullOrEmpty(itemDetails.SetName))
                    {
                        var setField = $"**{itemDetails.SetName}**";

                        if (!string.IsNullOrEmpty(itemDetails.SetEffects))
                        {
                            try
                            {
                                var effects = JsonConvert.DeserializeObject<List<dynamic>>(itemDetails.SetEffects);
                                if (effects != null && effects.Count > 0)
                                {
                                    var effectText = string.Join("\n", effects.Select(e =>
                                        $"({e.required_count}) {e.display_string}"));
                                    setField += $"\n{effectText}";
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to parse set effects for item {ItemId}", item.Id);
                            }
                        }

                        embed.AddField("Set Bonuses", setField, inline: false);
                    }
                }

                var iconUrl = NormalizeItemIconUrl(item.MediaUrl);
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    embed.WithThumbnailUrl(iconUrl);
                }

                embed.WithFooter($"Item ID: {item.Id} | Last updated: {item.LastUpdated:yyyy-MM-dd}");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error looking up item: {ItemName}", name);
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription($"An error occurred while looking up the item '{name}'.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("token", "Check current WoW Token price")]
        public async Task TokenPrice(
            [Summary("region", "Region (defaults to US)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("KR", "kr")]
            [Choice("TW", "tw")]
            string region = "us",

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var tokenPrice = await _tokenService.GetCurrentPriceAsync(region);

                if (tokenPrice == null)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle($"Token Price Not Available")
                        .WithDescription($"No token price data available for region '{region.ToUpper()}'. The bot may need time to collect data.")
                        .WithColor(Color.Orange)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var priceGold = tokenPrice.Price / 10000; // Convert copper to gold
                var trend = await _tokenService.GetPriceTrendAsync(region);

                var embed = new EmbedBuilder()
                    .WithTitle($"WoW Token Price - {region.ToUpper()}")
                    .WithColor(new Color(255, 209, 0)) // Gold color
                    .AddField("Current Price", $"{priceGold:N0} gold", inline: true)
                    .AddField("Last Updated", $"<t:{((DateTimeOffset)tokenPrice.Timestamp).ToUnixTimeSeconds()}:R>", inline: true);

                if (trend.HasValue)
                {
                    var trendGold = trend.Value / 10000;
                    var trendEmoji = trend.Value > 0 ? "Up" : trend.Value < 0 ? "Down" : "Stable";
                    var trendColor = trend.Value > 0 ? "+" : "";
                    embed.AddField("24h Change", $"{trendEmoji} {trendColor}{trendGold:N0} gold", inline: true);
                }

                embed.WithFooter($"WoW Token allows you to purchase 30 days of game time");
                embed.WithThumbnailUrl("https://render.worldofwarcraft.com/us/icons/56/wow_token01.jpg");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching token price for region: {Region}", region);
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription($"An error occurred while fetching the token price for '{region.ToUpper()}'.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        #region Helper Methods

        private string FormatStatName(string statKey)
        {
            var statMeta = new Dictionary<string, (string Label, string Emoji)>
            {
                { "INTELLECT", ("Int", "Int") },
                { "STRENGTH", ("Str", "Str") },
                { "AGILITY", ("Agi", "Agi") },
                { "STAMINA", ("Stam", "Stam") },
                { "CRITICAL_STRIKE", ("Crit", "Crit") },
                { "HASTE", ("Haste", "Haste") },
                { "MASTERY", ("Mastery", "Mastery") },
                { "VERSATILITY", ("Versatility", "Vers") },
                { "AVOIDANCE", ("Avoidance", "Avoid") },
                { "LEECH", ("Leech", "Leech") },
                { "SPEED", ("Speed", "Speed") },
                { "DODGE", ("Dodge", "Dodge") },
                { "PARRY", ("Parry", "Parry") },
                { "ARMOR", ("Armor", "Armor") }
            };

            if (statMeta.TryGetValue(statKey.ToUpperInvariant(), out var meta))
            {
                return meta.Label;
            }

            // Fallback: capitalize first letter of each word
            return string.Join(" ", statKey.Split('_').Select(w =>
                w.Length > 0 ? char.ToUpper(w[0]) + w.Substring(1).ToLower() : w));
        }

        private string NormalizeItemIconUrl(string mediaUrl)
        {
            if (string.IsNullOrEmpty(mediaUrl))
            {
                return null;
            }

            // If it's already a Wowhead/Zam render URL, just return it
            if (mediaUrl.Contains("wow.zamimg.com", StringComparison.OrdinalIgnoreCase))
            {
                return mediaUrl;
            }

            if (Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri))
            {
                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    return $"https://wow.zamimg.com/images/wow/icons/large/{fileName.ToLowerInvariant()}";
                }
            }

            return mediaUrl;
        }

        private string GetQualityEmojiByName(string qualityName)
        {
            return qualityName?.ToLower() switch
            {
                "legendary" => "Legendary",
                "artifact" => "Artifact",
                "epic" => "Epic",
                "rare" => "Rare",
                "uncommon" => "Uncommon",
                "common" => "Common",
                _ => ""
            };
        }

        private Color GetQualityColor(int quality)
        {
            return quality switch
            {
                >= 6 => new Color(255, 128, 0),  // Artifact/Mythic+ - Orange
                5 => new Color(255, 128, 0),     // Legendary - Orange
                4 => new Color(163, 53, 238),    // Epic - Purple
                3 => new Color(0, 112, 221),     // Rare - Blue
                2 => new Color(30, 255, 0),      // Uncommon - Green
                1 => new Color(157, 157, 157),   // Common - Gray
                _ => new Color(157, 157, 157)    // Poor/Default - Gray
            };
        }

        #endregion
    }
}
