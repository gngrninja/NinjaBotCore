#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Current M+ dungeon rotation. Edit this list at the start of each season.
    /// Slug is the Raider.IO short slug used in their dungeon-runs API path.
    /// </summary>
    public static class MythicPlusRotation
    {
        public record Dungeon(string Slug, string Name, string ShortName);

        // Midnight Season 1 (placeholder names — verify each Tuesday reset).
        public static readonly IReadOnlyList<Dungeon> Current = new List<Dungeon>
        {
            new("darkflame-cleft",       "Darkflame Cleft",         "DFC"),
            new("priory-of-the-sacred",  "Priory of the Sacred Flame","PSF"),
            new("the-rookery",           "The Rookery",             "RKY"),
            new("operation-floodgate",   "Operation: Floodgate",    "OFG"),
            new("eco-dome-aldani",       "Eco-Dome Aldani",         "EDA"),
            new("ara-kara-city-of-echoes","Ara-Kara, City of Echoes","AKC"),
            new("the-stonevault",        "The Stonevault",          "STV"),
            new("workshop",              "Mechagon Workshop",       "WRK"),
        };

        public static Dungeon? FindBySlug(string slug) =>
            Current.FirstOrDefault(d => d.Slug.Equals(slug, System.StringComparison.OrdinalIgnoreCase));

        public static Dungeon? FindByName(string name) =>
            Current.FirstOrDefault(d => d.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
    }
}
