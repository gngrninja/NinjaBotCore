#nullable enable

using System;
using System.Collections.Generic;

namespace NinjaBotCore.Models.Wow.Forever
{
    /// <summary>
    /// What the bot knows about Forever's professions without asking anyone: an emoji, the rank a
    /// skill cap stands for and where Wowhead keeps the profession. Keyed by Blizzard's profession id.
    /// </summary>
    public static class ForeverProfessions
    {
        /// <summary>Artisan's cap. Skill bars are drawn against it, or against a higher cap if the armory ever reports one.</summary>
        public const int MaxSkill = 300;

        public const string DefaultEmoji = "🧰";

        // Every id here had a page at wowhead.com/forever/skill={id} on 2026-11-20. A profession
        // Blizzard adds later gets no link until it is added here, so a link never leads nowhere.
        private static readonly Dictionary<int, string> Emojis = new()
        {
            [129] = "🩹", // First Aid
            [164] = "⚒️", // Blacksmithing
            [165] = "🥾", // Leatherworking
            [171] = "⚗️", // Alchemy
            [182] = "🌿", // Herbalism
            [185] = "🍳", // Cooking
            [186] = "⛏️", // Mining
            [197] = "🧵", // Tailoring
            [202] = "⚙️", // Engineering
            [333] = "🪄", // Enchanting
            [356] = "🎣", // Fishing
            [393] = "🔪"  // Skinning
        };

        private static readonly (int Cap, string Name)[] Ranks =
        {
            (75, "Apprentice"), (150, "Journeyman"), (225, "Expert"), (300, "Artisan")
        };

        public static IReadOnlyCollection<int> KnownIds => Emojis.Keys;

        public static string Emoji(int? id) => id is { } known && Emojis.TryGetValue(known, out var emoji) ? emoji : DefaultEmoji;

        /// <summary>Wowhead's Forever page for the profession, or null for one the bot doesn't know has a page.</summary>
        public static string? WowheadUrl(int? id)
            => id is { } known && Emojis.ContainsKey(known) ? $"https://www.wowhead.com/forever/skill={known}" : null;

        /// <summary>The rank whose cap this is ("Journeyman" for 150), or null for a cap that isn't a rank's.</summary>
        public static string? Rank(int cap)
        {
            var index = Array.FindIndex(Ranks, r => r.Cap == cap);
            return index >= 0 ? Ranks[index].Name : null;
        }

        /// <summary>The rank to train once this cap is reached, or null at the last rank or an unknown cap.</summary>
        public static string? NextRank(int cap)
        {
            var index = Array.FindIndex(Ranks, r => r.Cap == cap);
            return index >= 0 && index < Ranks.Length - 1 ? Ranks[index + 1].Name : null;
        }
    }
}
