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

    /// <summary>Puts the guild on the armory (Horde, PvP, US) and sets it as the server's.</summary>
    private async Task SetGuild(long server = ServerA, params ForeverArmoryMember[] members)
    {
        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", ForeverRuleset.PvP, ForeverFaction.Horde, members);
        var result = await _forever.SetGuildAsync(server, "Synthetic Guild", ForeverRegion.US, 1, "officer");
        Assert.True(result.Ok, result.Error);
    }

    /// <summary>Puts the character on the armory, in the guild's region, ruleset and faction unless told otherwise, and saves it.</summary>
    private async Task<ForeverCharacter> Add(long user, string first, string last = "Brightvale", long server = ServerA,
        ForeverRegion region = ForeverRegion.US, ForeverFaction faction = ForeverFaction.Horde)
    {
        _armory.AddCharacter(region, first, last, ForeverClass.Priest, ForeverRuleset.PvP, faction);
        var result = await _forever.AddCharacterAsync(user, server, first, last, region);
        Assert.True(result.Ok, result.Error);
        return result.Value!.Character;
    }

    [Fact]
    public async Task Add_takes_everything_from_the_armory_and_stays_off_the_roster()
    {
        await SetGuild();
        _armory.AddCharacter(ForeverRegion.US, "Asha", "McBright", ForeverClass.Mage, ForeverRuleset.Roleplay, ForeverFaction.Alliance, guild: "Synthetic Guild");

        var result = await _forever.AddCharacterAsync(Alice, ServerA, "  asha ", "mcbright");

        Assert.True(result.Ok, result.Error);
        var saved = result.Value!;
        Assert.Equal(ForeverLookupStatus.Found, saved.Lookup);
        Assert.Equal("Synthetic Guild", saved.Armory!.GuildName);
        // The armory's spelling, and its ruleset and faction rather than the server guild's.
        Assert.Equal("Asha McBright", saved.Character.FullName);
        Assert.Equal((ForeverRegion.US, ForeverRuleset.Roleplay, ForeverFaction.Alliance, (ForeverClass?)ForeverClass.Mage),
            (saved.Character.Region, saved.Character.Ruleset, saved.Character.Faction, saved.Character.Class));
        Assert.Empty(await _forever.GetSharedIdsAsync(Alice, ServerA));
        Assert.Empty((await _forever.GetRosterAsync(ServerA, _ => true)).Value!.Members);
    }

    [Fact]
    public async Task Add_needs_a_region_and_a_character_the_armory_has()
    {
        var noRegion = await _forever.AddCharacterAsync(Alice, ServerA, "Asha", "Brightvale");
        Assert.False(noRegion.Ok);
        Assert.Contains("pick a region", noRegion.Error);

        var missing = await _forever.AddCharacterAsync(Alice, ServerA, "Asha", "Brightvale", ForeverRegion.EU);
        Assert.False(missing.Ok);
        Assert.Contains("isn't on the EU armory", missing.Error);
        Assert.Contains("level 10", missing.Error);

        // The armory is asked in the character's own region, not the first one that has the name.
        _armory.AddCharacter(ForeverRegion.US, "Asha", "Brightvale");
        Assert.False((await _forever.AddCharacterAsync(Alice, ServerA, "Asha", "Brightvale", ForeverRegion.EU)).Ok);

        _armory.Down = true;
        var down = await _forever.AddCharacterAsync(Alice, ServerA, "Asha", "Brightvale", ForeverRegion.US);
        Assert.False(down.Ok);
        Assert.Equal(ForeverService.ArmoryDownMessage, down.Error);

        Assert.False((await _forever.AddCharacterAsync(Alice, ServerA, "A", "Brightvale", ForeverRegion.US)).Ok);
        Assert.Empty(await _forever.GetCharactersAsync(Alice));
    }

    [Fact]
    public async Task A_guildmate_below_level_ten_is_saved_from_the_guilds_roster()
    {
        await SetGuild(ServerA, FakeForeverArmory.Member("Mira", "Dawnsong", level: 6, characterClass: ForeverClass.Mage));

        var young = await _forever.AddCharacterAsync(Alice, ServerA, "mira", "dawnsong");

        Assert.True(young.Ok, young.Error);
        Assert.Equal((ForeverLookupStatus?)ForeverLookupStatus.NotFound, young.Value!.Lookup);
        Assert.Null(young.Value.Armory);
        Assert.Equal(("Mira Dawnsong", ForeverRuleset.PvP, ForeverFaction.Horde, (ForeverClass?)ForeverClass.Mage),
            (young.Value.Character.FullName, young.Value.Character.Ruleset, young.Value.Character.Faction, young.Value.Character.Class));

        // Someone who isn't on the roster, or a lookup in another region, has nothing to be saved from.
        Assert.False((await _forever.AddCharacterAsync(Alice, ServerA, "Thalen", "Dawnsong")).Ok);
        Assert.False((await _forever.AddCharacterAsync(Bob, ServerA, "Mira", "Dawnsong", ForeverRegion.EU)).Ok);
        // With the armory down there is no roster to save anyone from either.
        _armory.Down = true;
        Assert.False((await _forever.AddCharacterAsync(Bob, ServerA, "Mira", "Dawnsong")).Ok);
    }

    [Fact]
    public async Task A_ruleset_the_bot_does_not_know_yet_is_refused_rather_than_guessed()
    {
        // Not even the server guild's ruleset stands in: the armory said it is something else.
        await SetGuild();
        _armory.AddCharacter(ForeverRegion.US, "Asha", "Brightvale", ruleset: null, faction: ForeverFaction.Horde);

        var unknown = await _forever.AddCharacterAsync(Alice, ServerA, "Asha", "Brightvale", ForeverRegion.US);

        Assert.False(unknown.Ok);
        Assert.Contains("ruleset or faction I don't know yet", unknown.Error);
        Assert.Empty(await _forever.GetCharactersAsync(Alice));
    }

    [Fact]
    public async Task The_first_character_added_in_a_server_becomes_the_main_there()
    {
        await SetGuild(ServerA);
        await SetGuild(ServerB);

        var asha = await Add(Alice, "Asha");
        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerA));
        Assert.Null(await _forever.GetMainIdAsync(Alice, ServerB));

        await Add(Alice, "Thalen");
        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerA));

        // No main in the other server yet, so the first one added from there takes it.
        var mira = await Add(Alice, "Mira", server: ServerB);
        Assert.Equal(mira.Id, await _forever.GetMainIdAsync(Alice, ServerB));
        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerA));
    }

    [Fact]
    public async Task Refresh_brings_a_saved_character_up_to_date_and_keeps_it_when_the_armory_is_quiet()
    {
        var asha = await Add(Alice, "Asha", "Brightvale");
        _armory.AddCharacter(ForeverRegion.US, "Asha", "BrightVale", ForeverClass.Druid, ForeverRuleset.Normal, ForeverFaction.Alliance);

        var refreshed = await _forever.RefreshCharacterAsync(Alice, asha.Id);

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(ForeverLookupStatus.Found, refreshed.Value!.Lookup);
        Assert.Equal(asha.Id, refreshed.Value.Character.Id);
        var stored = (await _forever.GetCharacterAsync(Alice, asha.Id))!;
        Assert.Equal(("Asha BrightVale", ForeverRuleset.Normal, ForeverFaction.Alliance, (ForeverClass?)ForeverClass.Druid),
            (stored.FullName, stored.Ruleset, stored.Faction, stored.Class));
        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerA));

        _armory.Down = true;
        var quiet = await _forever.RefreshCharacterAsync(Alice, asha.Id);
        Assert.True(quiet.Ok);
        Assert.Equal((ForeverLookupStatus?)ForeverLookupStatus.Unavailable, quiet.Value!.Lookup);
        Assert.Equal(ForeverClass.Druid, (await _forever.GetCharacterAsync(Alice, asha.Id))!.Class);

        Assert.False((await _forever.RefreshCharacterAsync(Bob, asha.Id)).Ok);
    }

    [Fact]
    public async Task Guild_set_takes_the_armorys_spelling_ruleset_and_faction()
    {
        _armory.AddGuild(ForeverRegion.EU, "Kael's Chosen", ForeverRuleset.Roleplay, ForeverFaction.Alliance, FakeForeverArmory.Member("Asha", "Brightvale"));

        var result = await _forever.SetGuildAsync(ServerA, "kael's chosen", ForeverRegion.EU, 1, "officer");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(ForeverLookupStatus.Found, result.Value!.Lookup);
        Assert.Equal(1, result.Value.Armory!.MemberCount);
        var guild = (await _forever.GetGuildAsync(ServerA))!;
        Assert.Equal(("Kael's Chosen", "kaels-chosen", ForeverRuleset.Roleplay, ForeverFaction.Alliance),
            (guild.Name, guild.ArmorySlug, guild.Ruleset, guild.Faction));
    }

    [Fact]
    public async Task A_guild_the_armory_does_not_have_cannot_be_set_and_leaves_the_old_one()
    {
        await SetGuild();

        var missing = await _forever.SetGuildAsync(ServerA, "Other Guild", ForeverRegion.EU, 1, "officer");
        Assert.False(missing.Ok);
        Assert.Contains("couldn't find **Other Guild** on the EU armory", missing.Error);

        _armory.AddGuild(ForeverRegion.EU, "Odd Guild", ruleset: null, faction: ForeverFaction.Horde);
        var unknown = await _forever.SetGuildAsync(ServerA, "Odd Guild", ForeverRegion.EU, 1, "officer");
        Assert.False(unknown.Ok);
        Assert.Contains("ruleset or faction I don't know yet", unknown.Error);

        _armory.Down = true;
        var down = await _forever.SetGuildAsync(ServerA, "Synthetic Guild", ForeverRegion.US, 1, "officer");
        Assert.False(down.Ok);
        Assert.Equal(ForeverService.ArmoryDownMessage, down.Error);

        Assert.False((await _forever.SetGuildAsync(ServerA, "bad\nname", ForeverRegion.EU, 2, "x")).Ok);
        var kept = (await _forever.GetGuildAsync(ServerA))!;
        Assert.Equal(("Synthetic Guild", "synthetic-guild", ForeverRegion.US), (kept.Name, kept.ArmorySlug, kept.Region));
    }

    [Fact]
    public async Task Roster_carries_the_armory_roster_and_survives_it_going_quiet()
    {
        await SetGuild(ServerA, FakeForeverArmory.Member("Asha", "Brightvale"), FakeForeverArmory.Member("Thalen", "Brightvale"));
        var asha = await Add(Alice, "Asha");
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok);

        var matched = (await _forever.GetRosterAsync(ServerA, _ => true)).Value!;
        Assert.Equal(2, matched.Armory!.Members.Count);
        Assert.False(matched.ArmoryMissed);
        Assert.Single(matched.Members);

        // The armory going quiet leaves what members put on the roster.
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

        var dupe = await _forever.AddCharacterAsync(Alice, ServerA, "ASHA", "brightvale");
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
        Assert.Empty(await db.ForeverMains.Where(m => m.UserId == Alice).ToListAsync());
        Assert.Equal(bobs.Id, await _forever.GetMainIdAsync(Bob, ServerA));
        Assert.Equal(2, await db.ForeverRosterShares.CountAsync(s => s.UserId == Bob));
        Assert.Equal(0, await db.ForeverRosterShares.CountAsync(s => s.UserId == Alice));
        Assert.Null(await _forever.GetCharacterAsync(Alice, asha.Id));
    }

    [Fact]
    public async Task Main_is_per_server_and_one_row_per_user()
    {
        await SetGuild();
        var asha = await Add(Alice, "Asha");
        var thalen = await Add(Alice, "Thalen");

        Assert.True((await _forever.SetMainAsync(Alice, ServerB, thalen.Id)).Ok);
        Assert.True((await _forever.SetMainAsync(Alice, ServerA, thalen.Id)).Ok);
        Assert.True((await _forever.SetMainAsync(Alice, ServerA, asha.Id)).Ok);

        Assert.Equal(asha.Id, await _forever.GetMainIdAsync(Alice, ServerA));
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
        Assert.False((await _forever.RefreshCharacterAsync(Bob, asha.Id)).Ok);
        Assert.False(await _forever.UnshareAsync(Bob, ServerA, asha.Id));
        Assert.Null(await _forever.RemoveCharacterAsync(Bob, asha.Id));
        Assert.Null(await _forever.GetCharacterAsync(Bob, asha.Id));
        Assert.Equal("Asha", (await _forever.GetCharacterAsync(Alice, asha.Id))!.FirstName);
    }

    [Fact]
    public async Task Share_needs_a_guild_and_a_character_that_fits_it()
    {
        var asha = await Add(Alice, "Asha", server: ServerB);
        Assert.Equal(ForeverService.NoGuildMessage, (await _forever.ShareAsync(Alice, ServerA, asha.Id)).Error);

        // Setting the guild is all it takes: there is no roster switch to turn on first.
        await SetGuild();
        Assert.True((await _forever.GetGuildAsync(ServerA))!.RosterEnabled);
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

        // A character that changed faction no longer fits the guild: it is hidden, not taken off.
        _armory.AddCharacter(ForeverRegion.US, "Asha", "Brightvale", ForeverClass.Priest, ForeverRuleset.PvP, ForeverFaction.Alliance);
        Assert.True((await _forever.RefreshCharacterAsync(Alice, asha.Id)).Ok);
        alice = Assert.Single((await _forever.GetRosterAsync(ServerA, id => id == Alice)).Value!.Members);
        Assert.Equal(new[] { thalen.Id }, alice.Characters.Select(c => c.Id));
        Assert.Contains(asha.Id, await _forever.GetSharedIdsAsync(Alice, ServerA));

        Assert.True(await _forever.UnshareAsync(Alice, ServerA, thalen.Id));
        Assert.Empty((await _forever.GetRosterAsync(ServerA, id => id == Alice)).Value!.Members);
    }

    [Fact]
    public async Task Unsetting_the_guild_hides_the_roster_and_keeps_characters()
    {
        await SetGuild();
        var asha = await Add(Alice, "Asha");
        Assert.True((await _forever.ShareAsync(Alice, ServerA, asha.Id)).Ok);

        Assert.True(await _forever.UnsetGuildAsync(ServerA));
        Assert.Equal(ForeverService.NoGuildMessage, (await _forever.GetRosterAsync(ServerA, _ => true)).Error);
        Assert.False(await _forever.UnsetGuildAsync(ServerA));
        Assert.NotNull(await _forever.GetCharacterAsync(Alice, asha.Id));

        // What was on the roster comes back with the guild.
        await SetGuild();
        Assert.Single((await _forever.GetRosterAsync(ServerA, _ => true)).Value!.Members);
    }

    [Fact]
    public async Task Setting_the_guild_again_updates_it_in_place()
    {
        await SetGuild();
        _armory.AddGuild(ForeverRegion.EU, "Renamed", ForeverRuleset.Normal, ForeverFaction.Alliance);

        var updated = await _forever.SetGuildAsync(ServerA, "Renamed", ForeverRegion.EU, 2, "other");

        Assert.True(updated.Ok, updated.Error);
        var guild = await _forever.GetGuildAsync(ServerA);
        Assert.Equal(("Renamed", ForeverRegion.EU, ForeverFaction.Alliance, 2L), (guild!.Name, guild.Region, guild.Faction, guild.SetById!.Value));
        await using var db = Db();
        Assert.Equal(1, await db.ForeverGuilds.CountAsync());
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
