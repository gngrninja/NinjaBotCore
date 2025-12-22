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
using System.Threading;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Database;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    // Interaction modules must be public and inherit from an IInteractionModuleBase
    public class WowInteract : InteractionModuleBase<ShardedInteractionContext>
    {
        // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
        public InteractionService Commands { get; set; }
        private InteractionHandler _handler;
        private ChannelCheck _cc;
        private WarcraftLogs _logsApi;
        private WarcraftLogsV2Client _logsApiV2;
        private WowApi _wowApi;
        private DiscordShardedClient _client;
        private RaiderIOApi _rioApi;
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private readonly ILogger _logger;
        private WowUtilities _wowUtils;
        private readonly NinjaBotEntities _db;

        public WowInteract(IServiceProvider services)
        {
            _handler = services.GetRequiredService<InteractionHandler>();
            _logger = services.GetRequiredService<ILogger<WowInteract>>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _logsApi = services.GetRequiredService<WarcraftLogs>();
            _logsApiV2 = services.GetRequiredService<WarcraftLogsV2Client>();
            _wowApi = services.GetRequiredService<WowApi>();
            _rioApi = services.GetRequiredService<RaiderIOApi>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _config = services.GetRequiredService<IConfigurationRoot>();
            _wowUtils = services.GetRequiredService<WowUtilities>();
            _db = services.GetRequiredService<NinjaBotEntities>();
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
                var charAssociation = _db.WowCharAssociation
                    .Where(c => c.UserId == (long)Context.User.Id && c.IsMain)
                    .FirstOrDefault();

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
                        "Use `/setchar` with `isMain: true` to set one, or provide a character name to search.";
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
                    var guildie = _wowApi.GetCharFromGuild(
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
                    var chars = _wowApi.SearchArmory(charName);
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

            await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
        }

        [SlashCommand("clearhistoryrio", "Clear your RaiderIO search history")]
        public async Task ClearRioHistory()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                using (var db = new NinjaBotEntities())
                {
                    var userHistory = db.RioSearchHistory
                        .Where(h => h.DiscordUserId == (long)Context.User.Id)
                        .ToList();

                    if (userHistory.Any())
                    {
                        db.RioSearchHistory.RemoveRange(userHistory);
                        await db.SaveChangesAsync();

                        await FollowupAsync($"✅ Cleared **{userHistory.Count}** RaiderIO search history entries.", ephemeral: true);
                    }
                    else
                    {
                        await FollowupAsync("No search history found to clear.", ephemeral: true);
                    }
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

            // Handle autocomplete format: "CharName RealmName" or just "CharName"
            var parts = character.Split(' ', 2);
            charName = parts[0];

            if (parts.Length > 1)
            {
                realmName = parts[1];
            }

            // If no realm from autocomplete, try to look up the character
            if (string.IsNullOrEmpty(realmName))
            {
                try
                {
                    var result = await _wowUtils.GetCharFromArgs(character, Context);
                    charName = result.charName;
                    realmName = result.realmName;
                    regionName = result.regionName;
                    locale = result.locale;
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
                // Realm provided via autocomplete, look up additional info
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

            if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(realmName))
            {
                await FollowupAsync($"Unable to find character: **{character}**\n\nPlease use autocomplete to select a character.", ephemeral: true);
                return;
            }

            // Check if character already exists for this user
            using (var db = new NinjaBotEntities())
            {
                var existingChar = db.WowCharAssociation
                    .Where(a => a.UserId == (long)Context.User.Id &&
                                a.CharName.ToLower() == charName.ToLower() &&
                                a.WowRealm == realmName)
                    .FirstOrDefault();

                if (existingChar != null)
                {
                    // Update existing character
                    if (existingChar.IsMain != isMain)
                    {
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
                    }

                    await db.SaveChangesAsync();

                    var mainText = isMain ? " as your **main character**" : "";
                    await FollowupAsync($"Updated **{charName}** on **{realmName}**{mainText}!", ephemeral: true);
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
                    await FollowupAsync($"Successfully saved **{charName}** on **{realmName}**{mainText}!\n\nUse `/getchars` to see all your saved characters.", ephemeral: true);
                }
            }
        }


        [SlashCommand("getchars", "List your saved WoW characters")]
        public async Task GetChars()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            List<WowCharAssociation> savedChars;
            using (var db = new NinjaBotEntities())
            {
                savedChars = db.WowCharAssociation
                    .Where(c => c.UserId == (long)Context.User.Id)
                    .OrderByDescending(c => c.IsMain)
                    .ThenBy(c => c.CharName)
                    .ToList();
            }

            if (savedChars.Any())
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
                sb.AppendLine("*Use `/rio` to view RaiderIO profile*");
                sb.AppendLine("*Use `/setchar` with `isMain: true` to change your main character*");
            }
            else
            {
                embed.Title = "No Saved Characters";
                embed.WithColor(new Color(255, 165, 0));
                sb.AppendLine("You haven't saved any characters yet!");
                sb.AppendLine();
                sb.AppendLine("Use `/setchar` to associate a character with your Discord account.");
                sb.AppendLine("This allows you to quickly look up RaiderIO info with `/rio`.");
            }

            embed.Description = sb.ToString();
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            await RespondAsync(embed: embed.Build(), ephemeral: true);
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
            var guildStats = _rioApi.GetRioGuildInfo(guildName: guildObject.guildName, realmName: guildObject.realmSlug, region: guildObject.regionName);
                        
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

            affixes = _rioApi.GetCurrentAffix(region: region);

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
            bool enable = false;
            var embed = new EmbedBuilder();
            List<LogMonitoring> logMonitorList = null;
            StringBuilder sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                logMonitorList = db.LogMonitoring.ToList();
            }
            if (logMonitorList != null)
            {
                var getGuild = logMonitorList.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (getGuild != null)
                {
                    if (!getGuild.MonitorLogs)
                    {
                        enable = true;
                    }
                }
                else
                {
                    enable = true;
                }
            }
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
            using (var db = new NinjaBotEntities())
            {
                var getGuild = db.LogMonitoring.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (getGuild != null)
                {
                    getGuild.ChannelId = (long)Context.Channel.Id;
                    getGuild.ChannelName = Context.Channel.Name;
                    getGuild.MonitorLogs = enable;
                }
                else
                {
                    db.LogMonitoring.Add(new LogMonitoring
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        ChannelId = (long)Context.Channel.Id,
                        ChannelName = Context.Channel.Name,
                        MonitorLogs = enable,
                        LatestLog = DateTime.UtcNow
                    });
                }
                await db.SaveChangesAsync();
            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }
        [SlashCommand("wowdiscord", "list class discord servers")]
        public async Task ListWowDiscordServers()
        {
            try
            {
                List<WowResources> resourceList = null;
                using (var db = new NinjaBotEntities())
                {
                    resourceList = db.WowResources.Where(r => r.ResourceDescription == "Discord").ToList();
                }
                if (resourceList != null)
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
                    charAchievements = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName, charInfo.regionName);
                }
                else
                {
                    charAchievements = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName);
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
            int maxReturn = 2;
            int arrayCount = 0;
            string discordGuildName = string.Empty;
            var guildInfo = Context.Guild;
            var embed = new EmbedBuilder();            

            guildObject = await _wowUtils.GetGuildName(Context);
            guildName = guildObject.guildName;
            realmName = guildObject.realmName.Replace("'", string.Empty);
            guildRegion = guildObject.regionName;
            locale = guildObject.locale;
            var realmInfo = new WowRealm.Realm();            
            if (!string.IsNullOrEmpty(locale))
            {
                switch (locale)
                {
                    case "en_US":
                    {
                        realmInfo = WowApi.RealmInfo.realms.FirstOrDefault(r => r.name == guildObject.realmName);                        
                        break;
                    }
                    case "en_GB":
                    {
                        realmInfo = WowApi.RealmInfoEu.realms.FirstOrDefault(r => r.name == guildObject.realmName);   
                        break;
                    }
                    case "ru_RU":
                    {
                        realmInfo = WowApi.RealmInfoRu.realms.FirstOrDefault(r => r.name == guildObject.realmName);   
                        break;
                    }
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
                    for (int i = 0; i <= (guildLogs.Count - 1) && i <= maxReturn; i++)
                    {
                        var startTime = _logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start);
                        var endTime   =  _logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end);
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
                    sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].start)}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].end)}**");
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
                    for (int i = 0; i <= (guildLogs.Count - 1) && i <= maxReturn; i++)
                    {
                        DateTime startTime = DateTime.UtcNow;
                        DateTime endTime = DateTime.UtcNow;

                        if (realmInfo != null && !string.IsNullOrEmpty(realmInfo.timezone))
                        {                            
                            startTime = _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start), realmInfo.timezone);
                            endTime =  _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end), realmInfo.timezone);
                        }
                        else 
                        {
                            startTime = _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start));
                            endTime =  _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end));
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
                    sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].start)}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].end)}**");
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
                    members = _wowApi.GetGuildMembersBySlug(realmName, guildName, locale: locale, regionName: regionName);
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
                    members = await _db.WowGuildRosterMembers
                        .Where(x =>
                            x.GuildName == guildObject.guildName &&
                            x.GuildRealmSlug  == guildObject.realmSlug &&
                            x.Region == guildObject.regionName)
                        .ToListAsync();                        

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
                var guildMembers = _wowApi.GetGuildMembers(realmName, guildName, regionName);
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
            var vids = new List<WowResources>();
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
            using (var db = new NinjaBotEntities())
            {
                vids = db.WowResources.Where(r => r.ResourceDescription == "raidvid").ToList();
            }
            if (vids != null)
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
            var getRealmList = _wowApi.GetRealmStatus(region);                                                       
            var foundRealm = getRealmList.realms.Where(r => r.slug.ToLower().Contains(findMe.ToLower())).FirstOrDefault();
            var connectedUrlFinder = _wowApi.GetSingleRealmInfo(foundRealm.slug);
            var realmResult = _wowApi.GetConnectedRealmInfo(connectedUrlFinder.ConnectedRealm.Href.ToString());
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
            var usersToMove = from.Users;
            var numUsers = from.Users.Count;
            foreach (var user in usersToMove)
            {
                await user.ModifyAsync(u =>
                {
                    u.Channel = to;
                });
                Thread.Sleep(750);
            }
            var message = $"Yoinked [{numUsers}] users from [{from.Name}] to [{to.Name}]!";
            await RespondAsync(message);
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
                        // Use Task.Run with timeout to prevent blocking
                        var guildTask = Task.Run(() => _wowApi.GetCharFromGuild(
                            char2Name,
                            guildObject.realmName,
                            guildObject.guildName,
                            guildObject.regionName));

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
                    var chars = _wowApi.SearchArmory(char2Name);
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
                using (var db = new NinjaBotEntities())
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving RIO search history for user {UserId}", discordUserId);
                // Don't throw - search history is not critical
            }
        }
    }
}
