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

// A guild saved before the bot read the armory has no armory link, and with it no roster. These cover
// the bot making that link itself, and the character boxes that depend on it. Everything is SYNTHETIC.
public sealed class ForeverGuildLinkTests : IAsyncLifetime
{
    private const long Server = 1001;
    private const long Alice = 501;
    private const long Bob = 502;
    private static readonly DateTime SetOn = new(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly ServiceProvider Ef = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeForeverArmory _armory = new();
    private readonly Clock _clock = new();
    private ServiceProvider _services = null!;
    private ForeverService _forever = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddDbContext<NinjaBotEntities>(o => o.UseSqlite(_connection).UseInternalServiceProvider(Ef))
            .BuildServiceProvider();
        await using (var db = Db()) await db.Database.EnsureCreatedAsync();
        _forever = new ForeverService(_services.GetRequiredService<IServiceScopeFactory>(), _armory, NullLogger<ForeverService>.Instance, _clock);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private NinjaBotEntities Db() => new(new DbContextOptionsBuilder<NinjaBotEntities>()
        .UseSqlite(_connection).UseInternalServiceProvider(Ef).Options);

    /// <summary>A guild as an officer typed it before the armory was read: their spelling, ruleset and faction, no link.</summary>
    private async Task SaveOldGuild(string name = "synthetic guild", ForeverRegion region = ForeverRegion.US)
    {
        await using var db = Db();
        db.ForeverGuilds.Add(new ForeverGuild
        {
            DiscordGuildId = Server, Name = name, Region = region,
            Ruleset = ForeverRuleset.Normal, Faction = ForeverFaction.Alliance,
            RosterEnabled = false, SetById = 7, SetByName = "officer", TimeSet = SetOn
        });
        await db.SaveChangesAsync();
    }

    private void PutGuildOnArmory(params ForeverArmoryMember[] members)
        => _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", ForeverRuleset.PvP, ForeverFaction.Horde, members);

    private async Task<ForeverGuild> Stored()
    {
        await using var db = Db();
        return await db.ForeverGuilds.AsNoTracking().SingleAsync(g => g.DiscordGuildId == Server);
    }

    private static ForeverArmoryMember[] Guildmates() => new[]
    {
        FakeForeverArmory.Member("Thalen", "Duskvale", level: 12, ForeverClass.Warrior, rank: 3),
        FakeForeverArmory.Member("Mira", "Dawn", level: 40, ForeverClass.Druid, rank: 1),
        FakeForeverArmory.Member("Low", "Bie", level: 9, ForeverClass.Hunter, rank: 5)
    };

    // ===== The link =====

    [Fact]
    public async Task An_old_guild_is_linked_to_the_armory_the_first_time_it_is_asked_for()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());

        var guild = (await _forever.GetGuildAsync(Server))!;

        // The armory's spelling, ruleset and faction, as setting it again would have given.
        Assert.Equal(("Synthetic Guild", "synthetic-guild", ForeverRuleset.PvP, ForeverFaction.Horde, true),
            (guild.Name, guild.ArmorySlug, guild.Ruleset, guild.Faction, guild.RosterEnabled));
        var stored = await Stored();
        Assert.Equal(("Synthetic Guild", "synthetic-guild", ForeverRuleset.PvP, ForeverFaction.Horde, true),
            (stored.Name, stored.ArmorySlug, stored.Ruleset, stored.Faction, stored.RosterEnabled));
        // Who set it and when don't change: the log watcher reads the date as when the guild was linked.
        Assert.Equal((7L, "officer", SetOn, ForeverRegion.US), (stored.SetById!.Value, stored.SetByName, stored.TimeSet!.Value, stored.Region));

        // Linked once; after that the armory isn't asked about the guild again.
        await _forever.GetGuildAsync(Server);
        Assert.Equal(1, _armory.GuildAsks);
    }

    [Fact]
    public async Task The_guild_as_stored_is_read_without_asking_the_armory()
    {
        await SaveOldGuild();
        PutGuildOnArmory();

        var saved = (await _forever.GetSavedGuildAsync(Server))!;

        Assert.Equal(("synthetic guild", (string)null), (saved.Name, saved.ArmorySlug));
        Assert.Equal(0, _armory.GuildAsks);
        Assert.Null((await Stored()).ArmorySlug);
    }

    [Fact]
    public async Task A_guild_the_armory_does_not_have_stays_as_typed_and_is_asked_about_again_much_later()
    {
        await SaveOldGuild();

        for (var i = 0; i < 3; i++)
        {
            var guild = (await _forever.GetGuildAsync(Server))!;
            Assert.Equal(("synthetic guild", (string)null, ForeverRuleset.Normal, ForeverFaction.Alliance),
                (guild.Name, guild.ArmorySlug, guild.Ruleset, guild.Faction));
        }
        Assert.Equal(1, _armory.GuildAsks);

        // It shows up on the armory later; just under six hours on, the bot still hasn't looked again.
        PutGuildOnArmory();
        _clock.Now += TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1);
        Assert.Null((await _forever.GetGuildAsync(Server))!.ArmorySlug);
        Assert.Equal(1, _armory.GuildAsks);

        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal("synthetic-guild", (await _forever.GetGuildAsync(Server))!.ArmorySlug);
        Assert.Equal(2, _armory.GuildAsks);
    }

    [Fact]
    public async Task An_armory_that_is_down_is_tried_again_after_five_minutes_not_on_every_command()
    {
        await SaveOldGuild();
        PutGuildOnArmory();
        _armory.Down = true;

        Assert.Null((await _forever.GetGuildAsync(Server))!.ArmorySlug);
        Assert.Null((await _forever.GetGuildAsync(Server))!.ArmorySlug);
        Assert.Equal(1, _armory.GuildAsks);

        _armory.Down = false;
        _clock.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        Assert.Null((await _forever.GetGuildAsync(Server))!.ArmorySlug);
        Assert.Equal(1, _armory.GuildAsks);

        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal("synthetic-guild", (await _forever.GetGuildAsync(Server))!.ArmorySlug);
    }

    [Fact]
    public async Task An_armory_that_throws_leaves_the_guild_as_it_was()
    {
        await SaveOldGuild();
        PutGuildOnArmory();
        _armory.WhileFindingGuild = () => throw new InvalidOperationException("synthetic failure");

        var guild = (await _forever.GetGuildAsync(Server))!;

        Assert.Equal(("synthetic guild", (string)null), (guild.Name, guild.ArmorySlug));
        // Left alone for five minutes, like an armory that is down.
        _armory.WhileFindingGuild = null;
        Assert.Null((await _forever.GetGuildAsync(Server))!.ArmorySlug);
        _clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal("synthetic-guild", (await _forever.GetGuildAsync(Server))!.ArmorySlug);
    }

    [Fact]
    public async Task A_guild_with_a_ruleset_the_bot_does_not_know_is_not_linked()
    {
        await SaveOldGuild();
        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", ruleset: null, faction: ForeverFaction.Horde);

        var guild = (await _forever.GetGuildAsync(Server))!;

        Assert.Equal(((string)null, ForeverRuleset.Normal, ForeverFaction.Alliance), (guild.ArmorySlug, guild.Ruleset, guild.Faction));
    }

    [Fact]
    public async Task A_guild_under_another_name_at_that_address_is_not_linked()
    {
        // The armory answers the slug with a guild whose own name gives a different slug: not this guild.
        await SaveOldGuild("Synthetic Guild");
        var other = new ForeverArmoryGuild(1, "Another Crew", "synthetic-guild", ForeverRegion.US, ForeverFaction.Horde, ForeverRuleset.PvP, "PvP", 3);
        var armory = new StubGuildArmory(other);
        var forever = new ForeverService(_services.GetRequiredService<IServiceScopeFactory>(), armory, NullLogger<ForeverService>.Instance, _clock);

        var guild = (await forever.GetGuildAsync(Server))!;

        Assert.Equal(("Synthetic Guild", (string)null, ForeverFaction.Alliance), (guild.Name, guild.ArmorySlug, guild.Faction));
    }

    [Fact]
    public async Task A_guild_unset_while_the_armory_was_answering_stays_unset()
    {
        await SaveOldGuild();
        PutGuildOnArmory();
        _armory.WhileFindingGuild = async () => Assert.True(await _forever.UnsetGuildAsync(Server));

        Assert.Null(await _forever.GetGuildAsync(Server));

        await using var db = Db();
        Assert.Empty(await db.ForeverGuilds.ToListAsync());
    }

    [Theory]
    [InlineData("Other Crew", ForeverRegion.US)]
    [InlineData("synthetic guild", ForeverRegion.EU)]
    public async Task A_guild_changed_while_the_armory_was_answering_keeps_the_change(string newName, ForeverRegion newRegion)
    {
        await SaveOldGuild();
        PutGuildOnArmory();
        _armory.WhileFindingGuild = async () =>
        {
            await using var db = Db();
            var row = await db.ForeverGuilds.SingleAsync();
            row.Name = newName;
            row.Region = newRegion;
            await db.SaveChangesAsync();
        };

        var guild = (await _forever.GetGuildAsync(Server))!;

        // The answer was about the guild as it was, so it isn't written over what is there now.
        Assert.Equal((newName, newRegion, (string)null, ForeverFaction.Alliance), (guild.Name, guild.Region, guild.ArmorySlug, guild.Faction));
        var stored = await Stored();
        Assert.Equal((newName, newRegion, (string)null, ForeverFaction.Alliance), (stored.Name, stored.Region, stored.ArmorySlug, stored.Faction));
    }

    [Fact]
    public async Task An_old_guild_gets_its_armory_roster_and_can_save_a_young_guildmate()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());

        var roster = await _forever.GetRosterAsync(Server, _ => true);
        Assert.Equal(3, roster.Value!.Armory!.Members.Count);

        // Below level 10 the armory has no character, so only the roster can vouch for this one.
        var young = await _forever.AddCharacterAsync(Alice, Server, "Low", "Bie");
        Assert.True(young.Ok, young.Error);
        Assert.Equal(ForeverClass.Hunter, young.Value!.Character.Class);
    }

    [Fact]
    public async Task The_armory_roster_of_a_guild_that_cannot_be_linked_is_not_found()
    {
        await SaveOldGuild();

        var roster = await _forever.GetArmoryRosterAsync((await _forever.GetSavedGuildAsync(Server))!);

        Assert.Equal(ForeverLookupStatus.NotFound, roster.Status);
    }

    // ===== The character boxes =====

    private Task<string[]> AddBox(string input, ForeverRegion? region = null, TimeSpan? budget = null, long user = Alice)
        => Names(ForeverAddAutocomplete.SuggestAsync(_forever, _armory, Server, user, input, region, budget ?? Plenty));

    private Task<string[]> ViewBox(string input, ForeverRegion? region = null, TimeSpan? budget = null)
        => Names(ForeverLookupAutocomplete.SuggestAsync(_forever, _armory, Server, Alice, input, region, _ => true, budget ?? Plenty));

    private static async Task<string[]> Names(Task<System.Collections.Generic.List<Discord.AutocompleteResult>> suggestions)
        => (await suggestions).Select(s => s.Name).ToArray();

    [Fact]
    public async Task Saving_a_character_offers_guildmates_before_anything_is_typed_even_for_an_old_guild()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());

        Assert.Equal(
            new[] { "Mira Dawn · 40 Druid · guild roster", "Thalen Duskvale · 12 Warrior · guild roster", "Low Bie · 9 Hunter · guild roster" },
            await AddBox(""));
        // The box linked the guild on its way to the roster.
        Assert.Equal("synthetic-guild", (await Stored()).ArmorySlug);

        Assert.Equal(new[] { "Thalen Duskvale · 12 Warrior · guild roster" }, await AddBox("thal"));
        // A full name that isn't finished yet: the guildmate it is the start of, then the name as typed.
        Assert.Equal(new[] { "Mira Dawn · 40 Druid · guild roster", "Save Mira Da" }, await AddBox("mira da"));
    }

    [Fact]
    public async Task Saving_a_character_says_the_roster_is_still_loading_and_has_it_on_the_next_keystroke()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());
        var held = new TaskCompletionSource();
        _armory.RosterHeld = held.Task;

        Assert.Equal(new[] { ForeverSuggest.RosterLoadingHint }, await AddBox("", budget: TimeSpan.FromMilliseconds(50)));
        Assert.Equal(new[] { "Save Mira Dawn", ForeverSuggest.RosterLoadingHint }, await AddBox("mira dawn", budget: TimeSpan.FromMilliseconds(50)));

        held.SetResult();
        Assert.Equal(3, (await AddBox("")).Length);
    }

    [Fact]
    public async Task Saving_a_character_in_another_region_or_with_no_guild_offers_no_guildmates()
    {
        // No guild at all: only the hint, and nothing is said to be loading.
        Assert.Equal(new[] { ForeverSuggest.LookupHint }, await AddBox(""));

        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());
        // The roster is a US guild's, so it says nothing about an EU character.
        Assert.Equal(new[] { ForeverSuggest.LookupHint }, await AddBox("", ForeverRegion.EU));
        Assert.Equal(new[] { "Save Mira Dawn" }, await AddBox("mira dawn", ForeverRegion.EU));
        Assert.Equal(0, _armory.GuildAsks);
    }

    [Fact]
    public async Task Saving_a_character_leaves_out_guildmates_the_caller_already_saved()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());
        _armory.AddCharacter(ForeverRegion.US, "Mira", "Dawn", ForeverClass.Druid, ForeverRuleset.PvP, ForeverFaction.Horde);
        Assert.True((await _forever.AddCharacterAsync(Alice, Server, "Mira", "Dawn")).Ok);

        Assert.Equal(new[] { "Thalen Duskvale · 12 Warrior · guild roster", "Low Bie · 9 Hunter · guild roster" }, await AddBox(""));
        // Someone else hasn't saved her, so they are still offered her.
        Assert.Equal(3, (await AddBox("", user: Bob)).Length);
    }

    [Fact]
    public async Task Looking_up_a_character_offers_guildmates_for_an_old_guild_and_says_when_they_are_loading()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());
        var held = new TaskCompletionSource();
        _armory.RosterHeld = held.Task;

        Assert.Equal(new[] { ForeverSuggest.RosterLoadingHint }, await ViewBox("", budget: TimeSpan.FromMilliseconds(50)));

        held.SetResult();
        // The armory has nobody below level 10 to show, so the young guildmate isn't offered here.
        Assert.Equal(new[] { "Mira Dawn · 40 Druid · guild roster", "Thalen Duskvale · 12 Warrior · guild roster" }, await ViewBox(""));
        Assert.Equal(new[] { "Mira Dawn · 40 Druid · guild roster", "Look up Mira Da on the armory" }, await ViewBox("mira da"));
        Assert.Equal(new[] { ForeverSuggest.LookupHint }, await ViewBox("", ForeverRegion.EU));
    }

    [Fact]
    public async Task Looking_up_a_character_in_another_region_does_not_wait_on_the_guild()
    {
        await SaveOldGuild();
        PutGuildOnArmory(Guildmates());

        Assert.Equal(new[] { "Look up Mira Dawn on the armory" }, await ViewBox("mira dawn", ForeverRegion.EU));
        // The guild is only read for its region here, so the armory isn't asked to match it.
        Assert.Equal(0, _armory.GuildAsks);
    }

    /// <summary>An armory that answers every guild lookup with one guild, whatever was asked for.</summary>
    private sealed class StubGuildArmory(ForeverArmoryGuild guild) : IForeverArmory
    {
        public Task<ForeverLookup<ForeverArmoryGuild>> GetGuildAsync(ForeverRegion region, string guildName, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(ForeverLookup<ForeverArmoryGuild>.Found(guild));
        public Task<ForeverLookup<ForeverArmoryCharacter>> GetCharacterAsync(ForeverRegion region, string firstName, string surname, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(ForeverLookup<ForeverArmoryCharacter>.NotFound());
        public Task<ForeverLookup<ForeverArmoryCard>> GetCardAsync(ForeverRegion region, string firstName, string surname, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(ForeverLookup<ForeverArmoryCard>.NotFound());
        public Task<ForeverLookup<ForeverArmoryRoster>> GetRosterAsync(ForeverRegion region, string guildSlug, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(ForeverLookup<ForeverArmoryRoster>.NotFound());
        public Task<ForeverLookup<ForeverArmoryProfessions>> GetProfessionsAsync(ForeverRegion region, string characterSlug, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(ForeverLookup<ForeverArmoryProfessions>.NotFound());
    }
}
