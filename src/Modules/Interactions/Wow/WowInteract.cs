using Discord;
using Discord.Interactions;
using NinjaBotCore.Attributes;
using System;
using System.Threading.Tasks;
using NinjaBotCore.Services;
using System.Linq;
using System.Text;
using Discord.Net;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using System.IO;
using System.Threading;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Models.Wow.Housing;
using NinjaBotCore.Database;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    // Interaction modules must be public and inherit from an IInteractionModuleBase
    public class WowInteract : NinjaBotBaseModule
    {
        // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
        public InteractionService Commands { get; set; }
        private InteractionHandler _handler;
        private WarcraftLogs _logsApi;
        private WarcraftLogsV2Client _logsApiV2;
        private WowApi _wowApi;
        private DiscordShardedClient _client;
        private RaiderIOApi _rioApi;
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private WowUtilities _wowUtils;
        private WowCacheService _wowCache;
        private WowStaticDataService _wowStaticData;
        private WowTokenService _tokenService;

        // Pattern #3: Constructor injection instead of service locator
        public WowInteract(
            IServiceScopeFactory scopeFactory,
            InteractionHandler handler,
            ILogger<WowInteract> logger,
            WarcraftLogs logsApi,
            WarcraftLogsV2Client logsApiV2,
            WowApi wowApi,
            RaiderIOApi rioApi,
            DiscordShardedClient client,
            IConfigurationRoot config,
            WowUtilities wowUtils,
            WowCacheService wowCache,
            WowStaticDataService wowStaticData,
            WowTokenService tokenService)
            : base(scopeFactory)
        {
            _handler = handler;
            _logger = logger;
            _logsApi = logsApi;
            _logsApiV2 = logsApiV2;
            _wowApi = wowApi;
            _rioApi = rioApi;
            _client = client;
            _config = config;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
            _wowStaticData = wowStaticData;
            _tokenService = tokenService;
        }

        [SlashCommand("rio", "Get character's Raider.IO profile")]
        public async Task GetRioProfile(
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

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false,

            [Summary("compare", "Compare with another character")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string compareWith = null,

            [Summary("compare-realm", "Realm for comparison character (optional if using autocomplete)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string compareRealm = null,

            [Summary("compare-region", "Region for comparison character (defaults to same as main character)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("RU", "ru")]
            string compareRegion = null)
        {
            try
            {
                await DeferAsync(ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to defer interaction for /rio command");
                // Try to respond directly if defer failed
                await RespondAsync("The request took too long to process. Please try again.", ephemeral: true);
                return;
            }

            string charName = null;
            string realmName = null;
            string regionName = region;
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();

            // If no character specified, use user's main character
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
                    embed.Description = "You haven't set a main character yet!\n\n" +
                        "Use `/getchars` to manage your saved characters.";
                    await FollowupAsync(embed: embed.Build(), ephemeral: true); // Always private for errors
                    return;
                }
            }
            else
            {
                // Handle autocomplete format: "CharName~RealmName~Region" (tilde delimiter handles realms with spaces)
                var parts = character.Split('~', 3);
                charName = parts[0];

                // Parse realm from autocomplete if not explicitly provided
                if (string.IsNullOrEmpty(realmName) && parts.Length >= 2)
                {
                    realmName = parts[1];
                }
                else if (!string.IsNullOrEmpty(realm))
                {
                    realmName = realm;
                }

                // Parse region from autocomplete if not explicitly provided
                if (string.IsNullOrEmpty(regionName) && parts.Length >= 3)
                {
                    regionName = parts[2];
                }
            }

            // If still no realm, try to find in guild or fallback to API search
            if (string.IsNullOrEmpty(realmName))
            {
                var guildObject = await _wowUtils.GetGuildName(Context);

                if (!string.IsNullOrEmpty(guildObject.guildName))
                {
                    var guildie = await _wowApi.GetCharFromGuildAsync(
                        charName,
                        guildObject.realmName,
                        guildObject.guildName,
                        guildObject.regionName);

                    if (!string.IsNullOrEmpty(guildie.charName))
                    {
                        realmName = guildie.realmName;
                        regionName ??= guildie.regionName;
                    }
                }

                // Still no realm? Try API search
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
                        embed.Description = $"Could not find character **{charName}**.\n\n" +
                            "Please specify the realm name using the `realm` parameter, or use autocomplete to select your character.";
                        await FollowupAsync(embed: embed.Build(), ephemeral: true);
                        return;
                    }
                }
            }

            // Default region to US if not specified
            regionName ??= "us";

            // Now fetch RaiderIO data
            RaiderIOModels.RioMythicPlusChar mPlusInfo = null;
            string realmSlug = string.Empty;

            try
            {
                mPlusInfo = await _rioApi.GetCharMythicPlusInfoAsync(
                    charName: charName,
                    realmName: realmName.Replace(" ", "%20"),
                    region: regionName.ToLower());
            }
            catch (InvalidOperationException ex)
            {
                // Character not found on RaiderIO
                embed.Title = "Character Not Found";
                embed.WithColor(new Color(255, 0, 0));
                embed.Description = $"Could not find **{charName}** on **{realmName}** ({regionName.ToUpper()}) in RaiderIO.\n\n" +
                    "**Possible reasons:**\n" +
                    "• Character name or realm is incorrect\n" +
                    "• Character has no Mythic+ or raid activity this season\n" +
                    "• Try using autocomplete to select your character\n\n" +
                    $"*Error: {ex.Message}*";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }
            catch (Exception ex)
            {
                // Other errors
                _logger.LogError(ex, "Error fetching RaiderIO data for {Character} on {Realm}", charName, realmName);
                embed.Title = "Error Fetching Data";
                embed.WithColor(new Color(255, 165, 0));
                embed.Description = $"An error occurred while fetching RaiderIO data for **{charName}**.\n\n" +
                    "Please try again later.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            // Determine realm slug for WarcraftLogs URL
            switch (regionName.ToLower())
            {
                case "us":
                    realmSlug = WowApi.RealmInfo.realms
                        .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                        .Select(s => s.slug)
                        .FirstOrDefault() ?? realmName;
                    break;
                case "ru":
                    realmSlug = WowApi.RealmInfoRu.realms
                        .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                        .Select(s => s.slug)
                        .FirstOrDefault() ?? realmName;
                    break;
                case "eu":
                    realmSlug = WowApi.RealmInfoEu.realms
                        .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                        .Select(s => s.slug)
                        .FirstOrDefault() ?? realmName;
                    break;
                default:
                    realmSlug = WowApi.RealmInfo.realms
                        .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                        .Select(s => s.slug)
                        .FirstOrDefault() ?? realmName;
                    break;
            }

            embed.Title = $"{mPlusInfo.ActiveSpecName} {mPlusInfo.Class} - {mPlusInfo.Name}";

            // Item Level
            if (mPlusInfo.Gear != null)
            {
                if (mPlusInfo.Gear.ItemLevelTotal > mPlusInfo.Gear.ItemLevelEquipped)
                {
                    sb.AppendLine($"**Item Level:** {mPlusInfo.Gear.ItemLevelEquipped} (equipped) / {mPlusInfo.Gear.ItemLevelTotal} (max)");
                }
                else
                {
                    sb.AppendLine($"**Item Level:** {mPlusInfo.Gear.ItemLevelEquipped}");
                }
                sb.AppendLine();
            }

            // Season Scores Breakdown
            if (mPlusInfo.MythicPlusScores?.Length > 0)
            {
                var scores = mPlusInfo.MythicPlusScores[0].Scores;
                sb.AppendLine($"**__Season M+ Scores__**");
                sb.AppendLine($"Overall: **{scores.All:F1}**");
                if (scores.Dps > 0)
                    sb.AppendLine($"DPS: {scores.Dps:F1}");
                if (scores.Healer > 0)
                    sb.AppendLine($"Healer: {scores.Healer:F1}");
                if (scores.Tank > 0)
                    sb.AppendLine($"Tank: {scores.Tank:F1}");
                sb.AppendLine();
            }

            // Raid Progression
            if (mPlusInfo.RaidProgression.ManaforgeOmega != null)
            {
                var raid = mPlusInfo.RaidProgression.ManaforgeOmega;
                string normalKilled = _wowUtils.GetNumberEmojiFromString((int)raid.NormalBossesKilled);
                string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)raid.HeroicBossesKilled);
                string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)raid.MythicBossesKilled);
                string totalBosses = _wowUtils.GetNumberEmojiFromString((int)raid.TotalBosses);

                sb.AppendLine($"**__Raid Progression__**");
                sb.AppendLine($"__Manaforge Omega__");
                sb.AppendLine($"**Normal** [{normalKilled} / {totalBosses}] {GetProgressBar(raid.NormalBossesKilled, raid.TotalBosses)}");
                sb.AppendLine($"**Heroic** [{heroicKilled} / {totalBosses}] {GetProgressBar(raid.HeroicBossesKilled, raid.TotalBosses)}");
                sb.AppendLine($"**Mythic** [{mythicKilled} / {totalBosses}] {GetProgressBar(raid.MythicBossesKilled, raid.TotalBosses)}");
                sb.AppendLine();
            }

            // M+ Rankings
            sb.AppendLine($"**__M+ Rankings For Active Role ({mPlusInfo.ActiveSpecRole})__**");
            switch (mPlusInfo.ActiveSpecRole.ToLower())
            {
                case "dps":
                    sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Dps.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Dps.Region}**] World [**{mPlusInfo.MythicPlusRanks.Dps.World}**]");
                    break;
                case "healing":
                    sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Healer.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Healer.Region}**] World [**{mPlusInfo.MythicPlusRanks.Healer.World}**]");
                    break;
                case "tank":
                    sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Tank.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Tank.Region}**] World [**{mPlusInfo.MythicPlusRanks.Tank.World}**]");
                    break;
            }

            sb.AppendLine($"**__M+ Rankings For Class ({mPlusInfo.Class})__**");
            sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Class.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Class.Region}**] World [**{mPlusInfo.MythicPlusRanks.Class.World}**]");
            sb.AppendLine();

            // Best Runs
            if (mPlusInfo.MythicPlusBestRuns?.Length > 0)
            {
                sb.AppendLine($"**__Best Runs__**");
                foreach (var run in mPlusInfo.MythicPlusBestRuns)
                {
                    var keyEmoji = run.MythicLevel >= 20 ? "🔑" : "▪️";
                    var minutes = run.ClearTimeMs / 60000;
                    if (run.Url != null)
                    {
                        sb.AppendLine($"{keyEmoji} [{run.ShortName}(**+{run.MythicLevel}**) - {minutes}m]({run.Url.AbsoluteUri})");
                    }
                    else
                    {
                        sb.AppendLine($"{keyEmoji} {run.ShortName}(**+{run.MythicLevel}**) - {minutes}m");
                    }
                }
                sb.AppendLine();
            }

            // Weekly Progress (if available)
            if (mPlusInfo.MythicPlusWeeklyHighestLevelRuns?.Length > 0)
            {
                sb.AppendLine($"**__This Week's Highest Keys__**");
                foreach (var run in mPlusInfo.MythicPlusWeeklyHighestLevelRuns.Take(4))
                {
                    var keyEmoji = run.MythicLevel >= 15 ? "⭐" : "▪️";
                    if (run.Url != null)
                    {
                        sb.AppendLine($"{keyEmoji} [{run.ShortName} **+{run.MythicLevel}**]({run.Url.AbsoluteUri})");
                    }
                    else
                    {
                        sb.AppendLine($"{keyEmoji} {run.ShortName} **+{run.MythicLevel}**");
                    }
                }
                sb.AppendLine();
            }

            embed.AddField("Raider.IO", $"[{mPlusInfo.Name}]({mPlusInfo.ProfileUrl.AbsoluteUri})", true);
            embed.AddField("Warcraftlogs", $"[{mPlusInfo.Name}](https://www.warcraftlogs.com/character/{regionName}/{realmSlug}/{mPlusInfo.Name})", true);
            embed.ThumbnailUrl = $"{mPlusInfo.ThumbnailUrl.AbsoluteUri}";
            embed.Description = sb.ToString();

            // Color based on M+ score
            var score = mPlusInfo.MythicPlusScores?[0]?.Scores?.All ?? 0;
            var color = score >= 3000 ? new Color(255, 128, 0) : // Orange for 3000+
                        score >= 2500 ? new Color(163, 53, 238) : // Purple for 2500+
                        score >= 2000 ? new Color(0, 112, 221) : // Blue for 2000+
                        new Color(0, 200, 150); // Teal default
            embed.WithColor(color);

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"Raider.IO Score: {score:F1} | {mPlusInfo.Realm} ({regionName.ToUpper()})"
            };

            // Save search history
            await SaveSearchHistoryAsync(Context.User.Id, charName, realmName, regionName);

            // If compare mode, fetch second character and display comparison
            if (!string.IsNullOrEmpty(compareWith))
            {
                await HandleCompareMode(mPlusInfo, charName, realmName, regionName, realmSlug, compareWith, compareRealm, compareRegion, publicDisplay);
                return;
            }

            var components = await BuildRioComponents(Context.User.Id, charName, realmName, regionName);
            await FollowupAsync(embed: embed.Build(), components: components.Build(), ephemeral: !publicDisplay);
        }

        [SlashCommand("armory", "Show a character's gear from the Armory")]
        public async Task GetArmoryGear(
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

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            try
            {
                await DeferAsync(ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to defer interaction for /armory command");
                await RespondAsync("The request took too long to process. Please try again.", ephemeral: true);
                return;
            }

            string charName = null;
            string realmName = null;
            string regionName = region;
            var embed = new EmbedBuilder();

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
                    var guildie = await _wowApi.GetCharFromGuildAsync(
                        charName,
                        guildObject.realmName,
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
                        embed.Description = $"Could not find character **{charName}**.\n\nPlease specify the realm name using the `realm` parameter, or use autocomplete to select your character.";
                        await FollowupAsync(embed: embed.Build(), ephemeral: true);
                        return;
                    }
                }
            }

            regionName ??= "us";

            ArmorySummary armorySummary;
            ArmoryEquipment armoryEquipment;
            ArmoryMedia armoryMedia = null;
            var realmSlugForCache = realmName.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();

            try
            {
                var summaryTask = _wowApi.GetArmorySummaryAsync(charName, realmName, regionName);
                var equipmentTask = _wowApi.GetArmoryEquipmentAsync(charName, realmName, regionName);
                var mediaTask = _wowApi.GetArmoryMediaAsync(charName, realmName, regionName);

                await Task.WhenAll(summaryTask, equipmentTask, mediaTask);
                armorySummary = summaryTask.Result;
                armoryEquipment = equipmentTask.Result;
                armoryMedia = mediaTask.Result;

                // Cache the equipment and media data for subsequent item detail requests
                if (armoryEquipment != null && armoryMedia != null)
                {
                    _wowCache.SetCachedArmoryEquipment(charName, realmSlugForCache, regionName, armoryEquipment, armoryMedia);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching armory gear for {Character} on {Realm}", charName, realmName);
                embed.Title = "Error Fetching Armory";
                embed.WithColor(new Color(255, 0, 0));
                embed.Description = $"Could not load **{charName}** on **{realmName}** ({regionName.ToUpper()}). Please try again later.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            if (armorySummary == null || armoryEquipment?.EquippedItems == null)
            {
                embed.Title = "No Gear Found";
                embed.WithColor(new Color(255, 165, 0));
                embed.Description = $"The gear for **{charName}** on **{realmName}** could not be loaded.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            var slotOrder = new[]
            {
                "HEAD","NECK","SHOULDER","BACK","CHEST","WRIST","HANDS","WAIST","LEGS","FEET","FINGER_1","FINGER_2","TRINKET_1","TRINKET_2","MAIN_HAND","OFF_HAND"
            };

            var gearFields = new List<EmbedFieldBuilder>();
            var selectOptions = new List<SelectMenuOptionBuilder>();
            var setProgress = new Dictionary<int, (string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)>();
            var statTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in slotOrder)
            {
                var slotLabel = NormalizeSlot(slot);
                var item = armoryEquipment.EquippedItems.FirstOrDefault(i => string.Equals(i.Slot?.Type, slot, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    gearFields.Add(new EmbedFieldBuilder
                    {
                        Name = slotLabel,
                        Value = "_empty_",
                        IsInline = true
                    });
                    continue;
                }

                var qualityEmoji = GetQualityEmojiByName(item.Quality?.Name);
                var wowheadUrl = item.Item?.Id > 0 ? $"https://www.wowhead.com/item={item.Item.Id}" : null;
                var itemLevel = item.Level?.Value ?? 0;
                var fieldValue = new StringBuilder();

                if (!string.IsNullOrEmpty(qualityEmoji))
                {
                    fieldValue.Append($"{qualityEmoji} ");
                }

                fieldValue.Append(!string.IsNullOrEmpty(wowheadUrl) ? $"[{item.Name}]({wowheadUrl})" : item.Name);
                fieldValue.Append($"\n`ilvl {itemLevel}`");

                var notes = new List<string>();
                if (item.Enchantments == null || item.Enchantments.Count == 0)
                {
                    notes.Add("⚠️ no enchant");
                }

                if (item.Sockets != null && item.Sockets.Count > 0)
                {
                    var emptySockets = item.Sockets.Count(s => s.Item == null);
                    if (emptySockets > 0)
                    {
                        notes.Add($"🟥 {emptySockets} empty socket(s)");
                    }
                }

                if (notes.Count > 0)
                {
                    fieldValue.Append($"\n{string.Join(" · ", notes)}");
                }

                gearFields.Add(new EmbedFieldBuilder
                {
                    Name = slotLabel,
                    Value = fieldValue.ToString(),
                    IsInline = true
                });

                if (item.Stats != null)
                {
                    foreach (var stat in item.Stats)
                    {
                        if (string.IsNullOrEmpty(stat?.Type?.Type))
                        {
                            continue;
                        }

                        if (statTotals.ContainsKey(stat.Type.Type))
                        {
                            statTotals[stat.Type.Type] += stat.Value;
                        }
                        else
                        {
                            statTotals[stat.Type.Type] = stat.Value;
                        }
                    }
                }

                if (item.Set?.ItemSet?.Id > 0)
                {
                    if (!setProgress.TryGetValue(item.Set.ItemSet.Id, out var progress))
                    {
                        progress = (
                            item.Set.ItemSet.Name,
                            new HashSet<int>(),
                            item.Set.Effects ?? new List<ArmorySetEffect>(),
                            item.Set.Items?.Count ?? 0);
                    }

                    if (item.Item?.Id > 0)
                    {
                        progress.ItemIds.Add(item.Item.Id);
                    }

                    setProgress[item.Set.ItemSet.Id] = progress;
                }

                if (item.Item?.Id > 0)
                {
                    var optionLabel = $"{slotLabel} • ilvl {itemLevel}";
                    if (optionLabel.Length > 100)
                    {
                        optionLabel = optionLabel.Substring(0, 100);
                    }

                    var optionDescription = item.Name.Length > 100
                        ? $"{item.Name.Substring(0, 97)}..."
                        : item.Name;

                    selectOptions.Add(new SelectMenuOptionBuilder
                    {
                        Label = optionLabel,
                        Value = $"{slot}:{item.Item.Id}",
                        Description = optionDescription
                    });
                }
            }

            var specLabel = string.IsNullOrEmpty(armorySummary.ActiveSpec?.Name)
                ? $"Level {armorySummary.Level}"
                : armorySummary.ActiveSpec.Name;
            var className = armorySummary.CharacterClass?.Name ?? "Unknown Class";
            var realmSlug = armorySummary.Realm?.Slug ?? realmName.Replace(" ", "-");

            embed.Title = $"{specLabel} {className} - {armorySummary.Name}";
            embed.WithColor(new Color(0, 200, 150));
            embed.ThumbnailUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            embed.ImageUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "main-raw")?.Value;
            var equippedIlvl = armoryEquipment.EquippedItemLevel > 0
                ? armoryEquipment.EquippedItemLevel
                : armorySummary.EquippedItemLevel;
            var averageIlvl = armoryEquipment.AverageItemLevel > 0
                ? armoryEquipment.AverageItemLevel
                : (armorySummary.AverageItemLevel > 0 ? armorySummary.AverageItemLevel : equippedIlvl);

            var descriptionBuilder = new StringBuilder();
            descriptionBuilder.AppendLine($"**Item Level:** {equippedIlvl} (equipped) / {averageIlvl} (max)");

            embed.AddField("Class / Spec", $"{className} — {specLabel}", true);
            var guideUrl = BuildWowheadGuideUrl(className, specLabel);
            embed.AddField("Wowhead Guide", $"[Open guide]({guideUrl})", true);

            var statsSummary = BuildStatsSummary(statTotals);
            if (!string.IsNullOrEmpty(statsSummary))
            {
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("**Stats**");
                descriptionBuilder.AppendLine(statsSummary);
            }

            foreach (var field in gearFields)
            {
                embed.AddField(field);
            }

            var setsSection = BuildSetsSection(setProgress.Values);
            if (!string.IsNullOrEmpty(setsSection))
            {
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("**Sets**");
                descriptionBuilder.AppendLine(setsSection);
            }

            embed.Description = descriptionBuilder.ToString();

            var armoryLocale = regionName.ToLower() switch
            {
                "us" => "en-us",
                "eu" => "en-gb",
                "ru" => "ru-ru",
                _ => "en-us"
            };

            var armoryUrl = $"https://worldofwarcraft.blizzard.com/{armoryLocale}/character/{regionName.ToLower()}/{realmSlug}/{armorySummary.Name.ToLower()}";
            embed.AddField("Armory", $"[View on Battle.net]({armoryUrl})", true);
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{realmSlug} ({regionName.ToUpper()})"
            };

            MessageComponent components = null;
            if (selectOptions.Any())
            {
                var componentBuilder = new ComponentBuilder();
                var itemSelectorId = $"armory_item_select~{Context.User.Id}~{armorySummary.Name}~{realmSlug}~{regionName.ToLower()}";
                componentBuilder.WithSelectMenu(
                    customId: itemSelectorId,
                    options: selectOptions,
                    placeholder: "Select an item for details",
                    minValues: 1,
                    maxValues: 1,
                    row: 0);

                components = componentBuilder.Build();
            }

            await SaveSearchHistoryAsync(Context.User.Id, charName, realmName, regionName);
            await FollowupAsync(embed: embed.Build(), components: components, ephemeral: !publicDisplay);
        }

        [ComponentInteraction("armory_item_select~*~*~*~*")]
        public async Task HandleArmoryItemSelection(string originalUserIdStr, string characterName, string realmSlug, string regionName, string[] selections)
        {
            if (!ulong.TryParse(originalUserIdStr, out var originalUserId))
            {
                await RespondAsync("❌ Invalid interaction data. Please try again.", ephemeral: true);
                return;
            }

            if (Context.User.Id != originalUserId)
            {
                _logger.LogWarning(
                    "User {AttemptingUserId} ({AttemptingUsername}) tried to interact with User {OriginalUserId}'s armory gear selector",
                    Context.User.Id, Context.User.Username, originalUserId);
                await RespondAsync("❌ This selection belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No item was selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var selection = selections[0];
            var parts = selection.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var itemId))
            {
                await FollowupAsync("Could not read that item selection. Please try again.", ephemeral: true);
                return;
            }

            var slotType = parts[0];

            ArmoryEquipment armoryEquipment = null;
            ArmoryItemMedia itemMedia = null;

            // Try to get cached equipment first
            var cachedData = _wowCache.GetCachedArmoryEquipment(characterName, realmSlug, regionName);
            if (cachedData.HasValue)
            {
                armoryEquipment = cachedData.Value.Equipment;
                _logger.LogDebug("Using cached armory equipment for {Character} on {Realm}", characterName, realmSlug);

                // Still fetch item media for the icon (relatively cheap API call)
                try
                {
                    itemMedia = await _wowApi.GetItemMediaAsync(itemId, regionName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching item media for item {ItemId}, continuing without icon", itemId);
                    // Continue without item media - not critical
                }
            }
            else
            {
                // Cache miss - fall back to API calls
                _logger.LogDebug("Cache miss for armory equipment, fetching from API for {Character} on {Realm}", characterName, realmSlug);
                try
                {
                    var equipmentTask = _wowApi.GetArmoryEquipmentAsync(characterName, realmSlug, regionName);
                    var itemMediaTask = _wowApi.GetItemMediaAsync(itemId, regionName);

                    await Task.WhenAll(equipmentTask, itemMediaTask);
                    armoryEquipment = equipmentTask.Result;
                    itemMedia = itemMediaTask.Result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching armory item details for {Character} on {Realm}", characterName, realmSlug);
                    await FollowupAsync("Could not load that item right now. Please try again.", ephemeral: true);
                    return;
                }
            }

            var selectedItem = armoryEquipment?.EquippedItems?.FirstOrDefault(i =>
                string.Equals(i.Slot?.Type, slotType, StringComparison.OrdinalIgnoreCase) ||
                (i.Item?.Id == itemId));

            if (selectedItem == null)
            {
                await FollowupAsync("That item was not found on the character.", ephemeral: true);
                return;
            }

            var qualityEmoji = GetQualityEmojiByName(selectedItem.Quality?.Name) ?? GetQualityEmoji(null);
            var wowheadUrl = selectedItem.Item?.Id > 0 ? $"https://www.wowhead.com/item={selectedItem.Item.Id}" : null;
            var iconUrl = itemMedia?.Assets?.FirstOrDefault(a => a.Key == "icon")?.Value
                ?? itemMedia?.Assets?.FirstOrDefault()?.Value;
            var slotLabel = NormalizeSlot(selectedItem.Slot?.Type ?? slotType);
            var itemLevel = selectedItem.Level?.Value ?? 0;
            var notes = new List<string>();

            if (selectedItem.Enchantments != null && selectedItem.Enchantments.Count > 0)
            {
                notes.AddRange(selectedItem.Enchantments.Select(e => $"✨ {e.DisplayString}"));
            }
            else
            {
                notes.Add("⚠️ No enchant detected");
            }

            if (selectedItem.Sockets != null && selectedItem.Sockets.Count > 0)
            {
                var emptySockets = selectedItem.Sockets.Count(s => s.Item == null);
                var filled = selectedItem.Sockets.Count - emptySockets;
                notes.Add($"💎 Sockets: {filled}/{selectedItem.Sockets.Count}" + (emptySockets > 0 ? " (empty sockets)" : string.Empty));
            }

            if (selectedItem.Weapon != null)
            {
                var weapon = selectedItem.Weapon;
                var damage = weapon.Damage != null
                    ? $"{weapon.Damage.MinValue}-{weapon.Damage.MaxValue} dmg"
                    : "Weapon";
                var speedSec = weapon.AttackSpeed?.Value > 0 ? (weapon.AttackSpeed.Value / 1000.0).ToString("0.00") : "?";
                var dps = weapon.DPS?.Value > 0 ? weapon.DPS.Value.ToString() : "?";
                notes.Add($"🗡️ {damage}, {speedSec}s, {dps} dps");
            }

            if (selectedItem.Spells != null && selectedItem.Spells.Count > 0)
            {
                var spell = selectedItem.Spells.FirstOrDefault(s => !string.IsNullOrEmpty(s.Description));
                if (spell != null)
                {
                    var desc = spell.Description.Length > 180 ? spell.Description.Substring(0, 177) + "..." : spell.Description;
                    notes.Add($"📜 {desc}");
                }
            }

            var detailEmbed = new EmbedBuilder();
            detailEmbed.Title = $"{slotLabel} — {selectedItem.Name}";
            detailEmbed.WithColor(new Color(0, 200, 150));
            detailEmbed.Description = $"{qualityEmoji} {(wowheadUrl != null ? $"[{selectedItem.Name}]({wowheadUrl})" : selectedItem.Name)}\n`ilvl {itemLevel}`";

            if (!string.IsNullOrEmpty(iconUrl))
            {
                detailEmbed.ThumbnailUrl = iconUrl;
            }

            detailEmbed.AddField("Slot", slotLabel, true);
            detailEmbed.AddField("Quality", selectedItem.Quality?.Name ?? "Unknown", true);

            if (notes.Count > 0)
            {
                detailEmbed.AddField("Details", string.Join("\n", notes));
            }

            if (selectedItem.Set?.ItemSet?.Name != null)
            {
                var total = selectedItem.Set.Items?.Count ?? 0;
                var equipped = armoryEquipment?.EquippedItems?
                    .Where(i => i.Set?.ItemSet?.Id == selectedItem.Set.ItemSet.Id)
                    .Select(i => i.Item?.Id)
                    .Where(id => id.HasValue)
                    .Distinct()
                    .Count() ?? 1;
                var setSb = new StringBuilder();
                setSb.AppendLine();
                setSb.AppendLine($"🧩 **{selectedItem.Set.ItemSet.Name}** ({equipped}/{(total > 0 ? total : equipped)})");
                foreach (var effect in selectedItem.Set.Effects ?? Enumerable.Empty<ArmorySetEffect>())
                {
                    var marker = effect.IsActive ? "✅" : "▫️";
                    var display = effect.DisplayString;
                    if (!string.IsNullOrEmpty(display) && display.Length > 170)
                    {
                        display = display.Substring(0, 167) + "...";
                    }
                    setSb.AppendLine($" • {marker} ({effect.RequiredCount}) {display}");
                }

                detailEmbed.Description += setSb.ToString();
            }

            var armoryLocale = regionName.ToLower() switch
            {
                "us" => "en-us",
                "eu" => "en-gb",
                "ru" => "ru-ru",
                _ => "en-us"
            };
            var armoryUrl = $"https://worldofwarcraft.blizzard.com/{armoryLocale}/character/{regionName.ToLower()}/{realmSlug}/{characterName.ToLower()}";
            detailEmbed.AddField("Armory", $"[View on Battle.net]({armoryUrl})", true);

            await FollowupAsync(embed: detailEmbed.Build(), ephemeral: true);
        }

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
                _logger.LogError(ex, "Failed to defer interaction for /mount-missing command");
                await RespondAsync("The request took too long to process. Please try again.", ephemeral: true);
                return;
            }

            string charName = null;
            string realmName = null;
            string regionName = region;
            var embed = new EmbedBuilder();

            // Get character info (same logic as armory command)
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
                    var guildie = await _wowApi.GetCharFromGuildAsync(
                        charName,
                        guildObject.realmName,
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

            // Filter by expansion if specified
            var filteredMounts = allMounts;
            if (expansion != "all")
            {
                filteredMounts = filteredMounts.Where(m => string.Equals(m.Expansion, expansion, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Filter by source if specified
            if (source != "all")
            {
                filteredMounts = filteredMounts.Where(m => string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Filter by obtainability
            if (obtainable == "obtainable")
            {
                filteredMounts = filteredMounts.Where(m => m.IsObtainable).ToList();
            }
            else if (obtainable == "removed")
            {
                filteredMounts = filteredMounts.Where(m => !m.IsObtainable).ToList();
            }

            // Find missing mounts
            var missingMounts = filteredMounts
                .Where(m => !collectedMountIds.Contains(m.Id))
                .OrderBy(m => m.Expansion)
                .ThenBy(m => m.Source)
                .ThenBy(m => m.Name)
                .ToList();

            var collectedCount = filteredMounts.Count(m => collectedMountIds.Contains(m.Id));
            var totalCount = filteredMounts.Count;

            if (missingMounts.Count == 0)
            {
                embed.Title = $"Missing Mounts - {charName}";
                embed.WithColor(new Color(138, 43, 226));
                embed.Description = $"**Collected:** {collectedCount}/{totalCount} ({(totalCount > 0 ? (collectedCount * 100.0 / totalCount):0):F1}%)";
                if (expansion != "all")
                {
                    embed.Description += $"\n**Expansion:** {expansion}";
                }
                if (source != "all")
                {
                    embed.Description += $"\n**Source:** {source}";
                }
                embed.AddField("✅ Complete!", "You have collected all mounts in this category!");
                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
                return;
            }

            // Paginated display - 5 mounts per page
            int page = 0;
            int pageSize = 5;
            var pageData = await BuildMountPageAsync(missingMounts, page, pageSize, charName, realmName, regionName, collectedCount, totalCount, source, expansion);

            // Get current page mounts for the selection dropdown
            var pageMounts = missingMounts.Skip(page * pageSize).Take(pageSize).ToList();
            var components = BuildMountPaginationComponents(page, missingMounts.Count, pageSize, charName, realmName, regionName, source, expansion, Context.User.Id, pageMounts);

            await FollowupAsync(embed: pageData.Build(), components: components.Build(), ephemeral: !publicDisplay);
        }

        [SlashCommand("clearhistoryrio", "Clear your RaiderIO search history")]
        public async Task ClearRioHistory()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var historyCount = await WithDbAsync(async db =>
                {
                    var userHistory = db.RioSearchHistory
                        .Where(h => h.DiscordUserId == (long)Context.User.Id)
                        .ToList();

                    if (userHistory.Any())
                    {
                        db.RioSearchHistory.RemoveRange(userHistory);
                        await db.SaveChangesAsync();

                        // Invalidate cache after clearing history
                        _wowCache.InvalidateRioSearchHistory((long)Context.User.Id);

                        return userHistory.Count;
                    }
                    return 0;
                });

                if (historyCount > 0)
                {
                    await FollowupAsync($"✅ Cleared **{historyCount}** RaiderIO search history entries.", ephemeral: true);
                }
                else
                {
                    await FollowupAsync("No search history found to clear.", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing RIO search history for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while clearing your search history.", ephemeral: true);
            }
        }

        [SlashCommand("setchar", "Associate a WoW character with your Discord account")]
        public async Task SetMyChar(
            [Summary("character", "Character name (use autocomplete to select)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character,

            [Summary("ismain", "Set this as your main character")]
            bool isMain = false)
        {
            await DeferAsync(ephemeral: true);

            string charName = null;
            string realmName = null;
            string regionName = null;
            string locale = null;

            // Handle both autocomplete format: "CharName RealmName" (space-separated)
            // and cached search format: "CharName~RealmName~Region" (tilde-separated)
            if (character.Contains('~'))
            {
                // Cached search format with tildes
                var parts = character.Split('~', 3);
                charName = parts[0];

                if (parts.Length >= 2)
                {
                    realmName = parts[1];
                }

                if (parts.Length >= 3)
                {
                    regionName = parts[2];
                }
            }
            else
            {
                // Autocomplete format with spaces
                var parts = character.Split(' ', 2);
                charName = parts[0];

                if (parts.Length > 1)
                {
                    realmName = parts[1];
                }
            }

            // If no realm from autocomplete, try to look up the character
            if (string.IsNullOrEmpty(realmName))
            {
                try
                {
                    var charResult = await _wowUtils.GetCharFromArgs(character, Context);
                    charName = charResult.charName;
                    realmName = charResult.realmName;
                    regionName = charResult.regionName;
                    locale = charResult.locale;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to lookup character: {Character}", character);
                    await FollowupAsync($"Unable to find character: **{character}**\n\nPlease use autocomplete to select a character, or make sure the character name is correct.", ephemeral: true);
                    return;
                }
            }
            else
            {
                // Realm provided via autocomplete or cached search, look up additional info if needed
                // Only look up region/locale if not already provided (from cached search)
                if (string.IsNullOrEmpty(regionName))
                {
                    try
                    {
                        var guildObject = await _wowUtils.GetGuildName(Context);

                        // Try to get region/locale from guild or default to US
                        if (!string.IsNullOrEmpty(guildObject.regionName))
                        {
                            regionName = guildObject.regionName;
                            locale = guildObject.locale;
                        }
                        else
                        {
                            regionName = "us";
                            locale = "en_US";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to get guild info, defaulting to US region");
                        regionName = "us";
                        locale = "en_US";
                    }
                }
                else
                {
                    // Region provided from cached search, set locale based on region
                    locale = regionName.ToLower() switch
                    {
                        "us" => "en_US",
                        "eu" => "en_GB",
                        "kr" => "ko_KR",
                        "tw" => "zh_TW",
                        "cn" => "zh_CN",
                        _ => "en_US"
                    };
                }
            }

            if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(realmName))
            {
                await FollowupAsync($"Unable to find character: **{character}**\n\nPlease use autocomplete to select a character.", ephemeral: true);
                return;
            }

            // Normalize realm name for comparison (remove spaces, hyphens, apostrophes, and lowercase)
            string normalizedRealmName = realmName.Replace(" ", "").Replace("-", "").Replace("'", "").ToLower();

            var result = await WithDbAsync(async db =>
            {
                // Check if character already exists for this user
                var existingChar = db.WowCharAssociation
                    .Where(a => a.UserId == (long)Context.User.Id)
                    .AsEnumerable() // Switch to client-side evaluation for complex string operations
                    .Where(a => a.CharName.ToLower() == charName.ToLower() &&
                                a.WowRealm.Replace(" ", "").Replace("-", "").Replace("'", "").ToLower() == normalizedRealmName)
                    .FirstOrDefault();

                if (existingChar != null)
                {
                    // Check if anything needs updating
                    if (existingChar.IsMain == isMain)
                    {
                        // No changes needed
                        return (success: true, updated: false, message: $"**{charName}** on **{realmName}** " +
                            (isMain ? "is already saved as your **main character**!" : "is already saved!") +
                            (!isMain ? "\n\nUse `/setchar` with `ismain: true` to set it as your main character." : ""));
                    }
                    else
                    {
                        // Update existing character
                        existingChar.IsMain = isMain;

                        // If setting as main, unset other mains
                        if (isMain)
                        {
                            var otherMains = db.WowCharAssociation
                                .Where(a => a.UserId == (long)Context.User.Id &&
                                            a.IsMain &&
                                            a.Id != existingChar.Id)
                                .ToList();

                            foreach (var main in otherMains)
                            {
                                main.IsMain = false;
                            }
                        }

                        await db.SaveChangesAsync();

                        var mainText = isMain ? " as your **main character**" : "";
                        return (success: true, updated: true, message: $"Updated **{charName}** on **{realmName}**{mainText}!");
                    }
                }
                else
                {
                    // Add new character
                    db.WowCharAssociation.Add(new WowCharAssociation
                    {
                        UserId = (long)Context.User.Id,
                        IsMain = isMain,
                        CharName = charName,
                        WowRealm = realmName,
                        WowRegion = regionName,
                        Locale = locale
                    });

                    // If setting as main, unset other mains
                    if (isMain)
                    {
                        var otherMains = db.WowCharAssociation
                            .Where(a => a.UserId == (long)Context.User.Id && a.IsMain)
                            .ToList();

                        foreach (var main in otherMains)
                        {
                            main.IsMain = false;
                        }
                    }

                    await db.SaveChangesAsync();

                    var mainText = isMain ? " as your **main character**" : "";
                    return (success: true, updated: true, message: $"Successfully saved **{charName}** on **{realmName}**{mainText}!\n\nUse `/getchars` to see all your saved characters.");
                }
            });

            // Invalidate cache if character was updated
            if (result.updated)
            {
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);
            }

            await FollowupAsync(result.message, ephemeral: true);
        }


        [SlashCommand("getchars", "List your saved WoW characters")]
        public async Task GetChars()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            var savedChars = await _wowCache.GetUserCharactersAsync((long)Context.User.Id);
            savedChars = savedChars?
                .OrderByDescending(c => c.IsMain)
                .ThenBy(c => c.CharName)
                .ToList();

            if (savedChars != null && savedChars.Any())
            {
                embed.Title = $"Your Saved Characters ({savedChars.Count})";
                embed.WithColor(new Color(0, 200, 150));

                foreach (var character in savedChars)
                {
                    var mainIndicator = character.IsMain ? "★ [MAIN]" : "";
                    var realm = !string.IsNullOrEmpty(character.WowRealm) ? character.WowRealm : "Unknown Realm";
                    var region = !string.IsNullOrEmpty(character.WowRegion) ? character.WowRegion.ToUpper() : "US";

                    sb.AppendLine($"{mainIndicator} **{character.CharName}** - {realm} ({region})");
                }

                sb.AppendLine();
                sb.AppendLine("*Select a character below to manage it*");

                embed.Description = sb.ToString();
                embed.ThumbnailUrl = Context.User.GetAvatarUrl();

                // Build components for character management
                var components = BuildCharacterManagementComponents(savedChars);
                await RespondAsync(embed: embed.Build(), components: components.Build(), ephemeral: true);
            }
            else
            {
                embed.Title = "No Saved Characters";
                embed.WithColor(new Color(255, 165, 0));
                sb.AppendLine("You haven't saved any characters yet!");
                sb.AppendLine();
                sb.AppendLine("Use `/setchar` to associate a character with your Discord account.");
                sb.AppendLine("You can also save a character you lookup via `/rio`");

                embed.Description = sb.ToString();
                embed.ThumbnailUrl = Context.User.GetAvatarUrl();

                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
        }   

        [SlashCommand("ginfo", "Get guild information")]
        public async Task GetRioGuildStats()
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
            var guildStats = await _rioApi.GetRioGuildInfoAsync(guildName: guildObject.guildName, realmName: guildObject.realmSlug, region: guildObject.regionName);
                        
            string normalKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.NormalBossesKilled);
            string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.HeroicBossesKilled);
            string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.MythicBossesKilled);
            string totalBosses  = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.TotalBosses);
            
            title = $"{guildObject.guildName} on {guildObject.realmName}'s Raider.IO Stats";

            sb.AppendLine("**__Raid Progression:__**");
            sb.AppendLine($"\t **normal** [{normalKilled} / {totalBosses}]");
            sb.AppendLine($"\t **heroic** [{heroicKilled} / {totalBosses}]");
            sb.AppendLine($"\t **mythic** [{mythicKilled} / {totalBosses}]");
            sb.AppendLine();
            sb.AppendLine("**__Raid Rankings:__**");
            sb.AppendLine($"\t **normal** [ realm [**{guildStats.RaidRankings.ManaforgeOmega.Normal.Realm}**] world [**{guildStats.RaidRankings.ManaforgeOmega.Normal.World}**] region [**{guildStats.RaidRankings.ManaforgeOmega.Normal.Region}**] ]");            
            sb.AppendLine($"\t **heroic** [ realm [**{guildStats.RaidRankings.ManaforgeOmega.Heroic.Realm}**] world [**{guildStats.RaidRankings.ManaforgeOmega.Heroic.World}**] region [**{guildStats.RaidRankings.ManaforgeOmega.Heroic.Region}**] ]");
            sb.AppendLine($"\t **mythic** [ realm [**{guildStats.RaidRankings.ManaforgeOmega.Mythic.Realm}**] world [**{guildStats.RaidRankings.ManaforgeOmega.Mythic.World}**] region [**{guildStats.RaidRankings.ManaforgeOmega.Mythic.Region}**] ]");
            sb.AppendLine();
            sb.AppendLine($"[{guildObject.guildName} Profile]({guildStats.ProfileUrl.AbsoluteUri})");

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            embed.WithColor(new Color(0, 0, 255));
            embed.Description = sb.ToString();

            await RespondAsync(embed: embed.Build(), ephemeral: true);                        
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

        [SlashCommand("watchlogs", "watch logs for guild")]
        public async Task ToggleLogs()
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();

            var enable = await WithDbAsync(async db =>
            {
                List<LogMonitoring> logMonitorList = db.LogMonitoring.ToList();
                bool shouldEnable = false;

                if (logMonitorList != null)
                {
                    var getGuild = logMonitorList.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                    if (getGuild != null)
                    {
                        if (!getGuild.MonitorLogs)
                        {
                            shouldEnable = true;
                        }
                    }
                    else
                    {
                        shouldEnable = true;
                    }
                }

                var updateGuild = db.LogMonitoring.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (updateGuild != null)
                {
                    updateGuild.ChannelId = (long)Context.Channel.Id;
                    updateGuild.ChannelName = Context.Channel.Name;
                    updateGuild.MonitorLogs = shouldEnable;

                    // When enabling, always set LatestLogRetail to now so guild starts in Tier 1 (Active)
                    // This handles both new guilds and guilds with stale timestamps from previous monitoring
                    if (shouldEnable)
                    {
                        updateGuild.LatestLogRetail = DateTime.UtcNow;
                    }
                }
                else
                {
                    db.LogMonitoring.Add(new LogMonitoring
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        ChannelId = (long)Context.Channel.Id,
                        ChannelName = Context.Channel.Name,
                        MonitorLogs = shouldEnable,
                        LatestLog = DateTime.UtcNow,
                        // Initialize LatestLogRetail so guild starts in Tier 1 (Active - highest priority)
                        LatestLogRetail = shouldEnable ? DateTime.UtcNow : null
                    });
                }

                await db.SaveChangesAsync();
                return shouldEnable;
            });

            if (enable)
            {
                embed.Title = $"Enabling log watching for {Context.Guild.Name}!";
                sb.AppendLine($"When a new log is posted, you'll get a notification in this channel: **{Context.Channel.Name}**");
                sb.AppendLine($"If you'd like to have them posted in a different channel, use this command to disable the auto posting, and then again to enable them from the channel you'd like them posted in");
            }
            else
            {
                embed.Title = $"Disabling log watching for {Context.Guild.Name}!";
                sb.AppendLine($"Use the command again to enable log watching!");
            }

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

        [SlashCommand("ksm", "Check a character for the Keystone Master achievement")]
        public async Task CheckKsm(string args = null)
        {
            var charInfo = await _wowUtils.GetCharFromArgs(args, Context);
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            bool ksm = false;
            embed.Title = "Keystone Master Achievement Check";
            if (!string.IsNullOrEmpty(charInfo.charName))
            {
                Character charAchievements = null;
                if (!string.IsNullOrEmpty(charInfo.regionName))
                {
                    charAchievements = await _wowApi.GetCharInfoAsync(charInfo.charName, charInfo.realmName, charInfo.regionName);
                }
                else
                {
                    charAchievements = await _wowApi.GetCharInfoAsync(charInfo.charName, charInfo.realmName);
                }
                if (charAchievements != null)
                {
                    foreach (var cheeve in charAchievements.achievements.achievementsCompleted)
                    {
                        if (cheeve == 11162)
                        {
                            ksm = true;
                        }
                    }
                }
                if (!ksm)
                {
                    sb.AppendLine($"**{charAchievements.name}** from **{charAchievements.realm}** does not have the Keystone Master achievement! :(");
                    embed.WithColor(new Color(255, 0, 0));
                }
                else
                {
                    sb.AppendLine($"**{charAchievements.name}** from **{charAchievements.realm}** has the Keystone Master achievement! :)");
                    embed.WithColor(new Color(0, 255, 0));
                }
                embed.ThumbnailUrl = charAchievements.thumbnailURL;
            }
            else
            {
                sb.AppendLine($"Sorry, unable to find that character!");
            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("logs", "Gets logs from Warcraftlogs")]
        public async Task GetLogs(string args = "")
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            string guildRegion = string.Empty;
            string locale = string.Empty;
            StringBuilder sb = new StringBuilder();
            List<Reports> guildLogs = new List<Reports>();
            int maxReturn = 3;
            int arrayCount = 0;
            string discordGuildName = string.Empty;
            var guildInfo = Context.Guild;
            var embed = new EmbedBuilder();            

            guildObject = await _wowUtils.GetGuildName(Context);
            guildName = guildObject.guildName ?? string.Empty;
            realmName = guildObject.realmName?.Replace("'", string.Empty) ?? string.Empty;
            guildRegion = guildObject.regionName ?? string.Empty;
            locale = guildObject.locale ?? string.Empty;
            var realmInfo = new WowRealm.Realm();
            if (!string.IsNullOrEmpty(locale))
            {
                try
                {
                    WowRealm.Realm[] realms = locale switch
                    {
                        "en_US" => WowApi.RealmInfo?.realms,
                        "en_GB" => WowApi.RealmInfoEu?.realms,
                        "ru_RU" => WowApi.RealmInfoRu?.realms,
                        _ => null
                    };

                    if (realms != null)
                    {
                        realmInfo = realms.FirstOrDefault(r => r.name == guildObject.realmName) ?? new WowRealm.Realm();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error looking up realm info for {Realm} in locale {Locale}", guildObject.realmName, locale);
                }
            }
            if (!string.IsNullOrEmpty(guildObject.locale))
            {
                locale = guildObject.locale;
            }
            if (string.IsNullOrEmpty(guildRegion))
            {
                guildRegion = "US";
            }
            if (Context.Channel is IDMChannel)
            {
                discordGuildName = Context.Channel.Name;
            }
            else if (Context.Channel is IGuildChannel)
            {
                discordGuildName = Context.Guild.Name;
            }
            if (args != null && args.Split(' ')[0].ToLower() == "name")
            {
                try
                {
                    guildLogs = await _logsApi.GetReportsFromUser(args.Split(' ')[1]);
                   // arrayCount = guildLogs.Count - 1;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs from **{args.Split(' ')[1]}**");
                    _logger.LogError($"Erorr getting logs from user -> [{ex.Message}]");
                    await RespondAsync(sb.ToString());
                    return;
                }
                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i < guildLogs.Count && i < maxReturn; i++)
                    {
                        var startTime = guildLogs[arrayCount].start.UnixTimeStampToDateTime();
                        var endTime   =  guildLogs[arrayCount].end.UnixTimeStampToDateTime();
                        var wfUrl     = $"https://www.wipefest.net/report/{guildLogs[arrayCount].id}";
                        var wowAnUrl  = $"https://wowanalyzer.com/report/{guildLogs[arrayCount].id}";

                        sb.AppendLine($"[__**{guildLogs[arrayCount].title}** **/** **{guildLogs[arrayCount].zoneName}**__]({guildLogs[arrayCount].reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{startTime}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{endTime}**");
                        sb.AppendLine($"\t:mag: [WoWAnalyzer]({wowAnUrl}) | :sob: [WipeFest]({wfUrl})");

                        sb.AppendLine();
                        arrayCount++;
                    }
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");

                    embed.Title = $":1234:__Logs from **{args.Split(' ')[1]}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
                else if (guildLogs.Count == 1)
                {
                    sb.AppendLine($"[__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__]({guildLogs[0].reportURL})");
                    sb.AppendLine($"\t:timer: Start time: **{guildLogs[0].start.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{guildLogs[0].end.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[0].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");
                    sb.AppendLine();
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
            }
            else
            {
                if (args.Split(',').Count() > 1)
                {
                    if (args.Contains(',') && !string.IsNullOrEmpty(args))
                    {
                        switch (args.Split(',').Count())
                        {
                            case 2:
                                {
                                    realmName = args.Split(',')[0].ToString().Trim();
                                    guildName = args.Split(',')[1].ToString().Trim();
                                    break;
                                }
                            case 3:
                                {
                                    realmName = args.Split(',')[0].ToString().Trim();
                                    guildName = args.Split(',')[1].ToString().Trim();
                                    guildRegion = args.Split(',')[2].ToString().Trim();
                                    break;
                                }
                        }
                    }
                    else
                    {
                        sb.AppendLine("Please specify a guild and realm name!");
                        sb.AppendLine($"Example: /logs Thunderlord, UR KEY UR CARRY");
                        await RespondAsync(sb.ToString());
                        return;
                    }
                }
                if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
                {
                    sb.AppendLine("Please specify a guild and realm name!");
                    sb.AppendLine($"Example: /logs Thunderlord, UR KEY UR CARRY");
                    await RespondAsync(sb.ToString());
                    return;
                }
                try
                {
                    // Try v2 API first (faster, only fetches what we need)
                    try
                    {
                        string realmSlug = guildObject.realmSlug ?? realmName.ToLower().Replace(" ", "-").Replace("'", "");
                        var v2Reports = await _logsApiV2.GetGuildReportsAsync(guildName, realmSlug, guildRegion, limit: 3);

                        if (v2Reports != null && v2Reports.Count > 0)
                        {
                            guildLogs = v2Reports.Select(r => new Reports
                            {
                                id = r.Code,
                                title = r.Title,
                                owner = r.OwnerName,
                                start = r.StartTime,
                                end = r.EndTime,
                                zone = r.Zone?.Id ?? 0
                            }).ToList();
                            _logger.LogInformation($"[v2] Retrieved {guildLogs.Count} reports for {guildName}");
                        }
                    }
                    catch (Exception v2Ex)
                    {
                        _logger.LogWarning($"[v2] Failed for {guildName}, falling back to v1: {v2Ex.Message}");

                        // Fallback to v1 API
                        if (string.IsNullOrEmpty(locale))
                        {
                            guildLogs = await _logsApi.GetReportsFromGuild(guildName: guildName, realm: realmName, region: guildRegion);
                        }
                        else
                        {
                            guildLogs = await _logsApi.GetReportsFromGuild(guildName: guildName, realm: realmName, region: guildRegion, locale: locale, realmSlug: guildObject.realmSlug);
                        }
                        _logger.LogInformation($"[v1] Retrieved {guildLogs?.Count ?? 0} reports for {guildName}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs for **{guildName}** on **{realmName}**");
                    _logger.LogError($"{ex.Message}");
                    await RespondAsync(sb.ToString(), ephemeral: true);
                    return;
                }
                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i < guildLogs.Count && i < maxReturn; i++)
                    {
                        DateTime startTime = DateTime.UtcNow;
                        DateTime endTime = DateTime.UtcNow;

                        if (realmInfo != null && !string.IsNullOrEmpty(realmInfo.timezone))
                        {
                            startTime = _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].start.UnixTimeStampToDateTime(), realmInfo.timezone);
                            endTime =  _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].end.UnixTimeStampToDateTime(), realmInfo.timezone);
                        }
                        else
                        {
                            startTime = _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].start.UnixTimeStampToDateTime());
                            endTime =  _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].end.UnixTimeStampToDateTime());
                        }

                        sb.AppendLine($"[__**{guildLogs[arrayCount].title}** **/** **{guildLogs[arrayCount].zoneName}**__]({guildLogs[arrayCount].reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{startTime}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{endTime}**");
                        sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[arrayCount].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");

                        sb.AppendLine();
                        arrayCount++;
                    }
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234:__Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
                else if (guildLogs.Count == 1)
                {
                    sb.AppendLine($"[__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__]({guildLogs[0].reportURL})");
                    sb.AppendLine($"\t:timer: Start time: **{guildLogs[0].start.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{guildLogs[0].end.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[0].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");
                    sb.AppendLine($"\t");
                    sb.AppendLine();
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
                else
                {
                    embed.Title = $"Unable to find logs for {guildName} on {realmName} ({guildRegion})";
                    embed.Description = $"**{Context.User.Username}**, ensure you've uploaded the logs as attached to **{guildName}** on http://www.warcraftlogs.com \n";
                    embed.Description += $"More information: http://www.wowhead.com/guides/raiding/warcraft-logs";
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
            }
        }

        // Cooldown for guild association changes (10 minutes)
        private static readonly TimeSpan GuildChangeCooldown = TimeSpan.FromMinutes(10);

        [SlashCommand("setguild", "Associate a WoW guild with this Discord server")]
        public async Task SetGuild(
            [Summary("guild", "Guild name")]
            string guild,

            [Summary("realm", "Realm name (use autocomplete to select)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("RU", "ru")]
            string region = "us")
        {
            await DeferAsync(ephemeral: true);

            // Check permissions
            if (Context.Channel is IGuildChannel)
            {
                if (!((IGuildUser)Context.User).GuildPermissions.KickMembers)
                {
                    await FollowupAsync("You need **Kick Members** permission to set the guild association.", ephemeral: true);
                    return;
                }

                // Check cooldown
                var existingAssociation = await _wowUtils.GetGuildAssociation(Context.Guild.Name);
                if (!string.IsNullOrEmpty(existingAssociation?.guildName) && existingAssociation.timeSet.HasValue)
                {
                    var elapsed = DateTime.UtcNow - existingAssociation.timeSet.Value;
                    if (elapsed < GuildChangeCooldown)
                    {
                        var remaining = GuildChangeCooldown - elapsed;
                        await FollowupAsync(
                            $"**Cooldown Active**\nGuild associations can only be changed once every 10 minutes.\n" +
                            $"Please wait **{remaining.Minutes}m {remaining.Seconds}s** before changing the guild association.",
                            ephemeral: true);
                        return;
                    }
                }
            }

            string guildName = guild.Trim();
            string realmName = realm.Trim();
            string regionName = region.ToLower();
            string locale = _wowUtils.GetLocaleFromRegion(ref regionName);
            string discordGuildName = Context.Channel is IDMChannel
                ? Context.User.Username
                : Context.Guild.Name;

            try
            {
                // Verify guild exists on the realm
                await FollowupAsync($"Looking up **{guildName}** on **{realmName}** ({regionName.ToUpper()})...", ephemeral: true);

                GuildMembers members = null;
                try
                {
                    // RealmAutocomplete returns realm slug, so use GetGuildMembersBySlug
                    members = await _wowApi.GetGuildMembersBySlugAsync(realmName, guildName, locale: locale, regionName: regionName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting guild info for {Guild} on {Realm} ({Region})", guildName, realmName, regionName);
                }

                if (members != null && members.guild != null)
                {
                    // Use the official guild/realm names from the API response
                    string officialGuildName = members.guild.name;
                    string officialRealmSlug = members.guild.realm.slug;

                    await FollowupAsync($"Found **{officialGuildName}** with **{members.members.Count()}** members!", ephemeral: true);

                    // Save the association
                    await _wowUtils.SetGuildAssociation(
                        officialGuildName,
                        officialRealmSlug,
                        locale: locale,
                        regionName: regionName,
                        context: Context);

                    var embed = new EmbedBuilder();
                    embed.Title = "Guild Association Set!";
                    embed.WithColor(new Color(0, 200, 150));
                    embed.Description = $"**{discordGuildName}** is now associated with:\n\n" +
                        $"**Guild:** {officialGuildName}\n" +
                        $"**Realm:** {officialRealmSlug}\n" +
                        $"**Region:** {regionName.ToUpper()}\n" +
                        $"**Members:** {members.members.Count()}\n\n" +
                        $"Use `/getguild` to see the full guild info!";

                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
                else
                {
                    var embed = new EmbedBuilder();
                    embed.Title = "Guild Not Found";
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Description = $"Unable to find **{guildName}** on **{realmName}** ({regionName.ToUpper()}).\n\n" +
                        "**Possible reasons:**\n" +
                        "• Guild name is spelled incorrectly\n" +
                        "• Realm is incorrect\n" +
                        "• Wrong region selected\n" +
                        "• Guild doesn't exist or was recently deleted\n\n" +
                        "Please double-check and try again using autocomplete for the realm.";

                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-Guild error for {Guild} on {Realm} ({Region})", guildName, realmName, regionName);
                await FollowupAsync($"An error occurred while setting the guild association. Please try again later.", ephemeral: true);
            }
        }

        [SlashCommand("getguild", "Report Discord Server -> Guild Association")]
        public async Task GetGuild()
        {
            var embed = new EmbedBuilder();
            var members = new List<WowGuildRosterMember>();
            StringBuilder sb = new StringBuilder();
            string title = string.Empty;
            string thumbUrl = string.Empty;
            var guildInfo = Context.Guild;
            string discordGuildName = string.Empty;

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

            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);

            title = $"Guild association for **{discordGuildName}**";

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            if (guildObject.guildName != null)
            {
                try
                {
                    await DeferAsync(ephemeral: true);

                    await _wowUtils.RefreshGuildRosterAsync(guildObject);
                    members = await WithDbAsync(async db => await db.WowGuildRosterMembers
                        .Where(x =>
                            x.GuildName == guildObject.guildName &&
                            x.GuildRealmSlug  == guildObject.realmSlug &&
                            x.Region == guildObject.regionName)
                        .ToListAsync());

                }
                catch (Exception ex)
                {
                    _logger.LogError($"Get-Guild Error getting guild info: [{ex.Message}]");
                }
                string guildName = string.Empty;
                string guildRealm = string.Empty;
                string guildRegion = string.Empty;
                string faction = string.Empty;
                //string battlegroup = members.battlegroup;
                //int achievementPoints = members.achievementPoints;                
                switch (members[0].Faction)
                {
                    case "ALLIANCE":
                        {
                            faction = "Alliance";
                            embed.WithColor(new Color(0, 0, 255));
                            break;
                        }
                    case "HORDE":
                        {
                            faction = "Horde";
                            embed.WithColor(new Color(255, 0, 0));
                            break;
                        }
                }                
                guildName = members[0].GuildName;
                guildRealm = guildObject.realmName;
                guildRegion = guildObject.regionName;
                sb.AppendLine($"Guild Name: **{guildName}**");
                sb.AppendLine($"Realm Name: **{guildRealm}**");
                sb.AppendLine($"Members: **{members.Count().ToString()}**");
                //sb.AppendLine($"Battlegroup: **{battlegroup}**");
                sb.AppendLine($"Faction: **{faction}**");
                sb.AppendLine($"Region: **{guildRegion}**");
                //sb.AppendLine($"Achievement Points: **{achievementPoints.ToString()}**");
            }
            else
            {
                sb.AppendLine($"No guild association found for **{discordGuildName}**!");
                sb.AppendLine($"Please use /set-guild realmName, guild name, region (optional, defaults to US, valid values are eu or us) to associate a guild with **{discordGuildName}**");
            }
            embed.Description = sb.ToString();
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("wow", "Use this combined with rankings (gets guild rank from WoWProgress")]
        public async Task GetRanking(string args = null)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            string regionName = "us";
            await DeferAsync(ephemeral: true);
            if (string.IsNullOrEmpty(args))
            {
                guildObject = await _wowUtils.GetGuildName(Context);
                guildName = guildObject.guildName;
                realmName = guildObject.realmName;
                regionName = guildObject.regionName;
            }
            else
            {
                if (args.Contains(','))
                {
                    switch (args.Split(',').Count())
                    {
                        case 2:
                            {
                                realmName = args.Split(',')[0].ToString().Trim();
                                guildName = args.Split(',')[1].ToString().Trim();
                                break;
                            }
                        case 3:
                            {
                                realmName = args.Split(',')[0].ToString().Trim();
                                guildName = args.Split(',')[1].ToString().Trim();
                                regionName = args.Split(',')[2].ToString().Trim();
                                break;
                            }
                    }
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    var embed = new EmbedBuilder();
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Title = $"Unable to find a guild/realm association!\nTry /wow rankings Realm Name, Guild Name";
                    sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                    sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                    embed.Description = sb.ToString();
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
            }
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
            {
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $"Unable to find a guild/realm association!\nTry /wow rankings Realm Name, Guild Name";
                sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }
            try
            {
                var guildMembers = await _wowApi.GetGuildMembersAsync(realmName, guildName, regionName);
                int memberCount = 0;
                if (guildMembers != null)
                {
                    guildName = guildMembers.guild.name;
                    realmName = guildMembers.guild.realm.slug;
                    memberCount = guildMembers.members.Count();
                }
                var wowProgressApi = new WowProgress();
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 255, 0));
                var ranking = wowProgressApi.GetGuildRank(guildName, realmName, regionName);
                var realmObject = wowProgressApi.GetRealmObject(realmName, wowProgressApi._links, regionName);
                var topGuilds = realmObject.OrderBy(r => r.realm_rank).Take(3);
                var guild = realmObject.Where(r => r.name.ToLower() == guildName.ToLower()).FirstOrDefault();
                int guildRank = guild.realm_rank;
                var surroundingGuilds = realmObject.Where(r => r.realm_rank > (guild.realm_rank - 2) && r.realm_rank < (guild.realm_rank + 2));

                embed.Title = $"__:straight_ruler:Guild ranking for **{guildName}** [**{memberCount}** members] (Score: **{ranking.score}**):straight_ruler:__";
                sb.AppendLine($"Realm rank: **{ranking.realm_rank}** **|** World rank: **{ranking.world_rank}** **|** Area rank: **{ranking.area_rank}**");
                sb.AppendLine();
                sb.AppendLine($"__Where **{guildName}** fits in on **{realmName}**__");
                foreach (var singleGuild in surroundingGuilds)
                {
                    sb.AppendLine($"\t(**{singleGuild.realm_rank}**) **{singleGuild.name}** **|** World rank: **{singleGuild.world_rank}**");
                }
                sb.AppendLine();
                sb.AppendLine($"__:top:Top 3 guilds on **{realmName}**:top:__");
                foreach (var topGuild in topGuilds)
                {
                    sb.AppendLine($"\t(**{topGuild.realm_rank}**) **{topGuild.name}** **|** World Rank: **{topGuild.world_rank}**");
                }
                sb.AppendLine();
                sb.AppendLine("Ranking data gathered via **WoWProgress.com**");
                embed.WithUrl($"{guild.url}");
                embed.Description = sb.ToString();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex.Message} {ex.InnerException} {ex.Data}{ex.Source}{ex.StackTrace}");
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $":frowning: Sorry, {Context.User.Username}, something went wrong! Perhaps check the guild's home realm.:frowning: ";
                sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
        }
    
        [SlashCommand("top10", "Get the top 10 dps or hps for the latest raid in World of Warcraft (via warcraftlogs.com)")]
        public async Task GetTop10(string args = null)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            string fightName = string.Empty;
            string guildOnly = string.Empty;
            string difficulty = string.Empty;
            string metric = string.Empty;
            string raidName = string.Empty;
            string thumbUrl = string.Empty;
            var guildInfo = Context.Guild;
            string discordGuildName = string.Empty;
            int encounterID = 0;
            string region = "us";

            //Attempt to get guild info
            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);
            string realmName = guildObject.realmName.Replace("'", string.Empty);
            string guildName = guildObject.guildName;
            region = guildObject.regionName;

            var fightList = WarcraftLogs.Zones.Where(z => z.id == WarcraftLogs.CurrentRaidTier.WclZoneId)
                                                .Select(z => z.encounters)
                                                .FirstOrDefault();

            raidName = WarcraftLogs.CurrentRaidTier.RaidName;

            //Get Guild Information for Discord Server (or channel for DM)
            if (Context.Channel is IDMChannel)
            {
                discordGuildName = Context.User.Username;
                thumbUrl = Context.User.GetAvatarUrl();
            }
            else if (Context.Channel is IGuildChannel)
            {
                discordGuildName = Context.Guild.Name;
                thumbUrl = Context.Guild.IconUrl;
            }
            //Argument logic
            if (args == null || args.Split(',')[0] == "help")
            {
                sb.AppendLine($"**/top10** fightName(or ID from /top10 list) guild(type guild to get guild only results, all for all guilds) metric(dps(default), or hps) difficulty(lfr, flex, normal, heroic(default), or mythic) ");
                sb.AppendLine();
                sb.AppendLine($"**/top10** list");
                sb.AppendLine($"Get a list of all encounters and shortcut IDs");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1");
                sb.AppendLine($"The above command would get all top 10 **dps** results for **Garothi Worldbreaker** on **{realmName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, guild");
                sb.AppendLine($"The above command would get the top 10 **dps** results for **Garothi Worldbreaker** on **{realmName}** for **{guildName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, guild, hps");
                sb.AppendLine($"The above command would get the top 10 **hps** results for **Garothi Worldbreaker** on **{realmName}** for **{guildName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, all, hps");
                sb.AppendLine($"The above command would get all top 10 **hps** results for **Garothi Worldbreaker** on **{realmName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, guild, dps, mythic");
                sb.AppendLine($"The above command would get the top 10 **dps** results for **Garothi Worldbreaker** on **{realmName}** for **{guildName}** on **mythic** difficulty.");
                embed.Title = $"{Context.User.Username}, here are some examples for **/top10**";
                embed.Description = sb.ToString();
                await RespondAsync(embed: embed.Build(), ephemeral: true);
                return;
            }
            else
            {
                if (args.Split(' ')[0].ToLower() == "list")
                {
                    //list fights here
                    if (fightList != null)
                    {
                        embed.Title = $"__Fight names for **{raidName}**__";
                        int j = 1;
                        foreach (var fight in fightList)
                        {
                            sb.AppendLine($"[**{j}**] {fight.name}");
                            j++;
                        }
                        embed.Description = sb.ToString();
                        await RespondAsync(embed: embed.Build(), ephemeral: true);
                    }
                    return;
                }

                await DeferAsync(ephemeral: true);

                //set default difficulty
                difficulty = "heroic";

                //handle args
                int argCount = args.Split(',').Count();                
                string[] splitArgs = args.Split(',');
                switch (argCount)
                {
                    //Just name
                    case 1:
                        {
                            fightName = splitArgs[0].Trim();
                            break;
                        }
                    //Name + metric
                    case 2:
                        {
                            fightName = splitArgs[0].Trim();                            
                            guildOnly = splitArgs[1].Trim();
                            break;
                        }
                    //Name + metric + guild/all
                    case 3:
                        {
                            fightName = splitArgs[0].Trim();
                            guildOnly = splitArgs[1].Trim();
                            metric = splitArgs[2].Trim();
                            break;
                        }
                    //Name + metric + guild/all + difficulty
                    case 4:
                        {
                            fightName = splitArgs[0].Trim();
                            guildOnly = splitArgs[1].Trim();
                            metric = splitArgs[2].Trim();
                            difficulty = splitArgs[3].Trim();
                            break;
                        }
                }
                //Difficulty logic
                int difficultyID = 4;
                switch (difficulty.ToLower())
                {
                    case "lfr":
                        {
                            difficultyID = 1;
                            break;
                        }
                    case "flex":
                        {
                            difficultyID = 2;
                            break;
                        }
                    case "normal":
                        {
                            difficultyID = 3;
                            break;
                        }
                    case "heroic":
                        {
                            difficultyID = 4;
                            break;
                        }
                    case "mythic":
                        {
                            difficultyID = 5;
                            break;
                        }
                }
                //End difficulty logic
                //Get the list of fights that pertain to the specific zone id. 11 == Nighthold (True)
                //Begin fight logic                
                WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
                if (fightName.Length <= 2)
                {
                    switch (fightName)
                    {
                        case "1":
                            {
                                encounterID = fightList[0].id;
                                break;
                            }
                        case "2":
                            {
                                encounterID = fightList[1].id;
                                break;
                            }
                        case "3":
                            {
                                encounterID = fightList[2].id;
                                break;
                            }
                        case "4":
                            {
                                encounterID = fightList[3].id;
                                break;
                            }
                        case "5":
                            {
                                encounterID = fightList[4].id;
                                break;
                            }
                        case "6":
                            {
                                encounterID = fightList[5].id;
                                break;
                            }
                        case "7":
                            {
                                encounterID = fightList[6].id;
                                break;
                            }
                        case "8":
                            {
                                encounterID = fightList[7].id;
                                break;
                            }
                        case "9":
                            {
                                encounterID = fightList[8].id;
                                break;
                            }
                        case "10":
                            {
                                encounterID = fightList[9].id;
                                break;
                            }
                        case "11":
                            {
                                encounterID = fightList[10].id;
                                break;
                            }
                    }
                }
                else
                {
                    encounterID = _wowUtils.GetEncounterID(fightName);
                }
                //End fight logic               
                //Begin metric set
                string metricEmoji = string.Empty;
                if (string.IsNullOrEmpty(metric))
                {
                    metric = "dps";
                }
                switch (metric.ToLower())
                {
                    case "hps":
                        {
                            embed.WithColor(new Color(0, 255, 0));
                            metricEmoji = ":green_heart:";
                            break;
                        }
                    case "dps":
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            metricEmoji = ":dagger: ";
                            break;
                        }
                    default:
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            metricEmoji = ":dagger: ";
                            metric = "dps";
                            break;
                        }
                }
                //End metric set

                if (string.IsNullOrEmpty(fightName))
                {
                    sb.AppendLine($"{Context.User.Username}, please specify a fight name/number!");
                    sb.AppendLine($"**Example:** /top10 1");
                    sb.AppendLine($"**Encounter Lists:** /top10 list");
                    await FollowupAsync(sb.ToString(), ephemeral: true);
                    return;
                }

                IEnumerable<WarcraftlogRankings.Ranking> top10 = null;
                var guildOnlyList = new List<WarcraftlogRankings.RankingObject>();

                //Guild logic
                if (!(string.IsNullOrEmpty(guildOnly) || guildOnly.ToLower() != "guild"))
                {
                    bool proceed = true;                    
                    int page = 1;
                    while (proceed)
                    {
                        try 
                        {
                            if (!string.IsNullOrEmpty(guildObject.realmSlug))
                            {
                                l = await _logsApi.GetRankingsByEncounterGuildSlug(
                                        encounterID: encounterID,
                                        realmSlug:   guildObject.realmSlug, 
                                        guildName:   guildObject.guildName,
                                        page:        page.ToString(),
                                        metric:      metric, 
                                        difficulty:  difficultyID, 
                                        regionName:  region
                                        //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()
                                    );                   
                            }
                            else
                            {
                                l = await _logsApi.GetRankingsByEncounterGuild(
                                        encounterID: encounterID, 
                                        realmName:   guildObject.realmName, 
                                        guildName:   guildObject.guildName, 
                                        page:        page.ToString(),
                                        metric:      metric, 
                                        difficulty:  difficultyID, 
                                        regionName:  region
                                        //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()                                   
                                    );                                          
                            }  
                            _logger.LogInformation($"Adding page {page}!");
                        
                            if (l != null)
                            {
                                guildOnlyList.Add(l);
                                page++;
                            }
                            else
                            {
                                proceed = false;
                            }                                                                        
                            if (!l.hasMorePages || page >= 25)
                            {
                                proceed = false;
                            }                                                                                               
                        }   
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error getting top 10 data -> [{ex.Message}]"); 
                            proceed = false;      
                        }

                        top10 = guildOnlyList.SelectMany(p => p.rankings).Where(r => r.guildName == guildObject.guildName).OrderByDescending(o => o.total).Take(10);                        
                    }                      
                }
                else //else for non-guild (all realm top 10)
                {
                    if (!string.IsNullOrEmpty(guildObject.realmSlug))
                    {
                        l = await _logsApi.GetRankingsByEncounterSlug(
                                encounterID: encounterID, 
                                realmSlug: guildObject.realmSlug, 
                                metric: metric,
                                difficulty: difficultyID, 
                                regionName: region
                                //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()
                            );
                    }
                    else
                    {
                        l = await _logsApi.GetRankingsByEncounter(
                                encounterID: encounterID, 
                                realmName: realmName, 
                                metric: metric, 
                                difficulty: difficultyID, 
                                regionName: region
                                //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()
                            );
                    }

                   top10 = l.rankings.OrderByDescending(a => a.total).Take(10);                        

                }

                //Setup the results for the embed
                string difficultyName = string.Empty;
                switch (difficultyID)
                {
                    case 1:
                        {
                            difficultyName = "LFR";
                            break;
                        }
                    case 2:
                        {
                            difficultyName = "Flex";
                            break;
                        }
                    case 3:
                        {
                            difficultyName = "Normal";
                            break;
                        }
                    case 4:
                        {
                            difficultyName = "Heroic";
                            break;
                        }
                    case 5:
                        {
                            difficultyName = "Mythic";
                            break;
                        }
                }

                string fightNameFromEncounterID = fightList.Where(f => f.id == encounterID).Select(f => f.name).FirstOrDefault();

                //Build embed
                embed.Title = $"__Top 10 for fight [**{fightNameFromEncounterID}** (Metric [**{metric.ToUpper()}**] Difficulty [**{difficultyName}**]) Realm [**{guildObject.realmName}**]]__";

                int i = 1;
                if (top10 != null)
                {
                    foreach (var rank in top10)
                    {
                        var classInfo = WarcraftLogs.CharClasses.Where(c => c.id == rank._class).FirstOrDefault();
                        sb.AppendLine($"**{i}** [{rank.name}](http://{region}.battle.net/wow/en/character/{rank.serverName.Replace(" ","-")}/{rank.name}/advanced) ilvl **{rank.itemLevel}** {classInfo.name} from *[{rank.guildName}]*");
                        sb.AppendLine($"\t{metricEmoji}[**{rank.total.ToString("###,###")}** {metric.ToLower()}]");
                        i++;
                    }
                    sb.AppendLine($"Data gathered from **https://www.warcraftlogs.com**");
                    embed.Description = sb.ToString();
                    embed.ThumbnailUrl = thumbUrl;
                }
                else
                {
                    sb.AppendLine($"Error getting top 10 for {guildObject.guildName}!");
                    _logger.LogError($"Variable top10 was null for {guildObject.guildName} on {guildObject.realmSlug} [{guildObject.regionName}]");
                }
                try
                {
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
        }
        
        [SlashCommand("raidvids", "Get list of current raid videos")]
        public async Task GetRaidVids()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.WithColor(0,255,0);
            embed.WithFooter(new EmbedFooterBuilder{
                Text = $"Good luck and have fun!"
            });
            embed.ThumbnailUrl = "https://vignette.wikia.nocookie.net/wowwiki/images/1/17/Jainaunit.JPG/revision/latest?cb=20080826081813";
            var fightList = WarcraftLogs.Zones.Where(z => z.id == WarcraftLogs.CurrentRaidTier.WclZoneId)
                .Select(z => z.encounters)
                .FirstOrDefault();
            embed.Title = $"Raid Videos for {WarcraftLogs.CurrentRaidTier.RaidName}";

            var vids = await _wowCache.GetWowResourcesAsync("raidvid");

            if (vids != null && vids.Any())
            {
                foreach (var vid in vids)
                {
                    embed.AddField(new EmbedFieldBuilder
                    {
                        Name = $"{vid.ClassName}",
                        Value = $"{vid.Resource}",
                        IsInline = true
                    });
                }
            }
            else
            {

            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);            
        }

        [SlashCommand("realminfo", "Return WoW realm information")]
        public async Task GetRealmInfo(string args = "")
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            var guildInfo = await _wowUtils.GetGuildName(Context);
            string region = string.Empty;
            string findMe = string.Empty;
            findMe = args;
            await DeferAsync(ephemeral: true);
            
            if (!string.IsNullOrEmpty(guildInfo.regionName))
            {
                region = guildInfo.regionName;
            }
            else
            {
                region = "us";
            }

            if (!string.IsNullOrEmpty(guildInfo.realmName) && string.IsNullOrEmpty(findMe))
            {
                findMe = guildInfo.realmName;
            }            
            var getRealmList = await _wowApi.GetRealmStatusAsync(region, region);
            var foundRealm = getRealmList.realms.Where(r => r.slug.ToLower().Contains(findMe.ToLower())).FirstOrDefault();
            var connectedUrlFinder = await _wowApi.GetSingleRealmInfoAsync(foundRealm.slug);
            var realmResult = await _wowApi.GetConnectedRealmInfoAsync(connectedUrlFinder.ConnectedRealm.Href.ToString());
            if (foundRealm != null)
            {
                embed.Title = $"Realm Information for {foundRealm.name}!";
                sb.AppendLine($":black_small_square: Type: **{realmResult.Realms[0].Type.Name}**");
                sb.AppendLine($":black_small_square: Locale: **{realmResult.Realms[0].Locale}**");
                sb.AppendLine($":black_small_square: Population: **{realmResult.Population.Name}**");
                sb.AppendLine($":black_small_square: Status: **{realmResult.Status.Name}**");
                sb.AppendLine($":black_small_square: TimeZone: **{realmResult.Realms[0].Timezone}**");
                sb.AppendLine($":black_small_square: Queue: **{realmResult.HasQueue}**");
                sb.AppendLine($":black_small_square: Connected Realms:");
                foreach (var realm in realmResult.Realms)
                {
                    sb.AppendLine($"\t :black_small_square: **{realm.Name}**");
                }
            }
            if (foundRealm.status)
            {
                embed.WithColor(new Color(0, 255, 0));
            }
            else
            {
                embed.WithColor(new Color(255, 0, 0));
            }
            embed.Description = sb.ToString();
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }   

        [SlashCommand("yoink", "grab users from one voice channel and yoink them into another")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task Yoink(SocketVoiceChannel to, SocketVoiceChannel from)
        {
            // Defer the interaction immediately to avoid timeout with multiple users
            await DeferAsync(ephemeral: true);

            if (from.Id == to.Id)
            {
                await FollowupAsync("Please pick two different voice channels.", ephemeral: true);
                return;
            }

            var usersToMove = from.Users.Where(u => u.VoiceChannel?.Id == from.Id).ToList();

            if (usersToMove.Count == 0)
            {
                await FollowupAsync($"No users currently in [{from.Name}] to move.", ephemeral: true);
                return;
            }

            var movedUsers = 0;
            var skippedUsers = new List<string>();

            foreach (var user in usersToMove)
            {
                try
                {
                    await user.ModifyAsync(u =>
                    {
                        u.Channel = to;
                    });
                    movedUsers++;
                }
                catch (HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40032)
                {
                    // User left voice between gathering the list and moving them
                    skippedUsers.Add(user.Username);
                }

                await Task.Delay(750);
            }

            var message = $"Yoinked [{movedUsers}] users from [{from.Name}] to [{to.Name}]!";

            if (skippedUsers.Count > 0)
            {
                message += $" Skipped {skippedUsers.Count} user(s) no longer in voice: {string.Join(", ", skippedUsers)}.";
            }

            await FollowupAsync(message, ephemeral: true);
        }

        [SlashCommand("member", "give user the member role")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task AddMemberRole(IGuildUser user)
        {
            var serverRoles  = Context.Guild.Roles;
            var userRoles    = user.RoleIds;

            var memberRole   = serverRoles.Where(r => r.Name.ToLower() == "member").FirstOrDefault();
            var raiderRole   = serverRoles.Where(r => r.Name.ToLower() == "raider").FirstOrDefault();

            if (memberRole == null)
            {
                await RespondAsync($"Could not find the [**Member**] role, please add it if you'd like to use this command!");
                return;
            }

            var memberRoleId = memberRole.Id;
            var isMember     = userRoles.Where(u => u == memberRoleId).FirstOrDefault();
            var embed        = new EmbedBuilder();
            var sb           = new StringBuilder();

            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            embed.Title        = $"User role change for [{user.Username}]";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            if (isMember != 0)
            {
                if (raiderRole != null && userRoles.Where(r => r == raiderRole.Id).FirstOrDefault() != 0)
                {
                    await user.RemoveRoleAsync(raiderRole);
                }
                await user.RemoveRoleAsync(memberRole);        
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"Member role removed </3");                
                embed.WithColor(255, 0, 0);                      
            }
            else
            {
                await user.AddRoleAsync(memberRole);                
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"You should now be able to see more channels, welcome to [**{Context.Guild.Name}**]");                
                embed.WithColor(0, 255, 0);                         
            }  
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);                           
        }

        [SlashCommand("raider", "give user the raider role")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task AddRaiderRole(IGuildUser user)
        {
            var serverRoles  = Context.Guild.Roles;
            var userRoles    = user.RoleIds;
            var guild        = (IGuild)Context.Guild;
            var channels     = await guild.GetTextChannelsAsync();
            var raidCat      = guild.GetCategoriesAsync().Result.Where(c => c.Name.ToLower() == "raiding").FirstOrDefault();            
            var raiderRole   = serverRoles.Where(r => r.Name.ToLower() == "raider").FirstOrDefault();
                        
            if (raiderRole == null)
            {
                await RespondAsync($"Could not find the [**Raider**] role, please add it if you'd like to use this command!");
                return;
            }

            ITextChannel signUpChannel = null;
            ITextChannel stratChannel  = null;
            ITextChannel addonChannel  = null;
            ITextChannel logsChannel   = null;

            if (raidCat != null)
            {                
                signUpChannel = channels.Where(c => c.Name.ToLower() == "sign-up" && c.CategoryId == raidCat.Id).FirstOrDefault();
                stratChannel  = channels.Where(c => c.Name.ToLower() == "strategy" && c.CategoryId == raidCat.Id).FirstOrDefault();
                addonChannel  = channels.Where(c => c.Name.ToLower() == "addons" && c.CategoryId == raidCat.Id).FirstOrDefault();
                logsChannel   = channels.Where(c => c.Name.ToLower() == "logs" && c.CategoryId == raidCat.Id).FirstOrDefault();
            }

            var raiderRoleId = raiderRole.Id;
            var isRaider     = userRoles.Where(u => u == raiderRoleId).FirstOrDefault();
            var embed        = new EmbedBuilder();
            var sb           = new StringBuilder();
            
            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            embed.Title        = $"User role change for [{user.Username}]";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            if (isRaider != 0)
            {
                await user.RemoveRoleAsync(raiderRole);        
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"Raider role removed </3");                
                embed.WithColor(255, 0, 0);
                      
            }
            else
            {
                await user.AddRoleAsync(raiderRole);                
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"You should now be able to see raiding channels, welcome to the [**back2back mirror fam**]");   
                sb.AppendLine();
                sb.AppendLine("<:b2bm:710554622452039731>"); 
                sb.AppendLine();
                if (signUpChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Raid sign-ups are announced in [{signUpChannel.Mention}]");
                }  
                if (addonChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Mandatory addons for raiding are located in [{addonChannel.Mention}]");
                }
                if (stratChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Strats are posted in [{stratChannel.Mention}]");                
                }         
                if (logsChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Logs and WoWAnalyzer/Wipefest links are located in [{logsChannel.Mention}]");                
                }                           
                embed.WithColor(0, 255, 0);                         
            }  
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);                           
        }        
        
        [SlashCommand("listmythic", "list mythic raiders")]
        public async Task ListMythicRaiders()
        {
            var serverRoles      = Context.Guild.Roles;
            var mythicRole       = serverRoles.Where(r => r.Name.ToLower() == "mythic raider").FirstOrDefault();
            var mythicBackupRole = serverRoles.Where(r => r.Name.ToLower() == "mythic backup").FirstOrDefault();
            var guild            = (IGuild)Context.Guild;
            var guildMembers     = await guild.GetUsersAsync();
            var mythicRaiders    = guildMembers.Where(m => m.RoleIds.Contains(mythicRole.Id)).ToList();  
            var mythicBackups    = guildMembers.Where(m => m.RoleIds.Contains(mythicBackupRole.Id)).ToList();
            var sb               = new StringBuilder();

            foreach (var raider in mythicRaiders)
            {                
                if (!string.IsNullOrEmpty(raider.Nickname))
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**{raider.Nickname}**]");
                }
                else
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**none set**]");
                }                
            }

            sb.AppendLine("");
            sb.AppendLine($"Total [{mythicRaiders.Count}]");
            sb.AppendLine("");
            
            sb.AppendLine("__Backups__");
            foreach (var raider in mythicBackups)
            {                
                if (!string.IsNullOrEmpty(raider.Nickname))
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**{raider.Nickname}**]");
                }
                else
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**none set**]");
                }                
            }

            sb.AppendLine("");
            sb.AppendLine($"Total [{mythicBackups.Count}]");

            var embed = new EmbedBuilder();
            embed.Color = new Color(0, 255, 0);
            embed.Title = $"Mythic Raiders in [{Context.Guild.Name}]";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.Description = sb.ToString();
            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        private async Task HandleCompareMode(
            RaiderIOModels.RioMythicPlusChar char1Data,
            string char1Name,
            string char1Realm,
            string char1Region,
            string char1RealmSlug,
            string compareWith,
            string compareRealm,
            string compareRegion,
            bool publicDisplay)
        {
            // Parse second character - autocomplete may include realm and region in format "CharName~RealmName~Region" (tilde delimiter handles realms with spaces)
            var parts = compareWith.Split('~', 3);
            string char2Name = parts[0];
            string char2Realm = compareRealm; // Use explicit compareRealm parameter first
            string char2Region = compareRegion; // Use explicit compareRegion parameter first

            // If no explicit parameters, try parsing from compareWith (autocomplete format)
            if (string.IsNullOrEmpty(char2Realm) && parts.Length >= 2)
            {
                char2Realm = parts[1];
            }

            if (string.IsNullOrEmpty(char2Region) && parts.Length >= 3)
            {
                char2Region = parts[2];
            }

            // Default region to same as main character if still not specified
            if (string.IsNullOrEmpty(char2Region))
            {
                char2Region = char1Region;
            }

            char2Region = char2Region.ToLower();

            // Try to find realm for second character if not provided
            if (string.IsNullOrEmpty(char2Realm))
            {
                var guildObject = await _wowUtils.GetGuildName(Context);
                if (!string.IsNullOrEmpty(guildObject.guildName))
                {
                    try
                    {
                        // Use async method with timeout to prevent blocking
                        var guildTask = _wowApi.GetCharFromGuildAsync(
                            char2Name,
                            guildObject.realmName,
                            guildObject.guildName,
                            guildObject.regionName);

                        if (await Task.WhenAny(guildTask, Task.Delay(5000)) == guildTask)
                        {
                            // Task completed within timeout
                            var guildie = await guildTask;
                            if (!string.IsNullOrEmpty(guildie.charName))
                            {
                                char2Realm = guildie.realmName;
                                char2Region = guildie.regionName;
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Guild lookup timed out for {Character}, will try Armory search", char2Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Character {Character} not found in guild, will try Armory search", char2Name);
                        // Continue to Armory search
                    }
                }

                // Fallback to API search
                if (string.IsNullOrEmpty(char2Realm))
                {
                    var chars = await _wowApi.SearchArmoryAsync(char2Name);
                    if (chars != null && chars.Count > 0)
                    {
                        char2Realm = chars[0].realmName;
                    }
                    else
                    {
                        await FollowupAsync($"Could not find character **{char2Name}** for comparison.", ephemeral: true);
                        return;
                    }
                }
            }

            // Fetch second character's RaiderIO data
            RaiderIOModels.RioMythicPlusChar char2Data;
            try
            {
                char2Data = await _rioApi.GetCharMythicPlusInfoAsync(
                    charName: char2Name,
                    realmName: char2Realm.Replace(" ", "%20"),
                    region: char2Region.ToLower());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching RaiderIO data for comparison character {Character} on {Realm}", char2Name, char2Realm);
                await FollowupAsync($"Could not find **{char2Name}** on **{char2Realm}** ({char2Region.ToUpper()}) in RaiderIO.", ephemeral: true);
                return;
            }

            // Save search history for comparison character
            await SaveSearchHistoryAsync(Context.User.Id, char2Name, char2Realm, char2Region);

            // Get realm slug for second character
            string char2RealmSlug = char2Region.ToLower() switch
            {
                "us" => WowApi.RealmInfo.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(char2Realm.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? char2Realm,
                "ru" => WowApi.RealmInfoRu.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(char2Realm.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? char2Realm,
                "eu" => WowApi.RealmInfoEu.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(char2Realm.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? char2Realm,
                _ => char2Realm
            };

            // Build comparison embed
            var embed = new EmbedBuilder();
            embed.Title = $"⚔️ Character Comparison";

            var sb = new StringBuilder();

            // Character Names and Specs
            sb.AppendLine($"**{char1Data.ActiveSpecName} {char1Data.Class}** [{char1Data.Name}]({char1Data.ProfileUrl.AbsoluteUri})");
            sb.AppendLine($"vs");
            sb.AppendLine($"**{char2Data.ActiveSpecName} {char2Data.Class}** [{char2Data.Name}]({char2Data.ProfileUrl.AbsoluteUri})");
            sb.AppendLine();

            // M+ Scores
            var char1Score = char1Data.MythicPlusScores?[0]?.Scores?.All ?? 0;
            var char2Score = char2Data.MythicPlusScores?[0]?.Scores?.All ?? 0;
            var scoreDiff = char1Score - char2Score;
            var scoreWinner = scoreDiff > 0 ? "✓" : (scoreDiff < 0 ? "" : "=");
            var scoreWinner2 = scoreDiff < 0 ? "✓" : (scoreDiff > 0 ? "" : "=");

            sb.AppendLine($"**__M+ Score__**");
            sb.AppendLine($"{scoreWinner} {char1Score:F1} | {char2Score:F1} {scoreWinner2}");
            if (scoreDiff != 0)
                sb.AppendLine($"*Difference: {Math.Abs(scoreDiff):F1}*");
            sb.AppendLine();

            // Item Level
            if (char1Data.Gear != null && char2Data.Gear != null)
            {
                var char1Ilvl = char1Data.Gear.ItemLevelEquipped;
                var char2Ilvl = char2Data.Gear.ItemLevelEquipped;
                var ilvlDiff = char1Ilvl - char2Ilvl;
                var ilvlWinner = ilvlDiff > 0 ? "✓" : (ilvlDiff < 0 ? "" : "=");
                var ilvlWinner2 = ilvlDiff < 0 ? "✓" : (ilvlDiff > 0 ? "" : "=");

                sb.AppendLine($"**__Item Level__**");
                sb.AppendLine($"{ilvlWinner} {char1Ilvl} | {char2Ilvl} {ilvlWinner2}");
                sb.AppendLine();
            }

            // Best Key Levels
            var char1BestKey = char1Data.MythicPlusBestRuns?.FirstOrDefault()?.MythicLevel ?? 0;
            var char2BestKey = char2Data.MythicPlusBestRuns?.FirstOrDefault()?.MythicLevel ?? 0;
            if (char1BestKey > 0 || char2BestKey > 0)
            {
                var keyDiff = char1BestKey - char2BestKey;
                var keyWinner = keyDiff > 0 ? "✓" : (keyDiff < 0 ? "" : "=");
                var keyWinner2 = keyDiff < 0 ? "✓" : (keyDiff > 0 ? "" : "=");

                sb.AppendLine($"**__Highest Key__**");
                sb.AppendLine($"{keyWinner} +{char1BestKey} | +{char2BestKey} {keyWinner2}");
                sb.AppendLine();
            }

            // Raid Progression (Mythic)
            if (char1Data.RaidProgression.ManaforgeOmega != null && char2Data.RaidProgression.ManaforgeOmega != null)
            {
                var char1Mythic = char1Data.RaidProgression.ManaforgeOmega.MythicBossesKilled;
                var char2Mythic = char2Data.RaidProgression.ManaforgeOmega.MythicBossesKilled;
                var raidDiff = char1Mythic - char2Mythic;
                var raidWinner = raidDiff > 0 ? "✓" : (raidDiff < 0 ? "" : "=");
                var raidWinner2 = raidDiff < 0 ? "✓" : (raidDiff > 0 ? "" : "=");
                var totalBosses = char1Data.RaidProgression.ManaforgeOmega.TotalBosses;

                sb.AppendLine($"**__Manaforge Omega (Mythic)__**");
                sb.AppendLine($"{raidWinner} {char1Mythic}/{totalBosses} | {char2Mythic}/{totalBosses} {raidWinner2}");
                sb.AppendLine();
            }

            embed.Description = sb.ToString();

            // Color based on higher score
            var color = Math.Max(char1Score, char2Score) >= 3000 ? new Color(255, 128, 0) :
                        Math.Max(char1Score, char2Score) >= 2500 ? new Color(163, 53, 238) :
                        Math.Max(char1Score, char2Score) >= 2000 ? new Color(0, 112, 221) :
                        new Color(0, 200, 150);
            embed.WithColor(color);

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{char1Data.Realm} ({char1Region.ToUpper()}) vs {char2Data.Realm} ({char2Region.ToUpper()})"
            };

            await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
        }

        private static string GetProgressBar(long current, long total, int length = 10)
        {
            if (total == 0) return "";

            double percentage = (double)current / total;
            int filled = (int)(percentage * length);
            int empty = length - filled;

            string filledBar = new string('█', filled);
            string emptyBar = new string('░', empty);

            return $"[{filledBar}{emptyBar}]";
        }

        /// <summary>
        /// Save or update search history for a character lookup
        /// </summary>
        private async Task SaveSearchHistoryAsync(ulong discordUserId, string characterName, string realmName, string region)
        {
            try
            {
                await WithDbAsync(async db =>
                {
                    // Check if this search already exists
                    var existingSearch = db.RioSearchHistory
                        .FirstOrDefault(h =>
                            h.DiscordUserId == (long)discordUserId &&
                            h.CharacterName.ToLower() == characterName.ToLower() &&
                            h.RealmName.ToLower() == realmName.ToLower() &&
                            h.Region.ToLower() == region.ToLower());

                    if (existingSearch != null)
                    {
                        // Update existing record
                        existingSearch.LastSearched = DateTime.UtcNow;
                        existingSearch.SearchCount++;
                    }
                    else
                    {
                        // Create new record
                        db.RioSearchHistory.Add(new RioSearchHistory
                        {
                            DiscordUserId = (long)discordUserId,
                            CharacterName = characterName,
                            RealmName = realmName,
                            Region = region.ToLower(),
                            LastSearched = DateTime.UtcNow,
                            SearchCount = 1
                        });
                    }

                    await db.SaveChangesAsync();

                    // Invalidate cache since search history was updated
                    _wowCache.InvalidateRioSearchHistory((long)discordUserId);

                    // Cleanup old searches - keep only the 30 most recent per user
                    var userSearches = db.RioSearchHistory
                        .Where(h => h.DiscordUserId == (long)discordUserId)
                        .OrderByDescending(h => h.LastSearched)
                        .Skip(30)
                        .ToList();

                    if (userSearches.Any())
                    {
                        db.RioSearchHistory.RemoveRange(userSearches);
                        await db.SaveChangesAsync();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving RIO search history for user {UserId}", discordUserId);
                // Don't throw - search history is not critical
            }
        }

        /// <summary>
        /// Build component with recent searches select menu and optional "Save as Main" button
        /// </summary>
        private async Task<ComponentBuilder> BuildRioComponents(ulong userId, string charName = null, string realmName = null, string regionName = null)
        {
            var builder = new ComponentBuilder();
            try
            {
                var recentSearches = await WithDbAsync(db =>
                    db.RioSearchHistory
                        .Where(h => h.DiscordUserId == (long)userId)
                        .OrderByDescending(h => h.LastSearched)
                        .Take(10)
                        .ToListAsync());

                if (recentSearches.Any())
                {
                    // Encode original user ID in CustomId to prevent unauthorized access (Discord best practice)
                    var selectMenuBuilder = new SelectMenuBuilder()
                        .WithPlaceholder("🔍 Quick search recent characters...")
                        .WithCustomId($"rio_recent_search~{userId}")
                        .WithMinValues(1)
                        .WithMaxValues(1);

                    foreach (var search in recentSearches)
                    {
                        // Format: "CharName - RealmName (REGION)"
                        var label = $"{search.CharacterName} - {search.RealmName} ({search.Region.ToUpper()})";

                        // Truncate label if too long (max 100 chars for Discord)
                        if (label.Length > 100)
                        {
                            label = label.Substring(0, 97) + "...";
                        }

                        // Value encodes character info: "CharName~RealmName~Region"
                        var value = $"{search.CharacterName}~{search.RealmName}~{search.Region}";

                        // Description shows search count or last searched
                        var description = search.SearchCount > 1
                            ? $"Searched {search.SearchCount} times"
                            : "Searched once";

                        selectMenuBuilder.AddOption(label, value, description);
                    }

                    builder.WithSelectMenu(selectMenuBuilder);
                }

                // Add "Save Character" button if character info is provided
                if (!string.IsNullOrEmpty(charName) && !string.IsNullOrEmpty(realmName) && !string.IsNullOrEmpty(regionName))
                {
                    // Encode character info in custom ID: "rio_save_char~CharName~RealmName~Region"
                    var customId = $"rio_save_char~{charName}~{realmName}~{regionName}";

                    builder.WithButton(
                        label: "Save Character",
                        customId: customId,
                        style: ButtonStyle.Primary,
                        emote: new Emoji("💾"),
                        row: 1 // Put button on second row so it doesn't conflict with select menu
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building RIO components for user {UserId}", userId);
                // Return empty builder if there's an error
            }

            return builder;
        }

        /// <summary>
        /// Build component with character select menu and management buttons
        /// </summary>
        private ComponentBuilder BuildCharacterManagementComponents(List<WowCharAssociation> characters)
        {
            var builder = new ComponentBuilder();
            try
            {
                if (characters.Any())
                {
                    // Add select menu with all characters
                    var selectMenuBuilder = new SelectMenuBuilder()
                        .WithPlaceholder("Select a character to manage...")
                        .WithCustomId("char_select")
                        .WithMinValues(1)
                        .WithMaxValues(1);

                    foreach (var character in characters)
                    {
                        var mainIndicator = character.IsMain ? "★ " : "";
                        var realm = !string.IsNullOrEmpty(character.WowRealm) ? character.WowRealm : "Unknown Realm";
                        var region = !string.IsNullOrEmpty(character.WowRegion) ? character.WowRegion.ToUpper() : "US";

                        // Format: "★ CharName - RealmName (REGION)" or "CharName - RealmName (REGION)"
                        var label = $"{mainIndicator}{character.CharName} - {realm} ({region})";

                        // Truncate if too long
                        if (label.Length > 100)
                        {
                            label = label.Substring(0, 97) + "...";
                        }

                        // Value encodes character ID
                        var value = character.Id.ToString();

                        // Description shows main status
                        var description = character.IsMain ? "Your main character" : "Alt character";

                        selectMenuBuilder.AddOption(label, value, description);
                    }

                    builder.WithSelectMenu(selectMenuBuilder);

                    // Add management buttons on row 1 (disabled until character is selected)
                    builder.WithButton(
                        label: "Set as Main",
                        customId: "char_set_main",
                        style: ButtonStyle.Success,
                        emote: new Emoji("⭐"),
                        row: 1,
                        disabled: true
                    );

                    builder.WithButton(
                        label: "Remove Character",
                        customId: "char_remove",
                        style: ButtonStyle.Danger,
                        emote: new Emoji("🗑️"),
                        row: 1,
                        disabled: true
                    );

                    builder.WithButton(
                        label: "View RIO Profile",
                        customId: "char_view_rio",
                        style: ButtonStyle.Primary,
                        emote: new Emoji("📊"),
                        row: 1,
                        disabled: true
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building character management components");
                // Return empty builder if there's an error
            }

            return builder;
        }

        /// <summary>
        /// Handle recent search selection from component interaction
        /// Uses wildcard matching to extract and validate original user ID (Discord.NET best practice)
        /// </summary>
        [ComponentInteraction("rio_recent_search~*")]
        public async Task HandleRecentSearchSelection(string originalUserIdStr, string[] selections)
        {
            try
            {
                // Parse and validate the original user ID from CustomId
                if (!ulong.TryParse(originalUserIdStr, out var originalUserId))
                {
                    await RespondAsync("❌ Invalid interaction data. Please try again.", ephemeral: true);
                    return;
                }

                // SECURITY: Validate the person clicking is the original requester
                // This prevents users from hijacking each other's public RIO posts
                if (Context.User.Id != originalUserId)
                {
                    _logger.LogWarning(
                        "User {AttemptingUserId} ({AttemptingUsername}) tried to interact with User {OriginalUserId}'s RIO search dropdown",
                        Context.User.Id, Context.User.Username, originalUserId);

                    await RespondAsync(
                        "❌ This search history belongs to another user.\n\n" +
                        "Use `/rio` to search for your own characters.",
                        ephemeral: true);
                    return;
                }

                // Defer the interaction to acknowledge it (now safe - user validated)
                await DeferAsync();

                // Parse the selected character info: "CharName~RealmName~Region"
                var parts = selections[0].Split('~', 3);

                if (parts.Length < 3)
                {
                    await FollowupAsync("Invalid character selection. Please try again.", ephemeral: true);
                    return;
                }

                string charName = parts[0];
                string realmName = parts[1];
                string regionName = parts[2];

                _logger.LogInformation(
                    "User {UserId} selected recent search: {Character} - {Realm} ({Region})",
                    Context.User.Id, charName, realmName, regionName);

                var sb = new StringBuilder();
                var embed = new EmbedBuilder();

                // Fetch RaiderIO data
                RaiderIOModels.RioMythicPlusChar mPlusInfo = null;
                string realmSlug = string.Empty;

                try
                {
                    mPlusInfo = await _rioApi.GetCharMythicPlusInfoAsync(
                        charName: charName,
                        realmName: realmName.Replace(" ", "%20"),
                        region: regionName.ToLower());
                }
                catch (InvalidOperationException ex)
                {
                    embed.Title = "Character Not Found";
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Description = $"Could not find **{charName}** on **{realmName}** ({regionName.ToUpper()}) in RaiderIO.\n\n" +
                        "The character may have been deleted or has no recent activity.\n\n" +
                        $"*Error: {ex.Message}*";

                    // Update the message with error
                    var errorComponents = await BuildRioComponents(Context.User.Id, charName, realmName, regionName);
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = embed.Build();
                        msg.Components = errorComponents.Build();
                    });
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching RaiderIO data for {Character} on {Realm}", charName, realmName);
                    embed.Title = "Error Fetching Data";
                    embed.WithColor(new Color(255, 165, 0));
                    embed.Description = $"An error occurred while fetching RaiderIO data for **{charName}**.\n\n" +
                        "Please try again later.";

                    var errorComponents2 = await BuildRioComponents(Context.User.Id, charName, realmName, regionName);
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = embed.Build();
                        msg.Components = errorComponents2.Build();
                    });
                    return;
                }

                // Determine realm slug for WarcraftLogs URL
                switch (regionName.ToLower())
                {
                    case "us":
                        realmSlug = WowApi.RealmInfo.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                    case "ru":
                        realmSlug = WowApi.RealmInfoRu.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                    case "eu":
                        realmSlug = WowApi.RealmInfoEu.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                    default:
                        realmSlug = WowApi.RealmInfo.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                }

                embed.Title = $"{mPlusInfo.ActiveSpecName} {mPlusInfo.Class} - {mPlusInfo.Name}";

                // Item Level
                if (mPlusInfo.Gear != null)
                {
                    if (mPlusInfo.Gear.ItemLevelTotal > mPlusInfo.Gear.ItemLevelEquipped)
                    {
                        sb.AppendLine($"**Item Level:** {mPlusInfo.Gear.ItemLevelEquipped} (equipped) / {mPlusInfo.Gear.ItemLevelTotal} (max)");
                    }
                    else
                    {
                        sb.AppendLine($"**Item Level:** {mPlusInfo.Gear.ItemLevelEquipped}");
                    }
                    sb.AppendLine();
                }

                // Season Scores Breakdown
                if (mPlusInfo.MythicPlusScores?.Length > 0)
                {
                    var scores = mPlusInfo.MythicPlusScores[0].Scores;
                    sb.AppendLine($"**__Season M+ Scores__**");
                    sb.AppendLine($"Overall: **{scores.All:F1}**");
                    if (scores.Dps > 0)
                        sb.AppendLine($"DPS: {scores.Dps:F1}");
                    if (scores.Healer > 0)
                        sb.AppendLine($"Healer: {scores.Healer:F1}");
                    if (scores.Tank > 0)
                        sb.AppendLine($"Tank: {scores.Tank:F1}");
                    sb.AppendLine();
                }

                // Raid Progression
                if (mPlusInfo.RaidProgression.ManaforgeOmega != null)
                {
                    var raid = mPlusInfo.RaidProgression.ManaforgeOmega;
                    string normalKilled = _wowUtils.GetNumberEmojiFromString((int)raid.NormalBossesKilled);
                    string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)raid.HeroicBossesKilled);
                    string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)raid.MythicBossesKilled);
                    string totalBosses = _wowUtils.GetNumberEmojiFromString((int)raid.TotalBosses);

                    sb.AppendLine($"**__Raid Progression__**");
                    sb.AppendLine($"__Manaforge Omega__");
                    sb.AppendLine($"**Normal** [{normalKilled} / {totalBosses}] {GetProgressBar(raid.NormalBossesKilled, raid.TotalBosses)}");
                    sb.AppendLine($"**Heroic** [{heroicKilled} / {totalBosses}] {GetProgressBar(raid.HeroicBossesKilled, raid.TotalBosses)}");
                    sb.AppendLine($"**Mythic** [{mythicKilled} / {totalBosses}] {GetProgressBar(raid.MythicBossesKilled, raid.TotalBosses)}");
                    sb.AppendLine();
                }

                // M+ Rankings
                sb.AppendLine($"**__M+ Rankings For Active Role ({mPlusInfo.ActiveSpecRole})__**");
                switch (mPlusInfo.ActiveSpecRole.ToLower())
                {
                    case "dps":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Dps.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Dps.Region}**] World [**{mPlusInfo.MythicPlusRanks.Dps.World}**]");
                        break;
                    case "healing":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Healer.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Healer.Region}**] World [**{mPlusInfo.MythicPlusRanks.Healer.World}**]");
                        break;
                    case "tank":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Tank.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Tank.Region}**] World [**{mPlusInfo.MythicPlusRanks.Tank.World}**]");
                        break;
                }

                sb.AppendLine($"**__M+ Rankings For Class ({mPlusInfo.Class})__**");
                sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Class.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Class.Region}**] World [**{mPlusInfo.MythicPlusRanks.Class.World}**]");
                sb.AppendLine();

                // Best Runs
                if (mPlusInfo.MythicPlusBestRuns?.Length > 0)
                {
                    sb.AppendLine($"**__Best Runs__**");
                    foreach (var run in mPlusInfo.MythicPlusBestRuns)
                    {
                        var keyEmoji = run.MythicLevel >= 20 ? "🔑" : "▪️";
                        var minutes = run.ClearTimeMs / 60000;
                        if (run.Url != null)
                        {
                            sb.AppendLine($"{keyEmoji} [{run.ShortName}(**+{run.MythicLevel}**) - {minutes}m]({run.Url.AbsoluteUri})");
                        }
                        else
                        {
                            sb.AppendLine($"{keyEmoji} {run.ShortName}(**+{run.MythicLevel}**) - {minutes}m");
                        }
                    }
                    sb.AppendLine();
                }

                // Weekly Progress (if available)
                if (mPlusInfo.MythicPlusWeeklyHighestLevelRuns?.Length > 0)
                {
                    sb.AppendLine($"**__This Week's Highest Keys__**");
                    foreach (var run in mPlusInfo.MythicPlusWeeklyHighestLevelRuns.Take(4))
                    {
                        var keyEmoji = run.MythicLevel >= 15 ? "⭐" : "▪️";
                        if (run.Url != null)
                        {
                            sb.AppendLine($"{keyEmoji} [{run.ShortName} **+{run.MythicLevel}**]({run.Url.AbsoluteUri})");
                        }
                        else
                        {
                            sb.AppendLine($"{keyEmoji} {run.ShortName} **+{run.MythicLevel}**");
                        }
                    }
                    sb.AppendLine();
                }

                embed.AddField("Raider.IO", $"[{mPlusInfo.Name}]({mPlusInfo.ProfileUrl.AbsoluteUri})", true);
                embed.AddField("Warcraftlogs", $"[{mPlusInfo.Name}](https://www.warcraftlogs.com/character/{regionName}/{realmSlug}/{mPlusInfo.Name})", true);
                embed.ThumbnailUrl = $"{mPlusInfo.ThumbnailUrl.AbsoluteUri}";
                embed.Description = sb.ToString();

                // Color based on M+ score
                var score = mPlusInfo.MythicPlusScores?[0]?.Scores?.All ?? 0;
                var color = score >= 3000 ? new Color(255, 128, 0) : // Orange for 3000+
                            score >= 2500 ? new Color(163, 53, 238) : // Purple for 2500+
                            score >= 2000 ? new Color(0, 112, 221) : // Blue for 2000+
                            new Color(0, 200, 150); // Teal default
                embed.WithColor(color);

                embed.Footer = new EmbedFooterBuilder
                {
                    Text = $"Raider.IO Score: {score:F1} | {mPlusInfo.Realm} ({regionName.ToUpper()})"
                };

                // Update search history
                await SaveSearchHistoryAsync(Context.User.Id, charName, realmName, regionName);

                // Update the original message with the new character data
                var components = await BuildRioComponents(Context.User.Id, charName, realmName, regionName);
                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling recent search selection for user {UserId}", Context.User.Id);
                await FollowupAsync("An error occurred while processing your selection. Please try again.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Save Character" button interaction from RIO results
        /// </summary>
        [ComponentInteraction("rio_save_char~*~*~*")]
        public async Task HandleSaveCharacterButton(string charName, string realmName, string regionName)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation(
                    "User {UserId} attempting to save character: {Character} - {Realm} ({Region})",
                    Context.User.Id, charName, realmName, regionName);

                // Set locale based on region
                string locale = regionName.ToLower() switch
                {
                    "us" => "en_US",
                    "eu" => "en_GB",
                    "kr" => "ko_KR",
                    "tw" => "zh_TW",
                    "cn" => "zh_CN",
                    _ => "en_US"
                };

                // Normalize realm name for comparison (remove spaces, hyphens, apostrophes, and lowercase)
                string normalizedRealmName = realmName.Replace(" ", "").Replace("-", "").Replace("'", "").ToLower();

                var message = await WithDbAsync(async db =>
                {
                    // Check if character already exists for this user
                    var existingChar = db.WowCharAssociation
                        .Where(a => a.UserId == (long)Context.User.Id)
                        .AsEnumerable() // Switch to client-side evaluation for complex string operations
                        .Where(a => a.CharName.ToLower() == charName.ToLower() &&
                                    a.WowRealm.Replace(" ", "").Replace("-", "").Replace("'", "").ToLower() == normalizedRealmName)
                        .FirstOrDefault();

                    if (existingChar != null)
                    {
                        // Character already saved
                        var mainIndicator = existingChar.IsMain ? " (your **main character**)" : "";
                        return $"**{charName}** on **{realmName}** is already saved{mainIndicator}!\n\n" +
                            "Use `/getchars` to manage your saved characters.";
                    }
                    else
                    {
                        // Add new character (NOT as main)
                        db.WowCharAssociation.Add(new WowCharAssociation
                        {
                            UserId = (long)Context.User.Id,
                            IsMain = false,
                            CharName = charName,
                            WowRealm = realmName,
                            WowRegion = regionName,
                            Locale = locale
                        });

                        await db.SaveChangesAsync();

                        return $"✅ Successfully saved **{charName}** on **{realmName}** ({regionName.ToUpper()})!\n\n" +
                            "Use `/getchars` to manage all your saved characters.\n";     
                    }
                });

                // Invalidate cache after adding new character
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);

                await FollowupAsync(message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving character for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while saving your character. Please try again.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle character selection from getchars menu
        /// </summary>
        [ComponentInteraction("char_select")]
        public async Task HandleCharacterSelection(string[] selections)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(selections[0]);
                var character = await WithDbAsync(db =>
                    db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefaultAsync());

                if (character == null)
                {
                    await FollowupAsync("❌ Character not found.", ephemeral: true);
                    return;
                }

                // Build action buttons for the selected character
                var builder = new ComponentBuilder()
                    .WithButton(
                        label: "Set as Main",
                        customId: $"char_set_main~{characterId}",
                        style: ButtonStyle.Success,
                        emote: new Emoji("⭐"),
                        disabled: character.IsMain // Disable if already main
                    )
                    .WithButton(
                        label: "Remove Character",
                        customId: $"char_remove~{characterId}",
                        style: ButtonStyle.Danger,
                        emote: new Emoji("🗑️")
                    )
                    .WithButton(
                        label: "View RIO Profile",
                        customId: $"char_view_rio~{characterId}",
                        style: ButtonStyle.Primary,
                        emote: new Emoji("📊")
                    )
                    .WithButton(
                        label: "← Back to List",
                        customId: "char_back_to_list",
                        style: ButtonStyle.Secondary,
                        emote: new Emoji("↩️")
                    );

                var mainIndicator = character.IsMain ? "★ [MAIN]" : "";
                var realm = !string.IsNullOrEmpty(character.WowRealm) ? character.WowRealm : "Unknown Realm";
                var region = !string.IsNullOrEmpty(character.WowRegion) ? character.WowRegion.ToUpper() : "US";

                var embed = new EmbedBuilder()
                    .WithTitle("Character Management")
                    .WithDescription($"**Selected:** {mainIndicator} **{character.CharName}** - {realm} ({region})\n\nChoose an action below:")
                    .WithColor(character.IsMain ? new Color(255, 215, 0) : new Color(0, 200, 150))
                    .WithThumbnailUrl(Context.User.GetAvatarUrl())
                    .Build();

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = builder.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling character selection for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while processing your selection.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Set as Main" button for character management
        /// </summary>
        [ComponentInteraction("char_set_main~*")]
        public async Task HandleSetAsMain(string characterIdStr)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(characterIdStr);
                var (success, message) = await WithDbAsync(async db =>
                {
                    var character = db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefault();

                    if (character == null)
                    {
                        return (false, "❌ Character not found.");
                    }

                    if (character.IsMain)
                    {
                        return (false, $"**{character.CharName}** is already your main character!");
                    }

                    // Unset other mains
                    var otherMains = db.WowCharAssociation
                        .Where(a => a.UserId == (long)Context.User.Id && a.IsMain)
                        .ToList();

                    foreach (var main in otherMains)
                    {
                        main.IsMain = false;
                    }

                    // Set this character as main
                    character.IsMain = true;
                    await db.SaveChangesAsync();

                    return (true, $"⭐ **{character.CharName}** on **{character.WowRealm}** is now your main character!");
                });

                // Invalidate both main and all characters cache after updating IsMain flag
                _wowCache.InvalidateUserMainCharacter((long)Context.User.Id);
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);

                await FollowupAsync(message, ephemeral: true);

                if (success)
                {
                    // Refresh the character list
                    await RefreshCharacterList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting character as main for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while setting your main character.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Remove Character" button for character management
        /// </summary>
        [ComponentInteraction("char_remove~*")]
        public async Task HandleRemoveCharacter(string characterIdStr)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(characterIdStr);
                var (success, message) = await WithDbAsync(async db =>
                {
                    var character = db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefault();

                    if (character == null)
                    {
                        return (false, "❌ Character not found.");
                    }

                    var charName = character.CharName;
                    var realmName = character.WowRealm;

                    db.WowCharAssociation.Remove(character);
                    await db.SaveChangesAsync();

                    return (true, $"🗑️ Removed **{charName}** from **{realmName}**.");
                });

                // Invalidate cache after removing character
                // Also invalidate main character cache in case removed character was main
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);
                _wowCache.InvalidateUserMainCharacter((long)Context.User.Id);

                await FollowupAsync(message, ephemeral: true);

                if (success)
                {
                    // Refresh the character list
                    await RefreshCharacterList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing character for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while removing your character.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "View RIO Profile" button for character management
        /// </summary>
        [ComponentInteraction("char_view_rio~*")]
        public async Task HandleViewRioProfile(string characterIdStr)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(characterIdStr);
                var character = await WithDbAsync(db =>
                    db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefaultAsync());

                if (character == null)
                {
                    await FollowupAsync("❌ Character not found.", ephemeral: true);
                    return;
                }

                var charName = character.CharName;
                var realmName = character.WowRealm;
                var regionName = character.WowRegion?.ToLower() ?? "us";

                var sb = new StringBuilder();
                var embed = new EmbedBuilder();

                // Fetch RaiderIO data
                RaiderIOModels.RioMythicPlusChar mPlusInfo = null;
                string realmSlug = string.Empty;

                try
                {
                    mPlusInfo = await _rioApi.GetCharMythicPlusInfoAsync(
                        charName: charName,
                        realmName: realmName.Replace(" ", "%20"),
                        region: regionName);
                }
                catch (InvalidOperationException ex)
                {
                    embed.Title = "Character Not Found";
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Description = $"Could not find **{charName}** on **{realmName}** ({regionName.ToUpper()}) in RaiderIO.\n\n" +
                        "The character may have been deleted or has no recent activity.\n\n" +
                        $"*Error: {ex.Message}*";
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching RaiderIO data for {Character} on {Realm}", charName, realmName);
                    embed.Title = "Error Fetching Data";
                    embed.WithColor(new Color(255, 165, 0));
                    embed.Description = $"An error occurred while fetching RaiderIO data for **{charName}**.\n\n" +
                        "Please try again later.";
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }

                // Determine realm slug for WarcraftLogs URL
                switch (regionName)
                {
                    case "us":
                        realmSlug = WowApi.RealmInfo.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                    case "ru":
                        realmSlug = WowApi.RealmInfoRu.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                    case "eu":
                        realmSlug = WowApi.RealmInfoEu.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                    default:
                        realmSlug = WowApi.RealmInfo.realms
                            .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                            .Select(s => s.slug)
                            .FirstOrDefault() ?? realmName;
                        break;
                }

                embed.Title = $"{mPlusInfo.ActiveSpecName} {mPlusInfo.Class} - {mPlusInfo.Name}";

                // Item Level
                if (mPlusInfo.Gear != null)
                {
                    if (mPlusInfo.Gear.ItemLevelTotal > mPlusInfo.Gear.ItemLevelEquipped)
                    {
                        sb.AppendLine($"**Item Level:** {mPlusInfo.Gear.ItemLevelEquipped} (equipped) / {mPlusInfo.Gear.ItemLevelTotal} (max)");
                    }
                    else
                    {
                        sb.AppendLine($"**Item Level:** {mPlusInfo.Gear.ItemLevelEquipped}");
                    }
                    sb.AppendLine();
                }

                // Season Scores Breakdown
                if (mPlusInfo.MythicPlusScores?.Length > 0)
                {
                    var scores = mPlusInfo.MythicPlusScores[0].Scores;
                    sb.AppendLine($"**__Season M+ Scores__**");
                    sb.AppendLine($"Overall: **{scores.All:F1}**");
                    if (scores.Dps > 0)
                        sb.AppendLine($"DPS: {scores.Dps:F1}");
                    if (scores.Healer > 0)
                        sb.AppendLine($"Healer: {scores.Healer:F1}");
                    if (scores.Tank > 0)
                        sb.AppendLine($"Tank: {scores.Tank:F1}");
                    sb.AppendLine();
                }

                // Raid Progression
                if (mPlusInfo.RaidProgression.ManaforgeOmega != null)
                {
                    var raid = mPlusInfo.RaidProgression.ManaforgeOmega;
                    string normalKilled = _wowUtils.GetNumberEmojiFromString((int)raid.NormalBossesKilled);
                    string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)raid.HeroicBossesKilled);
                    string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)raid.MythicBossesKilled);
                    string totalBosses = _wowUtils.GetNumberEmojiFromString((int)raid.TotalBosses);

                    sb.AppendLine($"**__Raid Progression__**");
                    sb.AppendLine($"__Manaforge Omega__");
                    sb.AppendLine($"**Normal** [{normalKilled} / {totalBosses}] {GetProgressBar(raid.NormalBossesKilled, raid.TotalBosses)}");
                    sb.AppendLine($"**Heroic** [{heroicKilled} / {totalBosses}] {GetProgressBar(raid.HeroicBossesKilled, raid.TotalBosses)}");
                    sb.AppendLine($"**Mythic** [{mythicKilled} / {totalBosses}] {GetProgressBar(raid.MythicBossesKilled, raid.TotalBosses)}");
                    sb.AppendLine();
                }

                // M+ Rankings
                sb.AppendLine($"**__M+ Rankings For Active Role ({mPlusInfo.ActiveSpecRole})__**");
                switch (mPlusInfo.ActiveSpecRole.ToLower())
                {
                    case "dps":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Dps.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Dps.Region}**] World [**{mPlusInfo.MythicPlusRanks.Dps.World}**]");
                        break;
                    case "healing":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Healer.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Healer.Region}**] World [**{mPlusInfo.MythicPlusRanks.Healer.World}**]");
                        break;
                    case "tank":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Tank.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Tank.Region}**] World [**{mPlusInfo.MythicPlusRanks.Tank.World}**]");
                        break;
                }

                sb.AppendLine($"**__M+ Rankings For Class ({mPlusInfo.Class})__**");
                sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Class.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Class.Region}**] World [**{mPlusInfo.MythicPlusRanks.Class.World}**]");
                sb.AppendLine();

                // Best Runs
                if (mPlusInfo.MythicPlusBestRuns?.Length > 0)
                {
                    sb.AppendLine($"**__Best Runs__**");
                    foreach (var run in mPlusInfo.MythicPlusBestRuns)
                    {
                        var keyEmoji = run.MythicLevel >= 20 ? "🔑" : "▪️";
                        var minutes = run.ClearTimeMs / 60000;
                        if (run.Url != null)
                        {
                            sb.AppendLine($"{keyEmoji} [{run.ShortName}(**+{run.MythicLevel}**) - {minutes}m]({run.Url.AbsoluteUri})");
                        }
                        else
                        {
                            sb.AppendLine($"{keyEmoji} {run.ShortName}(**+{run.MythicLevel}**) - {minutes}m");
                        }
                    }
                    sb.AppendLine();
                }

                // Weekly Progress (if available)
                if (mPlusInfo.MythicPlusWeeklyHighestLevelRuns?.Length > 0)
                {
                    sb.AppendLine($"**__This Week's Highest Keys__**");
                    foreach (var run in mPlusInfo.MythicPlusWeeklyHighestLevelRuns.Take(4))
                    {
                        var keyEmoji = run.MythicLevel >= 15 ? "⭐" : "▪️";
                        if (run.Url != null)
                        {
                            sb.AppendLine($"{keyEmoji} [{run.ShortName} **+{run.MythicLevel}**]({run.Url.AbsoluteUri})");
                        }
                        else
                        {
                            sb.AppendLine($"{keyEmoji} {run.ShortName} **+{run.MythicLevel}**");
                        }
                    }
                    sb.AppendLine();
                }

                embed.AddField("Raider.IO", $"[{mPlusInfo.Name}]({mPlusInfo.ProfileUrl.AbsoluteUri})", true);
                embed.AddField("Warcraftlogs", $"[{mPlusInfo.Name}](https://www.warcraftlogs.com/character/{regionName}/{realmSlug}/{mPlusInfo.Name})", true);
                embed.ThumbnailUrl = mPlusInfo.ThumbnailUrl?.AbsoluteUri;
                embed.Description = sb.ToString();

                // Color based on M+ score
                var score = mPlusInfo.MythicPlusScores?[0]?.Scores?.All ?? 0;
                var color = score >= 3000 ? new Color(255, 128, 0) : // Orange for 3000+
                            score >= 2500 ? new Color(163, 53, 238) : // Purple for 2500+
                            score >= 2000 ? new Color(0, 112, 221) : // Blue for 2000+
                            new Color(0, 200, 150); // Teal default
                embed.WithColor(color);

                embed.Footer = new EmbedFooterBuilder
                {
                    Text = $"Raider.IO Score: {score:F1} | {mPlusInfo.Realm} ({regionName.ToUpper()})"
                };

                // Save search history
                await SaveSearchHistoryAsync(Context.User.Id, charName, realmName, regionName);

                // Add components with cached searches and save button
                var components = await BuildRioComponents(Context.User.Id, charName, realmName, regionName);

                await FollowupAsync(embed: embed.Build(), components: components.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error viewing RIO profile for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while loading the RIO profile.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Back to List" button to return to character list
        /// </summary>
        [ComponentInteraction("char_back_to_list")]
        public async Task HandleBackToList()
        {
            await DeferAsync();
            await RefreshCharacterList();
        }

        /// <summary>
        /// Helper method to refresh the character list display
        /// </summary>
        private async Task RefreshCharacterList()
        {
            try
            {
                var savedChars = await WithDbAsync(db =>
                    db.WowCharAssociation
                        .Where(c => c.UserId == (long)Context.User.Id)
                        .OrderByDescending(c => c.IsMain)
                        .ThenBy(c => c.CharName)
                        .ToListAsync());

                if (savedChars.Any())
                {
                    var embed = new EmbedBuilder();
                    var sb = new StringBuilder();

                    embed.Title = $"Your Saved Characters ({savedChars.Count})";
                    embed.WithColor(new Color(0, 200, 150));

                    foreach (var character in savedChars)
                    {
                        var mainIndicator = character.IsMain ? "★ [MAIN]" : "";
                        var realm = !string.IsNullOrEmpty(character.WowRealm) ? character.WowRealm : "Unknown Realm";
                        var region = !string.IsNullOrEmpty(character.WowRegion) ? character.WowRegion.ToUpper() : "US";

                        sb.AppendLine($"{mainIndicator} **{character.CharName}** - {realm} ({region})");
                    }

                    sb.AppendLine();
                    sb.AppendLine("*Select a character below to manage it*");

                    embed.Description = sb.ToString();
                    embed.ThumbnailUrl = Context.User.GetAvatarUrl();

                    var components = BuildCharacterManagementComponents(savedChars);

                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = embed.Build();
                        msg.Components = components.Build();
                    });
                }
                else
                {
                    var embed = new EmbedBuilder();
                    var sb = new StringBuilder();

                    embed.Title = "No Saved Characters";
                    embed.WithColor(new Color(255, 165, 0));
                    sb.AppendLine("You haven't saved any characters yet!");
                    sb.AppendLine();
                    sb.AppendLine("Use `/setchar` to associate a character with your Discord account.");

                    embed.Description = sb.ToString();
                    embed.ThumbnailUrl = Context.User.GetAvatarUrl();

                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = embed.Build();
                        msg.Components = new ComponentBuilder().Build();
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing character list for user {UserId}", Context.User.Id);
            }
        }

        private string NormalizeSlot(string slot)
        {
            return slot switch
            {
                "FINGER_1" => "Ring 1",
                "FINGER_2" => "Ring 2",
                "TRINKET_1" => "Trinket 1",
                "TRINKET_2" => "Trinket 2",
                "MAIN_HAND" => "Main Hand",
                "OFF_HAND" => "Off Hand",
                _ => slot.Replace('_', ' ').ToLowerInvariant().Split(' ').Select(w => char.ToUpper(w[0]) + w.Substring(1)).Aggregate((a, b) => $"{a} {b}")
            };
        }

        private string BuildWowheadGuideUrl(string className, string specName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(specName))
            {
                return "https://www.wowhead.com/class-guides";
            }

            var query = Uri.EscapeDataString($"{specName} {className} guide");
            return $"https://www.wowhead.com/search?q={query}";
        }

        private string BuildStatsSummary(Dictionary<string, int> statTotals)
        {
            if (statTotals == null || statTotals.Count == 0)
            {
                return null;
            }

            var statMeta = new (string Key, string Label, string Emoji)[]
            {
                ("INTELLECT", "Int", "🧠"),
                ("STRENGTH", "Str", "💪"),
                ("AGILITY", "Agi", "🦊"),
                ("STAMINA", "Stam", "❤️"),
                ("CRITICAL_STRIKE", "Crit", "🎯"),
                ("HASTE", "Haste", "⚡"),
                ("MASTERY", "Mast", "✨"),
                ("VERSATILITY", "Vers", "🛡️"),
                ("AVOIDANCE", "Avoid", "🌀"),
                ("LEECH", "Leech", "🩸")
            };

            var primary = new List<string>();
            var secondary = new List<string>();

            foreach (var (key, label, emoji) in statMeta)
            {
                if (statTotals.TryGetValue(key, out var value) && value > 0)
                {
                    var entry = $"{emoji} {label} {value}";
                    if (key is "INTELLECT" or "STRENGTH" or "AGILITY" or "STAMINA")
                    {
                        primary.Add(entry);
                    }
                    else
                    {
                        secondary.Add(entry);
                    }
                }
            }

            var lines = new List<string>();
            if (primary.Count > 0)
            {
                lines.Add(string.Join("   ", primary));
            }
            if (secondary.Count > 0)
            {
                lines.Add(string.Join("   ", secondary));
            }

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private string FormatStatName(string statKey)
        {
            var statMeta = new Dictionary<string, (string Label, string Emoji)>
            {
                { "INTELLECT", ("Int", "🧠") },
                { "STRENGTH", ("Str", "💪") },
                { "AGILITY", ("Agi", "🦊") },
                { "STAMINA", ("Stam", "❤️") },
                { "CRITICAL_STRIKE", ("Crit", "🎯") },
                { "HASTE", ("Haste", "⚡") },
                { "MASTERY", ("Mastery", "✨") },
                { "VERSATILITY", ("Versatility", "🛡️") },
                { "AVOIDANCE", ("Avoidance", "🌀") },
                { "LEECH", ("Leech", "🩸") },
                { "SPEED", ("Speed", "🏃") },
                { "DODGE", ("Dodge", "🤸") },
                { "PARRY", ("Parry", "🛡") },
                { "ARMOR", ("Armor", "🪖") }
            };

            if (statMeta.TryGetValue(statKey.ToUpperInvariant(), out var meta))
            {
                return $"{meta.Emoji} {meta.Label}";
            }

            // Fallback: capitalize first letter of each word
            return string.Join(" ", statKey.Split('_').Select(w =>
                w.Length > 0 ? char.ToUpper(w[0]) + w.Substring(1).ToLower() : w));
        }

        private string BuildSetsSection(IEnumerable<(string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)> sets)
        {
            if (sets == null)
            {
                return null;
            }

            var setStrings = new List<string>();
            foreach (var set in sets)
            {
                if (string.IsNullOrEmpty(set.Name))
                {
                    continue;
                }

                var equippedCount = set.ItemIds.Count;
                var total = set.TotalPieces > 0 ? set.TotalPieces : set.ItemIds.Count;
                var sb = new StringBuilder();
                sb.AppendLine($"🧩 **{set.Name}** ({equippedCount}/{total})");
                foreach (var effect in set.Effects ?? Enumerable.Empty<ArmorySetEffect>())
                {
                    var marker = effect.IsActive ? "✅" : "▫️";
                    var display = effect.DisplayString;
                    if (!string.IsNullOrEmpty(display) && display.Length > 170)
                    {
                        display = display.Substring(0, 167) + "...";
                    }
                    sb.AppendLine($" • {marker} ({effect.RequiredCount}) {display}");
                }
                setStrings.Add(sb.ToString().TrimEnd());
            }

            return setStrings.Count == 0 ? null : string.Join("\n\n", setStrings);
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

        private void AppendGearSlot(
            StringBuilder sb,
            string slotName,
            int? itemId,
            string itemName,
            string icon,
            int? itemLevel,
            int? quality = null,
            string qualityName = null)
        {
            if (!itemId.HasValue || string.IsNullOrEmpty(itemName))
            {
                sb.AppendLine($"▫️ **{slotName}:** _empty_");
                return;
            }

            var wowheadUrl = $"https://www.wowhead.com/item={itemId.Value}";
            var iconUrl = string.IsNullOrEmpty(icon)
                ? string.Empty
                : $" [icon](https://render.worldofwarcraft.com/icons/56/{icon}.jpg)";
            var qualityEmoji = GetQualityEmojiByName(qualityName) ?? GetQualityEmoji(quality);

            sb.AppendLine($"{qualityEmoji} **{slotName}:** [{itemName}]({wowheadUrl}) — ilvl {itemLevel ?? 0}{iconUrl}");
        }

        private string GetQualityEmoji(int? quality)
        {
            return quality switch
            {
                >= 6 => "🟠", // Artifact/Mythic+
                5 => "🟠",   // Legendary
                4 => "🟣",   // Epic
                3 => "🔵",   // Rare
                2 => "🟢",   // Uncommon
                _ => "⚪"    // Common/poor
            };
        }

        private string GetQualityEmojiByName(string qualityName)
        {
            return qualityName?.ToLower() switch
            {
                "legendary" => "🟠",
                "artifact" => "🟠",
                "epic" => "🟣",
                "rare" => "🔵",
                "uncommon" => "🟢",
                "common" => "⚪",
                _ => null
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

        #region Housing Commands

        [SlashCommand("housing-random-decor", "Get a random housing decor item")]
        public async Task GetRandomDecor(
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                // Fetch a larger page to get better random selection
                var url = "/data/wow/search/decor?namespace=static-us&_page=1&_pageSize=1000";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var decorSearch = JsonConvert.DeserializeObject<DecorSearchResponse>(response);

                if (decorSearch?.Results == null || decorSearch.Results.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Decor Items Found")
                        .WithDescription("Unable to fetch housing decor items at this time.")
                        .WithColor(Color.Red)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var random = new Random();
                var randomDecor = decorSearch.Results[random.Next(decorSearch.Results.Count)];

                var embed = new EmbedBuilder()
                    .WithTitle($"🏠 {randomDecor.Data.Name.EnUS}")
                    .WithColor(new Color(0, 176, 240))
                    .AddField("Decor ID", randomDecor.Data.Id, inline: true);

                if (randomDecor.Data.Item != null)
                {
                    var itemId = randomDecor.Data.Item.Id;
                    var itemName = randomDecor.Data.Item.Name?.EnUS ?? "Unknown Item";
                    var wowheadUrl = $"https://www.wowhead.com/item={itemId}";

                    embed.AddField("Item", $"[{itemName}]({wowheadUrl})", inline: true)
                        .AddField("Item ID", itemId, inline: true);

                    // Try to get item details and media
                    try
                    {
                        var itemUrl = $"/data/wow/item/{itemId}?namespace=static-us";
                        var itemResponse = await _wowApi.GetAPIRequestAsync(itemUrl, "en_US", "us");
                        var itemData = JsonConvert.DeserializeObject<dynamic>(itemResponse);

                        // Add quality/rarity if available
                        if (itemData?.quality?.name != null)
                        {
                            string qualityName = itemData.quality.name.ToString();
                            var qualityEmoji = GetQualityEmojiByName(qualityName);
                            if (qualityEmoji != null)
                            {
                                embed.AddField("Quality", $"{qualityEmoji} {qualityName}", inline: true);
                            }
                        }

                        // Get item media for larger image display
                        var mediaUrl = $"/data/wow/media/item/{itemId}?namespace=static-us";
                        var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrl, "en_US", "us");
                        var mediaData = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                        var assets = (IEnumerable<dynamic>)mediaData.assets;

                        var renderAsset = assets
                            .FirstOrDefault(a => (string)a.key == "render") ?? assets
                            .FirstOrDefault(a => (string)a.key == "icon");

                        string renderUrl = renderAsset?.value;

                        if (!string.IsNullOrEmpty(renderUrl))
                        {
                            embed.WithImageUrl(renderUrl);
                        }
                    }
                    catch
                    {
                        // If item/media fetch fails, just continue without extra details
                    }
                }

                // Calculate approximate total (pageCount * pageSize, or results count if only 1 page)
                long estimatedTotal = decorSearch.PageCount > 1
                    ? decorSearch.PageCount * decorSearch.PageSize
                    : decorSearch.Results.Count;

                string footerText = decorSearch.ResultCountCapped
                    ? $"Showing {decorSearch.Results.Count} of {estimatedTotal:N0}+ decor items"
                    : $"Showing {decorSearch.Results.Count} of ~{estimatedTotal:N0} total decor items";

                embed.WithFooter(footerText);

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching random decor");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while fetching random decor item.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
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
                        .WithTitle($"No Results Found")
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
                    var decorName = result.Data.Name.EnUS;
                    var decorId = result.Data.Id;

                    sb.Append($"🏠 **{decorName}** (ID: {decorId})");

                    if (result.Data.Item != null)
                    {
                        var itemId = result.Data.Item.Id;
                        var wowheadUrl = $"https://www.wowhead.com/item={itemId}";
                        sb.Append($" — [Item: {result.Data.Item.Name?.EnUS ?? "Unknown"}]({wowheadUrl})");
                    }

                    sb.AppendLine();
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Showing {decorSearch.Results.Take(10).Count()} of {decorSearch.Results.Count} results");

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

        [SlashCommand("housing-list-rooms", "List all available housing rooms")]
        public async Task ListRooms(
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var url = "/data/wow/room/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var roomIndex = JsonConvert.DeserializeObject<RoomIndexResponse>(response);

                if (roomIndex?.Rooms == null || roomIndex.Rooms.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Rooms Found")
                        .WithDescription("Unable to fetch housing rooms at this time.")
                        .WithColor(Color.Red)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                // Filter out invalid rooms (ID 0 or empty names)
                var validRooms = roomIndex.Rooms
                    .Where(r => r.Id > 0 && !string.IsNullOrWhiteSpace(r.Name))
                    .OrderBy(r => r.Name)
                    .ToList();

                var embed = new EmbedBuilder()
                    .WithTitle("🏡 Available Housing Rooms")
                    .WithColor(new Color(92, 184, 92));

                var sb = new StringBuilder();
                foreach (var room in validRooms)
                {
                    sb.AppendLine($"🚪 **{room.Name}** (ID: {room.Id})");
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Total rooms: {validRooms.Count}");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching room list");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while fetching room list.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("housing-random-room", "Get details about a random housing room")]
        public async Task GetRandomRoom(
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var url = "/data/wow/room/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var roomIndex = JsonConvert.DeserializeObject<RoomIndexResponse>(response);

                if (roomIndex?.Rooms == null || roomIndex.Rooms.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Rooms Found")
                        .WithDescription("Unable to fetch housing rooms at this time.")
                        .WithColor(Color.Red)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var random = new Random();
                var randomRoom = roomIndex.Rooms[random.Next(roomIndex.Rooms.Count)];

                // Get room details
                var detailUrl = $"/data/wow/room/{randomRoom.Id}?namespace=static-us";
                var detailResponse = await _wowApi.GetAPIRequestAsync(detailUrl, "en_US", "us");
                var roomDetail = JsonConvert.DeserializeObject<RoomResponse>(detailResponse);

                var embed = new EmbedBuilder()
                    .WithTitle($"🏡 {randomRoom.Name}")
                    .WithColor(new Color(92, 184, 92))
                    .AddField("Room ID", randomRoom.Id, inline: true);

                if (roomDetail != null && !string.IsNullOrEmpty(roomDetail.Name))
                {
                    embed.WithDescription($"**{roomDetail.Name}**");
                }

                // Try to fetch room media (though it might not exist)
                try
                {
                    var mediaUrl = $"/data/wow/media/room/{randomRoom.Id}?namespace=static-us";
                    var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrl, "en_US", "us");
                    var mediaData = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                    if (mediaData?.assets != null && mediaData.assets.Count > 0)
                    {
                        string imageUrl = mediaData.assets[0].value?.ToString();
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            embed.WithImageUrl(imageUrl);
                        }
                    }
                }
                catch
                {
                    // Room media likely doesn't exist in the API yet
                }

                embed.WithFooter($"Total rooms available: {roomIndex.Rooms.Count}");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching random room");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while fetching random room.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("housing-search-fixtures", "Search for housing fixtures")]
        public async Task SearchFixtures(
            [Summary("name", "Fixture name to search for")]
            string name,

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var encodedName = Uri.EscapeDataString(name);
                var url = $"/data/wow/search/fixture?namespace=static-us&name.en_US={encodedName}&_pageSize=10";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var fixtureSearch = JsonConvert.DeserializeObject<FixtureSearchResponse>(response);

                if (fixtureSearch?.Results == null || fixtureSearch.Results.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle($"No Results Found")
                        .WithDescription($"No fixtures found matching '{name}'")
                        .WithColor(Color.Orange)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"🔧 Fixture Search: {name}")
                    .WithColor(new Color(217, 83, 79));

                var sb = new StringBuilder();
                foreach (var result in fixtureSearch.Results.Take(10))
                {
                    sb.AppendLine($"🔧 **{result.Data.Name.EnUS}** (ID: {result.Data.Id})");
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Showing {fixtureSearch.Results.Take(10).Count()} of {fixtureSearch.Results.Count} results");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching fixtures");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while searching for fixtures.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        #endregion

        #region Static Data Commands

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
                    embed.AddField("Equippable", "✓", inline: true);
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
                                embed.AddField("📊 Stats", statText, inline: false);
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
                                var spellText = string.Join("\n", spells.Select(s => $"📜 {s.description}"));
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
                        embed.AddField("💎 Sockets", itemDetails.SocketCount.ToString(), inline: true);
                    }

                    // Display set information
                    if (!string.IsNullOrEmpty(itemDetails.SetName))
                    {
                        var setField = $"🧩 **{itemDetails.SetName}**";

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
                    .WithTitle($"💰 WoW Token Price - {region.ToUpper()}")
                    .WithColor(new Color(255, 209, 0)) // Gold color
                    .AddField("Current Price", $"{priceGold:N0} gold", inline: true)
                    .AddField("Last Updated", $"<t:{((DateTimeOffset)tokenPrice.Timestamp).ToUnixTimeSeconds()}:R>", inline: true);

                if (trend.HasValue)
                {
                    var trendGold = trend.Value / 10000;
                    var trendEmoji = trend.Value > 0 ? "📈" : trend.Value < 0 ? "📉" : "➡️";
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

        private async Task<EmbedBuilder> BuildMountPageAsync(List<WowMounts> mounts, int page, int pageSize, string charName, string realmName, string regionName, int collectedCount, int totalCount, string sourceFilter, string expansionFilter)
        {
            var embed = new EmbedBuilder();
            embed.Title = $"🏇 Missing Mounts - {charName}";

            // Dynamic color based on collection progress
            var progress = totalCount > 0 ? (collectedCount * 100.0 / totalCount) : 0;
            var embedColor = progress switch
            {
                >= 90 => new Color(0, 255, 0),      // Green - Almost complete!
                >= 70 => new Color(138, 43, 226),   // Purple - Good progress
                >= 50 => new Color(255, 165, 0),    // Orange - Halfway there
                _ => new Color(255, 87, 51)         // Red - Long way to go
            };
            embed.WithColor(embedColor);

            // Visual progress bar using Unicode blocks
            var filledBlocks = (int)(progress / 10);
            var emptyBlocks = 10 - filledBlocks;
            var progressBar = new string('█', filledBlocks) + new string('░', emptyBlocks);

            var description = $"**Collected:** {collectedCount}/{totalCount} ({progress:F1}%)\n{progressBar}";

            // Show active filters
            if (expansionFilter != "all")
            {
                description += $"\n**Expansion:** {expansionFilter}";
            }

            if (sourceFilter != "all")
            {
                var friendlySource = GetFriendlySourceName(sourceFilter);
                description += $"\n**Source:** {friendlySource}";
            }

            embed.Description = description;

            // Fetch character thumbnail
            try
            {
                var realmSlug = realmName.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
                var armoryMedia = await _wowApi.GetArmoryMediaAsync(charName, realmSlug, regionName);
                if (armoryMedia?.Assets != null)
                {
                    var avatarAsset = armoryMedia.Assets.FirstOrDefault(a => a.Key == "avatar");
                    if (avatarAsset != null && !string.IsNullOrEmpty(avatarAsset.Value))
                    {
                        embed.WithThumbnailUrl(avatarAsset.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch character thumbnail for {CharName}", charName);
                // Continue without thumbnail
            }

            // Add expansion breakdown if showing all expansions and there are mounts
            if (expansionFilter == "all" && mounts.Count > 0)
            {
                var expansionStats = mounts
                    .Where(m => !string.IsNullOrEmpty(m.Expansion))
                    .GroupBy(m => m.Expansion)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => $"{g.Key}: **{g.Count()}**")
                    .ToList();

                if (expansionStats.Count > 0)
                {
                    embed.AddField("📊 Missing by Expansion (Top 5)",
                        string.Join(" • ", expansionStats),
                        inline: false);
                }
            }

            var pageMounts = mounts.Skip(page * pageSize).Take(pageSize).ToList();

            foreach (var mount in pageMounts)
            {
                // Use Wowhead search since Blizzard mount IDs don't match Wowhead item IDs
                var encodedName = Uri.EscapeDataString(mount.Name);
                var wowheadUrl = $"https://www.wowhead.com/search?q={encodedName}";
                var fieldName = mount.Name; // Plain text - embed field names don't support markdown

                var fieldValue = new StringBuilder();

                // Add Wowhead search link
                fieldValue.AppendLine($"🔗 [Search on Wowhead]({wowheadUrl})");

                // Format source intelligently
                var sourceEmoji = mount.Source?.ToUpper() switch
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

                // Determine what to show for source
                string displaySource = null;

                // Priority: Instance name + encounter name > SourceDetail > Friendly source type
                if (!string.IsNullOrEmpty(mount.InstanceName))
                {
                    // Show instance and encounter from journal data
                    if (!string.IsNullOrEmpty(mount.EncounterName))
                    {
                        displaySource = $"{mount.InstanceName} - {mount.EncounterName}";
                    }
                    else
                    {
                        displaySource = mount.InstanceName;
                    }
                }
                else
                {
                    // Fallback to SourceDetail if meaningful
                    var sourceDetail = mount.SourceDetail;
                    var isGenericDetail = string.IsNullOrEmpty(sourceDetail) ||
                        sourceDetail.Equals(mount.Source, StringComparison.OrdinalIgnoreCase) ||
                        sourceDetail.Equals("Drop", StringComparison.OrdinalIgnoreCase) ||
                        sourceDetail.Equals("Achievement", StringComparison.OrdinalIgnoreCase) ||
                        sourceDetail.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ||
                        sourceDetail.Equals("Quest", StringComparison.OrdinalIgnoreCase);

                    if (!isGenericDetail)
                    {
                        displaySource = sourceDetail;
                    }
                    else
                    {
                        // Show friendly source type name
                        displaySource = mount.Source?.ToUpper() switch
                        {
                            "DROP" => "Boss Drop",
                            "ACHIEVEMENT" => "Achievement Reward",
                            "VENDOR" => "Vendor Purchase",
                            "QUEST" => "Quest Reward",
                            "PROFESSION" => "Crafted",
                            "WORLD_EVENT" => "Holiday Event",
                            "PROMOTION" => "Promotional",
                            "TRADING_POST" => "Trading Post",
                            "STORE" => "Blizzard Store",
                            "PVP" => "PvP Reward",
                            "REPUTATION" => "Reputation Reward",
                            "CLASS" => "Class Mount",
                            "COVENANT" => "Covenant",
                            "GARRISON" => "Garrison",
                            _ => mount.Source ?? "Unknown"
                        };
                    }
                }

                fieldValue.AppendLine($"{sourceEmoji} {displaySource}");

                if (!string.IsNullOrEmpty(mount.Description))
                {
                    var desc = mount.Description.Length > 100 ? mount.Description.Substring(0, 97) + "..." : mount.Description;
                    fieldValue.AppendLine($"_{desc}_");
                }

                embed.AddField(fieldName, fieldValue.ToString().TrimEnd(), inline: false);
            }

            var totalPages = (int)Math.Ceiling(mounts.Count / (double)pageSize);
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"Page {page + 1}/{totalPages} • {mounts.Count} missing mount(s) total"
            };

            return embed;
        }

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

        private ComponentBuilder BuildMountPaginationComponents(int currentPage, int totalMounts, int pageSize, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, ulong userId, List<WowMounts> pageMounts)
        {
            var totalPages = (int)Math.Ceiling(totalMounts / (double)pageSize);
            var componentBuilder = new ComponentBuilder();

            // Expansion filter dropdown (Row 0)
            var expansionOptions = new List<SelectMenuOptionBuilder>
            {
                new SelectMenuOptionBuilder("All Expansions", "all", "Show all expansions", isDefault: expansionFilter == "all"),
                new SelectMenuOptionBuilder("The War Within", "The War Within", isDefault: expansionFilter == "The War Within"),
                new SelectMenuOptionBuilder("Dragonflight", "Dragonflight", isDefault: expansionFilter == "Dragonflight"),
                new SelectMenuOptionBuilder("Shadowlands", "Shadowlands", isDefault: expansionFilter == "Shadowlands"),
                new SelectMenuOptionBuilder("Battle for Azeroth", "Battle for Azeroth", isDefault: expansionFilter == "Battle for Azeroth"),
                new SelectMenuOptionBuilder("Legion", "Legion", isDefault: expansionFilter == "Legion"),
                new SelectMenuOptionBuilder("Warlords of Draenor", "Warlords of Draenor", isDefault: expansionFilter == "Warlords of Draenor"),
                new SelectMenuOptionBuilder("Mists of Pandaria", "Mists of Pandaria", isDefault: expansionFilter == "Mists of Pandaria"),
                new SelectMenuOptionBuilder("Cataclysm", "Cataclysm", isDefault: expansionFilter == "Cataclysm"),
                new SelectMenuOptionBuilder("Wrath of the Lich King", "Wrath of the Lich King", isDefault: expansionFilter == "Wrath of the Lich King"),
                new SelectMenuOptionBuilder("The Burning Crusade", "The Burning Crusade", isDefault: expansionFilter == "The Burning Crusade"),
                new SelectMenuOptionBuilder("Classic", "Classic", isDefault: expansionFilter == "Classic")
            };

            componentBuilder.WithSelectMenu(
                customId: $"mount_expansion~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}",
                options: expansionOptions,
                placeholder: "Filter by Expansion",
                minValues: 1,
                maxValues: 1,
                row: 0);

            // Source filter dropdown (Row 1)
            var sourceOptions = new List<SelectMenuOptionBuilder>
            {
                new SelectMenuOptionBuilder("All Sources", "all", "Show all mounts", isDefault: sourceFilter == "all"),
                new SelectMenuOptionBuilder("Drops (Raids & Dungeons)", "DROP", "Mounts from boss drops", isDefault: sourceFilter == "DROP"),
                new SelectMenuOptionBuilder("Achievements", "ACHIEVEMENT", "Mounts from achievements", isDefault: sourceFilter == "ACHIEVEMENT"),
                new SelectMenuOptionBuilder("Vendors", "VENDOR", "Mounts purchased from vendors", isDefault: sourceFilter == "VENDOR"),
                new SelectMenuOptionBuilder("Quests", "QUEST", "Mounts from quest rewards", isDefault: sourceFilter == "QUEST"),
                new SelectMenuOptionBuilder("Professions", "PROFESSION", "Mounts crafted by professions", isDefault: sourceFilter == "PROFESSION"),
                new SelectMenuOptionBuilder("World Events", "WORLD_EVENT", "Mounts from holidays/events", isDefault: sourceFilter == "WORLD_EVENT")
            };

            componentBuilder.WithSelectMenu(
                customId: $"mount_source~{userId}~{charName}~{realmName}~{regionName}~{expansionFilter}",
                options: sourceOptions,
                placeholder: "Filter by Source",
                minValues: 1,
                maxValues: 1,
                row: 1);

            // Mount selection dropdown (Row 2) - only if there are mounts to display
            if (pageMounts != null && pageMounts.Any())
            {
                var mountOptions = pageMounts.Select(m =>
                {
                    var description = m.Expansion ?? "Unknown";
                    if (!string.IsNullOrEmpty(m.Source))
                    {
                        description += $" • {m.Source}";
                    }
                    return new SelectMenuOptionBuilder(
                        label: m.Name.Length > 100 ? m.Name.Substring(0, 97) + "..." : m.Name,
                        value: m.Id.ToString(),
                        description: description.Length > 100 ? description.Substring(0, 97) + "..." : description
                    );
                }).ToList();

                componentBuilder.WithSelectMenu(
                    customId: $"mount_details~{userId}~{regionName}~{charName}~{realmName}~{sourceFilter}~{expansionFilter}",
                    options: mountOptions,
                    placeholder: "View mount details...",
                    minValues: 1,
                    maxValues: 1,
                    row: 2);
            }

            // Pagination buttons (Row 3)
            var firstButton = new ButtonBuilder()
                .WithLabel("⏮ First")
                .WithCustomId($"mount_first~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}")
                .WithStyle(ButtonStyle.Secondary)
                .WithDisabled(currentPage == 0);

            var prevButton = new ButtonBuilder()
                .WithLabel("◀ Previous")
                .WithCustomId($"mount_prev~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}")
                .WithStyle(ButtonStyle.Primary)
                .WithDisabled(currentPage == 0);

            var pageIndicator = new ButtonBuilder()
                .WithLabel($"Page {currentPage + 1}/{totalPages}")
                .WithCustomId("mount_page_info")
                .WithStyle(ButtonStyle.Secondary)
                .WithDisabled(true);

            var nextButton = new ButtonBuilder()
                .WithLabel("Next ▶")
                .WithCustomId($"mount_next~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}")
                .WithStyle(ButtonStyle.Primary)
                .WithDisabled(currentPage >= totalPages - 1);

            var lastButton = new ButtonBuilder()
                .WithLabel("Last ⏭")
                .WithCustomId($"mount_last~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}~{currentPage}~{totalPages}")
                .WithStyle(ButtonStyle.Secondary)
                .WithDisabled(currentPage >= totalPages - 1);

            componentBuilder.WithButton(firstButton, row: 3);
            componentBuilder.WithButton(prevButton, row: 3);
            componentBuilder.WithButton(pageIndicator, row: 3);
            componentBuilder.WithButton(nextButton, row: 3);
            componentBuilder.WithButton(lastButton, row: 3);

            return componentBuilder;
        }

        [ComponentInteraction("mount_first~*~*~*~*~*~*~*")]
        public async Task HandleMountFirst(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("❌ This pagination belongs to another user.", ephemeral: true);
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
                await RespondAsync("❌ This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("❌ Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            int newPage = Math.Max(0, currentPage - 1);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, newPage);
        }

        [ComponentInteraction("mount_next~*~*~*~*~*~*~*")]
        public async Task HandleMountNext(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("❌ This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("❌ Invalid page data.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            int newPage = currentPage + 1;
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, newPage);
        }

        [ComponentInteraction("mount_last~*~*~*~*~*~*~*~*")]
        public async Task HandleMountLast(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, string currentPageStr, string totalPagesStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("❌ This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(totalPagesStr, out var totalPages))
            {
                await RespondAsync("❌ Invalid page data.", ephemeral: true);
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
                await RespondAsync("❌ This filter belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("❌ No source selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var newSource = selections[0];
            await UpdateMountPage(charName, realmName, regionName, newSource, expansionFilter, 0); // Reset to page 0 when changing filter
        }

        [ComponentInteraction("mount_expansion~*~*~*~*~*")]
        public async Task HandleMountExpansionFilter(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string[] selections)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("❌ This filter belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("❌ No expansion selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var newExpansion = selections[0];
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, newExpansion, 0); // Reset to page 0 when changing filter
        }

        [ComponentInteraction("mount_details~*~*~*~*~*~*")]
        public async Task HandleMountDetails(string userIdStr, string regionName, string charName, string realmName, string sourceFilter, string expansionFilter, string[] selections)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("❌ This belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("❌ No mount selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            try
            {
                if (!long.TryParse(selections[0], out var mountId))
                {
                    await FollowupAsync("❌ Invalid mount ID.", ephemeral: true);
                    return;
                }

                var mount = await WithDbAsync(async db =>
                    await db.WowMounts.FirstOrDefaultAsync(m => m.Id == mountId));

                if (mount == null)
                {
                    await FollowupAsync("❌ Mount not found in database.", ephemeral: true);
                    return;
                }

                // Use Wowhead search since Blizzard mount IDs don't match Wowhead item IDs
                var encodedName = Uri.EscapeDataString(mount.Name);
                var wowheadSearchUrl = $"https://www.wowhead.com/search?q={encodedName}";

                var embed = new EmbedBuilder()
                    .WithTitle(mount.Name)
                    .WithColor(new Color(138, 43, 226))
                    .WithUrl(wowheadSearchUrl);

                if (!string.IsNullOrEmpty(mount.Description))
                {
                    embed.WithDescription($"*{mount.Description}*");
                }

                // Source information with smart formatting
                var sourceEmoji = mount.Source?.ToUpper() switch
                {
                    "DROP" => "💀",
                    "ACHIEVEMENT" => "🏆",
                    "VENDOR" => "💰",
                    "QUEST" => "❗",
                    "PROFESSION" => "🔨",
                    "WORLD_EVENT" => "🎃",
                    "PROMOTION" => "🎁",
                    _ => "📍"
                };

                var sourceDetail = mount.SourceDetail;
                var isGenericDetail = string.IsNullOrEmpty(sourceDetail) ||
                    sourceDetail.Equals(mount.Source, StringComparison.OrdinalIgnoreCase) ||
                    sourceDetail.Equals("Drop", StringComparison.OrdinalIgnoreCase) ||
                    sourceDetail.Equals("Achievement", StringComparison.OrdinalIgnoreCase) ||
                    sourceDetail.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ||
                    sourceDetail.Equals("Quest", StringComparison.OrdinalIgnoreCase);

                string sourceFieldValue;
                if (!isGenericDetail)
                {
                    sourceFieldValue = sourceDetail;
                }
                else
                {
                    sourceFieldValue = mount.Source?.ToUpper() switch
                    {
                        "DROP" => "Boss Drop",
                        "ACHIEVEMENT" => "Achievement Reward",
                        "VENDOR" => "Vendor Purchase",
                        "QUEST" => "Quest Reward",
                        "PROFESSION" => "Crafted",
                        "WORLD_EVENT" => "Holiday Event",
                        "PROMOTION" => "Promotional",
                        _ => mount.Source ?? "Unknown"
                    };
                }

                embed.AddField($"{sourceEmoji} Source", sourceFieldValue, inline: false);

                // Drop location if available
                if (!string.IsNullOrEmpty(mount.DropLocation))
                {
                    embed.AddField("🎯 Location", mount.DropLocation, inline: true);
                }

                // Expansion
                if (!string.IsNullOrEmpty(mount.Expansion))
                {
                    embed.AddField("📚 Expansion", mount.Expansion, inline: true);
                }

                // Faction restriction
                if (!string.IsNullOrEmpty(mount.Faction))
                {
                    var factionEmoji = mount.Faction.ToLower() == "alliance" ? "🔵" : mount.Faction.ToLower() == "horde" ? "🔴" : "⚪";
                    embed.AddField("Faction", $"{factionEmoji} {mount.Faction}", inline: true);
                }

                // Mount types
                var mountTypes = new List<string>();
                if (mount.IsGround) mountTypes.Add("🐎 Ground");
                if (mount.IsFlying) mountTypes.Add("🦅 Flying");
                if (mount.IsAquatic) mountTypes.Add("🐠 Aquatic");

                if (mountTypes.Any())
                {
                    embed.AddField("Type", string.Join(", ", mountTypes), inline: true);
                }

                // Fetch mount image on-demand
                if (mount.CreatureDisplayId.HasValue)
                {
                    try
                    {
                        _logger.LogDebug("Fetching media for mount {MountId} ({Name}), creature display {DisplayId}",
                            mount.Id, mount.Name, mount.CreatureDisplayId.Value);

                        var creatureMedia = await _wowApi.GetCreatureDisplayMediaAsync(mount.CreatureDisplayId.Value, regionName);

                        if (creatureMedia?.Assets != null && creatureMedia.Assets.Count > 0)
                        {
                            // Prefer 'main' asset, fall back to first available
                            var mainAsset = creatureMedia.Assets.FirstOrDefault(a => a.Key == "main")
                                ?? creatureMedia.Assets[0];

                            embed.WithImageUrl(mainAsset.Value);
                            _logger.LogDebug("Mount {MountId} using asset '{AssetKey}': {Url}",
                                mount.Id, mainAsset.Key, mainAsset.Value);
                        }
                        else
                        {
                            _logger.LogWarning("Mount {MountId} creature display {DisplayId} returned no assets",
                                mount.Id, mount.CreatureDisplayId.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch media for mount {MountId} ({Name})",
                            mount.Id, mount.Name);
                    }
                }
                else
                {
                    _logger.LogDebug("Mount {MountId} ({Name}) has no creature display ID", mount.Id, mount.Name);
                }

                embed.WithFooter($"Mount ID: {mount.Id} | Last updated: {mount.LastUpdated:yyyy-MM-dd}");

                // Create back button to return to list
                var backButton = new ComponentBuilder()
                    .WithButton("← Back to List", $"mount_back~{userId}~{charName}~{realmName}~{regionName}~{sourceFilter}~{expansionFilter}", ButtonStyle.Secondary)
                    .Build();

                // Replace the original message with mount details
                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = backButton;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error displaying mount details for mount ID");
                await FollowupAsync("❌ An error occurred while loading mount details.", ephemeral: true);
            }
        }

        [ComponentInteraction("mount_back~*~*~*~*~*~*")]
        public async Task HandleMountBack(string userIdStr, string charName, string realmName, string regionName, string sourceFilter, string expansionFilter)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("❌ This pagination belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);
            await UpdateMountPage(charName, realmName, regionName, sourceFilter, expansionFilter, 0);
        }

        private async Task UpdateMountPage(string charName, string realmName, string regionName, string sourceFilter, string expansionFilter, int page)
        {
            try
            {
                // Fetch character's mount collection
                var mountCollection = await _wowApi.GetCharacterMountsAsync(charName, realmName, regionName);
                if (mountCollection?.Mounts == null)
                {
                    await FollowupAsync("❌ Could not load mount collection.", ephemeral: true);
                    return;
                }

                var collectedMountIds = new HashSet<long>(mountCollection.Mounts.Select(m => m.Mount.Id));
                var allMounts = await _wowStaticData.GetAllMountsAsync();

                // Filter by expansion if specified
                var filteredMounts = allMounts;
                if (expansionFilter != "all")
                {
                    filteredMounts = filteredMounts.Where(m => string.Equals(m.Expansion, expansionFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                // Filter by source if specified
                if (sourceFilter != "all")
                {
                    filteredMounts = filteredMounts.Where(m => string.Equals(m.Source, sourceFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                var missingMounts = filteredMounts
                    .Where(m => !collectedMountIds.Contains(m.Id))
                    .OrderBy(m => m.Expansion)
                    .ThenBy(m => m.Source)
                    .ThenBy(m => m.Name)
                    .ToList();

                var collectedCount = filteredMounts.Count(m => collectedMountIds.Contains(m.Id));
                var totalCount = filteredMounts.Count;

                int pageSize = 5;
                var pageData = await BuildMountPageAsync(missingMounts, page, pageSize, charName, realmName, regionName, collectedCount, totalCount, sourceFilter, expansionFilter);

                // Get current page mounts for the selection dropdown
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
                await FollowupAsync("❌ An error occurred while updating the page.", ephemeral: true);
            }
        }

        #endregion
    }
}
