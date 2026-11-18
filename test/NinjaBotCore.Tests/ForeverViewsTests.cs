using System;
using System.Collections.Generic;
using System.Linq;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests;

// What /forever shows once Blizzard's armory is involved. Every guild, user and character is SYNTHETIC.
public class ForeverViewsTests
{
    private const long Alice = 501;
    private const long Bob = 502;

    private static readonly ForeverGuild MatchedGuild = new()
    {
        Name = "Synthetic Guild", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde,
        RosterEnabled = true, ArmorySlug = "synthetic-guild"
    };

    private static ForeverCharacter Shared(long id, long user, string first, string last = "Brightvale", ForeverClass? characterClass = ForeverClass.Priest) => new()
    {
        Id = id, UserId = user, FirstName = first, LastName = last,
        NormalizedFirstName = ForeverNames.Normalize(first), NormalizedLastName = ForeverNames.Normalize(last),
        Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, Class = characterClass
    };

    private static ForeverArmoryCharacter Armory(string first = "Asha", string last = "Brightvale", string guild = "Synthetic Guild") => new(
        4242, first, last, ForeverNames.CharacterSlug(first, last), ForeverRegion.US, 29, "Undead", ForeverClass.Mage, "Mage",
        ForeverFaction.Horde, ForeverRuleset.Normal, "Normal", guild, guild == null ? null : "synthetic-guild", 19,
        DateTimeOffset.FromUnixTimeSeconds(1794280327));

    private static ForeverArmoryCard Card(ForeverArmoryCharacter character = null) => new(
        character ?? Armory(),
        "https://render.worldofwarcraft.com/forever-us/character/64/4242-avatar.jpg",
        new[]
        {
            new ForeverArmoryItem("MAIN_HAND", "Main Hand", "Executor Staff", "COMMON", 5),
            new ForeverArmoryItem("ODD_SLOT", "Odd", "Mystery *Item*", null, null),
            new ForeverArmoryItem("HEAD", "Head", "Linen Hood", "UNCOMMON", 12)
        },
        new[] { new ForeverArmoryProfession("Tailoring", 107, 150), new ForeverArmoryProfession("Fishing", 12, 0) },
        new[] { new ForeverArmoryTalentTree("Frost", 15), new ForeverArmoryTalentTree("Fire", 5) },
        new ForeverArmoryStats(Deaths: 2, Quests: 1241, Kills: null, Dungeons: 5));

    // AppendLine writes \r\n on Windows, where AppVeyor runs these.
    private static string[] Lines(string description)
        => description.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static List<ButtonComponent> Buttons(MessageComponent component)
        => component.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();

    [Fact]
    public void Armory_roster_lists_blizzards_members_by_rank_then_level_with_who_shared_them()
    {
        var roster = new ForeverRoster(MatchedGuild,
            new[]
            {
                new ForeverRosterMember(Alice, new[] { Shared(1, Alice, "asha"), Shared(2, Alice, "Private", "Alt", ForeverClass.Rogue) }, MainId: 1),
                new ForeverRosterMember(Bob, new[] { Shared(3, Bob, "ASHA") }, MainId: null)
            },
            new ForeverArmoryRoster(new[]
            {
                FakeForeverArmory.Member("Thalen", "Duskvale", level: 12, ForeverClass.Warrior, rank: 3),
                FakeForeverArmory.Member("Mira", "Dawn", level: 40, ForeverClass.Druid, rank: 3),
                FakeForeverArmory.Member("Asha", "Brightvale", level: 29, ForeverClass.Mage, rank: 0),
                FakeForeverArmory.Member("New", "Recruit", level: 0, characterClass: null, rank: null)
            }));

        var lines = ForeverViews.ArmoryRosterLines(roster, id => id == Alice ? "Alice_A" : null);

        Assert.Equal(new[]
        {
            // Two members can claim one character; nothing here proves who owns it.
            @"**Asha Brightvale** · 29 Mage · Guild Master — ★ Alice\_A, Unknown member",
            "**Mira Dawn** · 40 Druid · Rank 3",
            "**Thalen Duskvale** · 12 Warrior · Rank 3",
            "**New Recruit**",
            "\n__Shared here, not in the guild on the armory__\n**Private Alt** — Rogue — Alice\\_A"
        }, lines);
    }

    [Fact]
    public void Roster_page_shows_the_armory_roster_for_a_matched_guild_and_pages_it()
    {
        var members = Enumerable.Range(1, 45).Select(i => FakeForeverArmory.Member("Member", "N" + new string('x', i), level: i)).ToArray();
        var roster = new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>(), new ForeverArmoryRoster(members));

        var first = ForeverViews.RosterPage(roster, _ => null, 0);
        var last = ForeverViews.RosterPage(roster, _ => null, 99);

        Assert.Equal((0, 3), (first.Page, first.PageCount));
        Assert.Equal((2, 3), (last.Page, last.PageCount));
        Assert.Contains("45 members on Blizzard's armory · US · PvP · Horde", first.Embed.Description);
        Assert.Equal(20, Lines(first.Embed.Description).Count(l => l.StartsWith("**Member")));
        Assert.Equal(5, Lines(last.Embed.Description).Count(l => l.StartsWith("**Member")));
        Assert.StartsWith("Page 3/3 · ", last.Embed.Footer!.Value.Text);
        Assert.Contains("3 hours behind", last.Embed.Footer!.Value.Text);
        Assert.DoesNotContain("first 1,000", last.Embed.Footer!.Value.Text);
    }

    [Fact]
    public void A_full_armory_roster_says_blizzard_stops_at_a_thousand_and_never_overflows()
    {
        var members = Enumerable.Range(1, 1000)
            .Select(i => FakeForeverArmory.Member("Abcdefghijklmnopqrstuvwx", "Abcdefghijklmnopqrstuvwx", level: 60, ForeverClass.Warlock, rank: i % 10))
            .ToArray();
        var shared = Enumerable.Range(1, 5)
            .Select(i => new ForeverRosterMember(i, new[] { Shared(i, i, "Abcdefghijklmnopqrstuvwx", "Abcdefghijklmnopqrstuvwx") }, MainId: i))
            .ToList();
        var roster = new ForeverRoster(MatchedGuild, shared, new ForeverArmoryRoster(members));

        var first = ForeverViews.RosterPage(roster, id => new string('n', 32), 0);

        Assert.True(first.PageCount >= 50);
        for (var i = 0; i < first.PageCount; i++)
            Assert.True(ForeverViews.RosterPage(roster, id => new string('n', 32), i).Embed.Description.Length <= 4096);
        Assert.Contains("Blizzard lists the first 1,000", first.Embed.Footer!.Value.Text);
    }

    [Fact]
    public void Roster_page_falls_back_to_what_members_shared()
    {
        var shared = new[] { new ForeverRosterMember(Alice, new[] { Shared(1, Alice, "Asha") }, MainId: 1) };

        var unmatched = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, shared), _ => "Alice", 0);
        Assert.Contains("Member-reported characters", unmatched.Embed.Description);
        Assert.DoesNotContain("armory", unmatched.Embed.Description);
        Assert.Contains("Asha Brightvale", unmatched.Embed.Description);

        var quiet = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, shared, Armory: null, ArmoryMissed: true), _ => "Alice", 0);
        Assert.Contains("Blizzard's armory didn't send the guild's roster", quiet.Embed.Description);
        Assert.Contains("Asha Brightvale", quiet.Embed.Description);

        var empty = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>(), new ForeverArmoryRoster(Array.Empty<ForeverArmoryMember>())), _ => null, 0);
        Assert.Equal((0, 0), (empty.Page, empty.PageCount));
        Assert.Contains("lists no members", empty.Embed.Description);
    }

    [Fact]
    public void Card_overview_shows_what_the_armory_knows_and_links_to_it()
    {
        var embed = ForeverViews.Card(Card(), ForeverViews.CardOverview);

        Assert.Equal("Asha Brightvale", embed.Title);
        Assert.Equal("https://worldofwarcraft.blizzard.com/en-us/forever/us/armory/character/asha-brightvale", embed.Url);
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/64/4242-avatar.jpg", embed.Thumbnail!.Value.Url);
        Assert.Equal(new[]
        {
            "**Level 29** Undead Mage",
            "🔴 Horde · Normal · US",
            "**Guild:** Synthetic Guild",
            "**Talents:** Frost 15 / Fire 5",
            "**Item level:** 19",
            "**Professions:** Tailoring 107/150 · Fishing 12",
            "**Deaths:** 2 · **Quests:** 1,241 · **Dungeons entered:** 5",
            "**Last seen:** <t:1794280327:R>"
        }, Lines(embed.Description));
        Assert.Contains("Blizzard's armory", embed.Footer!.Value.Text);
    }

    [Fact]
    public void Card_leaves_out_whatever_the_armory_did_not_send()
    {
        var bare = new ForeverArmoryCard(
            new ForeverArmoryCharacter(1, "Asha", "", "asha-", ForeverRegion.KR, 10, null, null, null, null, null, "Hardcore", null, null, null, null),
            null, Array.Empty<ForeverArmoryItem>(), Array.Empty<ForeverArmoryProfession>(), Array.Empty<ForeverArmoryTalentTree>(), null);

        var overview = ForeverViews.Card(bare, ForeverViews.CardOverview);
        Assert.Equal("Asha", overview.Title);
        Assert.Equal(new[] { "**Level 10** ", "Hardcore · KR" }, Lines(overview.Description));
        Assert.Null(overview.Thumbnail);

        Assert.Contains("no gear", ForeverViews.Card(bare, ForeverViews.CardGear).Description);
    }

    [Fact]
    public void Card_gear_is_in_slot_order_with_unknown_slots_last()
    {
        var embed = ForeverViews.Card(Card(), ForeverViews.CardGear);

        Assert.Equal("Asha Brightvale · Gear", embed.Title);
        Assert.Equal(new[]
        {
            "**Item level:** 19",
            "🟢 **Head:** Linen Hood (12)",
            "⚪ **Main Hand:** Executor Staff (5)",
            @"⚪ **Odd:** Mystery \*Item\*"
        }, Lines(embed.Description));
    }

    [Fact]
    public void Card_buttons_switch_views_for_the_caller_and_always_link_to_the_armory()
    {
        var overview = Buttons(ForeverViews.CardButtons(7, Armory(), ForeverViews.CardOverview));
        Assert.Equal(("Gear", $"{ForeverViews.CardButtonPrefix}~7~g~0~Asha~Brightvale"), (overview[0].Label, overview[0].CustomId));
        Assert.Equal((ButtonStyle.Link, "https://worldofwarcraft.blizzard.com/en-us/forever/us/armory/character/asha-brightvale"), (overview[1].Style, overview[1].Url));

        var gear = Buttons(ForeverViews.CardButtons(7, Armory(), ForeverViews.CardGear));
        Assert.Equal(("Overview", $"{ForeverViews.CardButtonPrefix}~7~o~0~Asha~Brightvale"), (gear[0].Label, gear[0].CustomId));

        // Names the button couldn't carry back just lose the toggle; the link stays.
        var tooLong = new string('a', 40);
        Assert.Equal(ButtonStyle.Link, Assert.Single(Buttons(ForeverViews.CardButtons(ulong.MaxValue, Armory(tooLong, tooLong), ForeverViews.CardOverview))).Style);
        Assert.Equal(ButtonStyle.Link, Assert.Single(Buttons(ForeverViews.CardButtons(7, Armory("Asha", "Bright~vale"), ForeverViews.CardOverview))).Style);
        Assert.Equal(ButtonStyle.Link, Assert.Single(Buttons(ForeverViews.CardButtons(7, Armory("Asha", ""), ForeverViews.CardOverview))).Style);
    }

    [Fact]
    public void A_missing_card_says_why_and_what_to_check()
    {
        var notFound = ForeverViews.CardMissing("Asha Brightvale", ForeverRegion.EU, ForeverLookupStatus.NotFound);
        Assert.Contains("**Asha Brightvale** on the EU armory", notFound);
        Assert.Contains("level 10", notFound);

        Assert.Contains("didn't answer", ForeverViews.CardMissing("Asha Brightvale", ForeverRegion.EU, ForeverLookupStatus.Unavailable));
    }

    [Fact]
    public void A_saved_character_says_where_its_details_came_from()
    {
        var character = Shared(1, Alice, "Asha", characterClass: ForeverClass.Mage);

        var found = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, Armory(guild: "*Synthetic*"), ForeverLookupStatus.Found), "added", false, false);
        Assert.Contains(@"[On the armory](https://worldofwarcraft.blizzard.com/en-us/forever/us/armory/character/asha-brightvale): level 29 Undead, in \*Synthetic\*", found.Description);
        Assert.Contains("ownership isn't verified", found.Footer!.Value.Text);

        var notFound = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, null, ForeverLookupStatus.NotFound), "added", false, false);
        Assert.Contains("Not on the armory yet", notFound.Description);
        Assert.Contains("level 10", notFound.Description);
        Assert.Contains("Self-reported", notFound.Footer!.Value.Text);

        var down = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, null, ForeverLookupStatus.Unavailable), "added", false, false);
        Assert.Contains("didn't answer", down.Description);

        var notAsked = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, null, null), "saved", true, true);
        Assert.DoesNotContain("armory", notAsked.Description);
        Assert.Contains("★ Your main", notAsked.Description);
    }

    [Fact]
    public void The_guild_card_says_whether_the_armory_has_the_guild()
    {
        var armory = new ForeverArmoryGuild(1, "Synthetic Guild", "synthetic-guild", ForeverRegion.US, ForeverFaction.Horde, ForeverRuleset.PvP, "PvP", 1234);

        var justSet = ForeverViews.Guild(MatchedGuild, "Server", new ForeverSavedGuild(MatchedGuild, armory, ForeverLookupStatus.Found));
        Assert.Contains("**Armory:** matched · 1,234 members", justSet.Description);
        Assert.StartsWith("Ruleset and faction from Blizzard's armory", justSet.Footer!.Value.Text);

        Assert.Contains("**Armory:** matched\n", ForeverViews.Guild(MatchedGuild, "Server").Description.Replace("\r\n", "\n"));

        var typed = new ForeverGuild { Name = "Typed", Region = ForeverRegion.EU, Ruleset = ForeverRuleset.Normal, Faction = ForeverFaction.Alliance };
        var notFound = ForeverViews.Guild(typed, "Server", new ForeverSavedGuild(typed, null, ForeverLookupStatus.NotFound));
        Assert.Contains("**Armory:** no guild with this name on the EU armory", notFound.Description);
        Assert.StartsWith("Self-reported", notFound.Footer!.Value.Text);
        Assert.Contains("didn't answer", ForeverViews.Guild(typed, "Server", new ForeverSavedGuild(typed, null, ForeverLookupStatus.Unavailable)).Description);
        Assert.Contains("**Armory:** not matched", ForeverViews.Guild(typed, "Server").Description);
    }

    [Fact]
    public void Lookup_suggestions_offer_the_typed_name_first_then_the_callers_own()
    {
        var mine = new[] { Shared(11, Alice, "Asha"), Shared(12, Alice, "Thalen", "Ashvale") };

        var typed = ForeverLookupAutocomplete.Suggestions(mine, "mira ash");
        Assert.Equal(("Look up Mira Ash on the armory", "Mira Ash"), (typed[0].Name, (string)typed[0].Value));
        Assert.Empty(typed.Skip(1));

        // Typing one of your own in full just picks it.
        var own = ForeverLookupAutocomplete.Suggestions(mine, "asha brightvale");
        Assert.Equal("11", (string)Assert.Single(own).Value);

        Assert.Equal(new[] { "11", "12" }, ForeverLookupAutocomplete.Suggestions(mine, "ash").Select(s => (string)s.Value));
        Assert.Equal(new[] { "11", "12" }, ForeverLookupAutocomplete.Suggestions(mine, "").Select(s => (string)s.Value));

        var nothing = Assert.Single(ForeverLookupAutocomplete.Suggestions(Array.Empty<ForeverCharacter>(), "zz"));
        Assert.Equal((ForeverLookupAutocomplete.Hint, "0"), (nothing.Name, (string)nothing.Value));
        Assert.All(ForeverLookupAutocomplete.Suggestions(mine, new string('a', 24) + " " + new string('b', 24)), s => Assert.InRange(s.Name.Length, 1, 100));
    }
}
