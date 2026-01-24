using Discord;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the overview embed for character profiles, combining data from multiple sources
    /// </summary>
    public static class CharOverviewView
    {
        /// <summary>
        /// Build the overview embed with combined character data
        /// </summary>
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            RaiderIOModels.RioMythicPlusChar rioData,
            ArmoryEquipment armoryEquipment,
            ArmorySummary armorySummary,
            ArmoryMedia armoryMedia,
            List<LogCharRankings> wclRankings = null,
            ArmoryAchievementsSummary achievements = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            // Determine class/spec from available data
            string specName = rioData?.ActiveSpecName ?? armorySummary?.ActiveSpec?.Name ?? "";
            string className = rioData?.Class ?? armorySummary?.CharacterClass?.Name ?? "Unknown";

            // Title
            embed.Title = !string.IsNullOrEmpty(specName)
                ? $"{specName} {className} - {charInfo.Name}"
                : $"{className} - {charInfo.Name}";

            // Determine color from M+ score if available
            var mPlusScore = rioData?.MythicPlusScores?.FirstOrDefault()?.Scores?.All ?? 0;
            embed.WithColor(CharViewHelpers.GetMythicPlusScoreColor(mPlusScore));

            // Item Level - prefer summary endpoint as equipment endpoint doesn't always include these fields
            var equippedIlvl = armorySummary?.EquippedItemLevel ?? armoryEquipment?.EquippedItemLevel ?? 0;
            var maxIlvl = armorySummary?.AverageItemLevel ?? armoryEquipment?.AverageItemLevel ?? equippedIlvl;

            if (equippedIlvl > 0)
            {
                if (maxIlvl > equippedIlvl)
                {
                    sb.AppendLine($"**Item Level:** {equippedIlvl} / {maxIlvl}");
                }
                else
                {
                    sb.AppendLine($"**Item Level:** {equippedIlvl}");
                }
            }

            // M+ Score
            if (mPlusScore > 0)
            {
                sb.AppendLine($"**M+ Score:** {mPlusScore:F0}");
            }

            // Raid Progression
            var raidProg = rioData?.RaidProgression?.ManaforgeOmega;
            if (raidProg != null && raidProg.TotalBosses > 0)
            {
                var progParts = new List<string>();
                if (raidProg.MythicBossesKilled > 0)
                    progParts.Add($"{raidProg.MythicBossesKilled}/{raidProg.TotalBosses}M");
                if (raidProg.HeroicBossesKilled > 0)
                    progParts.Add($"{raidProg.HeroicBossesKilled}/{raidProg.TotalBosses}H");
                if (raidProg.NormalBossesKilled > 0 && progParts.Count == 0)
                    progParts.Add($"{raidProg.NormalBossesKilled}/{raidProg.TotalBosses}N");

                if (progParts.Any())
                {
                    sb.AppendLine($"**Raid:** {string.Join(" ", progParts)} Manaforge Omega");
                }
            }

            // WCL Summary (lazy loaded - show placeholder if not yet fetched)
            if (wclRankings != null && wclRankings.Any())
            {
                var logsSummary = CharLogsView.BuildCompactSummary(wclRankings);
                sb.AppendLine($"**Logs:** {logsSummary}");
            }
            else
            {
                sb.AppendLine($"**Logs:** *Click WarcraftLogs for parses*");
            }

            // Recent Achievements
            if (achievements?.RecentEvents?.Any() == true)
            {
                sb.AppendLine();
                sb.AppendLine($"**Recent Achievements** ({achievements.TotalPoints:N0} pts)");
                foreach (var ach in achievements.RecentEvents.Take(4))
                {
                    var date = DateTimeOffset.FromUnixTimeMilliseconds(ach.Timestamp).ToString("MMM d");
                    var achName = ach.Achievement?.Name ?? "Unknown Achievement";
                    sb.AppendLine($"• {achName} ({date})");
                }
            }

            sb.AppendLine();

            // Quick links section
            sb.AppendLine("**Quick Links**");
            sb.AppendLine($"[Raider.IO]({charInfo.RaiderIoUrl}) | [WarcraftLogs]({charInfo.WarcraftLogsUrl}) | [Armory]({charInfo.ArmoryUrl})");

            embed.Description = sb.ToString();

            // Thumbnail
            var thumbnailUrl = rioData?.ThumbnailUrl?.AbsoluteUri
                ?? armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                embed.ThumbnailUrl = thumbnailUrl;
            }

            // Footer
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Use buttons below for detailed views"
            };

            return embed;
        }

        /// <summary>
        /// Build the component buttons for the overview view
        /// </summary>
        public static ComponentBuilder BuildComponents(
            ulong userId,
            CharacterInfo charInfo,
            bool hasRioData,
            bool hasArmoryData,
            bool isAlreadySaved = false,
            bool hasAchievements = false)
        {
            var builder = new ComponentBuilder();

            // Encode character info for button custom IDs
            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";

            // Row 1: View buttons
            builder.WithButton(
                label: "Armory",
                customId: $"char_view_gear~{userId}~{charParam}",
                style: hasArmoryData ? ButtonStyle.Primary : ButtonStyle.Secondary,
                emote: new Emoji("\U0001F6E1\uFE0F"),
                disabled: !hasArmoryData,
                row: 0);

            builder.WithButton(
                label: "Raider.IO",
                customId: $"char_view_mplus~{userId}~{charParam}",
                style: hasRioData ? ButtonStyle.Primary : ButtonStyle.Secondary,
                emote: new Emoji("\U0001F511"),
                disabled: !hasRioData,
                row: 0);

            // WarcraftLogs button is always enabled (data is lazy-loaded on click)
            builder.WithButton(
                label: "WarcraftLogs",
                customId: $"char_view_logs~{userId}~{charParam}",
                style: ButtonStyle.Primary,
                emote: new Emoji("\U0001F4CA"),
                row: 0);

            builder.WithButton(
                label: "Achievements",
                customId: $"char_view_achievements~{userId}~{charParam}",
                style: hasAchievements ? ButtonStyle.Primary : ButtonStyle.Secondary,
                emote: new Emoji("🏆"),
                disabled: !hasAchievements,
                row: 0);

            // Row 2: Action buttons
            if (isAlreadySaved)
            {
                builder.WithButton(
                    label: "Saved",
                    customId: $"char_save~{userId}~{charParam}",
                    style: ButtonStyle.Secondary,
                    emote: new Emoji("✅"),
                    disabled: true,
                    row: 1);
            }
            else
            {
                builder.WithButton(
                    label: "Save",
                    customId: $"char_save~{userId}~{charParam}",
                    style: ButtonStyle.Success,
                    emote: new Emoji("💾"),
                    row: 1);
            }

            builder.WithButton(
                label: "Refresh",
                customId: $"char_refresh~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("🔄"),
                row: 1);

            builder.WithButton(
                label: "Share",
                customId: $"char_share~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("📢"),
                row: 1);

            builder.WithButton(
                label: "My Characters",
                customId: $"char_manage_ret~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("📋"),
                row: 1);

            return builder;
        }

        /// <summary>
        /// Build components for a specific detail view (back button + actions)
        /// </summary>
        public static ComponentBuilder BuildDetailViewComponents(
            ulong userId,
            CharacterInfo charInfo,
            string currentView,
            bool isAlreadySaved = false)
        {
            var builder = new ComponentBuilder();
            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";

            // Row 1: Navigation
            builder.WithButton(
                label: "Overview",
                customId: $"char_view_overview~{userId}~{charParam}",
                style: currentView == "overview" ? ButtonStyle.Success : ButtonStyle.Secondary,
                emote: new Emoji("📋"),
                row: 0);

            builder.WithButton(
                label: "Armory",
                customId: $"char_view_gear~{userId}~{charParam}",
                style: currentView == "gear" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("🛡️"),
                row: 0);

            builder.WithButton(
                label: "Raider.IO",
                customId: $"char_view_mplus~{userId}~{charParam}",
                style: currentView == "mplus" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("🔑"),
                row: 0);

            builder.WithButton(
                label: "WarcraftLogs",
                customId: $"char_view_logs~{userId}~{charParam}",
                style: currentView == "logs" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("📊"),
                row: 0);

            builder.WithButton(
                label: "Achievements",
                customId: $"char_view_achievements~{userId}~{charParam}",
                style: currentView == "achievements" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("🏆"),
                row: 0);

            // Row 2: Actions
            if (isAlreadySaved)
            {
                builder.WithButton(
                    label: "Saved",
                    customId: $"char_save~{userId}~{charParam}",
                    style: ButtonStyle.Secondary,
                    emote: new Emoji("✅"),
                    disabled: true,
                    row: 1);
            }
            else
            {
                builder.WithButton(
                    label: "Save",
                    customId: $"char_save~{userId}~{charParam}",
                    style: ButtonStyle.Success,
                    emote: new Emoji("💾"),
                    row: 1);
            }

            builder.WithButton(
                label: "Share",
                customId: $"char_share~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("📢"),
                row: 1);

            builder.WithButton(
                label: "My Characters",
                customId: $"char_manage_ret~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("📋"),
                row: 1);

            return builder;
        }
    }
}
