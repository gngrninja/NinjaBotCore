#nullable enable

using System;
using System.Collections.Generic;

namespace NinjaBotCore.Models.Wow.Forever
{
    public enum ForeverLookupStatus
    {
        Found,
        /// <summary>The armory has no such character or guild. Characters appear at level 10, after logging out.</summary>
        NotFound,
        /// <summary>The armory didn't give a usable answer.</summary>
        Unavailable
    }

    public sealed record ForeverLookup<T>(ForeverLookupStatus Status, T? Value) where T : class
    {
        public bool IsFound => Status == ForeverLookupStatus.Found && Value != null;

        public static ForeverLookup<T> Found(T value) => new(ForeverLookupStatus.Found, value);
        public static ForeverLookup<T> NotFound() => new(ForeverLookupStatus.NotFound, null);
        public static ForeverLookup<T> Unavailable() => new(ForeverLookupStatus.Unavailable, null);
    }

    /// <summary>A character as Blizzard's armory reports it. Enum fields are null when the armory uses a value the bot doesn't know.</summary>
    public sealed record ForeverArmoryCharacter(
        long Id,
        string FirstName,
        string Surname,
        string Slug,
        ForeverRegion Region,
        int Level,
        string? Race,
        ForeverClass? Class,
        string? ClassName,
        ForeverFaction? Faction,
        ForeverRuleset? Ruleset,
        string? RulesetName,
        string? GuildName,
        string? GuildSlug,
        int? ItemLevel,
        DateTimeOffset? LastSeen)
    {
        public string FullName => ForeverNames.FullName(FirstName, Surname);
    }

    public sealed record ForeverArmoryItem(string Slot, string SlotName, string Name, string? Quality, int? ItemLevel);

    public sealed record ForeverArmoryProfession(string Name, int Skill, int MaxSkill);

    public sealed record ForeverArmoryTalentTree(string Name, int Points);

    public sealed record ForeverArmoryStats(long? Deaths, long? Quests, long? Kills, long? Dungeons);

    /// <summary>Everything the character card shows. Only the profile is required; the rest is empty when its route didn't answer.</summary>
    /// <param name="RenderUrl">Blizzard's full-body render. It can lag the portrait by hours.</param>
    public sealed record ForeverArmoryCard(
        ForeverArmoryCharacter Character,
        string? AvatarUrl,
        IReadOnlyList<ForeverArmoryItem> Gear,
        IReadOnlyList<ForeverArmoryProfession> Professions,
        IReadOnlyList<ForeverArmoryTalentTree> Talents,
        ForeverArmoryStats? Stats,
        string? RenderUrl = null);

    public sealed record ForeverArmoryGuild(
        long Id,
        string Name,
        string Slug,
        ForeverRegion Region,
        ForeverFaction? Faction,
        ForeverRuleset? Ruleset,
        string? RulesetName,
        int MemberCount);

    public sealed record ForeverArmoryMember(
        long Id,
        string FirstName,
        string Surname,
        string Slug,
        int Level,
        ForeverClass? Class,
        int? Rank)
    {
        public string FullName => ForeverNames.FullName(FirstName, Surname);
    }

    public sealed record ForeverArmoryRoster(IReadOnlyList<ForeverArmoryMember> Members);

    /// <summary>Blizzard's ids for the values the bot stores as enums.</summary>
    public static class ForeverArmoryIds
    {
        public static ForeverRuleset? Ruleset(int? id) => id switch
        {
            1 => ForeverRuleset.PvP,
            2 => ForeverRuleset.Normal,
            3 => ForeverRuleset.Roleplay,
            15 => ForeverRuleset.RoleplayPvP,
            _ => null
        };

        public static ForeverClass? Class(int? id) => id switch
        {
            1 => ForeverClass.Warrior,
            2 => ForeverClass.Paladin,
            3 => ForeverClass.Hunter,
            4 => ForeverClass.Rogue,
            5 => ForeverClass.Priest,
            7 => ForeverClass.Shaman,
            8 => ForeverClass.Mage,
            9 => ForeverClass.Warlock,
            11 => ForeverClass.Druid,
            _ => null
        };

        public static ForeverFaction? Faction(string? type) => type?.ToUpperInvariant() switch
        {
            "ALLIANCE" => ForeverFaction.Alliance,
            "HORDE" => ForeverFaction.Horde,
            _ => null
        };
    }
}
