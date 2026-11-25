using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Tests;

/// <summary>An armory that knows only what a test puts in it. Empty, it answers "not found" like a pre-level-10 character.</summary>
internal sealed class FakeForeverArmory : IForeverArmory
{
    private readonly Dictionary<(ForeverRegion, string), ForeverArmoryCharacter> _characters = new();
    private readonly Dictionary<(ForeverRegion, string), ForeverArmoryGuild> _guilds = new();
    private readonly Dictionary<(ForeverRegion, string), ForeverArmoryRoster> _rosters = new();
    private readonly Dictionary<(ForeverRegion, string), ForeverArmoryProfessions> _professions = new();

    /// <summary>How many times professions were asked for, by character slug.</summary>
    public Dictionary<string, int> ProfessionAsks { get; } = new();

    /// <summary>Every lookup answers Unavailable, as when Blizzard is down or changed a route.</summary>
    public bool Down { get; set; }

    /// <summary>How many times a guild was looked up by name.</summary>
    public int GuildAsks { get; private set; }

    /// <summary>Runs while a guild lookup is on its way back: what happens in the meantime, or the armory throwing.</summary>
    public Func<Task> WhileFindingGuild { get; set; }

    /// <summary>A roster doesn't answer until this does, as when Blizzard is slow.</summary>
    public Task RosterHeld { get; set; } = Task.CompletedTask;

    public ForeverArmoryCharacter AddCharacter(
        ForeverRegion region, string first, string last,
        ForeverClass? characterClass = ForeverClass.Priest,
        ForeverRuleset? ruleset = ForeverRuleset.Normal,
        ForeverFaction? faction = ForeverFaction.Alliance,
        int level = 30, string guild = null)
    {
        var slug = ForeverNames.CharacterSlug(first, last);
        var character = new ForeverArmoryCharacter(1, first, last, slug, region, level, "Human", characterClass, characterClass?.ToString(),
            faction, ruleset, ruleset?.ToString() ?? "Hardcore", guild, guild == null ? null : ForeverNames.Slug(guild), 20, null);
        _characters[(region, slug)] = character;
        return character;
    }

    public ForeverArmoryGuild AddGuild(
        ForeverRegion region, string name,
        ForeverRuleset? ruleset = ForeverRuleset.Normal,
        ForeverFaction? faction = ForeverFaction.Alliance,
        params ForeverArmoryMember[] members)
    {
        var slug = ForeverNames.Slug(name);
        var guild = new ForeverArmoryGuild(1, name, slug, region, faction, ruleset, ruleset?.ToString() ?? "Hardcore", members.Length);
        _guilds[(region, slug)] = guild;
        _rosters[(region, slug)] = new ForeverArmoryRoster(members);
        return guild;
    }

    public static ForeverArmoryMember Member(string first, string last, int level = 30, ForeverClass? characterClass = ForeverClass.Priest, int? rank = 3)
        => new(1, first, last, ForeverNames.CharacterSlug(first, last), level, characterClass, rank);

    public Task<ForeverLookup<ForeverArmoryCharacter>> GetCharacterAsync(
        ForeverRegion region, string firstName, string surname, CancellationToken cancellationToken = default)
        => Answer(_characters, (region, ForeverNames.CharacterSlug(firstName, surname)));

    public async Task<ForeverLookup<ForeverArmoryCard>> GetCardAsync(
        ForeverRegion region, string firstName, string surname, CancellationToken cancellationToken = default)
    {
        var character = await GetCharacterAsync(region, firstName, surname, cancellationToken);
        return character.IsFound
            ? ForeverLookup<ForeverArmoryCard>.Found(new ForeverArmoryCard(character.Value!, null,
                new List<ForeverArmoryItem>(), new List<ForeverArmoryProfession>(), new List<ForeverArmoryTalentTree>(), null))
            : new ForeverLookup<ForeverArmoryCard>(character.Status, null);
    }

    public async Task<ForeverLookup<ForeverArmoryGuild>> GetGuildAsync(
        ForeverRegion region, string guildName, CancellationToken cancellationToken = default)
    {
        GuildAsks++;
        if (WhileFindingGuild != null) await WhileFindingGuild();
        return await Answer(_guilds, (region, ForeverNames.Slug(guildName)));
    }

    public async Task<ForeverLookup<ForeverArmoryRoster>> GetRosterAsync(
        ForeverRegion region, string guildSlug, CancellationToken cancellationToken = default)
    {
        await RosterHeld;
        return await Answer(_rosters, (region, guildSlug));
    }

    /// <summary>Gives a character professions. One the armory has no professions for answers "not found".</summary>
    public void AddProfessions(ForeverRegion region, string first, string last, params ForeverArmoryProfession[] professions)
        => _professions[(region, ForeverNames.CharacterSlug(first, last))] = new ForeverArmoryProfessions(professions);

    public Task<ForeverLookup<ForeverArmoryProfessions>> GetProfessionsAsync(
        ForeverRegion region, string characterSlug, CancellationToken cancellationToken = default)
    {
        lock (ProfessionAsks) ProfessionAsks[characterSlug] = ProfessionAsks.GetValueOrDefault(characterSlug) + 1;
        return Answer(_professions, (region, characterSlug));
    }

    private Task<ForeverLookup<T>> Answer<T>(Dictionary<(ForeverRegion, string), T> known, (ForeverRegion, string) key) where T : class
        => Task.FromResult(Down
            ? ForeverLookup<T>.Unavailable()
            : known.TryGetValue(key, out var value) ? ForeverLookup<T>.Found(value) : ForeverLookup<T>.NotFound());
}
