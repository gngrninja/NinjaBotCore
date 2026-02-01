using Discord;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the WarcraftLogs view embed for character profiles
    /// </summary>
    public static class CharLogsView
    {
        /// <summary>
        /// Build the logs view embed from WCL ranking data
        /// </summary>
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            List<LogCharRankings> rankings,
            string specName = null,
            string className = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            // Title
            var titleParts = new List<string>();
            if (!string.IsNullOrEmpty(specName)) titleParts.Add(specName);
            if (!string.IsNullOrEmpty(className)) titleParts.Add(className);
            titleParts.Add(charInfo.Name);

            embed.Title = titleParts.Count > 1
                ? $"{string.Join(" ", titleParts.Take(titleParts.Count - 1))} - {titleParts.Last()}"
                : charInfo.Name;

            if (rankings == null || rankings.Count == 0)
            {
                embed.WithColor(new Color(128, 128, 128));
                embed.Description = "No raid logs found for this character.\n\n" +
                    "This could mean:\n" +
                    "- Character has no logged raids this tier\n" +
                    "- Character name/realm might be different on WarcraftLogs\n" +
                    "- Logs are set to private";

                embed.AddField("WarcraftLogs", $"[View Profile]({charInfo.WarcraftLogsUrl})", true);
                return embed;
            }

            // Group rankings by difficulty (prioritize Mythic > Heroic > Normal)
            var mythicRankings = rankings.Where(r => r.difficulty == 5).ToList();
            var heroicRankings = rankings.Where(r => r.difficulty == 4).ToList();
            var normalRankings = rankings.Where(r => r.difficulty == 3).ToList();

            // Determine best average parse percentile for color
            var allPercentiles = rankings.Select(r => r.percentile).ToList();
            var avgPercentile = allPercentiles.Any() ? allPercentiles.Average() : 0;
            embed.WithColor(CharViewHelpers.GetParseColor(avgPercentile));

            // Show Mythic parses if available
            if (mythicRankings.Any())
            {
                sb.AppendLine("**__Mythic Parses__**");
                AppendRankings(sb, mythicRankings);
                sb.AppendLine();
            }

            // Show Heroic parses
            if (heroicRankings.Any())
            {
                sb.AppendLine("**__Heroic Parses__**");
                AppendRankings(sb, heroicRankings);
                sb.AppendLine();
            }

            // Show Normal parses only if no higher difficulty
            if (normalRankings.Any() && !mythicRankings.Any() && !heroicRankings.Any())
            {
                sb.AppendLine("**__Normal Parses__**");
                AppendRankings(sb, normalRankings);
                sb.AppendLine();
            }

            // Summary statistics
            var bestParse = allPercentiles.Any() ? allPercentiles.Max() : 0;
            var medianParse = allPercentiles.Any() ? allPercentiles.OrderBy(p => p).ElementAt(allPercentiles.Count / 2) : 0;
            var uniqueEncounters = rankings.Select(r => r.encounterId).Distinct().Count();

            sb.AppendLine("**__Summary__**");
            sb.AppendLine($"Best Parse: {CharViewHelpers.GetParseEmoji(bestParse)} **{bestParse:F0}%**");
            sb.AppendLine($"Median Parse: {CharViewHelpers.GetParseEmoji(medianParse)} **{medianParse:F0}%**");
            sb.AppendLine($"Bosses: **{uniqueEncounters}** | Kills: **{rankings.Count}**");

            embed.Description = sb.ToString();

            // Links
            embed.AddField("WarcraftLogs", $"[View Full Profile]({charInfo.WarcraftLogsUrl})", true);

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Data from WarcraftLogs"
            };

            return embed;
        }

        /// <summary>
        /// Build a compact logs summary for the overview view
        /// </summary>
        public static string BuildCompactSummary(List<LogCharRankings> rankings)
        {
            if (rankings == null || rankings.Count == 0)
                return "No logs";

            // Get best mythic or heroic parses
            var mythicRankings = rankings.Where(r => r.difficulty == 5).ToList();
            var heroicRankings = rankings.Where(r => r.difficulty == 4).ToList();

            var relevantRankings = mythicRankings.Any() ? mythicRankings : heroicRankings;
            if (!relevantRankings.Any())
                relevantRankings = rankings;

            var percentiles = relevantRankings.Select(r => r.percentile).ToList();
            var best = percentiles.Max();
            var avg = percentiles.Average();
            var difficulty = mythicRankings.Any() ? "M" : (heroicRankings.Any() ? "H" : "N");

            return $"{CharViewHelpers.GetParseEmoji(avg)} {avg:F0}% avg ({difficulty})";
        }

        private static void AppendRankings(StringBuilder sb, List<LogCharRankings> rankings)
        {
            // Sort by encounter, taking best parse per encounter
            var sortedRankings = rankings
                .GroupBy(r => r.encounterId)
                .Select(g => g.OrderByDescending(r => r.percentile).First())
                .OrderByDescending(r => r.percentile) // Show best parses first
                .ToList();

            foreach (var ranking in sortedRankings.Take(8)) // Limit to 8 bosses
            {
                var pct = ranking.percentile;
                var emoji = CharViewHelpers.GetParseEmoji(pct);
                var encounterName = CharViewHelpers.Truncate(ranking.encounterName, 18);

                // Format DPS/HPS (total is damage/healing done, duration is in ms)
                var dpsHps = ranking.duration > 0
                    ? (ranking.total / (ranking.duration / 1000.0))
                    : 0;
                var dpsFormatted = FormatNumber(dpsHps);

                // Build the line with more info
                sb.AppendLine($"{emoji} **{pct:F0}%** {encounterName}");
                sb.AppendLine($"   `{dpsFormatted}` | ilvl {ranking.itemLevel} | [{ranking.specName}]({ranking.reportURL})");
            }

            if (sortedRankings.Count > 8)
            {
                sb.AppendLine($"*...and {sortedRankings.Count - 8} more encounters*");
            }
        }

        private static string FormatNumber(double value)
        {
            if (value >= 1_000_000)
                return $"{value / 1_000_000:F2}M";
            if (value >= 1_000)
                return $"{value / 1_000:F1}K";
            return $"{value:F0}";
        }

        /// <summary>
        /// Build a select menu for choosing encounters
        /// </summary>
        public static SelectMenuBuilder BuildEncounterSelectMenu(
            ulong userId,
            CharacterInfo charInfo,
            List<LogCharRankings> rankings)
        {
            if (rankings == null || rankings.Count == 0)
                return null;

            // Get unique encounters, sorted by best parse
            var encounters = rankings
                .GroupBy(r => r.encounterId)
                .Select(g => new
                {
                    Id = g.Key,
                    Name = g.First().encounterName,
                    BestParse = g.Max(r => r.percentile),
                    Difficulty = g.Max(r => r.difficulty),
                    KillCount = g.Count()
                })
                .OrderByDescending(e => e.BestParse)
                .Take(25) // Discord limit
                .ToList();

            if (!encounters.Any())
                return null;

            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";
            var menu = new SelectMenuBuilder()
                .WithCustomId($"char_logs_encounter~{userId}~{charParam}")
                .WithPlaceholder("Select encounter for details...")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var enc in encounters)
            {
                var emoji = CharViewHelpers.GetParseEmoji(enc.BestParse);
                var diffLabel = enc.Difficulty == 5 ? "M" : (enc.Difficulty == 4 ? "H" : "N");
                menu.AddOption(
                    label: CharViewHelpers.Truncate(enc.Name, 25),
                    value: enc.Id.ToString(),
                    description: $"{emoji} {enc.BestParse:F0}% best | {enc.KillCount} kill(s) ({diffLabel})",
                    emote: new Emoji(emoji));
            }

            return menu;
        }

        /// <summary>
        /// Build detailed view for a specific encounter
        /// </summary>
        public static EmbedBuilder BuildEncounterDetail(
            CharacterInfo charInfo,
            List<LogCharRankings> allRankings,
            int encounterId)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            var encounterRankings = allRankings
                .Where(r => r.encounterId == encounterId)
                .OrderByDescending(r => r.percentile)
                .ToList();

            if (!encounterRankings.Any())
            {
                embed.WithColor(new Color(128, 128, 128));
                embed.Description = "No data found for this encounter.";
                return embed;
            }

            var firstRanking = encounterRankings.First();
            embed.Title = $"{firstRanking.encounterName} - {charInfo.Name}";

            var bestPct = encounterRankings.Max(r => r.percentile);
            embed.WithColor(CharViewHelpers.GetParseColor(bestPct));

            // Show all parses for this encounter
            foreach (var ranking in encounterRankings.Take(10))
            {
                var pct = ranking.percentile;
                var emoji = CharViewHelpers.GetParseEmoji(pct);
                var diffName = ranking.difficultyName;
                var dpsHps = ranking.duration > 0
                    ? (ranking.total / (ranking.duration / 1000.0))
                    : 0;

                // Format duration
                var durationSec = ranking.duration / 1000;
                var durationStr = $"{durationSec / 60}:{durationSec % 60:D2}";

                sb.AppendLine($"{emoji} **{pct:F0}%** ({diffName})");
                sb.AppendLine($"   `{FormatNumber(dpsHps)}` | ilvl {ranking.itemLevel} | {durationStr}");
                sb.AppendLine($"   Rank {ranking.rank:N0} / {ranking.outOf:N0} | [{ranking.specName}]({ranking.reportURL}#fight={ranking.fightID})");
                sb.AppendLine();
            }

            if (encounterRankings.Count > 10)
            {
                sb.AppendLine($"*...and {encounterRankings.Count - 10} more kills*");
            }

            embed.Description = sb.ToString();

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Use dropdown for other encounters"
            };

            return embed;
        }

        // ===== V2 API Methods =====

        /// <summary>
        /// Build the logs view embed from WCL V2 zone rankings data
        /// </summary>
        /// <param name="charInfo">Character info</param>
        /// <param name="rankings">Zone rankings data</param>
        /// <param name="encounterRankings">Optional per-encounter rankings for fight links and rank display</param>
        /// <param name="difficulty">Difficulty filter</param>
        /// <param name="zoneId">Zone ID</param>
        /// <param name="specName">Spec name for title</param>
        /// <param name="className">Class name for title</param>
        public static EmbedBuilder BuildV2(
            CharacterInfo charInfo,
            WclV2ZoneRankingsData rankings,
            Dictionary<int, WclV2EncounterRankingsData> encounterRankings = null,
            int? difficulty = null,
            int? zoneId = null,
            string specName = null,
            string className = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            // Title
            var titleParts = new List<string>();
            if (!string.IsNullOrEmpty(specName)) titleParts.Add(specName);
            if (!string.IsNullOrEmpty(className)) titleParts.Add(className);
            titleParts.Add(charInfo.Name);

            embed.Title = titleParts.Count > 1
                ? $"{string.Join(" ", titleParts.Take(titleParts.Count - 1))} - {titleParts.Last()}"
                : charInfo.Name;

            // Build zone-filtered WCL URL
            var zoneUrl = BuildWclZoneUrl(charInfo, zoneId, difficulty);

            if (rankings == null)
            {
                embed.WithColor(new Color(255, 165, 0)); // Orange for config issue
                embed.Description = "**WarcraftLogs data unavailable**\n\n" +
                    "The current raid tier may not be configured yet.\n" +
                    "A bot owner can run `/refresh-raid-tier` to detect it.\n\n" +
                    "You can still view the character's full profile on WarcraftLogs directly.";

                embed.AddField("WarcraftLogs", $"[View Profile]({charInfo.WarcraftLogsUrl})", true);
                return embed;
            }

            if (rankings.Rankings == null || rankings.Rankings.Count == 0)
            {
                embed.WithColor(new Color(128, 128, 128));
                embed.Description = "No raid logs found for this character.\n\n" +
                    "This could mean:\n" +
                    "- Character has no logged raids this tier\n" +
                    "- Character name/realm might be different on WarcraftLogs\n" +
                    "- Logs are set to private";

                embed.AddField("WarcraftLogs", $"[View Profile]({charInfo.WarcraftLogsUrl})", true);
                return embed;
            }

            // Sort by best percentile descending
            var sortedRankings = rankings.Rankings
                .Where(r => r.RankPercent.HasValue)
                .OrderByDescending(r => r.RankPercent ?? 0)
                .ToList();

            // Difficulty label
            var diffLabel = difficulty switch
            {
                5 => "Mythic",
                4 => "Heroic",
                3 => "Normal",
                _ => "All Difficulties"
            };

            // Handle case where rankings exist but none have parses for selected difficulty
            if (sortedRankings.Count == 0)
            {
                embed.WithColor(new Color(128, 128, 128));
                sb.AppendLine($"**__{diffLabel} Parses__**");
                sb.AppendLine();
                sb.AppendLine($"No {diffLabel.ToLower()} parses found for this character.");
                sb.AppendLine();
                sb.AppendLine("Try selecting a different difficulty or check WarcraftLogs directly.");

                embed.Description = sb.ToString();
                embed.AddField("WarcraftLogs", $"[View {diffLabel} Logs]({zoneUrl})", true);
                embed.Footer = new EmbedFooterBuilder
                {
                    Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Data from WarcraftLogs"
                };
                return embed;
            }

            // Determine color from best performance average
            var avgPerformance = rankings.BestPerformanceAverage ?? 0;
            embed.WithColor(CharViewHelpers.GetParseColor(avgPerformance));

            sb.AppendLine($"**__{diffLabel} Parses__**");
            sb.AppendLine();

            foreach (var ranking in sortedRankings.Take(8))
            {
                var pct = ranking.RankPercent ?? 0;
                var emoji = CharViewHelpers.GetParseEmoji(pct);
                var encounterName = CharViewHelpers.Truncate(ranking.Encounter?.Name ?? "Unknown", 18);
                var spec = ranking.BestSpec ?? ranking.Spec ?? "";
                var dpsFormatted = ranking.BestAmount.HasValue ? FormatNumber(ranking.BestAmount.Value) : "N/A";

                // Try to get best parse link and rank from encounter rankings
                var encId = ranking.Encounter?.Id ?? 0;
                string fightUrl = null;
                string rankDisplay = "";

                if (encounterRankings != null && encId > 0 && encounterRankings.TryGetValue(encId, out var encData))
                {
                    var bestParse = encData.Ranks?.FirstOrDefault();
                    if (bestParse != null)
                    {
                        // Build fight link
                        if (bestParse.Report != null)
                        {
                            fightUrl = $"https://www.warcraftlogs.com/reports/{bestParse.Report.Code}#fight={bestParse.Report.FightID}";
                        }

                        // Build rank display
                        if (bestParse.EstimatedRank.HasValue && bestParse.RankTotalParses.HasValue)
                        {
                            rankDisplay = $" #{bestParse.EstimatedRank.Value:N0}/{bestParse.RankTotalParses.Value:N0}";
                        }
                    }
                }

                // Use fight URL if available, otherwise fall back to encounter page URL
                var linkUrl = fightUrl ?? BuildWclEncounterUrl(charInfo, zoneId, encId, difficulty);

                sb.AppendLine($"{emoji} **{pct:F0}%**{rankDisplay} [{encounterName}]({linkUrl})");
                sb.AppendLine($"   `{dpsFormatted}` | {ranking.TotalKills} kills | {spec}");
            }

            if (sortedRankings.Count > 8)
            {
                sb.AppendLine($"*...and {sortedRankings.Count - 8} more encounters*");
            }

            sb.AppendLine();

            // Summary statistics
            sb.AppendLine("**__Summary__**");
            sb.AppendLine($"Best Avg: {CharViewHelpers.GetParseEmoji(avgPerformance)} **{avgPerformance:F1}%**");
            if (rankings.MedianPerformanceAverage.HasValue)
            {
                var medianAvg = rankings.MedianPerformanceAverage.Value;
                sb.AppendLine($"Median Avg: {CharViewHelpers.GetParseEmoji(medianAvg)} **{medianAvg:F1}%**");
            }
            sb.AppendLine($"Bosses: **{sortedRankings.Count}**");

            // All-Stars info if available
            if (rankings.AllStars != null && rankings.AllStars.Any())
            {
                var bestAllStar = rankings.AllStars.OrderByDescending(a => a.Points).First();
                sb.AppendLine($"All-Stars: **{bestAllStar.Points:F0}/{bestAllStar.PossiblePoints:F0}** ({bestAllStar.Spec})");
            }

            embed.Description = sb.ToString();

            // Links - include zone-specific link
            embed.AddField("WarcraftLogs", $"[View All Parses]({zoneUrl})", true);

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Data from WarcraftLogs"
            };

            return embed;
        }

        /// <summary>
        /// Build WarcraftLogs URL for character zone rankings
        /// </summary>
        private static string BuildWclZoneUrl(CharacterInfo charInfo, int? zoneId, int? difficulty)
        {
            // Base URL: https://www.warcraftlogs.com/character/{region}/{realm}/{name}
            var realmSlug = charInfo.RealmSlug ?? charInfo.Realm.ToLower().Replace(" ", "-").Replace("'", "");
            var baseUrl = $"https://www.warcraftlogs.com/character/{charInfo.Region.ToLower()}/{realmSlug}/{charInfo.Name.ToLower()}";

            // Add zone and difficulty filters
            var fragments = new List<string>();
            if (zoneId.HasValue)
                fragments.Add($"zone={zoneId.Value}");
            if (difficulty.HasValue && difficulty.Value > 0)
                fragments.Add($"difficulty={difficulty.Value}");

            return fragments.Count > 0 ? $"{baseUrl}#{string.Join("&", fragments)}" : baseUrl;
        }

        /// <summary>
        /// Build WarcraftLogs URL for specific encounter rankings
        /// </summary>
        private static string BuildWclEncounterUrl(CharacterInfo charInfo, int? zoneId, int? encounterId, int? difficulty)
        {
            // Base URL with zone filter
            var url = BuildWclZoneUrl(charInfo, zoneId, difficulty);

            // Add encounter filter (boss=encounterId)
            if (encounterId.HasValue)
            {
                url += url.Contains("#") ? $"&boss={encounterId.Value}" : $"#boss={encounterId.Value}";
            }

            return url;
        }

        /// <summary>
        /// Build difficulty select menu for filtering logs
        /// </summary>
        public static SelectMenuBuilder BuildDifficultySelectMenu(
            ulong userId,
            CharacterInfo charInfo,
            int zoneId,
            int currentDifficulty = 0)
        {
            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";
            var menu = new SelectMenuBuilder()
                .WithCustomId($"char_logs_difficulty~{userId}~{charParam}~{zoneId}")
                .WithPlaceholder("Filter by difficulty...")
                .WithMinValues(1)
                .WithMaxValues(1);

            menu.AddOption(
                label: "All Difficulties",
                value: "0",
                description: "Show best parses across all difficulties",
                isDefault: currentDifficulty == 0);

            menu.AddOption(
                label: "Mythic",
                value: "5",
                description: "Show Mythic parses only",
                emote: new Emoji("\U0001F525"), // fire
                isDefault: currentDifficulty == 5);

            menu.AddOption(
                label: "Heroic",
                value: "4",
                description: "Show Heroic parses only",
                emote: new Emoji("\u2694\uFE0F"), // crossed swords
                isDefault: currentDifficulty == 4);

            menu.AddOption(
                label: "Normal",
                value: "3",
                description: "Show Normal parses only",
                emote: new Emoji("\U0001F6E1\uFE0F"), // shield
                isDefault: currentDifficulty == 3);

            return menu;
        }

        /// <summary>
        /// Build encounter select menu for V2 data
        /// </summary>
        public static SelectMenuBuilder BuildEncounterSelectMenuV2(
            ulong userId,
            CharacterInfo charInfo,
            WclV2ZoneRankingsData rankings,
            int zoneId,
            int currentDifficulty = 0)
        {
            if (rankings?.Rankings == null || rankings.Rankings.Count == 0)
                return null;

            // Keep original API order (raid progression order) - don't sort by percentile
            var orderedRankings = rankings.Rankings
                .Where(r => r.Encounter != null && r.RankPercent.HasValue)
                .Take(25)
                .ToList();

            // Return null if no valid rankings after filtering (avoids Discord 0-option menu error)
            if (orderedRankings.Count == 0)
                return null;

            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";
            var menu = new SelectMenuBuilder()
                .WithCustomId($"char_logs_encounter_v2~{userId}~{charParam}~{zoneId}~{currentDifficulty}")
                .WithPlaceholder("Select encounter for details...")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var ranking in orderedRankings)
            {
                var pct = ranking.RankPercent ?? 0;
                var emoji = CharViewHelpers.GetParseEmoji(pct);
                menu.AddOption(
                    label: CharViewHelpers.Truncate(ranking.Encounter.Name, 25),
                    value: ranking.Encounter.Id.ToString(),
                    description: $"{emoji} {pct:F0}% best | {ranking.TotalKills} kill(s)",
                    emote: new Emoji(emoji));
            }

            return menu;
        }

        /// <summary>
        /// Build detailed view for a specific encounter from V2 data
        /// </summary>
        public static EmbedBuilder BuildEncounterDetailV2(
            CharacterInfo charInfo,
            WclV2ZoneRankingsData rankings,
            int encounterId,
            WclV2EncounterRankingsData encounterRankings = null,
            int? zoneId = null,
            int? difficulty = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            var bossRanking = rankings?.Rankings?.FirstOrDefault(r => r.Encounter?.Id == encounterId);

            if (bossRanking == null)
            {
                embed.WithColor(new Color(128, 128, 128));
                embed.Description = "No data found for this encounter.";
                return embed;
            }

            embed.Title = $"{bossRanking.Encounter.Name} - {charInfo.Name}";

            var pct = bossRanking.RankPercent ?? 0;
            embed.WithColor(CharViewHelpers.GetParseColor(pct));

            // Summary stats
            sb.AppendLine($"{CharViewHelpers.GetParseEmoji(pct)} **Best: {pct:F0}%** | Median: **{bossRanking.MedianPercent:F0}%** | Kills: **{bossRanking.TotalKills}**");
            sb.AppendLine();

            // Display individual parses if available
            if (encounterRankings?.Ranks != null && encounterRankings.Ranks.Any())
            {
                sb.AppendLine("**__Recent Parses__**");

                foreach (var parse in encounterRankings.Ranks.Take(8))
                {
                    var parsePct = parse.RankPercent ?? 0;
                    var emoji = CharViewHelpers.GetParseEmoji(parsePct);
                    var dpsFormatted = FormatNumber(parse.DpsHps);
                    var spec = parse.Spec ?? "Unknown";

                    // Build rank display (e.g., "#11/7,279")
                    var rankDisplay = "";
                    if (parse.EstimatedRank.HasValue && parse.RankTotalParses.HasValue)
                    {
                        rankDisplay = $" #{parse.EstimatedRank.Value:N0}/{parse.RankTotalParses.Value:N0}";
                    }

                    // Build line with fight link
                    if (!string.IsNullOrEmpty(parse.FightUrl))
                    {
                        sb.AppendLine($"{emoji} **{parsePct:F0}%**{rankDisplay} | `{dpsFormatted}` | {parse.DurationFormatted} | [{spec}]({parse.FightUrl})");
                    }
                    else
                    {
                        sb.AppendLine($"{emoji} **{parsePct:F0}%**{rankDisplay} | `{dpsFormatted}` | {parse.DurationFormatted} | {spec}");
                    }
                }

                if (encounterRankings.Ranks.Count > 8)
                {
                    sb.AppendLine($"*...and {encounterRankings.Ranks.Count - 8} more kills*");
                }
            }
            else
            {
                // Fallback to summary stats only
                if (bossRanking.BestAmount.HasValue)
                {
                    sb.AppendLine($"Best DPS/HPS: **{FormatNumber(bossRanking.BestAmount.Value)}**");
                }

                if (bossRanking.FastestKill.HasValue)
                {
                    var fastestSec = bossRanking.FastestKill.Value / 1000;
                    sb.AppendLine($"Fastest Kill: **{fastestSec / 60}:{fastestSec % 60:D2}**");
                }

                sb.AppendLine($"Spec: **{bossRanking.BestSpec ?? bossRanking.Spec ?? "Unknown"}**");
            }

            // All-star points for this boss
            if (bossRanking.AllStars != null)
            {
                sb.AppendLine();
                sb.AppendLine($"All-Stars: **{bossRanking.AllStars.Points:F0}/{bossRanking.AllStars.PossiblePoints:F0}** pts | Rank: **{bossRanking.AllStars.RankPercent:F0}%**");
            }

            // Add link to view all parses for this boss on WCL
            var encounterUrl = BuildWclEncounterUrl(charInfo, zoneId, encounterId, difficulty);
            sb.AppendLine();
            sb.AppendLine($"[View all parses on WarcraftLogs]({encounterUrl})");

            embed.Description = sb.ToString();

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Use dropdown for other encounters"
            };

            return embed;
        }
    }
}
