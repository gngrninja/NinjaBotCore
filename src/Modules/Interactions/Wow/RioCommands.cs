using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
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
    /// Raider.IO related commands for M+ profiles, comparisons, and search history.
    /// Includes: /rio, /clearhistoryrio, rio_recent_search~*, rio_save_char~*, char_view_rio~*
    /// </summary>
    public class RioCommands : NinjaBotBaseModule
    {
        private readonly ILogger<RioCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly RaiderIOApi _rioApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public RioCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<RioCommands> logger,
            WowApi wowApi,
            RaiderIOApi rioApi,
            WowUtilities wowUtils,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _rioApi = rioApi;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
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
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
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
            realmSlug = GetRealmSlug(realmName, regionName);

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
                realmSlug = GetRealmSlug(realmName, regionName);

                BuildRioEmbed(embed, sb, mPlusInfo, charName, realmName, regionName, realmSlug);

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
                            ServerId = (long)Context.Guild.Id,
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
                realmSlug = GetRealmSlug(realmName, regionName);

                BuildRioEmbed(embed, sb, mPlusInfo, charName, realmName, regionName, realmSlug);

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

        #region Private Helper Methods

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
            // Parse second character - autocomplete may include realm and region in format "CharName~RealmName~Region"
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
            string char2RealmSlug = GetRealmSlug(char2Realm, char2Region);

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
        /// Get realm slug for WarcraftLogs URL based on region
        /// </summary>
        private string GetRealmSlug(string realmName, string regionName)
        {
            return regionName.ToLower() switch
            {
                "us" => WowApi.RealmInfo.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName,
                "ru" => WowApi.RealmInfoRu.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName,
                "eu" => WowApi.RealmInfoEu.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName,
                _ => WowApi.RealmInfo.realms
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName
            };
        }

        /// <summary>
        /// Build the standard RIO embed with all character data
        /// </summary>
        private void BuildRioEmbed(EmbedBuilder embed, StringBuilder sb, RaiderIOModels.RioMythicPlusChar mPlusInfo,
            string charName, string realmName, string regionName, string realmSlug)
        {
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
        }

        #endregion
    }
}
