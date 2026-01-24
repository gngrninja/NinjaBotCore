using Discord;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    public static class CharPvPView
    {
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            ArmoryPvPSummary pvpSummary,
            List<ArmoryPvPBracket> bracketDetails,
            ArmorySummary summary,
            ArmoryMedia media)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            // Title
            var specLabel = summary?.ActiveSpec?.Name ?? "";
            var className = summary?.CharacterClass?.Name ?? "Unknown Class";
            embed.Title = !string.IsNullOrEmpty(specLabel)
                ? $"{specLabel} {className} - {charInfo.Name}"
                : $"{className} - {charInfo.Name}";

            embed.WithColor(new Color(255, 50, 50)); // Red for PvP

            // Honor stats
            sb.AppendLine($"**Honor Level:** {pvpSummary.HonorLevel}");
            sb.AppendLine($"**Honorable Kills:** {pvpSummary.HonorableKills:N0}");
            sb.AppendLine();

            // Rated brackets
            sb.AppendLine("**Rated PvP**");

            var twos = bracketDetails.FirstOrDefault(b => b.Bracket?.Type == "ARENA_2v2");
            var threes = bracketDetails.FirstOrDefault(b => b.Bracket?.Type == "ARENA_3v3");
            var rbg = bracketDetails.FirstOrDefault(b => b.Bracket?.Type == "BATTLEGROUNDS" || b.Bracket?.Type == "RBG");
            var shuffle = bracketDetails.FirstOrDefault(b => b.Bracket?.Type?.Contains("SHUFFLE") == true);

            if (twos != null)
            {
                var rankTitle = GetRankTitle(twos.Rating);
                var stats = twos.SeasonMatchStatistics;
                var record = stats != null ? $"{stats.Won}W / {stats.Lost}L" : "-";
                sb.AppendLine($"⚔️ **2v2 Arena:** {twos.Rating} {rankTitle}");
                sb.AppendLine($"   Season: {record}");
            }
            else
            {
                sb.AppendLine($"⚔️ **2v2 Arena:** No data");
            }

            if (threes != null)
            {
                var rankTitle = GetRankTitle(threes.Rating);
                var stats = threes.SeasonMatchStatistics;
                var record = stats != null ? $"{stats.Won}W / {stats.Lost}L" : "-";
                sb.AppendLine($"⚔️ **3v3 Arena:** {threes.Rating} {rankTitle}");
                sb.AppendLine($"   Season: {record}");
            }
            else
            {
                sb.AppendLine($"⚔️ **3v3 Arena:** No data");
            }

            if (shuffle != null)
            {
                var rankTitle = GetRankTitle(shuffle.Rating);
                var stats = shuffle.SeasonMatchStatistics;
                var record = stats != null ? $"{stats.Won}W / {stats.Lost}L" : "-";
                sb.AppendLine($"🔀 **Solo Shuffle:** {shuffle.Rating} {rankTitle}");
                sb.AppendLine($"   Season: {record}");
            }

            if (rbg != null)
            {
                var rankTitle = GetRankTitle(rbg.Rating);
                var stats = rbg.SeasonMatchStatistics;
                var record = stats != null ? $"{stats.Won}W / {stats.Lost}L" : "-";
                sb.AppendLine($"🏰 **Rated BG:** {rbg.Rating} {rankTitle}");
                sb.AppendLine($"   Season: {record}");
            }
            else
            {
                sb.AppendLine($"🏰 **Rated BG:** No data");
            }

            // Battleground stats
            if (pvpSummary.PvPMapStatistics != null && pvpSummary.PvPMapStatistics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Battleground Stats**");

                var totalWins = pvpSummary.PvPMapStatistics.Sum(m => m.MatchStatistics?.Won ?? 0);
                var totalLosses = pvpSummary.PvPMapStatistics.Sum(m => m.MatchStatistics?.Lost ?? 0);
                var totalPlayed = totalWins + totalLosses;
                var winRate = totalPlayed > 0 ? (totalWins * 100.0 / totalPlayed) : 0;

                sb.AppendLine($"Total: {totalWins}W / {totalLosses}L ({winRate:F1}% win rate)");

                // Top 3 battlegrounds
                var topBGs = pvpSummary.PvPMapStatistics
                    .Where(m => m.MatchStatistics != null)
                    .OrderByDescending(m => m.MatchStatistics.Played)
                    .Take(3)
                    .ToList();

                foreach (var bg in topBGs)
                {
                    var name = bg.WorldMap?.Name ?? "Unknown";
                    if (name.Length > 20) name = name.Substring(0, 17) + "...";
                    var bgWinRate = bg.MatchStatistics.Played > 0
                        ? (bg.MatchStatistics.Won * 100.0 / bg.MatchStatistics.Played)
                        : 0;
                    sb.AppendLine($"• {name}: {bg.MatchStatistics.Won}W/{bg.MatchStatistics.Lost}L ({bgWinRate:F0}%)");
                }
            }

            embed.Description = sb.ToString();

            // Links
            embed.AddField("Armory", $"[View]({charInfo.ArmoryUrl})", true);
            embed.AddField("Check-PvP", $"[View](https://check-pvp.fr/us/{charInfo.Realm}/{charInfo.Name})", true);

            // Image
            embed.ThumbnailUrl = media?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Data from Blizzard API"
            };

            return embed;
        }

        private static string GetRankTitle(int rating)
        {
            if (rating >= 2400) return "⭐ Gladiator";
            if (rating >= 2100) return "🏆 Duelist";
            if (rating >= 1800) return "🥇 Rival";
            if (rating >= 1600) return "🥈 Challenger";
            if (rating >= 1400) return "🥉 Combatant";
            if (rating > 0) return "";
            return "";
        }
    }
}
