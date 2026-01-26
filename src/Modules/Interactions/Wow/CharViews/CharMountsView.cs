using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the mount collection summary view for character profiles.
    /// Shows collection progress and top missing expansions without pagination.
    /// </summary>
    public static class CharMountsView
    {
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            MountCollectionResponse mountCollection,
            List<WowMounts> allMounts,
            ArmoryMedia media)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = $"Mount Collection - {charInfo.Name}";

            // Calculate collection stats
            var collectedMountIds = new HashSet<long>(
                mountCollection?.Mounts?.Select(m => m.Mount.Id) ?? Enumerable.Empty<long>());

            var totalMounts = allMounts?.Count ?? 0;
            var collectedCount = allMounts?.Count(m => collectedMountIds.Contains(m.Id)) ?? 0;
            var missingCount = totalMounts - collectedCount;
            var progress = totalMounts > 0 ? (collectedCount * 100.0 / totalMounts) : 0;

            // Color based on progress
            embed.WithColor(progress switch
            {
                >= 90 => new Color(0, 255, 0),      // Green - nearly complete
                >= 70 => new Color(138, 43, 226),   // Purple - great progress
                >= 50 => new Color(255, 165, 0),    // Orange - good progress
                _ => new Color(255, 87, 51)         // Red-orange - still working
            });

            // Collection progress
            sb.AppendLine($"**Collected:** {collectedCount:N0} / {totalMounts:N0} ({progress:F1}%)");

            // Visual progress bar
            var filledBlocks = (int)(progress / 5);
            var progressBar = new string('\u2588', filledBlocks) + new string('\u2591', 20 - filledBlocks);
            sb.AppendLine($"`{progressBar}`");
            sb.AppendLine();

            // Missing mounts by expansion (top 5)
            if (allMounts != null && allMounts.Count > 0)
            {
                var missingByExpansion = allMounts
                    .Where(m => !collectedMountIds.Contains(m.Id) && !string.IsNullOrEmpty(m.Expansion))
                    .GroupBy(m => m.Expansion)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .ToList();

                if (missingByExpansion.Any())
                {
                    sb.AppendLine("**Most Missing by Expansion**");
                    foreach (var group in missingByExpansion)
                    {
                        var expTotal = allMounts.Count(m => m.Expansion == group.Key);
                        var expCollected = expTotal - group.Count();
                        sb.AppendLine($"\u2022 {group.Key}: **{group.Count()}** missing ({expCollected}/{expTotal})");
                    }
                    sb.AppendLine();
                }

                // Missing by source type (top 5)
                var missingBySource = allMounts
                    .Where(m => !collectedMountIds.Contains(m.Id) && !string.IsNullOrEmpty(m.Source))
                    .GroupBy(m => m.Source)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .ToList();

                if (missingBySource.Any())
                {
                    sb.AppendLine("**Missing by Source Type**");
                    foreach (var group in missingBySource)
                    {
                        var sourceName = GetFriendlySourceName(group.Key);
                        var emoji = GetSourceEmoji(group.Key);
                        sb.AppendLine($"{emoji} {sourceName}: **{group.Count()}**");
                    }
                    sb.AppendLine();
                }

                // Obtainable vs removed stats
                var missingObtainable = allMounts.Count(m => !collectedMountIds.Contains(m.Id) && m.IsObtainable);
                var missingRemoved = allMounts.Count(m => !collectedMountIds.Contains(m.Id) && !m.IsObtainable);

                if (missingRemoved > 0)
                {
                    sb.AppendLine($"*{missingObtainable} obtainable, {missingRemoved} unobtainable*");
                }
            }

            embed.Description = sb.ToString();

            // Thumbnail
            var thumbnailUrl = media?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                embed.ThumbnailUrl = thumbnailUrl;
            }

            // Footer with hint
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Use /mounts-needed for detailed filtering"
            };

            return embed;
        }

        private static string GetSourceEmoji(string source) => source?.ToUpper() switch
        {
            "DROP" => "\uD83D\uDC80",           // skull
            "ACHIEVEMENT" => "\uD83C\uDFC6",   // trophy
            "VENDOR" => "\uD83D\uDCB0",        // money bag
            "QUEST" => "\u2757",               // exclamation
            "PROFESSION" => "\uD83D\uDD28",    // hammer
            "WORLD_EVENT" => "\uD83C\uDF83",   // pumpkin
            "PROMOTION" => "\uD83C\uDF81",     // gift
            "TRADING_POST" => "\uD83C\uDFEA",  // convenience store
            "STORE" => "\uD83D\uDED2",         // shopping cart
            "PVP" => "\u2694\uFE0F",            // crossed swords
            "REPUTATION" => "\uD83D\uDCDC",    // scroll
            "CLASS" => "\uD83C\uDFAD",         // masks
            "COVENANT" => "\uD83D\uDD2E",      // crystal ball
            "GARRISON" => "\uD83C\uDFF0",      // castle
            _ => "\uD83D\uDCCD"                // pin
        };

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
    }
}
