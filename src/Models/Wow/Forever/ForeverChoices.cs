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

        /// <summary>"US · PvP · Horde"</summary>
        public static string Context(ForeverRegion region, ForeverRuleset ruleset, ForeverFaction faction)
            => $"{region} · {ruleset.Name()} · {faction}";
    }
}
