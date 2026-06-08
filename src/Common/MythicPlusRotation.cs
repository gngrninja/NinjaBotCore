#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Live M+ dungeon rotation. The pool is refreshed at runtime from Raider.IO's
    /// static-data endpoint by <c>MythicPlusDungeonService</c> and cached in the DB,
    /// so it follows the active season automatically — no per-season code edits.
    /// The list below is only a fallback, used until the first successful refresh (or
    /// if Raider.IO is unreachable and the DB cache is empty).
    /// Slug is the Raider.IO short slug used in their dungeon-runs API path.
    /// </summary>
    public static class MythicPlusRotation
    {
        public record Dungeon(string Slug, string Name, string ShortName);

        // Fallback default — Midnight Season 1 (verified against Raider.IO static-data 2026-06).
        private static volatile IReadOnlyList<Dungeon> _current = new List<Dungeon>
        {
            new("algethar-academy",        "Algeth'ar Academy",       "AA"),
            new("magisters-terrace",       "Magisters' Terrace",      "MT"),
            new("maisara-caverns",         "Maisara Caverns",         "MC"),
            new("nexuspoint-xenas",        "Nexus-Point Xenas",       "NPX"),
            new("pit-of-saron",            "Pit of Saron",            "POS"),
            new("seat-of-the-triumvirate", "Seat of the Triumvirate", "SEAT"),
            new("skyreach",                "Skyreach",                "SR"),
            new("windrunner-spire",        "Windrunner Spire",        "WS"),
        };

        /// <summary>Current dungeon pool. Read-mostly; swapped atomically by the refresh service.</summary>
        public static IReadOnlyList<Dungeon> Current => _current;

        /// <summary>
        /// Replaces the live pool. Called by the refresh service after fetching from
        /// Raider.IO (or loading the DB cache). Ignores null/empty input so a failed
        /// upstream fetch never wipes the pool.
        /// </summary>
        public static void SetCurrent(IReadOnlyList<Dungeon> dungeons)
        {
            if (dungeons != null && dungeons.Count > 0)
            {
                _current = dungeons;
            }
        }

        public static Dungeon? FindBySlug(string slug) =>
            _current.FirstOrDefault(d => d.Slug.Equals(slug, System.StringComparison.OrdinalIgnoreCase));

        public static Dungeon? FindByName(string name) =>
            _current.FirstOrDefault(d => d.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
    }
}
