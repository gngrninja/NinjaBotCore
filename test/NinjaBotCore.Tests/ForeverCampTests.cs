using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services;
using Xunit;
using static NinjaBotCore.Tests.ForeverCardReader;

namespace NinjaBotCore.Tests;

// Camps: who brings what, and the public card that remembers its own sign-ups. Every user and character is SYNTHETIC.
public sealed class ForeverCampTests
{
    private const int FishBowl = 52639, CampTent = 52647, CampChair = 52644, FirstAidKit = 52641, SharpeningWheel = 52645, BasicCampfire = 52640;

    private static ForeverArmoryProfession Knows(int professionId, string profession, params (int Id, string Name)[] recipes)
        => new(profession, 60, 75, professionId, recipes.Length, Known: recipes.Select(r => new ForeverKnownRecipe(r.Id, r.Name)).ToList());

    private static ForeverCamper Camper(ulong user, string name, ForeverClass? characterClass, params ForeverArmoryProfession[] professions)
        => new(user, name, 30, characterClass, professions);

    private static readonly ForeverArmoryProfession Fishing = Knows(356, "Fishing", (FishBowl, "Fish Bowl"));
    private static readonly ForeverArmoryProfession Leatherworking = Knows(165, "Leatherworking", (CampTent, "Camp Tent"), (2149, "Handstitched Leather Boots"));
    private static readonly ForeverArmoryProfession Skinning = Knows(393, "Skinning", (CampChair, "Camp Chair"));
    private static readonly ForeverArmoryProfession FirstAid = Knows(129, "First Aid", (FirstAidKit, "First Aid Kit"));
    private static readonly ForeverArmoryProfession Cooking = Knows(185, "Cooking", (BasicCampfire, "Basic Campfire"), (2538, "Charred Wolf Meat"));

    [Fact]
    public void A_plan_gives_each_camper_one_thing_and_never_the_same_thing_twice()
    {
        var asha = Camper(1, "Asha Brightvale", ForeverClass.Mage, Fishing, Leatherworking, Cooking);
        var thalen = Camper(2, "Thalen Duskvale", ForeverClass.Warrior, Fishing);
        var mira = Camper(3, "Mira Dawn", ForeverClass.Rogue, Skinning, FirstAid);

        var plan = ForeverCamp.Plan(new[] { asha, thalen, mira });

        // Asha could bring the Fish Bowl, but then Thalen would have nothing to bring: it moves to him and she brings the tent.
        Assert.Equal(
            new[] { ("Fish Bowl", "Thalen Duskvale"), ("Camp Tent", "Asha Brightvale"), ("Camp Chair", "Mira Dawn") },
            plan.Picks.Select(p => (p.Recipe, p.Camper.CharacterName)));
        Assert.Equal(3, plan.Picks.Select(p => p.Camper).Distinct().Count());
        // Everything anyone could make is still on offer, picked or not.
        Assert.Equal(new[] { "Mira Dawn" }, plan.Offers.Single(o => o.Feature.Name == "First Aid Kit").Makers.Select(m => m.Camper.CharacterName));
        Assert.Equal(2, plan.Offers.Single(o => o.Feature.Name == "Fish Bowl").Makers.Count);
        Assert.Empty(plan.Offers.Single(o => o.Feature.Name == "Mana Well").Makers);
        Assert.Equal(10, plan.Offers.Count);

        Assert.Equal(("Asha Brightvale", "Basic Campfire"), plan.FireStarters.Select(f => (f.Camper.CharacterName, f.Recipe)).Single());
        Assert.False(plan.UpgradedFire);
    }

    [Fact]
    public void What_a_class_here_already_buffs_is_picked_last()
    {
        // A Priest's Fortitude makes the First Aid Kit add nothing, so the wheel goes first even though the kit usually ranks higher.
        var healer = Camper(1, "Mira Dawn", ForeverClass.Priest, FirstAid);
        var smith = Camper(2, "Thalen Duskvale", ForeverClass.Warrior, Knows(164, "Blacksmithing", (SharpeningWheel, "Sharpening Wheel")));

        var plan = ForeverCamp.Plan(new[] { healer, smith });

        Assert.Equal(new[] { "Sharpening Wheel", "First Aid Kit" }, plan.Picks.Select(p => p.Recipe));
        var kit = plan.Offers.Single(o => o.Feature.Name == "First Aid Kit");
        Assert.Equal(("Power Word: Fortitude", "Mira Dawn"), (kit.Feature.ReplacedBuff, kit.CoveredBy.Single().CharacterName));
        Assert.Empty(plan.Offers.Single(o => o.Feature.Name == "Sharpening Wheel").CoveredBy);
        Assert.Empty(plan.FireStarters);

        // Without the Priest the kit is back in its usual place, ahead of the wheel.
        var plain = ForeverCamp.Plan(new[] { healer with { Class = ForeverClass.Rogue }, smith });
        Assert.Equal(new[] { "First Aid Kit", "Sharpening Wheel" }, plain.Picks.Select(p => p.Recipe));
    }

    [Fact]
    public void Upgraded_camp_recipes_count_once_the_recipe_list_can_recognise_them()
    {
        var catalog = new FakeForeverCatalog();
        catalog.Add(SharpeningWheel, "Sharpening Wheel", 164, "Blacksmithing", ForeverCamp.Category);
        catalog.Add(57020, "Anvil", 164, "Blacksmithing", ForeverCamp.Category);
        catalog.Add(1631, "Rough Sharpening Stone", 164, "Blacksmithing", "Weapon Stones");
        catalog.Add(BasicCampfire, "Basic Campfire", 185, "Cooking", ForeverCamp.Category);
        catalog.Add(57001, "Roaring Campfire", 185, "Cooking", ForeverCamp.Category);
        // Filed under Camping, but in another profession: it is not this feature's upgrade.
        catalog.Add(57099, "Odd One", 197, "Tailoring", ForeverCamp.Category);
        var recipes = catalog.RecipesIfLoaded;

        var smith = Camper(1, "Thalen Duskvale", ForeverClass.Warrior,
            Knows(164, "Blacksmithing", (SharpeningWheel, "Sharpening Wheel"), (57020, "Anvil"), (57099, "Odd One"), (1631, "Rough Sharpening Stone")));
        var cook = Camper(2, "Mira Dawn", ForeverClass.Rogue, Knows(185, "Cooking", (BasicCampfire, "Basic Campfire"), (57001, "Roaring Campfire")));

        var withList = ForeverCamp.Plan(new[] { smith, cook }, recipes);
        Assert.Equal("Anvil", withList.Picks.Single().Recipe);
        Assert.Equal(("Roaring Campfire", true), (withList.FireStarters.Single().Recipe, withList.UpgradedFire));

        // Without the list only the basic recipes are known for what they are.
        var without = ForeverCamp.Plan(new[] { smith, cook });
        Assert.Equal("Sharpening Wheel", without.Picks.Single().Recipe);
        Assert.Equal(("Basic Campfire", false), (without.FireStarters.Single().Recipe, without.UpgradedFire));

        // Someone who knows only the upgrade still counts with the list, and not without it.
        var onlyAnvil = Camper(3, "Asha Brightvale", ForeverClass.Mage, Knows(164, "Blacksmithing", (57020, "Anvil")));
        Assert.Equal("Anvil", ForeverCamp.Plan(new[] { onlyAnvil }, recipes).Picks.Single().Recipe);
        Assert.Empty(ForeverCamp.Plan(new[] { onlyAnvil }).Picks);
    }

    [Fact]
    public void Recipes_are_matched_by_name_when_the_armory_sends_no_ids()
    {
        var engineer = Camper(1, "Asha Brightvale", ForeverClass.Mage,
            new ForeverArmoryProfession("Engineering", 40, 75, 202, 1, Known: new[] { new ForeverKnownRecipe(0, "reagent bot") }),
            new ForeverArmoryProfession("Fishing", 40, 75, null, 1, Known: new[] { new ForeverKnownRecipe(0, "Fish Bowl") }));
        // The right name in the wrong profession is some other recipe.
        var tailor = Camper(2, "Mira Dawn", ForeverClass.Rogue, Knows(197, "Tailoring", (0, "Fish Bowl")));
        var unknown = new ForeverCamper(3, "Thalen Duskvale", 12, ForeverClass.Warrior, null);

        var plan = ForeverCamp.Plan(new[] { engineer, tailor, unknown });

        Assert.Equal(new[] { "Fish Bowl" }, plan.Picks.Select(p => p.Recipe));
        Assert.Equal("reagent bot", plan.Offers.Single(o => o.Feature.Name == "Reagent Bot").Makers.Single().Recipe);
        Assert.Empty(ForeverCamp.Plan(Array.Empty<ForeverCamper>()).Picks);
    }

    [Fact]
    public void A_plan_never_lists_more_than_the_best_campfire_holds()
    {
        // Twelve campers who each know everything: ten features, ten picks, ten different people.
        var all = ForeverCamp.Features.Select(f => Knows(f.ProfessionId, f.Profession, (f.RecipeId, f.Name))).ToArray();
        var campers = Enumerable.Range(1, 12).Select(i => Camper((ulong)i, "Camper " + i, ForeverClass.Warrior, all)).ToArray();

        var plan = ForeverCamp.Plan(campers);

        Assert.Equal(ForeverCamp.MaxCapacity, plan.Picks.Count);
        Assert.Equal(10, plan.Picks.Select(p => p.Feature.Name).Distinct().Count());
        Assert.Equal(10, plan.Picks.Select(p => p.Camper.UserId).Distinct().Count());
        Assert.Equal(ForeverCamp.Features.Select(f => f.Name), plan.Picks.Select(p => p.Feature.Name));
    }

    // ===== The card =====

    [Fact]
    public void The_camp_card_says_who_is_in_who_brings_what_and_what_is_missing()
    {
        var asha = Camper(501, "Asha Bright_vale", ForeverClass.Priest, Fishing, FirstAid, Cooking);
        var thalen = Camper(502, "Thalen Duskvale", ForeverClass.Warrior, Fishing, Leatherworking);
        var quiet = new ForeverCamper(503, "Mira Dawn", 0, null, null);
        var state = new ForeverCampState("Fishing night at Menethil", 501, new ulong[] { 501, 502, 503, 504 });

        var card = ForeverViews.Camp(state, new[] { asha, thalen, quiet }, ForeverCamp.Plan(new[] { asha, thalen, quiet }));

        Assert.Equal(new[]
        {
            "# 🔥 Fishing night at Menethil",
            "Started by <@501>",
            "### 🏕️ Who's in · 4",
            @"- <@501> · ✨ **Asha Bright\_vale** · 30 Priest",
            "- <@502> · ⚔️ **Thalen Duskvale** · 30 Warrior",
            "- <@503> · ❔ **Mira Dawn**",
            "- <@504> · no saved character any more",
            "### 🎒 Who brings what",
            @"1. 🎣 **Fish Bowl** · +8% to all stats → Asha Bright\_vale",
            "2. 🥾 **Camp Tent** · rested XP worth 5% of a level → Thalen Duskvale",
            "-# One object per player. A Basic Campfire holds 3.",
            @"🔥 **Campfire** Asha Bright\_vale can light it",
            @"-# ⚠️ First Aid Kit doesn't stack with Power Word: Fortitude, which Asha Bright\_vale can cast",
            @"-# Also possible: 🩹 First Aid Kit (Asha Bright\_vale)",
            "-# Nobody here makes: 🔪 Camp Chair (Skinning) · 🪄 Enchanted Lute (Enchanting) · ⚒️ Sharpening Wheel (Blacksmithing) · 🌿 Incense Candle (Herbalism) · ⚗️ Mana Well (Alchemy) · 🧵 Faction Banner (Tailoring) · ⚙️ Reagent Bot (Engineering)",
            "-# The armory has no professions for Mira Dawn yet, so nothing is counted for them",
            "-# Joining shows your main here · crafts come from Blizzard's armory, which updates when a character logs out · save a character with /forever char add"
        }, Lines(card));
        Assert.Equal(
            new[] { ("I'm in", "forever_camp~join", false), ("Leave", "forever_camp~leave", false), ("Refresh", "forever_camp~refresh", false) },
            Buttons(card).Select(b => (b.Label, b.CustomId, b.IsDisabled)));
        AssertFits(card);
    }

    [Fact]
    public void An_empty_or_full_camp_still_makes_a_card_that_fits()
    {
        var empty = ForeverViews.Camp(new ForeverCampState("Camp", 501, Array.Empty<ulong>()), Array.Empty<ForeverCamper>(), ForeverCamp.Plan(Array.Empty<ForeverCamper>()));
        Assert.Contains("Nobody yet. Press **I'm in** to join.", Text(empty));
        Assert.DoesNotContain("Who brings what", Text(empty));
        AssertFits(empty);

        // Nobody knows anything yet: the card says what a camp needs instead of an empty list.
        var novice = Camper(501, "Asha Brightvale", ForeverClass.Mage);
        var bare = Text(ForeverViews.Camp(new ForeverCampState("Camp", 501, new ulong[] { 501 }), new[] { novice }, ForeverCamp.Plan(new[] { novice })));
        Assert.Contains("Nobody here knows a camp recipe yet. Each profession teaches one at skill 20.", bare);
        Assert.Contains("⚠️ **Campfire** nobody here knows one yet. It's the first Cooking recipe.", bare);

        var all = ForeverCamp.Features.Select(f => Knows(f.ProfessionId, f.Profession, (f.RecipeId, f.Name))).Append(Cooking).ToArray();
        var crowd = Enumerable.Range(1, ForeverCampState.MaxMembers)
            .Select(i => Camper((ulong)(900000000000000000 + i), "Camper" + new string('x', 20) + " Surname" + new string('y', 16) + i, ForeverClass.Druid, all))
            .ToArray();
        var state = new ForeverCampState(new string('T', ForeverCampState.MaxTitleLength), crowd[0].UserId, crowd.Select(c => c.UserId).ToList());
        var full = ForeverViews.Camp(state, crowd, ForeverCamp.Plan(crowd));
        AssertFits(full);
        Assert.True(Buttons(full).Single(b => b.Label == "I'm in").IsDisabled);
        // Every one of the 25 is still on the card, because the card is where the sign-ups are kept.
        Assert.Equal(state.Members, ForeverCamps.Read(ForeverViews.Texts(full.Components))!.Members);
        // More picks than a Basic Campfire holds: the card says where it stops.
        Assert.Contains("A Basic Campfire holds the first 3; a better campfire holds five or ten.", Text(full));
    }

    [Fact]
    public void A_camp_card_carries_its_own_sign_ups()
    {
        const ulong Host = 900000000000000777;
        var asha = Camper(900000000000000001, "Asha <@900000000000000099> B", ForeverClass.Priest, Fishing, FirstAid, Cooking);
        var state = new ForeverCampState(ForeverCamps.CleanTitle("  Fishing *night* at <@&12345> [Menethil](https://x.example) \n now "), Host,
            new ulong[] { 900000000000000001, 900000000000000002, 18446744073709551615 });
        Assert.Equal("Fishing night at &12345 Menethilhttps://x.example now", state.Title);

        var card = ForeverViews.Camp(state, new[] { asha }, ForeverCamp.Plan(new[] { asha }));
        var read = ForeverCamps.Read(ForeverViews.Texts(card.Components));

        // What comes back from the card is the camp that was drawn: a mention inside a character's name is not a camper,
        // and the host, who is mentioned in the header, is only a camper if they joined.
        Assert.Equal(state.Title, read!.Title);
        Assert.Equal(Host, read.HostId);
        Assert.Equal(state.Members, read.Members);

        // Windows line endings, as Discord may send them back, read the same.
        var crlf = ForeverCamps.Read(ForeverViews.Texts(card.Components).Select(t => t.Replace("\n", "\r\n")));
        Assert.Equal((state.Title, state.Members.Count), (crlf!.Title, crlf.Members.Count));

        var nobody = new ForeverCampState("Camp", Host, Array.Empty<ulong>());
        var emptyCard = ForeverViews.Camp(nobody, Array.Empty<ForeverCamper>(), ForeverCamp.Plan(Array.Empty<ForeverCamper>()));
        Assert.Equal(("Camp", Host, 0), Read(emptyCard));

        // An id that isn't shaped like a Discord id is not a camper, and a card without a real host is not a camp.
        Assert.Null(ForeverCamps.Read(new[] { "# 🔥 Camp\nStarted by <@777>" }));
        Assert.Empty(ForeverCamps.Read(new[] { $"# 🔥 Camp\nStarted by <@{Host}>", "### 🏕️ Who's in · 1\n- <@501> · nobody" })!.Members);

        // Something that isn't a camp card reads as nothing, so a click on it is refused instead of guessed at.
        Assert.Null(ForeverCamps.Read(new[] { "# 🔥 Camp", "### 🏕️ Who's in · 1\n- <@900000000000000001>" }));
        Assert.Null(ForeverCamps.Read(new[] { "# 🔥 Camp\nStarted by nobody" }));
        Assert.Null(ForeverCamps.Read(Array.Empty<string>()));
        Assert.Empty(ForeverViews.Texts(null));

        static (string, ulong, int) Read(MessageComponent c)
        {
            var s = ForeverCamps.Read(ForeverViews.Texts(c.Components))!;
            return (s.Title, s.HostId, s.Members.Count);
        }
    }

    [Fact]
    public void A_camps_name_is_one_plain_line()
    {
        Assert.Equal("Camp", ForeverCamps.CleanTitle(null));
        Assert.Equal("Camp", ForeverCamps.CleanTitle("  \n "));
        Assert.Equal("Camp", ForeverCamps.CleanTitle("<@>#`*_~|[]()\\"));
        Assert.Equal("everyone come fish", ForeverCamps.CleanTitle("@everyone come\tfish"));
        Assert.Equal("Bob's camp: 8pm, bring rods!", ForeverCamps.CleanTitle("Bob's camp: 8pm, bring rods!"));
        Assert.Equal(ForeverCampState.MaxTitleLength, ForeverCamps.CleanTitle(new string('a', 500)).Length);

        var state = new ForeverCampState("Camp", 1, new ulong[] { 1 });
        Assert.Same(state, state.With(1));
        Assert.Equal(new ulong[] { 1, 2 }, state.With(2).Members);
        Assert.Empty(state.Without(1).Members);
        Assert.Equal(new ulong[] { 1 }, state.Without(9).Members);
    }

    // ===== Who a member camps as =====

    private static readonly ServiceProvider Ef = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    [Fact]
    public async Task A_member_camps_as_their_main_with_what_the_armory_says_it_can_craft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<NinjaBotEntities>(o => o.UseSqlite(connection).UseInternalServiceProvider(Ef))
            .BuildServiceProvider();
        await using (var db = new NinjaBotEntities(new DbContextOptionsBuilder<NinjaBotEntities>().UseSqlite(connection).UseInternalServiceProvider(Ef).Options))
            await db.Database.EnsureCreatedAsync();
        var armory = new FakeForeverArmory();
        var forever = new ForeverService(services.GetRequiredService<IServiceScopeFactory>(), armory, NullLogger<ForeverService>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var camps = new ForeverCamps(forever, armory, cache);

        Assert.Null(await camps.CamperAsync(501, 1001));

        armory.AddCharacter(ForeverRegion.US, "Asha", "Brightvale", ForeverClass.Mage, level: 34);
        armory.AddCharacter(ForeverRegion.US, "Thalen", "Duskvale", ForeverClass.Warrior, level: 12);
        armory.AddProfessions(ForeverRegion.US, "Thalen", "Duskvale", Fishing);
        var asha = (await forever.AddCharacterAsync(501, 1001, "Asha", "Brightvale", ForeverRegion.US)).Value!.Character;
        var thalen = (await forever.AddCharacterAsync(501, 1001, "Thalen", "Duskvale", ForeverRegion.US)).Value!.Character;

        // The first one saved is the main; the armory has no professions for it, which is "unknown", not "none".
        var first = (await camps.CamperAsync(501, 1001))!;
        Assert.Equal((501UL, "Asha Brightvale", 34, (ForeverClass?)ForeverClass.Mage), (first.UserId, first.CharacterName, first.Level, first.Class));
        Assert.Null(first.Professions);

        Assert.True((await forever.SetMainAsync(501, 1001, thalen.Id)).Ok);
        var main = (await camps.CamperAsync(501, 1001))!;
        Assert.Equal("Thalen Duskvale", main.CharacterName);
        Assert.Equal("Fish Bowl", ForeverCamp.Plan(new[] { main }).Picks.Single().Recipe);

        // Professions are remembered for a few minutes, so a card with many campers isn't one request each per click.
        await camps.CamperAsync(501, 1001);
        Assert.Equal(1, armory.ProfessionAsks["thalen-duskvale"]);

        // In a server where they have no main, they camp as their first character.
        Assert.Equal("Asha Brightvale", (await camps.CamperAsync(501, 2002))!.CharacterName);

        // The armory going quiet still leaves a camper, from what was saved.
        armory.Down = true;
        var offline = (await camps.CamperAsync(501, 2002))!;
        Assert.Equal(("Asha Brightvale", 0, (ForeverClass?)ForeverClass.Mage), (offline.CharacterName, offline.Level, offline.Class));

        var state = new ForeverCampState("Camp", 501, new ulong[] { 501, 999 });
        Assert.Equal(new ulong[] { 501 }, (await camps.CampersAsync(state, 1001)).Select(c => c.UserId));

        // A click finds the camp in memory first, and on the card itself after a restart.
        camps.Remember(42, state);
        Assert.Same(state, camps.Recall(42, Array.Empty<string>()));
        Assert.Null(camps.Recall(43, Array.Empty<string>()));
        Assert.Equal(900000000000000777UL, camps.Recall(43, new[] { "# 🔥 Camp\nStarted by <@900000000000000777>" })!.HostId);
        using (await camps.LockAsync(42)) { }
        using (await camps.LockAsync(42)) { }
    }
}
