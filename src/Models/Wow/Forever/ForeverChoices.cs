using Discord.Interactions;

namespace NinjaBotCore.Models.Wow.Forever
{
    // Stored as ints: append new values, never reorder.

    public enum ForeverRegion
    {
        US,
        EU,
        KR,
        TW
    }

    // Hardcore arrives after launch; add it here when Blizzard turns it on.
    public enum ForeverRuleset
    {
        Normal,
        [ChoiceDisplay("PvP")]
        PvP,
        Roleplay,
        [ChoiceDisplay("Roleplay PvP")]
        RoleplayPvP
    }

    public enum ForeverFaction
    {
        Alliance,
        Horde
    }

    public enum ForeverClass
    {
        Warrior,
        Paladin,
        Hunter,
        Rogue,
        Priest,
        Shaman,
        Mage,
        Warlock,
        Druid
    }

    public static class ForeverDisplay
    {
        public static string Name(this ForeverRuleset ruleset) => ruleset switch
        {
            ForeverRuleset.PvP => "PvP",
            ForeverRuleset.RoleplayPvP => "Roleplay PvP",
            _ => ruleset.ToString()
        };

        public static string Emoji(this ForeverFaction faction) => faction == ForeverFaction.Horde ? "🔴" : "🔵";

        /// <summary>One emoji per class, so a long roster can be scanned by eye.</summary>
        public static string Emoji(this ForeverClass characterClass) => characterClass switch
        {
            ForeverClass.Warrior => "⚔️",
            ForeverClass.Paladin => "🔨",
            ForeverClass.Hunter => "🏹",
            ForeverClass.Rogue => "🗡️",
            ForeverClass.Priest => "✨",
            ForeverClass.Shaman => "⚡",
            ForeverClass.Mage => "🔮",
            ForeverClass.Warlock => "🔥",
            ForeverClass.Druid => "🐾",
            _ => "❔"
        };

        /// <summary>The class's colour in game, as 0xRRGGBB.</summary>
        public static uint Color(this ForeverClass characterClass) => characterClass switch
        {
            ForeverClass.Warrior => 0xC79C6E,
            ForeverClass.Paladin => 0xF58CBA,
            ForeverClass.Hunter => 0xABD473,
            ForeverClass.Rogue => 0xFFF569,
            ForeverClass.Priest => 0xFFFFFF,
            ForeverClass.Shaman => 0x0070DE,
            ForeverClass.Mage => 0x69CCF0,
            ForeverClass.Warlock => 0x9482C9,
            ForeverClass.Druid => 0xFF7D0A,
            _ => 0xE0B04A
        };

        /// <summary>"US · PvP · Horde"</summary>
        public static string Context(ForeverRegion region, ForeverRuleset ruleset, ForeverFaction faction)
            => $"{region} · {ruleset.Name()} · {faction}";
    }
}
