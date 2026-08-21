using Discord;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using System;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the Mythic+ view embed for character profiles (from Raider.IO data)
    /// </summary>
    public static class CharMythicPlusView
    {
        /// <summary>
        /// Build the M+ view embed from Raider.IO data
        /// </summary>
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            RaiderIOModels.RioMythicPlusChar mPlusInfo,
            WowUtilities wowUtils = null,
            string currentRaidName = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

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
            var currentRaid = CharViewHelpers.GetCurrentRaid(mPlusInfo.RaidProgression, currentRaidName);
            if (currentRaid != null)
            {
                var raid = currentRaid.Value.Value;
                var raidName = CharViewHelpers.FormatRaidName(currentRaid.Value.Key);
                string normalKilled = wowUtils?.GetNumberEmojiFromString((int)raid.NormalBossesKilled) ?? raid.NormalBossesKilled.ToString();
                string heroicKilled = wowUtils?.GetNumberEmojiFromString((int)raid.HeroicBossesKilled) ?? raid.HeroicBossesKilled.ToString();
                string mythicKilled = wowUtils?.GetNumberEmojiFromString((int)raid.MythicBossesKilled) ?? raid.MythicBossesKilled.ToString();
                string totalBosses = wowUtils?.GetNumberEmojiFromString((int)raid.TotalBosses) ?? raid.TotalBosses.ToString();

                sb.AppendLine($"**__Raid Progression__**");
                sb.AppendLine($"__{raidName}__");
                sb.AppendLine($"**Normal** [{normalKilled} / {totalBosses}] {CharViewHelpers.GetProgressBar(raid.NormalBossesKilled, raid.TotalBosses)}");
                sb.AppendLine($"**Heroic** [{heroicKilled} / {totalBosses}] {CharViewHelpers.GetProgressBar(raid.HeroicBossesKilled, raid.TotalBosses)}");
                sb.AppendLine($"**Mythic** [{mythicKilled} / {totalBosses}] {CharViewHelpers.GetProgressBar(raid.MythicBossesKilled, raid.TotalBosses)}");

                // AOTC / Cutting Edge badges
                var hasAotc = raid.HeroicBossesKilled == raid.TotalBosses && raid.TotalBosses > 0;
                var hasCe = raid.MythicBossesKilled == raid.TotalBosses && raid.TotalBosses > 0;
                if (hasAotc || hasCe)
                {
                    sb.Append("**Achievements:** ");
                    if (hasAotc) sb.Append("AOTC ");
                    if (hasCe) sb.Append("Cutting Edge");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            // M+ Rankings
            if (mPlusInfo.MythicPlusRanks != null)
            {
                sb.AppendLine($"**__M+ Rankings For Active Role ({mPlusInfo.ActiveSpecRole})__**");
                switch (mPlusInfo.ActiveSpecRole?.ToLower())
                {
                    case "dps":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Dps?.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Dps?.Region}**] World [**{mPlusInfo.MythicPlusRanks.Dps?.World}**]");
                        break;
                    case "healing":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Healer?.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Healer?.Region}**] World [**{mPlusInfo.MythicPlusRanks.Healer?.World}**]");
                        break;
                    case "tank":
                        sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Tank?.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Tank?.Region}**] World [**{mPlusInfo.MythicPlusRanks.Tank?.World}**]");
                        break;
                }

                if (mPlusInfo.MythicPlusRanks.Class != null)
                {
                    sb.AppendLine($"**__M+ Rankings For Class ({mPlusInfo.Class})__**");
                    sb.AppendLine($"Realm [**{mPlusInfo.MythicPlusRanks.Class.Realm}**] Region [**{mPlusInfo.MythicPlusRanks.Class.Region}**] World [**{mPlusInfo.MythicPlusRanks.Class.World}**]");
                }
                sb.AppendLine();
            }

            // Best Runs
            if (mPlusInfo.MythicPlusBestRuns?.Length > 0)
            {
                sb.AppendLine($"**__Best Runs__**");
                foreach (var run in mPlusInfo.MythicPlusBestRuns.Take(8))
                {
                    var timedIndicator = CharViewHelpers.GetTimedIndicator(run.NumKeystoneUpgrades);
                    var keyEmoji = run.NumKeystoneUpgrades > 0 ? "+" : "x";
                    var minutes = run.ClearTimeMs / 60000;
                    if (run.Url != null)
                    {
                        sb.AppendLine($"[{run.ShortName}(**{keyEmoji}{run.MythicLevel}**) - {minutes}m {timedIndicator}]({run.Url.AbsoluteUri})");
                    }
                    else
                    {
                        sb.AppendLine($"{run.ShortName}(**{keyEmoji}{run.MythicLevel}**) - {minutes}m {timedIndicator}");
                    }
                }
                sb.AppendLine();
            }

            // Weekly Progress
            if (mPlusInfo.MythicPlusWeeklyHighestLevelRuns?.Length > 0)
            {
                sb.AppendLine($"**__Weekly Progress__**");

                var previousRuns = mPlusInfo.MythicPlusPreviousWeeklyHighestLevelRuns ?? Array.Empty<RaiderIOModels.MythicPlusRun>();

                foreach (var run in mPlusInfo.MythicPlusWeeklyHighestLevelRuns.Take(4))
                {
                    var timedIndicator = CharViewHelpers.GetTimedIndicator(run.NumKeystoneUpgrades);

                    // Find matching dungeon from last week
                    var lastWeekRun = previousRuns.FirstOrDefault(r => r.ShortName == run.ShortName);
                    var comparison = "";
                    if (lastWeekRun != null)
                    {
                        var diff = run.MythicLevel - lastWeekRun.MythicLevel;
                        if (diff > 0) comparison = $" *(+{diff})*";
                        else if (diff < 0) comparison = $" *({diff})*";
                    }

                    if (run.Url != null)
                    {
                        sb.AppendLine($"[{run.ShortName} **+{run.MythicLevel}** {timedIndicator}]({run.Url.AbsoluteUri}){comparison}");
                    }
                    else
                    {
                        sb.AppendLine($"{run.ShortName} **+{run.MythicLevel}** {timedIndicator}{comparison}");
                    }
                }
                sb.AppendLine();
            }

            // Links
            embed.AddField("Raider.IO", $"[{mPlusInfo.Name}]({mPlusInfo.ProfileUrl?.AbsoluteUri ?? charInfo.RaiderIoUrl})", true);
            embed.AddField("WarcraftLogs", $"[{mPlusInfo.Name}]({charInfo.WarcraftLogsUrl})", true);

            embed.ThumbnailUrl = mPlusInfo.ThumbnailUrl?.AbsoluteUri;
            embed.Description = sb.ToString();

            // Color based on M+ score
            var score = mPlusInfo.MythicPlusScores?.FirstOrDefault()?.Scores?.All ?? 0;
            embed.WithColor(CharViewHelpers.GetMythicPlusScoreColor(score));

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"Raider.IO Score: {score:F1} | {charInfo.Realm} ({charInfo.Region.ToUpper()})"
            };

            return embed;
        }

        /// <summary>
        /// Build a compact M+ summary for the overview view
        /// </summary>
        public static string BuildCompactSummary(RaiderIOModels.RioMythicPlusChar mPlusInfo)
        {
            if (mPlusInfo?.MythicPlusScores == null || mPlusInfo.MythicPlusScores.Length == 0)
                return "No M+ data";

            var score = mPlusInfo.MythicPlusScores[0].Scores?.All ?? 0;
            var highestKey = mPlusInfo.MythicPlusBestRuns?.FirstOrDefault()?.MythicLevel ?? 0;

            return $"**{score:F0}** score | +{highestKey} best";
        }
    }
}
