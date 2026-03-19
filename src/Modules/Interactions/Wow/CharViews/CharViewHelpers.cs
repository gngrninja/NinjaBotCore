using Discord;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Shared helper methods for character view embeds
    /// </summary>
    public static class CharViewHelpers
    {
        /// <summary>
        /// Get color based on M+ score tier
        /// </summary>
        public static Color GetMythicPlusScoreColor(double score)
        {
            return score switch
            {
                >= 3000 => new Color(255, 128, 0),   // Orange for 3000+
                >= 2500 => new Color(163, 53, 238),  // Purple for 2500+
                >= 2000 => new Color(0, 112, 221),   // Blue for 2000+
                >= 1500 => new Color(0, 200, 150),   // Teal for 1500+
                _ => new Color(128, 128, 128)        // Gray default
            };
        }

        /// <summary>
        /// Get color based on WCL parse percentile
        /// </summary>
        public static Color GetParseColor(double percentile)
        {
            return percentile switch
            {
                >= 99 => new Color(229, 204, 128),   // Gold/Pink for 99+
                >= 95 => new Color(255, 128, 0),    // Orange for 95+
                >= 75 => new Color(163, 53, 238),   // Purple for 75+
                >= 50 => new Color(0, 112, 221),    // Blue for 50+
                >= 25 => new Color(30, 255, 0),     // Green for 25+
                _ => new Color(128, 128, 128)       // Gray for <25
            };
        }

        /// <summary>
        /// Get emoji indicator for parse percentile
        /// </summary>
        public static string GetParseEmoji(double percentile)
        {
            return percentile switch
            {
                >= 99 => "🟡",  // Gold
                >= 95 => "🟠",  // Orange
                >= 75 => "🟣",  // Purple
                >= 50 => "🔵",  // Blue
                >= 25 => "🟢",  // Green
                _ => "⚪"       // Gray
            };
        }

        /// <summary>
        /// Get timed indicator for M+ keystones
        /// </summary>
        public static string GetTimedIndicator(long numKeystoneUpgrades)
        {
            return numKeystoneUpgrades switch
            {
                >= 3 => "⬆⬆⬆",  // +3 (big time)
                2 => "⬆⬆",      // +2
                1 => "⬆",       // +1 (just timed)
                _ => ""          // depleted
            };
        }

        /// <summary>
        /// Get progress bar visualization
        /// </summary>
        public static string GetProgressBar(long current, long total, int length = 10)
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
        /// Get quality emoji for item quality
        /// </summary>
        public static string GetQualityEmoji(string qualityName)
        {
            return qualityName?.ToLower() switch
            {
                "legendary" => "🟠",
                "artifact" => "🟠",
                "epic" => "🟣",
                "rare" => "🔵",
                "uncommon" => "🟢",
                "common" => "⚪",
                _ => "⚪"
            };
        }

        /// <summary>
        /// Format a number with emoji representation
        /// </summary>
        public static string GetNumberEmoji(int number)
        {
            return number switch
            {
                0 => "0️⃣",
                1 => "1️⃣",
                2 => "2️⃣",
                3 => "3️⃣",
                4 => "4️⃣",
                5 => "5️⃣",
                6 => "6️⃣",
                7 => "7️⃣",
                8 => "8️⃣",
                9 => "9️⃣",
                10 => "🔟",
                _ => number.ToString()
            };
        }

        private static readonly Dictionary<string, string> TierSlugNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["tier-mn-1"] = "Midnight Season 1",
        };

        /// <summary>
        /// Convert a raid slug to display name.
        /// Checks known tier slugs first, then falls back to title-casing the slug.
        /// Example: "tier-mn-1" -> "Midnight Season 1", "manaforge-omega" -> "Manaforge Omega"
        /// </summary>
        public static string FormatRaidName(string slug)
        {
            if (string.IsNullOrEmpty(slug))
                return "Unknown Raid";

            if (TierSlugNames.TryGetValue(slug, out var friendlyName))
                return friendlyName;

            return string.Join(" ", slug.Split('-')
                .Select(word => word.Length > 0
                    ? char.ToUpper(word[0]) + word.Substring(1)
                    : ""));
        }

        /// <summary>
        /// Get the current (latest) raid from a Raider.IO raid dictionary.
        /// Raider.IO returns raids chronologically, so the last entry is the current tier.
        /// </summary>
        public static KeyValuePair<string, T>? GetCurrentRaid<T>(Dictionary<string, T> raidDict) where T : class
        {
            if (raidDict == null || raidDict.Count == 0)
                return null;
            return raidDict.Last();
        }

        /// <summary>
        /// Truncate text to max length with ellipsis
        /// </summary>
        public static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Normalize realm name for comparison (removes spaces, dashes, apostrophes)
        /// Example: "Sisters of Elune" → "sistersofelune"
        /// </summary>
        public static string NormalizeRealmForComparison(string realm)
        {
            return realm?.Replace(" ", "").Replace("-", "").Replace("'", "").ToLower() ?? "";
        }

        /// <summary>
        /// Convert realm name to slug format for API calls
        /// Example: "Sisters of Elune" → "sisters-of-elune"
        /// </summary>
        public static string ToRealmSlug(string realm)
        {
            return realm?.ToLower().Replace(" ", "-").Replace("'", "") ?? "";
        }

        /// <summary>
        /// Normalize equipment slot name for display (MAIN_HAND -> Main Hand)
        /// </summary>
        public static string NormalizeSlot(string slot)
        {
            return slot switch
            {
                "FINGER_1" => "Ring 1",
                "FINGER_2" => "Ring 2",
                "TRINKET_1" => "Trinket 1",
                "TRINKET_2" => "Trinket 2",
                "MAIN_HAND" => "Main Hand",
                "OFF_HAND" => "Off Hand",
                _ => slot.Replace('_', ' ').ToLowerInvariant()
                    .Split(' ')
                    .Select(w => char.ToUpper(w[0]) + w.Substring(1))
                    .Aggregate((a, b) => $"{a} {b}")
            };
        }
    }
}
