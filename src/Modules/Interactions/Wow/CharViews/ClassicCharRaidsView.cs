using Discord;
using NinjaBotCore.Models.Wow;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the Raids view embed for Classic WoW character profiles from Classic Raider.IO data
    /// </summary>
    public static class ClassicCharRaidsView
    {
        public static EmbedBuilder Build(ClassicRaiderIOModels.ClassicCharProfile profile)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = $"Raid Progression - {profile.Name}";
            embed.WithColor(ClassicCharOverviewView.GetFactionColor(profile.Faction));

            if (profile.RaidProgression == null || !profile.RaidProgression.Any())
            {
                sb.AppendLine("*No raid progression data available*");
                embed.Description = sb.ToString();
                return embed;
            }

            // Sort raids: highest difficulty first, then most kills
            var sortedRaids = profile.RaidProgression
                .Where(r => r.Value.TotalBosses > 0)
                .OrderByDescending(r => ClassicCharOverviewView.GetHighestDifficulty(r.Value))
                .ThenByDescending(r => ClassicCharOverviewView.GetTotalKills(r.Value))
                .ToList();

            foreach (var raid in sortedRaids)
            {
                // Guard against exceeding Discord's 4096-char embed description limit
                // Each raid section can add ~200 chars, so break early enough
                if (sb.Length > 3600)
                {
                    sb.AppendLine("*...and more*");
                    break;
                }

                var raidName = ClassicCharOverviewView.FormatRaidName(raid.Key);
                var entry = raid.Value;
                var hasKills = ClassicCharOverviewView.HasAnyKills(entry);

                sb.AppendLine($"**{raidName}** ({entry.TotalBosses} bosses)");

                if (hasKills)
                {
                    if (entry.Heroic25BossesKilled > 0)
                    {
                        var bar = CharViewHelpers.GetProgressBar(entry.Heroic25BossesKilled, entry.TotalBosses);
                        sb.AppendLine($"  H25: {bar} {entry.Heroic25BossesKilled}/{entry.TotalBosses}");
                    }
                    if (entry.Heroic10BossesKilled > 0)
                    {
                        var bar = CharViewHelpers.GetProgressBar(entry.Heroic10BossesKilled, entry.TotalBosses);
                        sb.AppendLine($"  H10: {bar} {entry.Heroic10BossesKilled}/{entry.TotalBosses}");
                    }
                    if (entry.Normal25BossesKilled > 0)
                    {
                        var bar = CharViewHelpers.GetProgressBar(entry.Normal25BossesKilled, entry.TotalBosses);
                        sb.AppendLine($"  N25: {bar} {entry.Normal25BossesKilled}/{entry.TotalBosses}");
                    }
                    if (entry.Normal10BossesKilled > 0)
                    {
                        var bar = CharViewHelpers.GetProgressBar(entry.Normal10BossesKilled, entry.TotalBosses);
                        sb.AppendLine($"  N10: {bar} {entry.Normal10BossesKilled}/{entry.TotalBosses}");
                    }
                }
                else
                {
                    // No kills tracked yet — show summary or 0 progress
                    var summary = !string.IsNullOrWhiteSpace(entry.Summary)
                        ? entry.Summary
                        : $"0/{entry.TotalBosses}";
                    sb.AppendLine($"  {summary}");
                }

                sb.AppendLine();
            }

            // Note if no raids have any tracked kills
            if (!sortedRaids.Any(r => ClassicCharOverviewView.HasAnyKills(r.Value)))
            {
                sb.AppendLine("*Kill tracking may be delayed*");
            }

            // Trim trailing newlines
            embed.Description = sb.ToString().TrimEnd();

            // Thumbnail
            if (profile.ThumbnailUrl != null)
            {
                embed.ThumbnailUrl = profile.ThumbnailUrl.AbsoluteUri;
            }

            // Footer
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{profile.Realm} ({profile.Region?.ToUpper()}) | Classic | Powered by Raider.IO"
            };

            return embed;
        }
    }
}
