using System.Collections.Generic;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Shared constants for WoW expansion names.
    /// Used by slash commands, component dropdowns, and expansion detection logic.
    /// Update this single location when new expansions are released.
    /// </summary>
    public static class WowExpansions
    {
        // Expansion names - newest to oldest
        public const string Midnight = "Midnight";
        public const string TheWarWithin = "The War Within";
        public const string Dragonflight = "Dragonflight";
        public const string Shadowlands = "Shadowlands";
        public const string BattleForAzeroth = "Battle for Azeroth";
        public const string Legion = "Legion";
        public const string WarlordsOfDraenor = "Warlords of Draenor";
        public const string MistsOfPandaria = "Mists of Pandaria";
        public const string Cataclysm = "Cataclysm";
        public const string WrathOfTheLichKing = "Wrath of the Lich King";
        public const string TheBurningCrusade = "The Burning Crusade";
        public const string Classic = "Classic";

        /// <summary>
        /// All expansions in order from newest to oldest.
        /// Use this for building dropdown menus and filters.
        /// </summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Midnight,
            TheWarWithin,
            Dragonflight,
            Shadowlands,
            BattleForAzeroth,
            Legion,
            WarlordsOfDraenor,
            MistsOfPandaria,
            Cataclysm,
            WrathOfTheLichKing,
            TheBurningCrusade,
            Classic
        };

        /// <summary>
        /// Mount ID ranges for expansion detection (fallback method).
        /// Maps minimum mount ID to expansion name.
        /// IMPORTANT: These are approximate. New mounts for old expansions get high IDs.
        /// </summary>
        public static readonly IReadOnlyList<(long MinId, string Expansion)> MountIdRanges = new[]
        {
            (2700L, Midnight),              // Midnight expansion (mount ID 2734 confirmed)
            (1700L, TheWarWithin),
            (1500L, Dragonflight),
            (1200L, Shadowlands),
            (900L, BattleForAzeroth),
            (700L, Legion),
            (600L, WarlordsOfDraenor),
            (500L, MistsOfPandaria),
            (400L, Cataclysm),
            (300L, WrathOfTheLichKing),
            (200L, TheBurningCrusade),
            (1L, Classic)
        };

        /// <summary>
        /// Get expansion from mount ID using ID range fallback.
        /// </summary>
        public static string GetExpansionFromMountId(long mountId)
        {
            foreach (var (minId, expansion) in MountIdRanges)
            {
                if (mountId >= minId)
                    return expansion;
            }
            return "Unknown";
        }
    }
}
