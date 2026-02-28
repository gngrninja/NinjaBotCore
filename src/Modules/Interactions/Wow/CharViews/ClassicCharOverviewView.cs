using Discord;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the overview embed for Classic WoW character profiles from Classic Raider.IO data
    /// </summary>
    public static class ClassicCharOverviewView
    {
        public static EmbedBuilder Build(ClassicRaiderIOModels.ClassicCharProfile profile)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            // Title: "Class - Name"
            embed.Title = $"{profile.Class ?? "Unknown"} - {profile.Name}";

            // Color based on faction
            embed.WithColor(GetFactionColor(profile.Faction));

            // Level, Race, Guild
            sb.AppendLine($"**Level:** {profile.Level} | **Race:** {profile.Race ?? "Unknown"}");

            if (profile.Guild != null)
            {
                sb.AppendLine($"**Guild:** <{profile.Guild.Name}>");
            }

            // Talents
            if (profile.Talents?.Trees != null && profile.Talents.Trees.Any())
            {
                var talentSpec = profile.Talents.SpecName ?? string.Join("/", profile.Talents.Trees.Select(t => t.Points));
                sb.AppendLine($"**Spec:** {talentSpec}");
            }

            // Item Level
            if (profile.Gear != null)
            {
                var equipped = profile.Gear.ItemLevelEquipped;
                var total = profile.Gear.ItemLevelTotal;

                if (total > equipped && total > 0)
                {
                    sb.AppendLine($"**Item Level:** {equipped} / {total}");
                }
                else if (equipped > 0)
                {
                    sb.AppendLine($"**Item Level:** {equipped}");
                }
            }

            // Raid Progression — show top raids with kills, or all raids if none tracked
            if (profile.RaidProgression != null && profile.RaidProgression.Any())
            {
                sb.AppendLine();
                sb.AppendLine("**Raid Progression**");

                var raidsWithKills = profile.RaidProgression
                    .Where(r => r.Value.TotalBosses > 0 && HasAnyKills(r.Value))
                    .OrderByDescending(r => GetHighestDifficulty(r.Value))
                    .ThenByDescending(r => GetTotalKills(r.Value))
                    .Take(4)
                    .ToList();

                if (raidsWithKills.Any())
                {
                    foreach (var raid in raidsWithKills)
                    {
                        var raidName = FormatRaidName(raid.Key);
                        var progress = FormatClassicProgress(raid.Value);
                        sb.AppendLine($"• {raidName}: {progress}");
                    }
                }
                else
                {
                    // Kills not yet tracked — show available raids with summary or boss count
                    var availableRaids = profile.RaidProgression
                        .Where(r => r.Value.TotalBosses > 0)
                        .Take(4);

                    foreach (var raid in availableRaids)
                    {
                        var raidName = FormatRaidName(raid.Key);
                        var summary = !string.IsNullOrWhiteSpace(raid.Value.Summary)
                            ? raid.Value.Summary
                            : $"0/{raid.Value.TotalBosses}";
                        sb.AppendLine($"• {raidName}: {summary}");
                    }
                    sb.AppendLine("*Kill tracking may be delayed*");
                }
            }

            sb.AppendLine();

            // Quick links
            if (profile.ProfileUrl != null)
            {
                sb.AppendLine($"[Classic Raider.IO]({profile.ProfileUrl})");
            }

            embed.Description = sb.ToString();

            // Thumbnail
            if (profile.ThumbnailUrl != null)
            {
                embed.ThumbnailUrl = profile.ThumbnailUrl.AbsoluteUri;
            }

            // Footer
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{profile.Realm} ({profile.Region?.ToUpper()}) | Classic"
            };

            return embed;
        }

        /// <summary>
        /// Build component buttons for the Classic overview view
        /// </summary>
        public static ComponentBuilder BuildComponents(
            ulong userId,
            ClassicRaiderIOModels.ClassicCharProfile profile)
        {
            var builder = new ComponentBuilder();
            var charParam = $"{profile.Name}~{profile.Realm}~{profile.Region}";

            // Row 0: View buttons
            builder.WithButton(
                label: "Gear",
                customId: $"{ModalConstants.ClassicCharGear}~{userId}~{charParam}",
                style: profile.Gear?.Items != null ? ButtonStyle.Primary : ButtonStyle.Secondary,
                emote: new Emoji("\U0001F6E1\uFE0F"),
                disabled: profile.Gear?.Items == null,
                row: 0);

            builder.WithButton(
                label: "Raids",
                customId: $"{ModalConstants.ClassicCharRaids}~{userId}~{charParam}",
                style: profile.RaidProgression?.Any() == true ? ButtonStyle.Primary : ButtonStyle.Secondary,
                emote: new Emoji("\U0001F3F0"),
                disabled: profile.RaidProgression == null || !profile.RaidProgression.Any(),
                row: 0);

            // Row 1: Action buttons
            builder.WithButton(
                label: "Refresh",
                customId: $"{ModalConstants.ClassicCharRefresh}~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("\U0001F504"),
                row: 1);

            builder.WithButton(
                label: "Share",
                customId: $"{ModalConstants.ClassicCharShare}~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("\U0001F4E2"),
                row: 1);

            return builder;
        }

        /// <summary>
        /// Build components for a detail view (back button + actions)
        /// </summary>
        public static ComponentBuilder BuildDetailViewComponents(
            ulong userId,
            string charParam,
            string currentView)
        {
            var builder = new ComponentBuilder();

            // Row 0: View buttons with current highlighted
            builder.WithButton(
                label: "Overview",
                customId: $"{ModalConstants.ClassicCharOverview}~{userId}~{charParam}",
                style: currentView == "overview" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("\U0001F4CB"),
                row: 0);

            builder.WithButton(
                label: "Gear",
                customId: $"{ModalConstants.ClassicCharGear}~{userId}~{charParam}",
                style: currentView == "gear" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("\U0001F6E1\uFE0F"),
                row: 0);

            builder.WithButton(
                label: "Raids",
                customId: $"{ModalConstants.ClassicCharRaids}~{userId}~{charParam}",
                style: currentView == "raids" ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji("\U0001F3F0"),
                row: 0);

            // Row 1: Action buttons
            builder.WithButton(
                label: "Refresh",
                customId: $"{ModalConstants.ClassicCharRefresh}~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("\U0001F504"),
                row: 1);

            builder.WithButton(
                label: "Share",
                customId: $"{ModalConstants.ClassicCharShare}~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("\U0001F4E2"),
                row: 1);

            return builder;
        }

        #region Helpers

        public static Color GetFactionColor(string faction)
        {
            return faction?.ToLower() switch
            {
                "alliance" => new Color(0, 112, 221),    // Blue
                "horde" => new Color(200, 35, 35),       // Red
                _ => new Color(128, 128, 128)            // Gray
            };
        }

        public static bool HasAnyKills(ClassicRaiderIOModels.ClassicRaidProgressionEntry entry)
        {
            return entry.Normal10BossesKilled > 0 || entry.Normal25BossesKilled > 0 ||
                   entry.Heroic10BossesKilled > 0 || entry.Heroic25BossesKilled > 0;
        }

        public static int GetHighestDifficulty(ClassicRaiderIOModels.ClassicRaidProgressionEntry entry)
        {
            if (entry.Heroic25BossesKilled > 0) return 4;
            if (entry.Heroic10BossesKilled > 0) return 3;
            if (entry.Normal25BossesKilled > 0) return 2;
            if (entry.Normal10BossesKilled > 0) return 1;
            return 0;
        }

        public static long GetTotalKills(ClassicRaiderIOModels.ClassicRaidProgressionEntry entry)
        {
            return entry.Normal10BossesKilled + entry.Normal25BossesKilled +
                   entry.Heroic10BossesKilled + entry.Heroic25BossesKilled;
        }

        public static string FormatRaidName(string slug)
        {
            // Convert "icecrown-citadel" to "Icecrown Citadel"
            return string.Join(" ", slug.Split('-')
                .Select(word => word.Length > 0
                    ? char.ToUpper(word[0]) + word.Substring(1)
                    : ""));
        }

        public static string FormatClassicProgress(ClassicRaiderIOModels.ClassicRaidProgressionEntry entry)
        {
            var parts = new List<string>();

            // Prefer 10/25 man format if available, fall back to simple N/H
            if (entry.Heroic25BossesKilled > 0)
                parts.Add($"{entry.Heroic25BossesKilled}/{entry.TotalBosses} H25");
            if (entry.Heroic10BossesKilled > 0)
                parts.Add($"{entry.Heroic10BossesKilled}/{entry.TotalBosses} H10");
            if (entry.Normal25BossesKilled > 0)
                parts.Add($"{entry.Normal25BossesKilled}/{entry.TotalBosses} N25");
            if (entry.Normal10BossesKilled > 0)
                parts.Add($"{entry.Normal10BossesKilled}/{entry.TotalBosses} N10");

            return parts.Any() ? string.Join(", ", parts) : entry.Summary ?? "No progress";
        }

        #endregion
    }
}
