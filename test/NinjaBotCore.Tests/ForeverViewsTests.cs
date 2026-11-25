using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services;
using Xunit;
using static NinjaBotCore.Tests.ForeverCardReader;

namespace NinjaBotCore.Tests;

// What /forever shows: Components V2 cards and autocomplete. Every guild, user and character is SYNTHETIC.
public class ForeverViewsTests
{
    private const long Alice = 501;
    private const long Bob = 502;
    private const ulong Caller = 7;
    private const string Avatar = "https://render.worldofwarcraft.com/forever-us/character/64/4242-avatar.jpg";
    private const string Render = "https://render.worldofwarcraft.com/forever-us/character/64/4242-main-raw.png";
    private const string ArmoryLink = "https://worldofwarcraft.blizzard.com/en-us/forever/us/armory/character/asha-brightvale";

    private static readonly ForeverGuild MatchedGuild = new()
    {
        Name = "Synthetic Guild", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde,
        RosterEnabled = true, ArmorySlug = "synthetic-guild"
    };

    private static ForeverCharacter Shared(long id, long user, string first, string last = "Brightvale", ForeverClass? characterClass = ForeverClass.Priest,
        ForeverRegion region = ForeverRegion.US) => new()
    {
        Id = id, UserId = user, FirstName = first, LastName = last,
        NormalizedFirstName = ForeverNames.Normalize(first), NormalizedLastName = ForeverNames.Normalize(last),
        Region = region, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, Class = characterClass
    };

    private static ForeverArmoryCharacter Armory(string first = "Asha", string last = "Brightvale", string guild = "Synthetic Guild") => new(
        4242, first, last, ForeverNames.CharacterSlug(first, last), ForeverRegion.US, 29, "Undead", ForeverClass.Mage, "Mage",
        ForeverFaction.Horde, ForeverRuleset.Normal, "Normal", guild, guild == null ? null : ForeverNames.Slug(guild), 19,
        DateTimeOffset.FromUnixTimeSeconds(1794280327));

    private static ForeverArmoryCard Card(ForeverArmoryCharacter character = null) => new(
        character ?? Armory(),
        Avatar,
        new[]
        {
            new ForeverArmoryItem("MAIN_HAND", "Main Hand", "Executor Staff", "COMMON", 5, Id: 3277, Enchants: new[] { "Weapon Damage +2", "Flame_tongue 1" }),
            new ForeverArmoryItem("ODD_SLOT", "Odd", "Mystery *Item*", null, null),
            new ForeverArmoryItem("FINGER_1", "Ring 1", "Copper Band", "RARE", 14, Id: 11965),
            new ForeverArmoryItem("HEAD", "Head", "Linen Hood", "UNCOMMON", 12, Id: 4000, Enchants: Array.Empty<string>())
        },
        new[]
        {
            new ForeverArmoryProfession("Tailoring", 107, 150, Id: 197, Recipes: 43),
            // One the bot has never heard of, in the older shape with no cap.
            new ForeverArmoryProfession("Rune_carving", 12, 0),
            new ForeverArmoryProfession("Fishing", 150, 150, Id: 356, Recipes: 1, IsPrimary: false),
            new ForeverArmoryProfession("First Aid", 300, 300, Id: 129, Recipes: 13, IsPrimary: false)
        },
        new[] { new ForeverArmoryTalentTree("Frost", 15), new ForeverArmoryTalentTree("Fire", 5) },
        new ForeverArmoryStats(Deaths: 2, Quests: 1241, Kills: null, Dungeons: 5),
        Render,
        new[] { new ForeverArmorySet("Synthetic Raiment", Equipped: 2, Pieces: 5, Id: 2133, ActiveBonuses: 1, Bonuses: 4) });

    private static ForeverArmoryMember[] Guildmates() => new[]
    {
        FakeForeverArmory.Member("Thalen", "Duskvale", level: 12, ForeverClass.Warrior, rank: 3),
        FakeForeverArmory.Member("Mira", "Dawn", level: 40, ForeverClass.Druid, rank: 3),
        FakeForeverArmory.Member("Asha", "Brightvale", level: 29, ForeverClass.Mage, rank: 0),
        FakeForeverArmory.Member("New", "Recruit", level: 0, characterClass: null, rank: null)
    };

    // ===== Roster =====

    [Fact]
    public void Armory_roster_lists_blizzards_members_by_rank_then_level_with_who_shared_them()
    {
        var roster = new ForeverRoster(MatchedGuild,
            new[]
            {
                new ForeverRosterMember(Alice, new[] { Shared(1, Alice, "asha"), Shared(2, Alice, "Private", "Alt", ForeverClass.Rogue) }, MainId: 1),
                new ForeverRosterMember(Bob, new[] { Shared(3, Bob, "ASHA") }, MainId: null)
            },
            new ForeverArmoryRoster(Guildmates()));

        var lines = ForeverViews.ArmoryRosterLines(roster, id => id == Alice ? "Alice_A" : null);

        Assert.Equal(new[]
        {
            // Two members can claim one character; nothing here proves who owns it.
            @"🔮 **Asha Brightvale** · 29 Mage · Guild Master — ★ Alice\_A, Unknown member",
            "🐾 **Mira Dawn** · 40 Druid · Rank 3",
            "⚔️ **Thalen Duskvale** · 12 Warrior · Rank 3",
            "❔ **New Recruit**",
            "\n__On this server's roster, not in the guild on the armory__\n🗡️ **Private Alt** — Rogue — Alice\\_A"
        }, lines);
    }

    [Theory]
    [InlineData(ForeverRosterSort.Rank, "Asha,Mira,Thalen,New")]
    [InlineData(ForeverRosterSort.Level, "Mira,Asha,Thalen,New")]
    [InlineData(ForeverRosterSort.Name, "Asha,Mira,New,Thalen")]
    [InlineData(ForeverRosterSort.Class, "Thalen,Asha,Mira,New")]
    public void Armory_roster_can_be_sorted(ForeverRosterSort sort, string expected)
    {
        var roster = new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>(), new ForeverArmoryRoster(Guildmates()));

        var order = ForeverViews.ArmoryRosterLines(roster, _ => null, sort).Select(l => l.Split("**")[1].Split(' ')[0]);

        Assert.Equal(expected.Split(','), order);
    }

    [Fact]
    public void Roster_page_shows_the_armory_roster_for_a_matched_guild_and_pages_it()
    {
        var members = Enumerable.Range(1, 45).Select(i => FakeForeverArmory.Member("Member", "N" + new string('x', i), level: i)).ToArray();
        var roster = new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>(), new ForeverArmoryRoster(members));

        var first = ForeverViews.RosterPage(roster, _ => null, 0, Caller, iconUrl: "https://cdn.example/icon.png");
        var middle = ForeverViews.RosterPage(roster, _ => null, 1, Caller, ForeverRosterSort.Level);
        var last = ForeverViews.RosterPage(roster, _ => null, 99, Caller);

        Assert.Equal((0, 3), (first.Page, first.PageCount));
        Assert.Equal((2, 3), (last.Page, last.PageCount));
        Assert.Equal(new Color(200, 35, 35), Accent(first.Card));
        Assert.Equal("https://cdn.example/icon.png", Assert.Single(All<ThumbnailComponent>(first.Card)).Media.Url);
        Assert.Empty(All<ThumbnailComponent>(last.Card));

        Assert.Equal(new[]
        {
            "# 👥 Synthetic Guild roster",
            "**45** members on Blizzard's armory · US · PvP · Horde",
            "✨ 45"
        }, Lines(first.Card).Take(3));
        Assert.Equal(20, Lines(first.Card).Count(l => l.StartsWith("✨ **Member")));
        Assert.Equal(5, Lines(last.Card).Count(l => l.StartsWith("✨ **Member")));

        var footer = Lines(last.Card).Last();
        Assert.StartsWith("-# ", footer);
        Assert.Contains("3 hours behind", footer);
        Assert.DoesNotContain("first 1,000", footer);

        // Prev, the page, Next: the sort rides on the page so it survives paging.
        Assert.Equal(
            new[] { ("Prev", "forever_roster~7~0l", false), ("Page 2/3", "forever_roster_info", true), ("Next", "forever_roster~7~2l", false) },
            Buttons(middle.Card).Select(b => (b.Label, b.CustomId, b.IsDisabled)));
        Assert.True(Buttons(first.Card)[0].IsDisabled);
        Assert.True(Buttons(last.Card)[2].IsDisabled);

        var sort = Menu(middle.Card, ForeverViews.RosterSortPrefix);
        Assert.Equal("forever_rsort~7", sort.CustomId);
        Assert.Equal(new[] { "r", "l", "n", "c" }, sort.Options.Select(o => o.Value));
        Assert.Equal("l", sort.Options.Single(o => o.IsDefault == true).Value);

        // The character menu only offers what the armory can show: level 10 and up, with a name the bot can read back.
        var pick = Menu(middle.Card, ForeverViews.RosterPickPrefix);
        Assert.Equal("forever_rpick~7", pick.CustomId);
        Assert.Equal(Enumerable.Range(10, 14).Reverse().Select(i => "Member~N" + new string('x', i)), pick.Options.Select(o => o.Value));
        Assert.Equal("23 Priest · Rank 3", pick.Options.First().Description);
        Assert.Null(Menu(last.Card, ForeverViews.RosterPickPrefix));

        AssertFits(first.Card);
        AssertFits(middle.Card);
        AssertFits(last.Card);
    }

    [Fact]
    public void Roster_header_counts_classes_and_level_sixties()
    {
        var members = Guildmates()
            .Append(FakeForeverArmory.Member("Zed", "Sixty", level: 60, ForeverClass.Mage, rank: 1))
            .Append(FakeForeverArmory.Member("Yan", "Sixty", level: 60, ForeverClass.Mage, rank: 1))
            .ToArray();
        var roster = new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>(), new ForeverArmoryRoster(members));

        var card = ForeverViews.RosterPage(roster, _ => null, 0, Caller).Card;

        Assert.Equal("🔮 3 · ⚔️ 1 · 🐾 1 · **2** at level 60", Lines(card)[2]);
        Assert.Empty(Buttons(card));
        AssertFits(card);
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

        var first = ForeverViews.RosterPage(roster, id => new string('n', 32), 0, Caller);

        Assert.True(first.PageCount >= 50);
        var shown = 0;
        for (var i = 0; i < first.PageCount; i++)
        {
            var page = ForeverViews.RosterPage(roster, id => new string('n', 32), i, Caller, ForeverRosterSort.Class).Card;
            AssertFits(page);
            shown += Lines(page).Count(l => l.StartsWith("🔥 **Abcdefghijklmnopqrstuvwx"));
        }
        // Fitting must come from paging, never from cutting members off the end of a page.
        Assert.Equal(1000, shown);
        Assert.Contains("**1,000** members", Text(first.Card));
        Assert.Contains("Blizzard lists the first 1,000", Lines(first.Card).Last());
    }

    [Fact]
    public void Roster_page_falls_back_to_what_members_shared()
    {
        var shared = new[] { new ForeverRosterMember(Alice, new[] { Shared(1, Alice, "Asha") }, MainId: 1) };

        var unmatched = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, shared), _ => "Alice", 0, Caller).Card;
        Assert.Contains("**1** member · characters members added · US · PvP · Horde", Text(unmatched));
        Assert.DoesNotContain("armory", Text(unmatched));
        Assert.Contains("__Alice__\n★ ✨ **Asha Brightvale** — Priest", Text(unmatched));
        Assert.Empty(All<SelectMenuComponent>(unmatched));
        AssertFits(unmatched);

        var quiet = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, shared, Armory: null, ArmoryMissed: true), _ => "Alice", 0, Caller).Card;
        Assert.Contains("Blizzard's armory didn't send the guild's roster", Text(quiet));
        Assert.Contains("Asha Brightvale", Text(quiet));

        var nobody = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>()), _ => null, 0, Caller);
        Assert.Equal((0, 0), (nobody.Page, nobody.PageCount));
        Assert.Contains("Nobody has put a character on the roster yet", Text(nobody.Card));

        var empty = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, Array.Empty<ForeverRosterMember>(), new ForeverArmoryRoster(Array.Empty<ForeverArmoryMember>())), _ => null, 0, Caller);
        Assert.Equal((0, 0), (empty.Page, empty.PageCount));
        Assert.Contains("lists no members", Text(empty.Card));
        Assert.Empty(All<SelectMenuComponent>(empty.Card));
    }

    [Fact]
    public void Shared_roster_pages_with_prev_and_next()
    {
        var members = Enumerable.Range(1, 31)
            .Select(i => new ForeverRosterMember(i, new[] { Shared(i, i, "Asha", "N" + new string('x', i % 20 + 1)) }, MainId: null))
            .ToList();

        var page = ForeverViews.RosterPage(new ForeverRoster(MatchedGuild, members), id => "Member " + id.ToString("00"), 1, Caller);

        Assert.Equal((1, 3), (page.Page, page.PageCount));
        Assert.Equal(new[] { "forever_roster~7~0r", "forever_roster_info", "forever_roster~7~2r" }, Buttons(page.Card).Select(b => b.CustomId));
        Assert.Contains("**31** members", Text(page.Card));
        AssertFits(page.Card);
    }

    [Theory]
    [InlineData("3", 3, ForeverRosterSort.Rank)] // buttons from before sorting carry only the page
    [InlineData("2l", 2, ForeverRosterSort.Level)]
    [InlineData("0n", 0, ForeverRosterSort.Name)]
    [InlineData("14c", 14, ForeverRosterSort.Class)]
    [InlineData("5z", 5, ForeverRosterSort.Rank)]
    [InlineData("-4", 0, ForeverRosterSort.Rank)]
    [InlineData("junk", 0, ForeverRosterSort.Rank)]
    [InlineData("", 0, ForeverRosterSort.Rank)]
    public void Roster_buttons_carry_the_page_and_the_sort(string value, int page, ForeverRosterSort sort)
    {
        Assert.Equal((page, sort), ForeverViews.ParseRosterPage(value));
    }

    // ===== Armory character card =====

    [Fact]
    public void Card_overview_shows_what_the_armory_knows_and_links_to_it()
    {
        var card = ForeverViews.Card(Card(), ForeverViews.CardOverview, Caller);

        Assert.Equal(new[]
        {
            "# 🔮 Asha Brightvale",
            "**Level 29** Undead Mage · <Synthetic Guild>",
            "🔴 Horde · Normal · US",
            "-# ▰▰▰▰▱▱▱▱▱▱ level 29 of 60",
            "🌳 **Talents** Frost 15 / Fire 5",
            "🛡️ **Item level** 19",
            "🧰 **Professions**",
            "🧵 [Tailoring](https://www.wowhead.com/forever/skill=197) `███▒▒░░░░░` 107/150",
            @"🧰 Rune\_carving `█░░░░░░░░░` 12",
            "-# 🎣 Fishing 150/150 · 🩹 First Aid 300/300",
            "💀 **Deaths** 2 · 📜 **Quests** 1,241 · 🏰 **Dungeons entered** 5",
            "🕒 **Last seen** <t:1794280327:R>",
            "-# From Blizzard's armory · updates when the character logs out · profession names open Wowhead"
        }, Lines(card));
        Assert.Equal(Avatar, Assert.Single(All<ThumbnailComponent>(card)).Media.Url);
        Assert.Equal(new Color(0x69CCF0), Accent(card));
        // The full-body render belongs to the Gear tab.
        Assert.Empty(All<MediaGalleryComponent>(card));
        AssertFits(card);
    }

    [Fact]
    public void Card_leaves_out_whatever_the_armory_did_not_send()
    {
        var bare = new ForeverArmoryCard(
            new ForeverArmoryCharacter(1, "Asha", "", "asha-", ForeverRegion.KR, 60, null, null, null, null, null, "Hardcore", null, null, null, null),
            null, Array.Empty<ForeverArmoryItem>(), Array.Empty<ForeverArmoryProfession>(), Array.Empty<ForeverArmoryTalentTree>(), null);

        var overview = ForeverViews.Card(bare, ForeverViews.CardOverview, Caller);
        Assert.Equal(new[]
        {
            "# ❔ Asha",
            "**Level 60**",
            "Hardcore · KR",
            "*The armory has no more details for this character yet.*",
            "-# From Blizzard's armory · updates when the character logs out"
        }, Lines(overview));
        Assert.Empty(All<ThumbnailComponent>(overview));
        Assert.Empty(All<SectionComponent>(overview));
        Assert.Equal(new Color(0xE0B04A), Accent(overview));

        var gear = ForeverViews.Card(bare, ForeverViews.CardGear, Caller);
        Assert.Contains("no gear", Text(gear));
        Assert.Empty(All<MediaGalleryComponent>(gear));
        AssertFits(gear);

        // An old Professions button on a character who has since dropped them all.
        var professions = ForeverViews.Card(bare, ForeverViews.CardProfessions, Caller);
        Assert.Equal("*The armory has no professions for this character right now.*", Lines(professions)[3]);
        Assert.Equal("-# From Blizzard's armory · updates when the character logs out", Lines(professions).Last());
    }

    [Fact]
    public void Card_professions_tab_shows_rank_recipes_and_a_bar_of_the_whole_climb()
    {
        var card = ForeverViews.Card(Card(), ForeverViews.CardProfessions, Caller);

        Assert.Equal("# 🔮 Asha Brightvale", Lines(card)[0]);
        Assert.Equal(new[]
        {
            "### Primary",
            "🧵 **[Tailoring](https://www.wowhead.com/forever/skill=197)** · Journeyman · 43 recipes",
            "`███████▒▒▒░░░░░░░░░░` **107** / 150",
            @"🧰 **Rune\_carving**",
            "`█░░░░░░░░░░░░░░░░░░░` **12**",
            "### Secondary",
            "🎣 **[Fishing](https://www.wowhead.com/forever/skill=356)** · Journeyman · 1 recipe",
            "`██████████░░░░░░░░░░` **150** / 150 · at the cap, Expert is next",
            "🩹 **[First Aid](https://www.wowhead.com/forever/skill=129)** · Artisan · 13 recipes",
            "`████████████████████` **300** / 300 · maxed",
            "-# █ skill · ▒ still to earn in the trained rank · ░ ranks not trained yet",
            "-# From Blizzard's armory · updates when the character logs out · profession names open Wowhead"
        }, Lines(card).Skip(3));
        Assert.Empty(All<MediaGalleryComponent>(card));
        AssertFits(card);

        // Five professions with the longest names Discord would ever see still fit.
        var many = Enumerable.Range(0, 5).Select(i => new ForeverArmoryProfession(new string('*', 100), 299, 300, Id: 197, Recipes: 9999, IsPrimary: i < 2)).ToList();
        AssertFits(ForeverViews.Card(Card() with { Professions = many }, ForeverViews.CardProfessions, Caller));
        AssertFits(ForeverViews.Card(Card() with { Professions = many }, ForeverViews.CardOverview, Caller));
    }

    [Theory]
    [InlineData(0, 75, "▒▒▒▒▒░░░░░░░░░░░░░░░")]
    [InlineData(1, 75, "█▒▒▒▒░░░░░░░░░░░░░░░")]
    [InlineData(75, 75, "█████░░░░░░░░░░░░░░░")]
    [InlineData(152, 225, "██████████▒▒▒▒▒░░░░░")]
    [InlineData(263, 300, "█████████████████▒▒▒")]
    [InlineData(299, 300, "███████████████████▒")]
    [InlineData(300, 300, "████████████████████")]
    // Nothing the armory sends can push a bar past its ends.
    [InlineData(-5, 0, "░░░░░░░░░░░░░░░░░░░░")]
    [InlineData(40, 0, "██░░░░░░░░░░░░░░░░░░")]
    public void A_skill_bar_is_solid_for_skill_medium_for_the_trained_rank_and_light_beyond(int skill, int cap, string bar)
    {
        var professions = new[] { new ForeverArmoryProfession("Mining", skill, cap, Id: 186) };

        var body = All<TextDisplayComponent>(ForeverViews.Card(Card() with { Professions = professions }, ForeverViews.CardProfessions, Caller))[1].Content;

        Assert.Contains($"`{bar}` **{skill}**", body);
    }

    [Fact]
    public void Skill_bars_stretch_if_the_armory_ever_reports_more_than_artisan()
    {
        var professions = new[]
        {
            new ForeverArmoryProfession("Mining", 375, 375, Id: 186),
            new ForeverArmoryProfession("Herbalism", 300, 300, Id: 182)
        };

        var lines = Lines(ForeverViews.Card(Card() with { Professions = professions }, ForeverViews.CardProfessions, Caller));

        // A cap the bot has no name for gets no rank, and 300 is no longer the end of the bar.
        Assert.Contains("⛏️ **[Mining](https://www.wowhead.com/forever/skill=186)**", lines);
        Assert.Contains("`████████████████████` **375** / 375 · maxed", lines);
        Assert.Contains("🌿 **[Herbalism](https://www.wowhead.com/forever/skill=182)** · Artisan", lines);
        Assert.Contains("`████████████████░░░░` **300** / 300", lines);
    }

    [Fact]
    public void Overview_puts_secondary_professions_on_one_line_when_there_are_no_primary_ones()
    {
        var professions = new[]
        {
            new ForeverArmoryProfession("Cooking", 20, 75, Id: 185, Recipes: 6, IsPrimary: false),
            new ForeverArmoryProfession("Fishing", 1, 75, Id: 356, IsPrimary: false)
        };

        var overview = ForeverViews.Card(Card() with { Professions = professions }, ForeverViews.CardOverview, Caller);
        Assert.Contains("🧰 **Professions** 🍳 Cooking 20/75 · 🎣 Fishing 1/75", Lines(overview));
        // Nothing on this view is a link, so the footer doesn't promise one.
        Assert.Equal("-# From Blizzard's armory · updates when the character logs out", Lines(overview).Last());

        var tab = Lines(ForeverViews.Card(Card() with { Professions = professions }, ForeverViews.CardProfessions, Caller));
        Assert.DoesNotContain("### Primary", tab);
        Assert.Contains("🍳 **[Cooking](https://www.wowhead.com/forever/skill=185)** · Apprentice · 6 recipes", tab);
        // Fishing's own ranks aren't recipes, so a fisher with nothing else known shows no count.
        Assert.Contains("🎣 **[Fishing](https://www.wowhead.com/forever/skill=356)** · Apprentice", tab);
        Assert.EndsWith("profession names open Wowhead", tab.Last());
    }

    [Fact]
    public void Every_forever_profession_has_an_emoji_a_wowhead_page_and_a_rank_ladder()
    {
        // Blizzard's profession index for Forever, less its test entry.
        var ids = new[] { 129, 164, 165, 171, 182, 185, 186, 197, 202, 333, 356, 393 };
        Assert.Equal(ids, ForeverProfessions.KnownIds.OrderBy(id => id));
        Assert.All(ids, id => Assert.Equal($"https://www.wowhead.com/forever/skill={id}", ForeverProfessions.WowheadUrl(id)));
        Assert.Equal(ids.Length, ids.Select(id => ForeverProfessions.Emoji(id)).Distinct().Count());
        Assert.DoesNotContain(ForeverProfessions.DefaultEmoji, ids.Select(id => ForeverProfessions.Emoji(id)));

        // A profession added later, or the index's "Test Profession [DNT]", gets no link to nowhere.
        Assert.Null(ForeverProfessions.WowheadUrl(2933));
        Assert.Null(ForeverProfessions.WowheadUrl(null));
        Assert.Equal(ForeverProfessions.DefaultEmoji, ForeverProfessions.Emoji(2933));

        Assert.Equal(new[] { "Apprentice", "Journeyman", "Expert", "Artisan", null }, new[] { 75, 150, 225, 300, 310 }.Select(ForeverProfessions.Rank));
        Assert.Equal(new[] { "Journeyman", "Expert", "Artisan", null, null }, new[] { 75, 150, 225, 300, 310 }.Select(ForeverProfessions.NextRank));
    }

    private static ForeverArmoryCard Wearing(IReadOnlyList<ForeverArmoryItem> gear, params ForeverArmorySet[] sets) => new(
        Armory(), Avatar, gear, Array.Empty<ForeverArmoryProfession>(), Array.Empty<ForeverArmoryTalentTree>(), null, Render, sets);

    private static string GearBody(ForeverArmoryCard card)
        => All<TextDisplayComponent>(ForeverViews.Card(card, ForeverViews.CardGear, Caller))[1].Content.Replace("\r\n", "\n");

    // One of everything a level 60 wears, each with a Wowhead id.
    private static ForeverArmoryItem[] FullSet(Func<int, string> name) => new[]
    {
        ("HEAD", "Head"), ("NECK", "Neck"), ("SHOULDER", "Shoulders"), ("BACK", "Back"), ("CHEST", "Chest"), ("SHIRT", "Shirt"),
        ("TABARD", "Tabard"), ("WRIST", "Wrist"), ("HANDS", "Hands"), ("WAIST", "Waist"), ("LEGS", "Legs"), ("FEET", "Feet"),
        ("FINGER_1", "Ring 1"), ("FINGER_2", "Ring 2"), ("TRINKET_1", "Trinket 1"), ("TRINKET_2", "Trinket 2"),
        ("MAIN_HAND", "Main Hand"), ("OFF_HAND", "Off Hand"), ("RANGED", "Ranged")
    }.Select((slot, i) => new ForeverArmoryItem(slot.Item1, slot.Item2, name(i), "EPIC", 60 + i, Id: 280000 + i, Enchants: new[] { "Stamina +7" })).ToArray();

    [Fact]
    public void Card_gear_is_one_aligned_line_an_item_under_its_group_with_the_render_below()
    {
        var card = ForeverViews.Card(Card(), ForeverViews.CardGear, Caller);

        Assert.Equal("# 🔮 Asha Brightvale", Lines(card)[0]);
        var body = All<TextDisplayComponent>(card)[1].Content.Replace("\r\n", "\n");
        Assert.Equal(
            "🛡️ **Item level** 19\n"
            + "🧩 **Set** [Synthetic Raiment](https://www.wowhead.com/forever/item-set=2133) 2/5 · 1 of 4 bonuses active\n"
            // The item of no known quality gets a dot but is nobody's count.
            + "-# 🔵 1 rare · 🟢 1 uncommon · ⚪ 1 common\n"
            + "### Armor\n"
            + "`Head      12` 🟢 [Linen Hood](https://www.wowhead.com/forever/item=4000)\n"
            + "### Jewelry\n"
            + "`Ring 1    14` 🔵 [Copper Band](https://www.wowhead.com/forever/item=11965)\n"
            + "### Weapons\n"
            + "`Main Hand  5` ⚪ [Executor Staff](https://www.wowhead.com/forever/item=3277)\n"
            + @"-# ↳ ✨ Weapon Damage +2 · Flame\_tongue 1" + "\n"
            + "### Other\n"
            + @"`Odd        –` ⚪ Mystery \*Item\*" + "\n"
            + "-# Empty slots: Neck · Shoulders · Back · Chest · Wrist · Hands · Waist · Legs · Feet · Ring 2 · Trinket 1 · Trinket 2",
            body);
        Assert.Equal(Render, Assert.Single(Assert.Single(All<MediaGalleryComponent>(card)).Items).Media.Url);
        Assert.Equal("-# From Blizzard's armory · updates when the character logs out · item names open Wowhead", Lines(card).Last());
        AssertFits(card);
    }

    [Fact]
    public void Gear_groups_follow_the_character_sheet_and_a_full_set_has_no_empty_slots()
    {
        var gear = FullSet(i => $"Item {i}").Reverse()
            .Append(new ForeverArmoryItem("AMMO", "Ammo", "Jagged Arrow", "COMMON", 45, Id: 11285))
            .ToArray();

        var lines = GearBody(Wearing(gear)).Split('\n');

        Assert.Equal(
            new[]
            {
                "### Armor", "Head", "Shoulders", "Back", "Chest", "Shirt", "Tabard", "Wrist", "Hands", "Waist", "Legs", "Feet",
                "### Jewelry", "Neck", "Ring 1", "Ring 2", "Trinket 1", "Trinket 2",
                "### Weapons", "Main Hand", "Off Hand", "Ranged", "Ammo"
            },
            lines.Where(l => l.StartsWith('`') || l.StartsWith("###")).Select(l => l.StartsWith('`') ? l[1..10].TrimEnd() : l));
        Assert.Equal("`Main Hand 76` 🟣 [Item 16](https://www.wowhead.com/forever/item=280016)", lines.Single(l => l.Contains("Main Hand")));
        Assert.Equal("`Ammo      45` ⚪ [Jagged Arrow](https://www.wowhead.com/forever/item=11285)", lines.Single(l => l.Contains("Ammo")));
        Assert.Equal(19, lines.Count(l => l == "-# ↳ ✨ Stamina +7"));
        Assert.Equal("-# 🟣 19 epic · ⚪ 1 common", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("Empty slots"));
        AssertFits(ForeverViews.Card(Wearing(gear), ForeverViews.CardGear, Caller));
    }

    [Fact]
    public void Gear_columns_line_up_whatever_the_slot_names_and_item_levels_are()
    {
        var body = GearBody(Wearing(new[]
        {
            new ForeverArmoryItem("HEAD", "Head", "Cap", "POOR", 7),
            new ForeverArmoryItem("CHEST", "Chest`piece\nof the Extremely Long Slot", "Robe", "LEGENDARY", 105),
            // No name from the armory: the slot's type stands in.
            new ForeverArmoryItem("OFF_HAND", " ", "Torch", "epic", 0),
            new ForeverArmoryItem("", "", "Loose Thing", "Shiny", null)
        }));

        var rows = body.Split('\n').Where(l => l.StartsWith('`')).ToArray();
        Assert.Equal(
            new[]
            {
                "`Head           7` 🔘 Cap",
                "`Chest'piece  105` 🟠 Robe",
                "`Off Hand       –` 🟣 Torch",
                "`Item           –` ⚪ Loose Thing"
            },
            rows);
        // Every label is as wide as the next, and none can end its own code span early.
        Assert.Single(rows.Select(r => r.IndexOf("` ", StringComparison.Ordinal)).Distinct());
        Assert.All(rows, r => Assert.Equal(2, r.Count(ch => ch == '`')));
        Assert.Contains("-# 🟠 1 legendary · 🟣 1 epic · 🔘 1 poor\n", body);

        // With no item levels at all there is no column for them.
        var bare = GearBody(Wearing(new[] { new ForeverArmoryItem("HEAD", "Head", "Cap", null, null), new ForeverArmoryItem("NECK", "Neck", "Chain", null, null) }));
        Assert.Contains("`Head` ⚪ Cap\n", bare);
        Assert.Contains("`Neck` ⚪ Chain\n", bare);
        Assert.DoesNotContain("-# ⚪", bare);
    }

    [Fact]
    public void Gear_links_only_what_wowhead_can_be_asked_for()
    {
        var card = Wearing(
            new[]
            {
                new ForeverArmoryItem("HEAD", "Head", "Linen Hood", "COMMON", 12),
                new ForeverArmoryItem("NECK", "Neck", "Zero Chain", "COMMON", 12, Id: 0),
                // A square bracket in the name would end the link early, so the name stays plain.
                new ForeverArmoryItem("BACK", "Back", "Test Cloak [DNT]", "COMMON", 12, Id: 5),
                new ForeverArmoryItem("CHEST", "Chest", "Half] Robe", "COMMON", 12, Id: 6)
            },
            new ForeverArmorySet("Nameless [PH] Set", 1, 2, Id: 9),
            new ForeverArmorySet("Unnumbered Set", 0, 0));

        var body = GearBody(card);

        Assert.DoesNotContain("wowhead", body);
        Assert.Contains("🧩 **Set** Nameless [PH] Set 1/2\n", body);
        // Without piece or bonus counts the set is just named.
        Assert.Contains("🧩 **Set** Unnumbered Set\n", body);
        Assert.Contains("`Back  12` ⚪ Test Cloak [DNT]\n", body);
        Assert.Contains("`Chest 12` ⚪ Half] Robe\n", body);
        Assert.Equal("-# From Blizzard's armory · updates when the character logs out", Lines(ForeverViews.Card(card, ForeverViews.CardGear, Caller)).Last());
    }

    [Fact]
    public void Gear_gives_up_its_links_before_it_would_be_cut_short()
    {
        // Nineteen enchanted items with names far longer than the game's: linked, they would not fit.
        var longNames = Wearing(FullSet(i => new string((char)('a' + i), 120)));
        var card = ForeverViews.Card(longNames, ForeverViews.CardGear, Caller);
        var body = GearBody(longNames);

        Assert.DoesNotContain("wowhead", body);
        Assert.DoesNotContain("…", body);
        Assert.Equal(19, body.Split('\n').Count(l => l.StartsWith('`')));
        Assert.Equal(19, body.Split('\n').Count(l => l == "-# ↳ ✨ Stamina +7"));
        Assert.Equal("-# From Blizzard's armory · updates when the character logs out", Lines(card).Last());
        AssertFits(card);

        // The longest names the game has keep their links.
        var gameNames = Wearing(FullSet(i => new string((char)('a' + i), 60)));
        Assert.Equal(19, GearBody(gameNames).Split('\n').Count(l => l.Contains("](https://www.wowhead.com/forever/item=")));
        AssertFits(ForeverViews.Card(gameNames, ForeverViews.CardGear, Caller));
    }

    [Fact]
    public void Wowhead_links_are_built_from_blizzards_ids()
    {
        Assert.Equal("https://www.wowhead.com/forever/item=250499", ForeverWowhead.Item(250499));
        Assert.Equal("https://www.wowhead.com/forever/item-set=2133", ForeverWowhead.ItemSet(2133));
        Assert.Equal(new string[] { null, null, null }, new int?[] { null, 0, -4 }.Select(ForeverWowhead.Item));
        Assert.Equal(new string[] { null, null, null }, new int?[] { null, 0, -4 }.Select(ForeverWowhead.ItemSet));
    }

    [Fact]
    public void Card_tabs_switch_views_for_the_caller_and_always_link_to_the_armory()
    {
        var overview = Buttons(ForeverViews.Card(Card(), ForeverViews.CardOverview, Caller));
        Assert.Equal(
            new[]
            {
                ("Overview", $"{ForeverViews.CardButtonPrefix}~7~o~0~Asha~Brightvale", ButtonStyle.Success),
                ("Gear", $"{ForeverViews.CardButtonPrefix}~7~g~0~Asha~Brightvale", ButtonStyle.Primary),
                ("Professions", $"{ForeverViews.CardButtonPrefix}~7~p~0~Asha~Brightvale", ButtonStyle.Primary),
                ("Armory", null, ButtonStyle.Link)
            },
            overview.Select(b => (b.Label, b.CustomId, b.Style)));
        Assert.Equal(ArmoryLink, overview[3].Url);

        var gear = Buttons(ForeverViews.Card(Card(), ForeverViews.CardGear, Caller));
        Assert.Equal(new[] { ButtonStyle.Primary, ButtonStyle.Success, ButtonStyle.Primary, ButtonStyle.Link }, gear.Select(b => b.Style));
        var professions = Buttons(ForeverViews.Card(Card(), ForeverViews.CardProfessions, Caller));
        Assert.Equal(new[] { ButtonStyle.Primary, ButtonStyle.Primary, ButtonStyle.Success, ButtonStyle.Link }, professions.Select(b => b.Style));

        // A character with no professions has no tab for them.
        var none = Buttons(ForeverViews.Card(Card() with { Professions = Array.Empty<ForeverArmoryProfession>() }, ForeverViews.CardOverview, Caller));
        Assert.Equal(new[] { "Overview", "Gear", "Armory" }, none.Select(b => b.Label));

        // Opened from the roster, the card remembers it and offers the way back: a full row of five.
        var fromRoster = Buttons(ForeverViews.Card(Card(), ForeverViews.FromRoster + ForeverViews.CardProfessions, Caller));
        Assert.Equal(
            new[] { "forever_card~7~ro~0~Asha~Brightvale", "forever_card~7~rg~0~Asha~Brightvale", "forever_card~7~rp~0~Asha~Brightvale", "forever_roster~7~0r", null },
            fromRoster.Select(b => b.CustomId));
        Assert.Equal(ButtonStyle.Success, fromRoster[2].Style);
        Assert.Equal("Roster", fromRoster[3].Label);
        Assert.Single(All<ActionRowComponent>(ForeverViews.Card(Card(), ForeverViews.FromRoster + ForeverViews.CardProfessions, Caller)));

        // A view code the card doesn't know is the Overview.
        Assert.Equal(new[] { "o", "g", "p", "o", "g", "p", "o", "o" },
            new[] { "o", "g", "p", "ro", "rg", "rp", "x", null }.Select(ForeverViews.Tab));

        // Names the buttons couldn't carry back just lose the tabs; the link stays.
        var tooLong = new string('a', 40);
        Assert.Equal(ButtonStyle.Link, Assert.Single(Buttons(ForeverViews.Card(Card(Armory(tooLong, tooLong)), ForeverViews.CardOverview, ulong.MaxValue))).Style);
        Assert.Equal(ButtonStyle.Link, Assert.Single(Buttons(ForeverViews.Card(Card(Armory("Asha", "Bright~vale")), ForeverViews.CardOverview, Caller))).Style);
        Assert.Equal(ButtonStyle.Link, Assert.Single(Buttons(ForeverViews.Card(Card(Armory("Asha", "")), ForeverViews.CardOverview, Caller))).Style);
    }

    [Fact]
    public void A_missing_card_says_why_and_what_to_check()
    {
        var notFound = ForeverViews.CardMissing("Asha Brightvale", ForeverRegion.EU, ForeverLookupStatus.NotFound);
        Assert.Contains("**Asha Brightvale** on the EU armory", notFound);
        Assert.Contains("level 10", notFound);

        Assert.Contains("didn't answer", ForeverViews.CardMissing("Asha Brightvale", ForeverRegion.EU, ForeverLookupStatus.Unavailable));
    }

    // ===== Saved characters =====

    [Fact]
    public void A_saved_character_says_what_the_armory_knows_and_what_it_is_to_the_caller_here()
    {
        var character = Shared(1, Alice, "Asha", characterClass: ForeverClass.Mage);

        var found = ForeverViews.CharacterSaved(
            new ForeverSavedCharacter(character, Armory(guild: "*Synthetic*"), ForeverLookupStatus.Found), "added", false, false, Caller, canShare: true);
        Assert.Equal(new[]
        {
            "## 🔮 Asha Brightvale · added ✅",
            "Mage · US · PvP · Horde",
            $@"[On the armory]({ArmoryLink}): level 29 Undead, in \*Synthetic\*",
            "Not your main in this server.",
            "🔒 Private: only you can see that it's yours.",
            "-# From Blizzard's armory · nothing proves a character is yours"
        }, Lines(found));
        Assert.Equal(
            new[]
            {
                ("Make main", "forever_main~7~1"),
                ("Show on roster", "forever_share~7~1"),
                ("Armory card", "forever_card~7~o~0~Asha~Brightvale"),
                ("Remove", "forever_remove~7~1~n"),
                ("Your characters", "forever_list~7")
            },
            Buttons(found).Select(b => (b.Label, b.CustomId)));
        Assert.Equal(new Color(0x69CCF0), Accent(found));
        AssertFits(found);

        var notFound = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, null, ForeverLookupStatus.NotFound), "added", false, false, Caller, canShare: false);
        Assert.Contains("Not on the armory yet", Text(notFound));
        Assert.Contains("level 10", Text(notFound));
        Assert.Equal(new[] { "Make main", "Remove", "Your characters" }, Buttons(notFound).Select(b => b.Label));

        var down = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, null, ForeverLookupStatus.Unavailable), null, false, false, Caller, canShare: false);
        Assert.Contains("didn't answer", Text(down));
        Assert.StartsWith("## 🔮 Asha Brightvale\n", Text(down));

        var onRoster = ForeverViews.CharacterSaved(new ForeverSavedCharacter(character, null, null), "on the roster", true, true, Caller, canShare: true);
        Assert.Contains("## 🔮 Asha Brightvale · on the roster ✅", Text(onRoster));
        Assert.DoesNotContain("armory yet", Text(onRoster));
        Assert.Contains("★ Your main", Text(onRoster));
        Assert.Contains("👥 On this server's roster, with your name next to it.", Text(onRoster));
        Assert.Equal(new[] { "Hide from roster", "Remove", "Your characters" }, Buttons(onRoster).Select(b => b.Label));
    }

    [Fact]
    public void The_character_list_marks_mains_and_the_roster_and_opens_any_of_them()
    {
        var mine = new[] { Shared(11, Alice, "Asha"), Shared(12, Alice, "Thalen", "Ash_vale", ForeverClass.Rogue), Shared(13, Alice, "Mira", "Dawn", characterClass: null) };

        var card = ForeverViews.CharacterList(mine, mainId: 11, new HashSet<long> { 12 }, Caller);

        Assert.Equal(new[]
        {
            "# 📜 Your Forever characters",
            "**3** characters · only you can see this list",
            "★ ✨ **Asha Brightvale** — Priest · US · PvP · Horde",
            @"👥 🗡️ **Thalen Ash\_vale** — Rogue · US · PvP · Horde",
            "❔ **Mira Dawn** — class not known yet · US · PvP · Horde",
            "-# ★ main in this server · 👥 on this server's roster · add another with /forever char add"
        }, Lines(card));
        var menu = Menu(card, ForeverViews.ManagePrefix);
        Assert.Equal("forever_manage~7", menu.CustomId);
        Assert.Equal(new[] { ("Asha Brightvale", "11"), ("Thalen Ash_vale", "12"), ("Mira Dawn", "13") }, menu.Options.Select(o => (o.Label, o.Value)));
        AssertFits(card);

        var none = ForeverViews.CharacterList(Array.Empty<ForeverCharacter>(), null, new HashSet<long>(), Caller);
        Assert.Contains("You haven't saved any yet. Use `/forever char add`.", Text(none));
        Assert.Empty(All<SelectMenuComponent>(none));

        var many = Enumerable.Range(1, 40).Select(i => Shared(i, Alice, "Asha", "N" + new string('x', i % 20 + 1))).ToList();
        AssertFits(ForeverViews.CharacterList(many, null, new HashSet<long>(), Caller));
    }

    [Fact]
    public void A_members_card_lists_what_they_shared()
    {
        var card = ForeverViews.Member("Alice_A", "https://cdn.example/alice.png", new[] { Shared(1, Alice, "Asha"), Shared(2, Alice, "Private", "Alt", ForeverClass.Rogue) }, mainId: 1);

        Assert.Equal(new[]
        {
            @"# 👤 Alice\_A",
            "**2** characters on this server's Forever roster",
            "★ ✨ **Asha Brightvale** — Priest",
            "🗡️ **Private Alt** — Rogue",
            "-# ★ main · Put there by the member · nothing proves a character is theirs"
        }, Lines(card));
        Assert.Equal("https://cdn.example/alice.png", Assert.Single(All<ThumbnailComponent>(card)).Media.Url);

        var nothing = ForeverViews.Member("Bob", null, Array.Empty<ForeverCharacter>(), null);
        Assert.Contains("No characters on this server's roster.", Text(nothing));
        Assert.Empty(All<ThumbnailComponent>(nothing));
    }

    // ===== Recipes on the Professions tab =====

    private static ForeverArmoryCard CardWithRecipes(params ForeverKnownRecipe[] tailoring) => Card() with
    {
        Professions = new[]
        {
            new ForeverArmoryProfession("Tailoring", 107, 150, Id: 197, Recipes: tailoring.Length, Known: tailoring),
            // Nothing known, and one whose id the armory didn't send: neither can be listed.
            new ForeverArmoryProfession("Fishing", 150, 150, Id: 356, IsPrimary: false, Known: Array.Empty<ForeverKnownRecipe>()),
            new ForeverArmoryProfession("Rune_carving", 12, 0, Recipes: 1, Known: new[] { new ForeverKnownRecipe(1, "Odd") })
        }
    };

    [Fact]
    public void The_professions_tab_lists_a_professions_recipes_from_its_menu()
    {
        var card = CardWithRecipes(
            new ForeverKnownRecipe(2385, "Brown Linen Vest"), new ForeverKnownRecipe(2963, "Bolt of *Linen* Cloth"), new ForeverKnownRecipe(0, "brown linen vest"));

        var skills = ForeverViews.Card(card, "p", Caller);
        var menu = Menu(skills, ForeverViews.CardMenuPrefix);
        Assert.Equal("forever_cardmenu~7~n~0~Asha~Brightvale", menu.CustomId);
        Assert.Equal(new[] { ("Tailoring recipes", "k197", "3 known", false) }, menu.Options.Select(o => (o.Label, o.Value, o.Description, o.IsDefault == true)));
        AssertFits(skills);

        // Opened from the roster, the way back is kept through the menu too.
        var recipes = ForeverViews.Card(card, "rk197", Caller);
        Assert.Equal(new[]
        {
            "### 🧵 [Tailoring](https://www.wowhead.com/forever/skill=197) · 2 recipes",
            "-# 107/150 · Journeyman · `/forever craft` shows what one takes and who else makes it",
            @"Bolt of \*Linen\* Cloth · Brown Linen Vest",
            "-# From Blizzard's armory · updates when the character logs out · profession names open Wowhead"
        }, Lines(recipes).Skip(3));
        var back = Menu(recipes, ForeverViews.CardMenuPrefix);
        Assert.Equal("forever_cardmenu~7~r~0~Asha~Brightvale", back.CustomId);
        Assert.Equal(new[] { ("Skill bars", "p", false), ("Tailoring recipes", "k197", true) }, back.Options.Select(o => (o.Label, o.Value, o.IsDefault == true)));
        // The Professions tab stays lit, and every tab still leads back to the roster.
        Assert.Equal(
            new[] { ("Overview", "forever_card~7~ro~0~Asha~Brightvale", ButtonStyle.Primary), ("Gear", "forever_card~7~rg~0~Asha~Brightvale", ButtonStyle.Primary),
                ("Professions", "forever_card~7~rp~0~Asha~Brightvale", ButtonStyle.Success) },
            Buttons(recipes).Take(3).Select(b => (b.Label, b.CustomId, b.Style)));
        Assert.Contains(Buttons(recipes), b => b.Label == "Roster");
        AssertFits(recipes);

        // A profession the character doesn't have, or has nothing listed for, shows the skill bars instead.
        Assert.Equal(Text(skills), Text(ForeverViews.Card(card, "k999", Caller)));
        Assert.Equal(Text(skills), Text(ForeverViews.Card(card, "k356", Caller)));
        // The other tabs have no recipe menu, and neither has a character whose recipes the armory didn't send.
        Assert.Empty(All<SelectMenuComponent>(ForeverViews.Card(card, "o", Caller)));
        Assert.Empty(All<SelectMenuComponent>(ForeverViews.Card(card, "g", Caller)));
        Assert.Empty(All<SelectMenuComponent>(ForeverViews.Card(Card(), "p", Caller)));
    }

    [Fact]
    public void A_long_recipe_list_is_cut_at_a_whole_name_and_says_how_many_are_left()
    {
        var many = Enumerable.Range(1, 600).Select(i => new ForeverKnownRecipe(i, $"Recipe Number {i:D3} of the Long_Name")).ToArray();

        var card = ForeverViews.Card(CardWithRecipes(many), "k197", Caller);

        AssertFits(card);
        var body = Lines(card).Skip(3).ToArray();
        Assert.StartsWith("### 🧵 [Tailoring](https://www.wowhead.com/forever/skill=197) · 600 recipes", body[0]);
        var shown = body[2].Split(" · ");
        Assert.All(shown, name => Assert.Matches(@"^Recipe Number \d{3} of the Long\\_Name$", name));
        Assert.Equal($"-# …and {600 - shown.Length} more", body[3]);
        Assert.InRange(shown.Length, 50, 599);
    }

    [Fact]
    public void View_codes_name_a_tab_and_anything_else_is_the_overview()
    {
        Assert.Equal(new[] { "o", "g", "p", "k197", "o", "g", "p", "k197" },
            new[] { "o", "g", "p", "k197", "ro", "rg", "rp", "rk197" }.Select(ForeverViews.Tab));
        Assert.Equal(new int?[] { 197, 164, null, null, null, null, null, null },
            new[] { "k197", "rk164", "p", "k", "kx", "k0", "k-5", null }.Select(ForeverViews.RecipesProfession));
        Assert.Equal(new[] { "o", "o", "o", "o", "o" }, new[] { "k", "kx", "k0", "zz", null }.Select(ForeverViews.Tab));
        Assert.True(ForeverViews.IsFromRoster("rk197"));
        Assert.False(ForeverViews.IsFromRoster("k197"));
    }

    // ===== Guild =====

    [Fact]
    public void The_guild_card_shows_what_the_armory_has_and_opens_the_roster()
    {
        var armory = new ForeverArmoryGuild(1, "Synthetic Guild", "synthetic-guild", ForeverRegion.US, ForeverFaction.Horde, ForeverRuleset.PvP, "PvP", 1234);

        var justSet = ForeverViews.Guild(MatchedGuild, "Server_One", "https://cdn.example/icon.png", Caller, armory, justSet: true);
        Assert.Equal(new[]
        {
            "# 🏰 Synthetic Guild",
            @"WoW Forever guild for **Server\_One**",
            "🌍 **Region** US",
            "📜 **Ruleset** PvP",
            "🔴 **Faction** Horde",
            "👥 **Members** 1,234",
            "Members can now save their characters with `/forever char add`, and `/forever craft` shows who can make what.",
            "-# From Blizzard's armory"
        }, Lines(justSet));
        Assert.Equal(("View roster", "forever_roster~7~0r"), Buttons(justSet).Select(b => (b.Label, b.CustomId)).Single());
        Assert.Single(All<ThumbnailComponent>(justSet));
        AssertFits(justSet);

        // Only showing it: no "what next", and no member count when the armory didn't answer.
        var shown = ForeverViews.Guild(MatchedGuild, "Server", null, Caller);
        Assert.DoesNotContain("Members", Text(shown));
        Assert.Single(Buttons(shown));

        // A guild saved before the armory was the only way in has no slug; an officer can match it by setting it again.
        var typed = new ForeverGuild
        {
            Name = "Typed", Region = ForeverRegion.EU, Ruleset = ForeverRuleset.Normal, Faction = ForeverFaction.Alliance,
            SetByName = "officer_one", TimeSet = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc)
        };
        var unmatched = ForeverViews.Guild(typed, "Server", null, Caller);
        Assert.Contains("🪪 **Armory** not matched yet. An officer can run `/forever guild set` again", Text(unmatched));
        Assert.Equal(@"-# Added by members, not checked against Blizzard's roster · set by officer\_one on 2026-11-18", Lines(unmatched).Last());
        Assert.Equal(new Color(0, 112, 221), Accent(unmatched));
        Assert.DoesNotContain("Roster** o", Text(unmatched));

        var none = ForeverViews.NoGuild("Server", null);
        Assert.Contains("None set for **Server** yet", Text(none));
        Assert.Contains("`/forever guild set`", Text(none));
    }

    // ===== Help =====

    private static readonly ForeverGuild OtherGuild = new()
    {
        Name = "Synthetic *Guild*", Region = ForeverRegion.EU, Ruleset = ForeverRuleset.Normal, Faction = ForeverFaction.Alliance
    };

    private static ForeverSetup Setup(ForeverGuild guild = null, int characters = 0, string main = null, int shared = 0, bool officer = false)
        => new(guild, characters, main, shared, officer);

    private static MessageComponent Help(ForeverSetup setup) => ForeverViews.Help("Server_One", "https://cdn.example/icon.png", Caller, setup);

    [Fact]
    public void Help_for_a_new_member_in_a_new_server_starts_with_saving_a_character()
    {
        var card = Help(Setup());

        Assert.Equal(new[]
        {
            "# 🧭 Getting started with WoW Forever",
            @"in **Server\_One** · **0 of 3** steps done",
            "⬜ **Set this server's guild** · an officer runs `/forever guild set`",
            "👉 **Save your character** · `/forever char add`",
            "⬜ **Put it on the roster** · `/forever char list`, pick it, then **Show on roster**",
            "### Commands"
        }, Lines(card).Take(6));
        Assert.Contains("level 10", Text(card));
        Assert.Contains("Saving a character is private", Text(card));
        Assert.Contains("TradeSkillMaster's regional averages", Text(card));
        Assert.StartsWith("-# ", Lines(card).Last());
        Assert.Empty(Buttons(card));
        Assert.Equal("https://cdn.example/icon.png", Assert.Single(All<ThumbnailComponent>(card)).Media.Url);
        Assert.Equal(new Color(0xE0B04A), Accent(card));
        AssertFits(card);
    }

    [Fact]
    public void Help_ticks_off_what_is_done_and_offers_the_roster_once_there_is_a_guild()
    {
        var card = Help(Setup(MatchedGuild, characters: 2, main: "Asha Bright_vale", shared: 1, officer: true));

        Assert.Equal(new[]
        {
            "# 🧭 Getting started with WoW Forever",
            @"in **Server\_One** · all set up 🎉",
            "✅ **Guild set** · Synthetic Guild · US · PvP · Horde",
            @"✅ **2 characters saved** · ★ Asha Bright\_vale is your main here",
            "✅ **1 on the roster** · your name shows next to it"
        }, Lines(card).Take(5));
        Assert.DoesNotContain("👉", Text(card));
        Assert.Equal(("View roster", "forever_roster~7~0r"), Buttons(card).Select(b => (b.Label, b.CustomId)).Single());
        Assert.Equal(new Color(200, 35, 35), Accent(card));
        AssertFits(card);

        // The roster needs nothing switched on: a guild is enough for the button, whatever older rows say.
        var other = Help(Setup(OtherGuild, characters: 1));
        Assert.Contains(@"✅ **Guild set** · Synthetic \*Guild\* · EU · Normal · Alliance", Text(other));
        Assert.Contains("✅ **1 character saved**\n", Text(other));
        Assert.Contains("**2 of 3** steps done", Text(other));
        Assert.Single(Buttons(other));
        Assert.Equal(new Color(0, 112, 221), Accent(other));
        Assert.Contains("✅ **3 on the roster** · your name shows next to them", Text(Help(Setup(MatchedGuild, 3, "Asha Brightvale", 3))));
        AssertFits(Help(Setup(new ForeverGuild { Name = new string('*', 64) }, int.MaxValue, new string('_', 200), int.MaxValue)));
    }

    [Fact]
    public void Help_points_at_the_first_step_the_caller_can_do_right_now()
    {
        // The three checklist lines' marks, in order: guild, character, roster.
        static string Marks(ForeverSetup setup)
            => string.Concat(Lines(Help(setup)).Skip(2).Take(3).Select(line => line[..line.IndexOf(' ')]));

        // An officer starts with the guild. A member can't set it, so starts with a character.
        Assert.Equal("👉⬜⬜", Marks(Setup(officer: true)));
        Assert.Equal("⬜👉⬜", Marks(Setup()));
        Assert.Equal("✅👉⬜", Marks(Setup(OtherGuild)));
        // The roster needs both a character and the server's guild.
        Assert.Equal("⬜✅⬜", Marks(Setup(characters: 1)));
        Assert.Equal("👉✅⬜", Marks(Setup(characters: 1, officer: true)));
        Assert.Equal("✅✅👉", Marks(Setup(MatchedGuild, characters: 2, main: "Asha Brightvale")));
        Assert.Equal("✅✅✅", Marks(Setup(MatchedGuild, characters: 2, main: "Asha Brightvale", shared: 1)));

        // Officers are told the command; everyone else is told who runs it.
        Assert.Contains("**Set this server's guild** · `/forever guild set`", Text(Help(Setup(officer: true))));
        Assert.DoesNotContain("an officer runs", Text(Help(Setup(officer: true))));
    }

    [Fact]
    public void Empty_cards_point_at_help()
    {
        Assert.Equal("-# " + ForeverViews.HelpPointer, Lines(ForeverViews.NoGuild("Server", null)).Last());
        Assert.Equal("-# " + ForeverViews.HelpPointer, Lines(ForeverViews.CharacterList(Array.Empty<ForeverCharacter>(), null, new HashSet<long>(), Caller)).Last());
        Assert.Contains("`/forever help`", ForeverViews.HelpPointer);
    }

    // ===== Notices =====

    [Fact]
    public void Notices_are_small_cards_in_three_tones()
    {
        var done = ForeverViews.Done("📋 Removed from this server's roster.");
        Assert.Equal("📋 Removed from this server's roster.", Text(done));
        Assert.Equal(new Color(0x3BA55D), Accent(done));

        Assert.Equal(new Color(0xE0B04A), Accent(ForeverViews.Info("This server had no Forever guild set.")));

        var problem = ForeverViews.Problem("Pick one of your characters from the list.");
        Assert.Equal("⚠️ Pick one of your characters from the list.", Text(problem));
        Assert.Equal(new Color(0xF0B132), Accent(problem));
        Assert.Equal("⚠️ That didn't work. Please try again.", Text(ForeverViews.Problem(null)));
        AssertFits(ForeverViews.Problem(new string('x', 9000)));
    }

    // ===== Autocomplete =====

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

    [Fact]
    public void Lookup_suggestions_add_guildmates_from_the_armory_and_characters_shared_here()
    {
        var mine = new[] { Shared(11, Alice, "Asha") };
        var roster = Guildmates()
            .Append(FakeForeverArmory.Member("Low", "Bie", level: 9, ForeverClass.Hunter))
            .Append(FakeForeverArmory.Member("Two Words", "Surname", level: 30))
            .ToArray();
        var shared = new[] { Shared(21, Bob, "Private", "Alt", ForeverClass.Rogue), Shared(22, Bob, "Mira", "Dawn", ForeverClass.Druid) };

        // Nothing typed: yours, then guildmates the armory can show (level 10+) by rank and level, then what others shared.
        var all = ForeverSuggest.Lookup(mine, "", roster, shared);
        Assert.Equal(
            new[]
            {
                ("Asha Brightvale — Priest · US · PvP · Horde", "11"),
                ("Mira Dawn · 40 Druid · guild roster", "Mira Dawn"),
                ("Thalen Duskvale · 12 Warrior · guild roster", "Thalen Duskvale"),
                ("Private Alt · Rogue · shared in this server", "Private Alt")
            },
            all.Select(s => (s.Name, (string)s.Value)));

        // A surname matches too.
        Assert.Equal(new[] { "Thalen Duskvale" }, ForeverSuggest.Lookup(mine, "dusk", roster, shared).Select(s => (string)s.Value));

        // The armory's answer for the typed name leads, in the armory's spelling, and isn't repeated from the roster.
        var live = ForeverLookup<ForeverArmoryCharacter>.Found(Armory("Mira", "Dawn"));
        var found = ForeverSuggest.Lookup(mine, "mira dawn", roster, shared, live);
        Assert.Equal(("Mira Dawn · 29 Undead Mage · Synthetic Guild · on the armory", "Mira Dawn"), (found[0].Name, (string)found[0].Value));
        Assert.Single(found);

        var missing = ForeverSuggest.Lookup(mine, "zed nobody", roster, shared, ForeverLookup<ForeverArmoryCharacter>.NotFound());
        Assert.Equal("Look up Zed Nobody on the armory", Assert.Single(missing).Name);

        var big = Enumerable.Range(1, 60).Select(i => FakeForeverArmory.Member("Member", "N" + new string('x', i % 20 + 1) + (char)('a' + i % 26), level: 20)).ToArray();
        var capped = ForeverSuggest.Lookup(mine, "", big, shared);
        Assert.Equal(25, capped.Count);
        Assert.All(capped, s => Assert.InRange(s.Name.Length, 1, 100));
    }

    [Fact]
    public void Adding_suggests_guildmates_not_saved_yet_around_the_typed_name()
    {
        var mine = new[] { Shared(11, Alice, "Asha"), Shared(12, Alice, "Mira", "Dawn", region: ForeverRegion.EU) };
        var roster = Guildmates().Append(FakeForeverArmory.Member("Mira", "Dusk", level: 9, ForeverClass.Hunter)).ToArray();

        // Nothing typed: guildmates by rank and level. Asha is already saved in US; the EU Mira isn't this one.
        // A guildmate below level 10 is offered too, because the roster can stand in for the armory.
        var all = ForeverSuggest.Add(mine, ForeverRegion.US, "", roster);
        Assert.Equal(
            new[]
            {
                ("Mira Dawn · 40 Druid · guild roster", "Mira Dawn"), ("Thalen Duskvale · 12 Warrior · guild roster", "Thalen Duskvale"),
                ("Mira Dusk · 9 Hunter · guild roster", "Mira Dusk"), ("New Recruit · guild roster", "New Recruit")
            },
            all.Select(s => (s.Name, (string)s.Value)));
        Assert.Equal(new[] { "Thalen Duskvale" }, ForeverSuggest.Add(mine, ForeverRegion.US, "th", roster).Select(s => (string)s.Value));
        // A surname matches too.
        Assert.Equal(new[] { "Mira Dusk", "Thalen Duskvale" }, ForeverSuggest.Add(mine, ForeverRegion.US, "dusk", roster).Select(s => (string)s.Value).OrderBy(v => v));

        // A full name leads: with what the armory said about it, or as typed when it said nothing.
        var live = ForeverLookup<ForeverArmoryCharacter>.Found(Armory("Mira", "Dawn"));
        var found = ForeverSuggest.Add(mine, ForeverRegion.US, "mira dawn", roster, live);
        Assert.Equal(("Mira Dawn · 29 Undead Mage · Synthetic Guild · on the armory", "Mira Dawn"), (found[0].Name, (string)found[0].Value));
        Assert.Single(found);
        Assert.Equal(("Save Zed Nobody", "Zed Nobody"), ForeverSuggest.Add(mine, ForeverRegion.US, "zed nobody").Select(s => (s.Name, (string)s.Value)).Single());

        // One the caller already has says so instead of offering to save it twice.
        Assert.Equal("Asha Brightvale · already saved", ForeverSuggest.Add(mine, ForeverRegion.US, "asha brightvale", roster)[0].Name);

        var hint = Assert.Single(ForeverSuggest.Add(mine, ForeverRegion.US, "as"));
        Assert.Equal((ForeverSuggest.LookupHint, "0"), (hint.Name, (string)hint.Value));
        var big = Enumerable.Range(1, 60).Select(i => FakeForeverArmory.Member("Member", "N" + new string('x', i % 20 + 1) + (char)('a' + i % 26), level: 20)).ToArray();
        Assert.Equal(25, ForeverSuggest.Add(mine, ForeverRegion.US, "", big).Count);
    }

    [Fact]
    public void Adding_puts_real_guildmates_above_a_name_nothing_confirmed()
    {
        var mine = new[] { Shared(11, Alice, "Asha") };
        var roster = Guildmates()
            .Append(FakeForeverArmory.Member("Mira", "Dusk", level: 9, ForeverClass.Hunter, rank: 5))
            .Append(FakeForeverArmory.Member("Mira", "Duskvale", level: 60, ForeverClass.Mage, rank: 0))
            .ToArray();
        static (string, string)[] Rows(List<AutocompleteResult> list) => list.Select(s => (s.Name, (string)s.Value)).ToArray();

        // Two words that aren't a whole name yet: the guildmates they start, then the words as typed.
        Assert.Equal(
            new[]
            {
                ("Mira Duskvale · 60 Mage · guild roster", "Mira Duskvale"), ("Mira Dusk · 9 Hunter · guild roster", "Mira Dusk"),
                ("Save Mira Du", "Mira Du")
            },
            Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "mira du", roster)));

        // A guildmate's whole name: that guildmate first, above a longer name of higher rank, and no
        // "Save" row for a character the list already has. The armory doesn't know her below level 10.
        Assert.Equal(
            new[] { ("Mira Dusk · 9 Hunter · guild roster", "Mira Dusk"), ("Mira Duskvale · 60 Mage · guild roster", "Mira Duskvale") },
            Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "MIRA dusk", roster, ForeverLookup<ForeverArmoryCharacter>.NotFound())));

        // Typed without the capital the guildmate's surname has: still her, so still no "Save" row.
        var scots = new[] { FakeForeverArmory.Member("Kael", "McBright") };
        Assert.Equal(new[] { ("Kael McBright · 30 Priest · guild roster", "Kael McBright") },
            Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "kael mcbright", scots)));

        // A name the armory confirmed still leads: it is exactly who was asked for.
        var live = ForeverLookup<ForeverArmoryCharacter>.Found(Armory("Mira", "Dusk", guild: "Other Crew"));
        Assert.Equal(
            new[] { ("Mira Dusk · 29 Undead Mage · Other Crew · on the armory", "Mira Dusk"), ("Mira Duskvale · 60 Mage · guild roster", "Mira Duskvale") },
            Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "mira dusk", roster, live)));

        // While the roster is on its way the list says so, last, and without it there is nothing else to say.
        Assert.Equal(new[] { ("Save Zed Nobody", "Zed Nobody"), (ForeverSuggest.RosterLoadingHint, "0") },
            Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "zed nobody", null, null, rosterLoading: true)));
        Assert.Equal(new[] { (ForeverSuggest.RosterLoadingHint, "0") }, Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "", null, null, rosterLoading: true)));

        // Twenty-five rows at most, with the typed name and the loading note kept in.
        var big = Enumerable.Range(1, 60).Select(i => FakeForeverArmory.Member("Member", "N" + new string('x', i % 20 + 1) + (char)('a' + i % 26), level: 20)).ToArray();
        var full = Rows(ForeverSuggest.Add(mine, ForeverRegion.US, "member nx", big, null, rosterLoading: true));
        Assert.Equal(25, full.Length);
        Assert.Equal(new[] { ("Save Member Nx", "Member Nx"), (ForeverSuggest.RosterLoadingHint, "0") }, full[^2..]);
        Assert.All(full[..^2], row => Assert.EndsWith("guild roster", row.Item1));
    }

    [Fact]
    public void A_box_gives_the_roster_only_what_is_left_of_its_time()
    {
        var second = System.Diagnostics.Stopwatch.Frequency;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        // Asked a second ago with a second and a half to answer in: about half a second is left.
        Assert.InRange(ForeverSuggest.Left(TimeSpan.FromMilliseconds(1500), now - second), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        // Out of time is no time, not a negative wait.
        Assert.Equal(TimeSpan.Zero, ForeverSuggest.Left(TimeSpan.FromMilliseconds(1500), now - 5 * second));
        // Discord allows three seconds in all; the box keeps well inside that.
        Assert.InRange(ForeverSuggest.Budget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Looking_up_puts_real_characters_above_a_name_nothing_confirmed()
    {
        var mine = new[] { Shared(11, Alice, "Mira", "Dane") };
        var roster = Guildmates();
        var shared = new[] { Shared(21, Bob, "Mira", "Dale") };
        static (string, string)[] Rows(List<AutocompleteResult> list) => list.Select(s => (s.Name, (string)s.Value)).ToArray();

        var rows = Rows(ForeverSuggest.Lookup(mine, "mira da", roster, shared));

        Assert.Equal(
            new[] { "11", "Mira Dawn", "Mira Dale", "Mira Da" },
            rows.Select(r => r.Item2).ToArray());
        Assert.Equal("Look up Mira Da on the armory", rows[^1].Item1);

        // A guildmate's whole name needs no "Look up" row of its own.
        Assert.Equal(new[] { "Mira Dawn" }, Rows(ForeverSuggest.Lookup(mine, "mira dawn", roster, shared)).Select(r => r.Item2).ToArray());

        // Still loading: the caller's own, the typed name, then the note.
        Assert.Equal(new[] { "11", "Mira Da", "0" },
            Rows(ForeverSuggest.Lookup(mine, "mira da", null, null, null, rosterLoading: true)).Select(r => r.Item2).ToArray());
        var big = Enumerable.Range(1, 60).Select(i => FakeForeverArmory.Member("Mira", "Da" + new string('x', i % 20 + 1) + (char)('a' + i % 26), level: 20)).ToArray();
        var full = Rows(ForeverSuggest.Lookup(mine, "mira da", big, shared, null, rosterLoading: true));
        Assert.Equal(25, full.Length);
        Assert.Equal(new[] { "Mira Da", "0" }, full[^2..].Select(r => r.Item2).ToArray());
    }

    [Fact]
    public void Guild_suggestions_show_what_the_armory_found_then_the_callers_own_guilds()
    {
        var us = new ForeverArmoryGuild(1, "Synthetic Guild", "synthetic-guild", ForeverRegion.US, ForeverFaction.Horde, ForeverRuleset.PvP, "PvP", 1234);
        var eu = new ForeverArmoryGuild(2, "Synthetic Guild", "synthetic-guild", ForeverRegion.EU, ForeverFaction.Alliance, null, "Hardcore", 1);
        var mine = new[] { Armory(), Armory("Mira", "Dawn", guild: "Other Crew"), Armory("No", "Guild", guild: null) };

        // Found in two regions: both are offered, so the officer picks the region to match. Asha's guild is the US one already.
        var found = ForeverSuggest.Guilds("synthetic guild", new[] { eu, us }, mine, checkedEverywhere: true);
        Assert.Equal(
            new[]
            {
                ("Synthetic Guild · US · PvP · Horde · 1,234 members", "Synthetic Guild"),
                ("Synthetic Guild · EU · Hardcore · Alliance · 1 member", "Synthetic Guild")
            },
            found.Select(s => (s.Name, (string)s.Value)));

        // Nothing typed yet: the guilds the caller's own characters are in.
        var own = ForeverSuggest.Guilds("", Array.Empty<ForeverArmoryGuild>(), mine, checkedEverywhere: false);
        Assert.Equal(
            new[] { ("Other Crew · US · Mira Dawn's guild", "Other Crew"), ("Synthetic Guild · US · Asha Brightvale's guild", "Synthetic Guild") },
            own.Select(s => (s.Name, (string)s.Value)));

        // Not found: the typed name stays selectable, and only says "not found" when the armory really answered.
        var typed = Assert.Single(ForeverSuggest.Guilds("Typed Name", Array.Empty<ForeverArmoryGuild>(), mine, checkedEverywhere: true));
        Assert.Equal(("Typed Name · not found on the armory", "Typed Name"), (typed.Name, (string)typed.Value));
        Assert.Equal("Typed Name", Assert.Single(ForeverSuggest.Guilds("Typed Name", Array.Empty<ForeverArmoryGuild>(), mine, checkedEverywhere: false)).Name);

        Assert.False(ForeverSuggest.WorthAsking("ab"));
        Assert.True(ForeverSuggest.WorthAsking(" abc "));
        Assert.All(ForeverSuggest.Guilds(new string('g', 64), Array.Empty<ForeverArmoryGuild>(), mine, true), s => Assert.InRange(s.Name.Length, 1, 100));
    }

    [Fact]
    public async Task A_slow_armory_answer_is_skipped_but_keeps_running()
    {
        var quick = await ForeverSuggest.WithinAsync(Task.FromResult("answer"), TimeSpan.FromSeconds(5));
        Assert.Equal("answer", quick);

        var slow = new TaskCompletionSource<string>();
        Assert.Null(await ForeverSuggest.WithinAsync(slow.Task, TimeSpan.FromMilliseconds(20)));
        slow.SetResult("late");
        Assert.Equal("late", await slow.Task);

        Assert.Null(await ForeverSuggest.WithinAsync(Task.FromException<string>(new InvalidOperationException("boom")), TimeSpan.FromSeconds(5)));
    }
}
