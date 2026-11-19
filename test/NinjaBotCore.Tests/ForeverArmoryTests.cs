using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests;

// The JSON here follows the shape of real profile-forever responses, cut down to what the bot
// reads. Every character and guild is SYNTHETIC.
public sealed class ForeverArmoryTests : IDisposable
{
    private const string Profile = """
        {
          "id": 4242, "name": "Asha", "surname": "Brightvale", "name_search": "Asha Brightvale",
          "level": 29, "experience": 4674, "achievement_points": 0,
          "average_item_level": 20, "equipped_item_level": 19,
          "last_login_timestamp": 1794280327000,
          "character_class": { "id": 8, "name": "Mage", "key": { "href": "https://us.api.blizzard.com/data/wow/playable-class/8" } },
          "race": { "id": 5, "name": "Undead" },
          "faction": { "type": "HORDE", "name": "Horde" },
          "gender": { "type": "MALE", "name": "Male" },
          "ruleset": { "id": 2, "name": "Normal", "key": { "href": "https://us.api.blizzard.com/data/wow/ruleset/2" } },
          "guild": { "id": 77, "name": "Synthetic Guild", "slug": "synthetic-guild", "faction": { "type": "HORDE", "name": "Horde" } },
          "equipment": { "href": "https://us.api.blizzard.com/profile/wow/character/asha-brightvale/equipment" }
        }
        """;

    private const string Equipment = """
        {
          "equipped_items": [
            { "slot": { "type": "MAIN_HAND", "name": "Main Hand" }, "name": "Executor Staff", "quality": { "type": "COMMON", "name": "Common" }, "level": { "value": 5, "display_string": "Item Level 5" }, "item": { "id": 3277 } },
            { "slot": { "type": "CHEST", "name": "Chest" }, "name": "White Linen Robe", "quality": { "type": "UNCOMMON", "name": "Uncommon" }, "level": { "value": 10 }, "item": { "id": 6241 } },
            { "slot": { "type": "SHIRT", "name": "Shirt" }, "quality": { "type": "COMMON" } }
          ]
        }
        """;

    private const string Media = """
        {
          "assets": [
            { "key": "main-raw", "value": "https://render.worldofwarcraft.com/forever-us/character/64/4242-main-raw.png" },
            { "key": "avatar", "value": "https://render.worldofwarcraft.com/forever-us/character/64/4242-avatar.jpg" }
          ]
        }
        """;

    private const string Professions = """
        {
          "primaries": [
            { "profession": { "id": 197, "name": "Tailoring" }, "tiers": [ { "tier": { "name": "Tailoring" }, "skill_points": 107, "max_skill_points": 150 } ] },
            { "profession": { "id": 333, "name": "Enchanting" }, "tiers": [
                { "tier": { "name": "Apprentice" }, "skill_points": 75, "max_skill_points": 75 },
                { "tier": { "name": "Expert" }, "skill_points": 150, "max_skill_points": 225 } ] }
          ],
          "secondaries": [
            { "profession": { "id": 185, "name": "Cooking" }, "skill_points": 20, "max_skill_points": 75 }
          ]
        }
        """;

    private const string Talents = """
        {
          "specialization_groups": [
            { "is_active": false, "specializations": [ { "specialization_name": "Arcane", "spent_points": 31 } ] },
            { "is_active": true, "specializations": [
                { "specialization_name": "Fire", "spent_points": 5 },
                { "specialization_name": "Frost", "spent_points": 15 },
                { "specialization_name": "Arcane", "spent_points": 0 } ] }
          ]
        }
        """;

    private const string Statistics = """
        {
          "categories": [
            { "id": 122, "name": "Deaths", "statistics": [ { "id": 60, "name": "Total deaths", "quantity": 2.0 } ] },
            { "id": 128, "name": "Kills", "statistics": [ { "id": 1197, "name": "Total kills", "quantity": 2351.0 } ],
              "sub_categories": [ { "id": 135, "name": "Creatures", "statistics": [ { "id": 107, "name": "Creatures killed", "quantity": 2320.0 } ] } ] },
            { "id": 14807, "name": "Dungeons & Raids", "statistics": [ { "id": 932, "name": "Total 5-player dungeons entered", "quantity": 5.0 } ],
              "sub_categories": [ { "id": 133, "name": "Quests", "statistics": [ { "id": 98, "name": "Quests completed", "quantity": 241.0 } ] } ] }
          ]
        }
        """;

    private const string Guild = """
        {
          "id": 228203, "name": "Kael's Chosen", "name_search": "Kael's Chosen", "slug": "kaels-chosen",
          "faction": { "type": "ALLIANCE", "name": "Alliance" },
          "member_count": 26, "created_timestamp": 1793844747000,
          "ruleset": { "id": 15, "name": "Roleplay PvP" }
        }
        """;

    private const string Roster = """
        {
          "guild": { "name": "Kael's Chosen", "slug": "kaels-chosen" },
          "members": [
            { "rank": 0, "character": { "id": 1, "name": "Asha", "surname": "Brightvale", "slug": "asha-brightvale", "level": 29,
                "faction": { "type": "ALLIANCE" }, "playable_class": { "id": 2 }, "playable_race": { "id": 3 } } },
            { "rank": 5, "character": { "id": 2, "name": "Thalen", "surname": "Duskvale", "level": 7, "playable_class": { "id": 99 } } },
            { "character": { "id": 3, "name": "Mira", "surname": "Dawn", "slug": "mira-dawn" } },
            { "rank": 2, "character": { "id": 4 } }
          ]
        }
        """;

    private readonly Dictionary<string, WowApiResponse> _routes = new();
    private readonly List<(string Url, string Region)> _requests = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly ForeverArmory _armory;
    private Exception _failure;
    private TaskCompletionSource _gate;

    public ForeverArmoryTests()
    {
        var api = new Mock<IWowApi>();
        api.Setup(a => a.TryGetAPIRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string url, string region, CancellationToken _) =>
            {
                lock (_requests) _requests.Add((url, region));
                if (_failure != null) throw _failure;
                // A closed gate is Blizzard taking its time.
                if (_gate != null) await _gate.Task;
                var path = url[..url.IndexOf('?')];
                return _routes.TryGetValue(path, out var response) ? response : new WowApiResponse(HttpStatusCode.NotFound, string.Empty);
            });
        _armory = new ForeverArmory(api.Object, _cache, NullLogger<ForeverArmory>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private void Serve(string path, string body) => _routes[path] = new WowApiResponse(HttpStatusCode.OK, body);

    private const string AshaPath = "/profile/wow/character/asha-brightvale";

    private void ServeWholeCard()
    {
        Serve(AshaPath, Profile);
        Serve(AshaPath + "/equipment", Equipment);
        Serve(AshaPath + "/character-media", Media);
        Serve(AshaPath + "/professions", Professions);
        Serve(AshaPath + "/specializations", Talents);
        Serve(AshaPath + "/achievements/statistics", Statistics);
    }

    [Fact]
    public async Task Character_is_asked_for_by_realmless_slug_in_its_regions_forever_namespace()
    {
        Serve(AshaPath, Profile);

        var result = await _armory.GetCharacterAsync(ForeverRegion.EU, " ASHA ", "brightvale");

        Assert.Equal(ForeverLookupStatus.Found, result.Status);
        Assert.Equal(($"{AshaPath}?namespace=profile-forever-eu&locale=en_GB", "eu"), Assert.Single(_requests));
        var c = result.Value!;
        Assert.Equal(("Asha", "Brightvale", "asha-brightvale", ForeverRegion.EU), (c.FirstName, c.Surname, c.Slug, c.Region));
        Assert.Equal((29, "Undead", (ForeverClass?)ForeverClass.Mage, "Mage"), (c.Level, c.Race, c.Class, c.ClassName));
        Assert.Equal(((ForeverFaction?)ForeverFaction.Horde, (ForeverRuleset?)ForeverRuleset.Normal), (c.Faction, c.Ruleset));
        Assert.Equal(("Synthetic Guild", "synthetic-guild", (int?)19), (c.GuildName, c.GuildSlug, c.ItemLevel));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794280327000), c.LastSeen);
    }

    [Theory]
    [InlineData(ForeverRegion.US, "us", "en_US")]
    [InlineData(ForeverRegion.KR, "kr", "en_US")]
    [InlineData(ForeverRegion.TW, "tw", "en_US")]
    public async Task Each_region_uses_its_own_host_and_namespace(ForeverRegion region, string host, string locale)
    {
        await _armory.GetCharacterAsync(region, "Asha", "Brightvale");

        Assert.Equal(($"{AshaPath}?namespace=profile-forever-{host}&locale={locale}", host), Assert.Single(_requests));
    }

    [Fact]
    public async Task Names_outside_ascii_are_escaped_in_the_path_and_apostrophes_dropped()
    {
        await _armory.GetCharacterAsync(ForeverRegion.EU, "Élan", "O'Dawn");

        Assert.StartsWith("/profile/wow/character/%C3%A9lan-odawn?", Assert.Single(_requests).Url);
    }

    [Fact]
    public async Task A_character_the_armory_does_not_have_is_not_found_not_an_error()
    {
        var result = await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale");

        Assert.Equal(ForeverLookupStatus.NotFound, result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Any_other_status_is_unavailable(HttpStatusCode status)
    {
        _routes[AshaPath] = new WowApiResponse(status, string.Empty);

        Assert.Equal(ForeverLookupStatus.Unavailable, (await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale")).Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "name": "", "surname": "Brightvale" }""")]
    public async Task A_reply_the_bot_cannot_read_is_unavailable(string body)
    {
        Serve(AshaPath, body);

        Assert.Equal(ForeverLookupStatus.Unavailable, (await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale")).Status);
    }

    [Fact]
    public async Task A_failed_request_is_unavailable_but_a_cancelled_caller_still_cancels()
    {
        _failure = new HttpRequestException("connection refused");
        Assert.Equal(ForeverLookupStatus.Unavailable, (await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale")).Status);

        _failure = new InvalidOperationException("Blizzard API access token has not been initialized.");
        Assert.Equal(ForeverLookupStatus.Unavailable, (await _armory.GetGuildAsync(ForeverRegion.US, "Synthetic Guild")).Status);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _failure = new OperationCanceledException(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _armory.GetCharacterAsync(ForeverRegion.EU, "Asha", "Brightvale", cancelled.Token));
    }

    [Fact]
    public async Task Values_the_bot_does_not_know_are_null_and_keep_blizzards_name()
    {
        Serve(AshaPath, """
            {
              "name": "Asha", "level": 60,
              "character_class": { "id": 6, "name": "Death Knight" },
              "faction": { "type": "NEUTRAL" },
              "ruleset": { "id": 20, "name": "Hardcore" },
              "average_item_level": 55
            }
            """);

        var c = (await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale")).Value!;

        Assert.Equal((null, "Death Knight", null, null, "Hardcore"), (c.Class, c.ClassName, c.Faction, c.Ruleset, c.RulesetName));
        Assert.Equal((string.Empty, (string)null, (int?)55, (DateTimeOffset?)null), (c.Surname, c.GuildName, c.ItemLevel, c.LastSeen));
    }

    [Fact]
    public async Task Names_sent_as_a_map_per_locale_are_read_in_english()
    {
        Serve(AshaPath, """
            {
              "name": "Asha", "surname": "Brightvale", "level": 12,
              "race": { "id": 5, "name": { "de_DE": "Untoter", "en_US": "Undead" } },
              "character_class": { "id": 8, "name": { "fr_FR": "Mage" } }
            }
            """);

        var c = (await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale")).Value!;

        Assert.Equal(("Undead", "Mage"), (c.Race, c.ClassName));
    }

    [Fact]
    public async Task Card_gathers_gear_portrait_professions_talents_and_statistics()
    {
        ServeWholeCard();

        var card = (await _armory.GetCardAsync(ForeverRegion.US, "Asha", "Brightvale")).Value!;

        Assert.Equal("Asha Brightvale", card.Character.FullName);
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/64/4242-avatar.jpg", card.AvatarUrl);
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/64/4242-main-raw.png", card.RenderUrl);
        Assert.Equal(new[] { ("MAIN_HAND", "Main Hand", "Executor Staff", "COMMON", (int?)5), ("CHEST", "Chest", "White Linen Robe", "UNCOMMON", (int?)10) },
            card.Gear.Select(i => (i.Slot, i.SlotName, i.Name, i.Quality, i.ItemLevel)));
        Assert.Equal(new[] { ("Tailoring", 107, 150), ("Enchanting", 150, 225), ("Cooking", 20, 75) },
            card.Professions.Select(p => (p.Name, p.Skill, p.MaxSkill)));
        Assert.Equal(new[] { ("Frost", 15), ("Fire", 5) }, card.Talents.Select(t => (t.Name, t.Points)));
        Assert.Equal(new ForeverArmoryStats(Deaths: 2, Quests: 241, Kills: 2351, Dungeons: 5), card.Stats);
        Assert.Equal(6, _requests.Count);
        Assert.All(_requests, r => Assert.EndsWith("?namespace=profile-forever-us&locale=en_US", r.Url));
    }

    [Fact]
    public async Task Card_still_shows_when_its_details_are_missing_broken_or_unsafe()
    {
        Serve(AshaPath, Profile);
        Serve(AshaPath + "/equipment", """{ "equipped_items": "soon" }""");
        Serve(AshaPath + "/character-media", """{ "assets": [ { "key": "avatar", "value": "http://render.worldofwarcraft.com/avatar.jpg" } ] }""");
        _routes[AshaPath + "/professions"] = new WowApiResponse(HttpStatusCode.InternalServerError, string.Empty);
        Serve(AshaPath + "/achievements/statistics", """{ "categories": [ { "statistics": [ { "id": 60, "quantity": "many" } ] } ] }""");

        var result = await _armory.GetCardAsync(ForeverRegion.US, "Asha", "Brightvale");

        Assert.Equal(ForeverLookupStatus.Found, result.Status);
        var card = result.Value!;
        Assert.Equal(29, card.Character.Level);
        Assert.Null(card.AvatarUrl);
        Assert.Null(card.RenderUrl);
        Assert.Empty(card.Gear);
        Assert.Empty(card.Professions);
        Assert.Empty(card.Talents);
        Assert.Null(card.Stats);
    }

    [Fact]
    public async Task Card_for_a_missing_character_asks_only_for_the_profile()
    {
        var result = await _armory.GetCardAsync(ForeverRegion.US, "Asha", "Brightvale");

        Assert.Equal(ForeverLookupStatus.NotFound, result.Status);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Answers_are_remembered_but_an_outage_is_asked_about_again()
    {
        ServeWholeCard();
        await _armory.GetCardAsync(ForeverRegion.US, "Asha", "Brightvale");
        await _armory.GetCardAsync(ForeverRegion.US, "asha", "BRIGHTVALE");
        await _armory.GetCharacterAsync(ForeverRegion.US, "Asha", "Brightvale");
        Assert.Equal(6, _requests.Count);

        // Another region is another character.
        await _armory.GetCharacterAsync(ForeverRegion.EU, "Asha", "Brightvale");
        await _armory.GetCharacterAsync(ForeverRegion.EU, "Asha", "Brightvale");
        Assert.Equal(7, _requests.Count);

        _routes["/profile/wow/character/thalen-duskvale"] = new WowApiResponse(HttpStatusCode.ServiceUnavailable, string.Empty);
        await _armory.GetCharacterAsync(ForeverRegion.US, "Thalen", "Duskvale");
        await _armory.GetCharacterAsync(ForeverRegion.US, "Thalen", "Duskvale");
        Assert.Equal(9, _requests.Count);
    }

    [Fact]
    public async Task Guild_is_found_by_its_name_as_a_slug()
    {
        Serve("/data/wow/guild/kaels-chosen", Guild);

        var result = await _armory.GetGuildAsync(ForeverRegion.EU, "  Kael's  Chosen ");

        Assert.Equal(("/data/wow/guild/kaels-chosen?namespace=profile-forever-eu&locale=en_GB", "eu"), Assert.Single(_requests));
        var guild = result.Value!;
        Assert.Equal(("Kael's Chosen", "kaels-chosen", ForeverRegion.EU, 26), (guild.Name, guild.Slug, guild.Region, guild.MemberCount));
        Assert.Equal(((ForeverFaction?)ForeverFaction.Alliance, (ForeverRuleset?)ForeverRuleset.RoleplayPvP), (guild.Faction, guild.Ruleset));

        Assert.Equal(ForeverLookupStatus.NotFound, (await _armory.GetGuildAsync(ForeverRegion.EU, "Nobody Here")).Status);
        Assert.Equal(ForeverLookupStatus.NotFound, (await _armory.GetGuildAsync(ForeverRegion.EU, "'")).Status);
    }

    [Fact]
    public async Task Guild_answers_are_remembered_because_autocomplete_asks_on_every_pause_in_typing()
    {
        Serve("/data/wow/guild/kaels-chosen", Guild);

        await _armory.GetGuildAsync(ForeverRegion.EU, "Kael's Chosen");
        await _armory.GetGuildAsync(ForeverRegion.EU, "kaels chosen");
        await _armory.GetGuildAsync(ForeverRegion.EU, "Nobody Here");
        await _armory.GetGuildAsync(ForeverRegion.EU, "Nobody Here");
        Assert.Equal(2, _requests.Count);

        // The same name in another region is another guild.
        await _armory.GetGuildAsync(ForeverRegion.US, "Kael's Chosen");
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public async Task The_same_question_asked_twice_at_once_is_one_request()
    {
        Serve("/data/wow/guild/kaels-chosen/roster", Roster);
        _gate = new TaskCompletionSource();

        var first = _armory.GetRosterAsync(ForeverRegion.EU, "kaels-chosen");
        var second = _armory.GetRosterAsync(ForeverRegion.EU, "kaels-chosen");
        var other = _armory.GetRosterAsync(ForeverRegion.US, "kaels-chosen");
        Assert.False(first.IsCompleted);
        Assert.Equal(2, _requests.Count);

        _gate.SetResult();
        Assert.Same(await first, await second);
        Assert.Equal(3, (await first).Value!.Members.Count);
        Assert.Equal(ForeverLookupStatus.Found, (await other).Status);

        // Nothing is left waiting: an outage that wasn't remembered is asked about again.
        _gate = null;
        _routes["/data/wow/guild/down/roster"] = new WowApiResponse(HttpStatusCode.ServiceUnavailable, string.Empty);
        await _armory.GetRosterAsync(ForeverRegion.EU, "down");
        await _armory.GetRosterAsync(ForeverRegion.EU, "down");
        Assert.Equal(4, _requests.Count);
    }

    [Fact]
    public async Task Roster_keeps_every_named_member_and_is_remembered()
    {
        Serve("/data/wow/guild/kaels-chosen/roster", Roster);

        var roster = (await _armory.GetRosterAsync(ForeverRegion.EU, "kaels-chosen")).Value!;
        await _armory.GetRosterAsync(ForeverRegion.EU, "kaels-chosen");

        Assert.Equal("/data/wow/guild/kaels-chosen/roster?namespace=profile-forever-eu&locale=en_GB", Assert.Single(_requests).Url);
        Assert.Equal(new[]
        {
            ("Asha Brightvale", "asha-brightvale", 29, (ForeverClass?)ForeverClass.Paladin, (int?)0),
            ("Thalen Duskvale", "thalen-duskvale", 7, null, 5),
            ("Mira Dawn", "mira-dawn", 0, null, null)
        }, roster.Members.Select(m => (m.FullName, m.Slug, m.Level, m.Class, m.Rank)));

        Serve("/data/wow/guild/empty/roster", """{ "guild": { "name": "Empty" } }""");
        Assert.Equal(ForeverLookupStatus.Unavailable, (await _armory.GetRosterAsync(ForeverRegion.EU, "empty")).Status);
    }

    [Theory]
    [InlineData(1, ForeverRuleset.PvP)]
    [InlineData(2, ForeverRuleset.Normal)]
    [InlineData(3, ForeverRuleset.Roleplay)]
    [InlineData(15, ForeverRuleset.RoleplayPvP)]
    public void Blizzards_ruleset_ids_map_to_the_bots_rulesets(int id, ForeverRuleset expected)
        => Assert.Equal(expected, ForeverArmoryIds.Ruleset(id));

    [Fact]
    public void Every_bot_class_has_exactly_one_blizzard_id()
    {
        var mapped = Enumerable.Range(0, 20).Select(id => ForeverArmoryIds.Class(id)).Where(c => c != null).Select(c => c!.Value).ToList();

        Assert.Equal(Enum.GetValues<ForeverClass>().OrderBy(c => c), mapped.OrderBy(c => c));
        Assert.Null(ForeverArmoryIds.Ruleset(null));
        Assert.Null(ForeverArmoryIds.Ruleset(4));
        Assert.Equal(ForeverFaction.Horde, ForeverArmoryIds.Faction("horde"));
    }
}
