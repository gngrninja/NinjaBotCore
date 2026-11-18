using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests;

// Real SQLite so the unique indexes and FKs are enforced. Every guild, user and character is SYNTHETIC.
public sealed class ForeverServiceTests : IAsyncLifetime
{
    private const long ServerA = 1001;
    private const long ServerB = 1002;
    private const long Alice = 501;
    private const long Bob = 502;

    private static readonly ServiceProvider Ef = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeForeverArmory _armory = new();
    private ServiceProvider _services = null!;
    private ForeverService _forever = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddDbContext<NinjaBotEntities>(o => o.UseSqlite(_connection).UseInternalServiceProvider(Ef))
            .BuildServiceProvider();
        await using (var db = Db()) await db.Database.EnsureCreatedAsync();
        _forever = new ForeverService(_services.GetRequiredService<IServiceScopeFactory>(), _armory, NullLogger<ForeverService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private NinjaBotEntities Db() => new(new DbContextOptionsBuilder<NinjaBotEntities>()
        .UseSqlite(_connection).UseInternalServiceProvider(Ef).Options);

    private async Task SetGuild(long server = ServerA, bool roster = true)
    {
        var result = await _forever.SetGuildAsync(server, "Synthetic Guild", ForeverRegion.US, ForeverRuleset.PvP, ForeverFaction.Horde, 1, "officer");
        Assert.True(result.Ok, result.Error);
        if (roster) Assert.True((await _forever.SetRosterEnabledAsync(server, true, 1, "officer")).Ok);
    }

    private async Task<ForeverCharacter> Add(long user, string first, string last = "Brightvale", long server = ServerA,
        ForeverRegion? region = null, ForeverRuleset? ruleset = null, ForeverFaction? faction = null)
    {
        var result = await _forever.AddCharacterAsync(user, server, new ForeverCharacterInput(first, last, region, ruleset, faction));
        Assert.True(result.Ok, result.Error);
        return result.Value!.Character;
    }

    private Task<ForeverCharacter> AddUsPvpHorde(long user, string first, long server)
        => Add(user, first, server: server, region: ForeverRegion.US, ruleset: ForeverRuleset.PvP, faction: ForeverFaction.Horde);

    [Fact]
    public async Task Add_fills_context_from_the_server_guild_and_stays_private()
    {
        await SetGuild();
        var asha = await Add(Alice, "  asha ");

        Assert.Equal("Asha", asha.FirstName);
        Assert.Equal((ForeverRegion.US, ForeverRuleset.PvP, ForeverFaction.Horde), (asha.Region, asha.Ruleset, asha.Faction));
        Assert.Null(await _forever.GetMainIdAsync(Alice, ServerA));
        Assert.Empty(await _forever.GetSharedIdsAsync(Alice, ServerA));
        Assert.Empty((await _forever.GetRosterAsync(ServerA, _ => true)).Value!.Members);
    }

    [Fact]
    public async Task Add_without_a_guild_asks_for_the_missing_context()
    {
        var noRegion = await _forever.AddCharacterAsync(Alice, ServerA, new ForeverCharacterInput("Asha", "Brightvale"));
        Assert.False(noRegion.Ok);
        Assert.Contains("pick a region", noRegion.Error);

        var result = await _forever.AddCharacterAsync(Alice, ServerA, new ForeverCharacterInput("Asha", "Brightvale", ForeverRegion.EU));

        Assert.False(result.Ok);
        Assert.Contains("isn't on the EU armory yet", result.Error);
        Assert.Contains("level 10", result.Error);
        Assert.Contains("ruleset and faction", result.Error);
        Assert.Empty(await _forever.GetCharactersAsync(Alice));
    }

    [Fact]
    public async Task Add_takes_class_ruleset_faction_and_spelling_from_the_armory_over_what_was_typed()
    {
        _armory.AddCharacter(ForeverRegion.EU, "Asha", "McBright", ForeverClass.Priest, ForeverRuleset.Roleplay, ForeverFaction.Alliance, guild: "Synthetic Guild");

        var result = await _forever.AddCharacterAsync(Alice, ServerA,
            new ForeverCharacterInput("asha", "mcbright", ForeverRegion.EU, ForeverRuleset.PvP, ForeverFaction.Horde, ForeverClass.Mage));

        Assert.True(result.Ok, result.Error);
        var saved = result.Value!;
        Assert.Equal(ForeverLookupStatus.Found, saved.Lookup);
        Assert.Equal("Synthetic Guild", saved.Armory!.GuildName);
        Assert.Equal("Asha McBright", saved.Character.FullName);
        Assert.Equal((ForeverRegion.EU, ForeverRuleset.Roleplay, ForeverFaction.Alliance, (ForeverClass?)ForeverClass.Priest),
            (saved.Character.Region, saved.Character.Ruleset, saved.Character.Faction, saved.Character.Class));

        // The armory is asked in the character's own region, not the first one that has the name.
        var us = await _forever.AddCharacterAsync(Alice, ServerA,
            new ForeverCharacterInput("Asha", "McBright", ForeverRegion.US, ForeverRuleset.PvP, ForeverFaction.Horde));
        Assert.Equal((ForeverLookupStatus?)ForeverLookupStatus.NotFound, us.Value!.Lookup);
        Assert.Equal(ForeverRuleset.PvP, us.Value.Character.Ruleset);
    }

    [Fact]
    public async Task Add_saves_what_was_typed_when_the_armory_has_nothing_or_is_down()
    {
        var typed = new ForeverCharacterInput("Asha", "Brightvale", ForeverRegion.US, ForeverRuleset.PvP, ForeverFaction.Horde, ForeverClass.Mage);

        var missing = await _forever.AddCharacterAsync(Alice, ServerA, typed);
        Assert.True(missing.Ok, missing.Error);
        Assert.Equal((ForeverLookupStatus?)ForeverLookupStatus.NotFound, missing.Value!.Lookup);
        Assert.Null(missing.Value.Armory);
        Assert.Equal(ForeverClass.Mage, missing.Value.Character.Class);

        _armory.Down = true;
        var down = await _forever.AddCharacterAsync(Bob, ServerA, typed);
        Assert.True(down.Ok, down.Error);
        Assert.Equal((ForeverLookupStatus?)ForeverLookupStatus.Unavailable, down.Value!.Lookup);

        var downAndIncomplete = await _forever.AddCharacterAsync(Bob, ServerA, new ForeverCharacterInput("Thalen", "Brightvale", ForeverRegion.US));
        Assert.False(downAndIncomplete.Ok);
        Assert.Contains("didn't answer", downAndIncomplete.Error);
    }

    [Fact]
    public async Task A_ruleset_the_bot_does_not_know_yet_falls_back_to_what_was_typed()
    {
        _armory.AddCharacter(ForeverRegion.US, "Asha", "Brightvale", ruleset: null, faction: ForeverFaction.Horde);

        var unknown = await _forever.AddCharacterAsync(Alice, ServerA, new ForeverCharacterInput("Asha", "Brightvale", ForeverRegion.US));
        Assert.False(unknown.Ok);
        Assert.Contains("ruleset I don't know yet", unknown.Error);

        var typed = await _forever.AddCharacterAsync(Alice, ServerA,
            new ForeverCharacterInput("Asha", "Brightvale", ForeverRegion.US, ForeverRuleset.PvP, ForeverFaction.Alliance));
        Assert.True(typed.Ok, typed.Error);
        Assert.Equal((ForeverRuleset.PvP, ForeverFaction.Horde), (typed.Value!.Character.Ruleset, typed.Value.Character.Faction));
    }

    [Fact]
    public async Task Edit_with_no_changes_brings_a_character_up_to_date_with_the_armory()
    {
        var asha = (await _forever.AddCharacterAsync(Alice, ServerA,
            new ForeverCharacterInput("asha", "brightvale", ForeverRegion.US, ForeverRuleset.PvP, ForeverFaction.Horde, ForeverClass.Mage))).Value!.Character;
        _armory.AddCharacter(ForeverRegion.US, "Asha", "BrightVale", ForeverClass.Druid, ForeverRuleset.Normal, ForeverFaction.Alliance);

        var refreshed = await _forever.EditCharacterAsync(Alice, asha.Id, new ForeverCharacterInput());

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(ForeverLookupStatus.Found, refreshed.Value!.Lookup);
        var stored = (await _forever.GetCharacterAsync(Alice, asha.Id))!;
        Assert.Equal(("Asha BrightVale", ForeverRuleset.Normal, ForeverFaction.Alliance, (ForeverClass?)ForeverClass.Druid),
            (stored.FullName, stored.Ruleset, stored.Faction, stored.Class));

        // Off the armory, an edit is still just an edit.
        _armory.Down = true;
        var manual = await _forever.EditCharacterAsync(Alice, asha.Id, new ForeverCharacterInput(Class: ForeverClass.Rogue));
        Assert.Equal((ForeverLookupStatus?)ForeverLookupStatus.Unavailable, manual.Value!.Lookup);
        Assert.Equal(ForeverClass.Rogue, (await _forever.GetCharacterAsync(Alice, asha.Id))!.Class);
    }

    [Fact]
    public async Task Guild_set_fills_in_from_the_armory_and_remembers_its_slug()
    {
        _armory.AddGuild(ForeverRegion.EU, "Kael's Chosen", ForeverRuleset.Roleplay, ForeverFaction.Alliance, FakeForeverArmory.Member("Asha", "Brightvale"));

        var result = await _forever.SetGuildAsync(ServerA, "kael's chosen", ForeverRegion.EU, ForeverRuleset.PvP, null, 1, "officer");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(ForeverLookupStatus.Found, result.Value!.Lookup);
        Assert.Equal(1, result.Value.Armory!.MemberCount);
        var guild = (await _forever.GetGuildAsync(ServerA))!;
        Assert.Equal(("Kael's Chosen", "kaels-chosen", ForeverRuleset.Roleplay, ForeverFaction.Alliance),
            (guild.Name, guild.ArmorySlug, guild.Ruleset, guild.Faction));
    }

    [Fact]
    public async Task A_guild_the_armory_does_not_have_is_saved_as_typed_and_unmatched()
    {
        _armory.AddGuild(ForeverRegion.EU, "Synthetic Guild");
        Assert.True((await _forever.SetGuildAsync(ServerA, "Synthetic Guild", ForeverRegion.EU, null, null, 1, "officer")).Ok);

        var incomplete = await _forever.SetGuildAsync(ServerA, "Other Guild", ForeverRegion.EU, ForeverRuleset.PvP, null, 1, "officer");
        Assert.False(incomplete.Ok);
        Assert.Contains("couldn't find **Other Guild** on the EU armory", incomplete.Error);
        Assert.Contains("pick a faction", incomplete.Error);
        Assert.Equal("synthetic-guild", (await _forever.GetGuildAsync(ServerA))!.ArmorySlug);

        var typed = await _forever.SetGuildAsync(ServerA, "Other Guild", ForeverRegion.EU, ForeverRuleset.PvP, ForeverFaction.Horde, 1, "officer");
        Assert.True(typed.Ok, typed.Error);
        Assert.Equal(ForeverLookupStatus.NotFound, typed.Value!.Lookup);
        var guild = (await _forever.GetGuildAsync(ServerA))!;
        Assert.Equal(("Other Guild", (string)null, ForeverRuleset.PvP), (guild.Name, guild.ArmorySlug, guild.Ruleset));

        _armory.Down = true;
        var down = await _forever.SetGuildAsync(ServerB, "Synthetic Guild", ForeverRegion.EU, null, null, 1, "officer");
        Assert.False(down.Ok);
        Assert.Contains("didn't answer", down.Error);
    }

    [Fact]
    public async Task Roster_carries_the_armory_roster_only_for_a_matched_guild()
    {
        await SetGuild();
        var unmatched = (await _forever.GetRosterAsync(ServerA, _ => true)).Value!;
        Assert.Null(unmatched.Armory);
        Assert.False(unmatched.ArmoryMissed);

        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", ForeverRuleset.PvP, ForeverFaction.Horde,
            FakeForeverArmory.Member("Asha", "Brightvale"), FakeForeverArmory.Member("Thalen", "Brightvale"));
        await SetGuild();
        var asha = await Add(Alice, "Asha");
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok);

        var matched = (await _forever.GetRosterAsync(ServerA, _ => true)).Value!;
        Assert.Equal(2, matched.Armory!.Members.Count);
        Assert.False(matched.ArmoryMissed);
        Assert.Single(matched.Members);

        // The armory going quiet leaves what members shared.
        _armory.Down = true;
        var quiet = (await _forever.GetRosterAsync(ServerA, _ => true)).Value!;
        Assert.Null(quiet.Armory);
        Assert.True(quiet.ArmoryMissed);
        Assert.Single(quiet.Members);
    }

    [Fact]
    public async Task Same_name_is_unique_per_owner_and_region_only()
    {
        await SetGuild();
        await Add(Alice, "Asha");

        var dupe = await _forever.AddCharacterAsync(Alice, ServerA, new ForeverCharacterInput("ASHA", "brightvale"));
        Assert.False(dupe.Ok);
        Assert.Contains("already have", dupe.Error);

        await Add(Alice, "Asha", region: ForeverRegion.EU);
        await Add(Alice, "Asha", "Duskvale");
        await Add(Alice, "Thalen");
        await Add(Bob, "Asha"); // Nobody reserves a name by claiming it first.

        Assert.Equal(4, (await _forever.GetCharactersAsync(Alice)).Count);
        Assert.Single(await _forever.GetCharactersAsync(Bob));
    }

    [Fact]
    public async Task Database_unique_index_backstops_duplicate_inserts()
    {
        var now = DateTime.UtcNow;
        ForeverCharacter Row() => new()
        {
            UserId = Alice, FirstName = "Asha", LastName = "Brightvale",
            NormalizedFirstName = "ASHA", NormalizedLastName = "BRIGHTVALE", CreatedAt = now, UpdatedAt = now
        };
        await using var db = Db();
        db.ForeverCharacters.AddRange(Row(), Row());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Edit_keeps_id_main_and_share_and_rejects_collisions()
    {
        await SetGuild();
        var asha = await Add(Alice, "Asha");
        await Add(Alice, "Thalen");
        Assert.True((await _forever.SetMainAsync(Alice, ServerA, asha.Id)).Ok);
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok);

        var renamed = await _forever.EditCharacterAsync(Alice, asha.Id, new ForeverCharacterInput("Ashara", Class: ForeverClass.Mage));
        Assert.True(renamed.Ok, renamed.Error);
        Assert.Equal(asha.Id, renamed.Value!.Character.Id);
        Assert.Equal("Ashara Brightvale", renamed.Value.Character.FullName);
        Assert.Equal(ForeverClass.Mage, renamed.Value.Character.Class);
        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerA));
        Assert.Contains(asha.Id, await _forever.GetSharedIdsAsync(Alice, ServerA));

        var collision = await _forever.EditCharacterAsync(Alice, asha.Id, new ForeverCharacterInput("thalen"));
        Assert.False(collision.Ok);
        Assert.Equal("Ashara", (await _forever.GetCharacterAsync(Alice, asha.Id))!.FirstName);
    }

    [Fact]
    public async Task Remove_clears_main_and_shares_in_every_server_and_nothing_else()
    {
        await SetGuild(ServerA);
        await SetGuild(ServerB);
        var asha = await Add(Alice, "Asha");
        var bobs = await Add(Bob, "Asha");
        foreach (var server in new[] { ServerA, ServerB })
        {
            Assert.True((await _forever.SetMainAsync(Alice, server, asha.Id)).Ok);
            Assert.True((await _forever.ShareAsync(Alice, server, asha.Id)).Ok);
            Assert.True((await _forever.ShareAsync(Bob, server, bobs.Id)).Ok);
        }

        Assert.NotNull(await _forever.RemoveCharacterAsync(Alice, asha.Id));

        await using var db = Db();
        Assert.Empty(await db.ForeverMains.ToListAsync());
        Assert.Equal(2, await db.ForeverRosterShares.CountAsync(s => s.UserId == Bob));
        Assert.Equal(0, await db.ForeverRosterShares.CountAsync(s => s.UserId == Alice));
        Assert.Null(await _forever.GetCharacterAsync(Alice, asha.Id));
    }

    [Fact]
    public async Task Main_is_per_server_and_one_row_per_user()
    {
        var asha = await AddUsPvpHorde(Alice, "Asha", ServerA);
        await SetGuild();
        var thalen = await Add(Alice, "Thalen");

        Assert.True((await _forever.SetMainAsync(Alice, ServerA, asha.Id)).Ok);
        Assert.True((await _forever.SetMainAsync(Alice, ServerB, thalen.Id)).Ok);
        Assert.True((await _forever.SetMainAsync(Alice, ServerA, thalen.Id)).Ok);

        Assert.Equal(thalen.Id, await _forever.GetMainIdAsync(Alice, ServerA));
        Assert.Equal(thalen.Id, await _forever.GetMainIdAsync(Alice, ServerB));
        await using var db = Db();
        Assert.Equal(2, await db.ForeverMains.CountAsync());
    }

    [Fact]
    public async Task Nobody_can_act_on_another_users_character()
    {
        await SetGuild();
        var asha = await Add(Alice, "Asha");

        Assert.False((await _forever.SetMainAsync(Bob, ServerA, asha.Id)).Ok);
        Assert.False((await _forever.ShareAsync(Bob, ServerA, asha.Id)).Ok);
        Assert.False((await _forever.EditCharacterAsync(Bob, asha.Id, new ForeverCharacterInput("Mine"))).Ok);
        Assert.Null(await _forever.RemoveCharacterAsync(Bob, asha.Id));
        Assert.Null(await _forever.GetCharacterAsync(Bob, asha.Id));
        Assert.Equal("Asha", (await _forever.GetCharacterAsync(Alice, asha.Id))!.FirstName);
    }

    [Fact]
    public async Task Share_needs_a_guild_the_roster_on_and_a_matching_character()
    {
        var asha = await AddUsPvpHorde(Alice, "Asha", ServerB);
        Assert.Equal(ForeverService.NoGuildMessage, (await _forever.ShareAsync(Alice, ServerA, asha.Id)).Error);

        await SetGuild(roster: false);
        Assert.Equal(ForeverService.RosterOffMessage, (await _forever.ShareAsync(Alice, ServerA, asha.Id)).Error);

        await _forever.SetRosterEnabledAsync(ServerA, true, 1, "officer");
        var alliance = await Add(Alice, "Mira", faction: ForeverFaction.Alliance);
        var mismatch = await _forever.ShareAsync(Alice, ServerA, alliance.Id);
        Assert.False(mismatch.Ok);
        Assert.Contains("Only matching characters", mismatch.Error);

        Assert.False((await _forever.ShareAsync(Alice, ServerA, 0)).Ok);
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok);
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok); // idempotent
        Assert.Single(await _forever.GetSharedIdsAsync(Alice, ServerA));
    }

    [Fact]
    public async Task Roster_shows_current_members_matching_shares_with_main_first()
    {
        await SetGuild();
        var asha = await Add(Alice, "Asha");
        var thalen = await Add(Alice, "Thalen");
        var bobs = await Add(Bob, "Mira");
        var hidden = await Add(Alice, "Private");
        foreach (var c in new[] { asha, thalen })
            Assert.True((await _forever.ShareAsync(Alice, ServerA, c.Id)).Ok);
        Assert.True((await _forever.ShareAsync(Bob, ServerA, bobs.Id)).Ok);
        Assert.True((await _forever.SetMainAsync(Alice, ServerA, thalen.Id)).Ok);

        var roster = (await _forever.GetRosterAsync(ServerA, id => id == Alice)).Value!;
        var alice = Assert.Single(roster.Members);
        Assert.Equal(Alice, alice.UserId);
        Assert.Equal(new[] { thalen.Id, asha.Id }, alice.Characters.Select(c => c.Id));
        Assert.Equal(thalen.Id, alice.MainId);
        Assert.DoesNotContain(alice.Characters, c => c.Id == hidden.Id);

        // A context change hides shares that no longer match, without deleting them.
        await _forever.EditCharacterAsync(Alice, asha.Id, new ForeverCharacterInput(Region: ForeverRegion.EU));
        alice = Assert.Single((await _forever.GetRosterAsync(ServerA, id => id == Alice)).Value!.Members);
        Assert.Equal(new[] { thalen.Id }, alice.Characters.Select(c => c.Id));
        Assert.Contains(asha.Id, await _forever.GetSharedIdsAsync(Alice, ServerA));

        Assert.True(await _forever.UnshareAsync(Alice, ServerA, thalen.Id));
        Assert.Empty((await _forever.GetRosterAsync(ServerA, id => id == Alice)).Value!.Members);
    }

    [Fact]
    public async Task Roster_off_or_unset_hides_everything_and_unset_keeps_characters()
    {
        await SetGuild();
        var asha = await Add(Alice, "Asha");
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok);

        await _forever.SetRosterEnabledAsync(ServerA, false, 1, "officer");
        Assert.Equal(ForeverService.RosterOffMessage, (await _forever.GetRosterAsync(ServerA, _ => true)).Error);

        Assert.True(await _forever.UnsetGuildAsync(ServerA));
        Assert.Equal(ForeverService.NoGuildMessage, (await _forever.GetRosterAsync(ServerA, _ => true)).Error);
        Assert.False(await _forever.UnsetGuildAsync(ServerA));
        Assert.NotNull(await _forever.GetCharacterAsync(Alice, asha.Id));
    }

    [Fact]
    public async Task Setting_the_guild_again_updates_in_place_and_keeps_the_roster_switch()
    {
        await SetGuild();
        var updated = await _forever.SetGuildAsync(ServerA, "Renamed", ForeverRegion.EU, ForeverRuleset.Normal, ForeverFaction.Alliance, 2, "other");

        Assert.True(updated.Ok);
        var guild = await _forever.GetGuildAsync(ServerA);
        Assert.Equal(("Renamed", ForeverRegion.EU, true, 2L), (guild!.Name, guild.Region, guild.RosterEnabled, guild.SetById!.Value));
        Assert.False((await _forever.SetGuildAsync(ServerA, "bad\nname", ForeverRegion.EU, ForeverRuleset.Normal, ForeverFaction.Alliance, 2, "x")).Ok);
        Assert.False((await _forever.SetRosterEnabledAsync(ServerB, true, 1, "x")).Ok);
    }

    [Fact]
    public async Task Leaving_a_server_drops_shares_and_main_there_only()
    {
        await SetGuild(ServerA);
        await SetGuild(ServerB);
        var asha = await Add(Alice, "Asha");
        foreach (var server in new[] { ServerA, ServerB })
        {
            Assert.True((await _forever.SetMainAsync(Alice, server, asha.Id)).Ok);
            Assert.True((await _forever.ShareAsync(Alice, server, asha.Id)).Ok);
        }

        Assert.Equal(2, await _forever.ForgetMemberAsync(ServerA, Alice));
        Assert.Equal(0, await _forever.ForgetMemberAsync(ServerA, Alice));

        Assert.Null(await _forever.GetMainIdAsync(Alice, ServerA));
        Assert.Empty(await _forever.GetSharedIdsAsync(Alice, ServerA));
        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerB));
        Assert.Contains(asha.Id, await _forever.GetSharedIdsAsync(Alice, ServerB));
        Assert.NotNull(await _forever.GetCharacterAsync(Alice, asha.Id));
    }

    [Fact]
    public async Task Forever_never_touches_retail_character_associations()
    {
        await using (var db = Db())
        {
            db.WowCharAssociation.Add(new WowCharAssociation { UserId = Alice, ServerId = ServerA, CharName = "Retailmain", IsMain = true });
            await db.SaveChangesAsync();
        }

        await SetGuild();
        var asha = await Add(Alice, "Asha");
        await _forever.SetMainAsync(Alice, ServerA, asha.Id);
        await _forever.ShareAsync(Alice, ServerA, asha.Id);
        await _forever.RemoveCharacterAsync(Alice, asha.Id);

        await using var check = Db();
        var legacy = await check.WowCharAssociation.SingleAsync();
        Assert.Equal(("Retailmain", true), (legacy.CharName, legacy.IsMain));
    }
}
