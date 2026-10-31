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
    private ServiceProvider _services = null!;
    private ForeverService _forever = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddDbContext<NinjaBotEntities>(o => o.UseSqlite(_connection).UseInternalServiceProvider(Ef))
            .BuildServiceProvider();
        await using (var db = Db()) await db.Database.EnsureCreatedAsync();
        _forever = new ForeverService(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ForeverService>.Instance);
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
        return result.Value!;
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
        var result = await _forever.AddCharacterAsync(Alice, ServerA, new ForeverCharacterInput("Asha", "Brightvale", ForeverRegion.EU));

        Assert.False(result.Ok);
        Assert.Contains("ruleset, faction", result.Error);
        Assert.DoesNotContain("region", result.Error);
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
        Assert.Equal(asha.Id, renamed.Value!.Id);
        Assert.Equal("Ashara Brightvale", renamed.Value.FullName);
        Assert.Equal(ForeverClass.Mage, renamed.Value.Class);
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
