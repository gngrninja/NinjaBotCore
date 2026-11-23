using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services;
using Xunit;
using static NinjaBotCore.Tests.ForeverCardReader;

namespace NinjaBotCore.Tests;

// Recipes, prices and who can craft what. Every guild, user and character is SYNTHETIC; recipes and
// items are well-known low-level ones with made-up prices.
public sealed class ForeverWorkshopTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly Clock _clock = new();

    public void Dispose() => _cache.Dispose();

    // ===== Recipes from Blizzard's game data =====

    private readonly Dictionary<string, WowApiResponse> _routes = new();
    private readonly List<string> _asked = new();

    private ForeverCatalog Catalog()
    {
        var api = new Mock<IWowApi>();
        api.Setup(a => a.TryGetAPIRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, string region, CancellationToken _) =>
            {
                lock (_asked) _asked.Add($"{region} {url}");
                var path = url[..url.IndexOf('?')];
                return _routes.TryGetValue(path, out var response) ? response : new WowApiResponse(HttpStatusCode.NotFound, string.Empty);
            });
        return new ForeverCatalog(api.Object, _cache, NullLogger<ForeverCatalog>.Instance, _clock);
    }

    private void Serve(string path, string body) => _routes[path] = new WowApiResponse(HttpStatusCode.OK, body);

    private void ServeProfessions()
    {
        Serve("/data/wow/profession/index", """
            { "professions": [ { "id": 164, "name": "Blacksmithing" }, { "id": 186, "name": "Mining" }, { "id": 2933, "name": "Test Profession [DNT]" }, { "name": "No id" } ] }
            """);
        Serve("/data/wow/profession/164", """{ "id": 164, "name": "Blacksmithing", "skill_tiers": [ { "id": 2938, "name": "Blacksmithing" } ] }""");
        Serve("/data/wow/profession/164/skill-tier/2938", """
            { "id": 2938, "categories": [
                { "name": "Weapon Stones", "recipes": [ { "id": 1631, "name": "Rough Sharpening Stone" }, { "id": 9, "name": "Blacksmithing" }, { "name": "No id" } ] },
                { "name": "Camping", "recipes": [ { "id": 52645, "name": "Sharpening Wheel" }, { "id": 57020, "name": "Anvil" } ] } ] }
            """);
        Serve("/data/wow/profession/186", """{ "id": 186, "name": "Mining", "skill_tiers": [ { "id": 2945, "name": "Mining" } ] }""");
        Serve("/data/wow/profession/186/skill-tier/2945", """{ "categories": [ { "name": "Smelting", "recipes": [ { "id": 2657, "name": "Smelt Copper" } ] } ] }""");
    }

    [Fact]
    public async Task The_recipe_list_is_every_professions_recipes_read_once_from_the_us_game_data()
    {
        ServeProfessions();
        var catalog = Catalog();
        Assert.Null(catalog.RecipesIfLoaded);

        var recipes = await catalog.GetRecipesAsync();

        // The profession's own rank, the nameless test profession and entries without an id are left out.
        Assert.Equal(
            new[]
            {
                (57020, "Anvil", 164, "Blacksmithing", "Camping"),
                (1631, "Rough Sharpening Stone", 164, "Blacksmithing", "Weapon Stones"),
                (52645, "Sharpening Wheel", 164, "Blacksmithing", "Camping"),
                (2657, "Smelt Copper", 186, "Mining", "Smelting")
            },
            recipes!.Recipes.Select(r => (r.Id, r.Name, r.ProfessionId, r.ProfessionName, r.Category)));
        Assert.Equal("Anvil", recipes.Find(57020)!.Name);
        Assert.Null(recipes.Find(9));
        Assert.All(_asked, a => Assert.StartsWith("us /data/wow/profession/", a));
        Assert.All(_asked, a => Assert.Contains("namespace=static-forever-us&locale=en_US", a));
        Assert.DoesNotContain(_asked, a => a.Contains("/2933"));

        // Asked again, nothing goes to Blizzard for a day.
        var requests = _asked.Count;
        Assert.Same(recipes, await catalog.GetRecipesAsync());
        Assert.Same(recipes, catalog.RecipesIfLoaded);
        _clock.Now += TimeSpan.FromHours(23);
        Assert.Same(recipes, await catalog.GetRecipesAsync());
        Assert.Equal(requests, _asked.Count);
        _clock.Now += TimeSpan.FromHours(2);
        Assert.NotSame(recipes, await catalog.GetRecipesAsync());
    }

    [Fact]
    public async Task A_recipe_list_blizzard_does_not_send_is_null_and_asked_for_again_later()
    {
        var catalog = Catalog();
        Assert.Null(await catalog.GetRecipesAsync());
        var requests = _asked.Count;

        // Not hammered: the next try waits a minute.
        ServeProfessions();
        Assert.Null(await catalog.GetRecipesAsync());
        Assert.Equal(requests, _asked.Count);

        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(4, (await catalog.GetRecipesAsync())!.Recipes.Count);
    }

    [Fact]
    public async Task A_profession_that_did_not_answer_leaves_a_shorter_list_that_is_soon_read_again()
    {
        ServeProfessions();
        _routes["/data/wow/profession/186/skill-tier/2945"] = new WowApiResponse(HttpStatusCode.ServiceUnavailable, string.Empty);
        var catalog = Catalog();

        var partial = await catalog.GetRecipesAsync();
        Assert.Equal(new[] { 164 }, partial!.Recipes.Select(r => r.ProfessionId).Distinct());

        ServeProfessions();
        _clock.Now += TimeSpan.FromMinutes(20);
        Assert.Contains((await catalog.GetRecipesAsync())!.Recipes, r => r.ProfessionId == 186);
    }

    [Fact]
    public async Task A_recipe_says_what_it_makes_and_takes()
    {
        Serve("/data/wow/recipe/1353", """
            { "id": 1353, "name": "Handstitched Leather Boots", "crafted_item": { "id": 2302, "name": "Handstitched Leather Boots" },
              "crafted_quantity": { "value": 1.0 },
              "reagents": [ { "reagent": { "id": 2318, "name": "Light Leather" }, "quantity": 2 }, { "reagent": { "id": 2320, "name": "Coarse Thread" }, "quantity": 1 }, { "reagent": { "name": "No id" }, "quantity": 9 } ] }
            """);
        Serve("/data/wow/recipe/2427", """
            { "id": 2427, "name": "Coarse Dynamite", "crafted_item": { "id": 4365, "name": "Coarse Dynamite" },
              "crafted_quantity": { "minimum": 1.0, "maximum": 4.0 }, "reagents": [ { "reagent": { "id": 4364, "name": "Coarse Blasting Powder" }, "quantity": 3 } ] }
            """);
        Serve("/data/wow/recipe/4289", """
            { "id": 4289, "name": "Enchant Bracer - Inferior Stamina", "description": "Permanently enchant bracers.", "crafted_quantity": { "value": 1.0 },
              "reagents": [ { "reagent": { "id": 247786 }, "quantity": 1 } ] }
            """);
        Serve("/data/wow/recipe/4766", """{ "id": 4766, "name": "Skinning", "rank": 1 }""");
        Serve("/data/wow/recipe/7", """{ "id": 7, "reagents": "soon" }""");
        var catalog = Catalog();

        var boots = (await catalog.GetRecipeAsync(1353)).Value!;
        Assert.Equal((2302, "Handstitched Leather Boots", 1d, 1d), (boots.CraftedItemId!.Value, boots.CraftedItemName, boots.MinQuantity, boots.MaxQuantity));
        Assert.Equal(new[] { new ForeverReagent(2318, "Light Leather", 2), new ForeverReagent(2320, "Coarse Thread", 1) }, boots.Reagents);

        var dynamite = (await catalog.GetRecipeAsync(2427)).Value!;
        Assert.Equal((1d, 4d, 2.5), (dynamite.MinQuantity, dynamite.MaxQuantity, dynamite.AverageQuantity));

        // An enchant makes no item, and a reagent Blizzard didn't name still has its id.
        var enchant = (await catalog.GetRecipeAsync(4289)).Value!;
        Assert.Null(enchant.CraftedItemId);
        Assert.Equal("Permanently enchant bracers.", enchant.Description);
        Assert.Equal(new ForeverReagent(247786, "Item 247786", 1), Assert.Single(enchant.Reagents));

        Assert.Empty((await catalog.GetRecipeAsync(4766)).Value!.Reagents);
        Assert.Equal(ForeverLookupStatus.Unavailable, (await catalog.GetRecipeAsync(7)).Status);
        Assert.Equal(ForeverLookupStatus.NotFound, (await catalog.GetRecipeAsync(404)).Status);
        Assert.Equal(ForeverLookupStatus.NotFound, (await catalog.GetRecipeAsync(0)).Status);

        // A recipe that was read isn't asked for again.
        var requests = _asked.Count;
        Assert.Same(boots, (await catalog.GetRecipeAsync(1353)).Value);
        Assert.Equal(ForeverLookupStatus.NotFound, (await catalog.GetRecipeAsync(404)).Status);
        Assert.Equal(requests, _asked.Count);
    }

    // ===== Prices from TradeSkillMaster =====

    private const string Csv =
        "itemId,name,marketValue,historical,avgSalePrice,saleRate,soldPerDay,updatedAt\r\n" +
        "2589,Linen Cloth,27,30,25,0.95,12794.6,2026-11-22T08:58:35Z\r\n" +
        "4306,\"Silk, \"\"Fine\"\" Cloth\",200,180,0,0,0,2026-11-22T08:58:35Z\n" +
        "286978,,7695,7000,7100,0.767,16.491,2026-11-22T08:58:35Z\n" +
        "oops,Not an item,1,1,1,1,1,x\n" +
        "0,Zero,1,1,1,1,1,x\n" +
        "\n";

    [Fact]
    public void A_price_file_is_read_by_its_column_names()
    {
        var sheet = ForeverPrices.Parse(ForeverRegion.EU, Csv)!;

        Assert.Equal(ForeverRegion.EU, sheet.Region);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 8, 58, 35, TimeSpan.Zero), sheet.ScannedAt);
        Assert.Equal(new[] { 2589, 4306, 286978 }, sheet.Items.Keys.OrderBy(k => k));
        Assert.Equal(new ForeverPrice(2589, "Linen Cloth", 27, 30, 25, 0.95, 12794.6), sheet.Find(2589));
        // Quotes, commas and doubled quotes inside a name; TSM leaves some new items unnamed.
        Assert.Equal("Silk, \"Fine\" Cloth", sheet.Find(4306)!.Name);
        Assert.Equal("", sheet.Find(286978)!.Name);
        Assert.Null(sheet.Find(1));
        Assert.Null(sheet.Find(null));

        // Columns in another order still land where they belong; a file that isn't TSM's is not prices.
        var reordered = ForeverPrices.Parse(ForeverRegion.US, "name,marketValue,itemId\nCopper Ore,122,2770\n")!;
        Assert.Equal((122L, "Copper Ore", 0L), (reordered.Find(2770)!.MarketValue, reordered.Find(2770)!.Name, reordered.Find(2770)!.Historical));
        Assert.Null(reordered.ScannedAt);
        Assert.Null(ForeverPrices.Parse(ForeverRegion.US, "<html>blocked</html>"));
        Assert.Null(ForeverPrices.Parse(ForeverRegion.US, "itemId,name,marketValue\n"));
        Assert.Null(ForeverPrices.Parse(ForeverRegion.US, "itemId,name,marketValue\noops,Not an item,1\n"));
        Assert.Null(ForeverPrices.Parse(ForeverRegion.US, ""));
    }

    private sealed class Tsm : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }

        public static HttpResponseMessage File(string csv, string etag)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(csv) };
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            return response;
        }
    }

    [Fact]
    public async Task Prices_are_downloaded_once_per_update_and_kept_when_tsm_goes_quiet()
    {
        var tsm = new Tsm { Answer = _ => Tsm.File(Csv, "\"v1\"") };
        var prices = new ForeverPrices(tsm, NullLogger<ForeverPrices>.Instance, _clock);
        Assert.Null(prices.IfLoaded(ForeverRegion.US));

        var first = await prices.GetAsync(ForeverRegion.US);
        Assert.Equal(3, first!.Items.Count);
        var request = Assert.Single(tsm.Requests);
        Assert.Equal("https://public-data.tradeskillmaster.com/forever/us/region/items.csv", request.RequestUri!.ToString());
        Assert.Contains("NinjaBot", request.Headers.UserAgent.ToString());
        Assert.Same(first, prices.IfLoaded(ForeverRegion.US));

        // Inside four hours nothing is asked, however often prices are wanted.
        _clock.Now += TimeSpan.FromHours(3);
        Assert.Same(first, await prices.GetAsync(ForeverRegion.US));
        Assert.Single(tsm.Requests);

        // After that TSM is asked whether the file changed, and "no" keeps what is loaded.
        _clock.Now += TimeSpan.FromHours(2);
        tsm.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        Assert.Same(first, await prices.GetAsync(ForeverRegion.US));
        Assert.Equal("\"v1\"", tsm.Requests[1].Headers.IfNoneMatch.Single().ToString());

        // An outage keeps the last prices and tries again sooner.
        _clock.Now += TimeSpan.FromHours(5);
        tsm.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Same(first, await prices.GetAsync(ForeverRegion.US));
        _clock.Now += TimeSpan.FromMinutes(5);
        Assert.Same(first, await prices.GetAsync(ForeverRegion.US));
        Assert.Equal(3, tsm.Requests.Count);
        _clock.Now += TimeSpan.FromMinutes(6);
        tsm.Answer = _ => Tsm.File("itemId,name,marketValue\n2770,Copper Ore,122\n", "\"v2\"");
        var second = await prices.GetAsync(ForeverRegion.US);
        Assert.Equal(122, second!.Find(2770)!.MarketValue);

        // Each region has its own file, and one that was never read is simply missing.
        tsm.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await prices.GetAsync(ForeverRegion.TW));
        Assert.EndsWith("/forever/tw/region/items.csv", tsm.Requests.Last().RequestUri!.ToString());
        tsm.Answer = _ => throw new HttpRequestException("no route");
        Assert.Null(await prices.GetAsync(ForeverRegion.KR));
        tsm.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>moved</html>") };
        Assert.Null(await prices.GetAsync(ForeverRegion.EU));
    }

    [Fact]
    public void Money_reads_as_gold_silver_and_copper()
    {
        Assert.Equal("0c", ForeverMoney.Format(0));
        Assert.Equal("0c", ForeverMoney.Format(-5));
        Assert.Equal("27c", ForeverMoney.Format(27));
        Assert.Equal("2s", ForeverMoney.Format(200));
        Assert.Equal("1s 20c", ForeverMoney.Format(120));
        // Copper is dropped once there is gold.
        Assert.Equal("76g 95s", ForeverMoney.Format(769_512));
        Assert.Equal("1g", ForeverMoney.Format(10_000));
        Assert.Equal("1,234g 5s", ForeverMoney.Format(12_340_599));
    }

    // ===== Who can craft what =====

    private static ForeverArmoryProfession Profession(string name, int id, int skill, params (int Id, string Name)[] known)
        => new(name, skill, 150, id, known.Length, Known: known.Select(k => new ForeverKnownRecipe(k.Id, k.Name)).ToList());

    private readonly FakeForeverArmory _armory = new();

    private ForeverCrafters Crafters() => new(_armory, NullLogger<ForeverCrafters>.Instance, _clock);

    private static ForeverCraftCandidate Candidate(string first, string last, int level = 0)
        => new(first, last, ForeverNames.CharacterSlug(first, last), level, ForeverClass.Priest);

    [Fact]
    public async Task The_crafting_index_reads_the_guilds_members_the_armory_can_show()
    {
        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", members: new[]
        {
            FakeForeverArmory.Member("Asha", "Brightvale", level: 40, ForeverClass.Mage),
            FakeForeverArmory.Member("Thalen", "Duskvale", level: 22, ForeverClass.Warrior),
            FakeForeverArmory.Member("Quiet", "One", level: 30),
            FakeForeverArmory.Member("Low", "Bie", level: 9)
        });
        _armory.AddProfessions(ForeverRegion.US, "Asha", "Brightvale", Profession("Tailoring", 197, 150, (2385, "Brown Linen Vest"), (0, "No id")));
        _armory.AddProfessions(ForeverRegion.US, "Thalen", "Duskvale",
            Profession("Blacksmithing", 164, 75, (1631, "Rough Sharpening Stone")), Profession("Tailoring", 197, 20, (2385, "Brown Linen Vest")));
        _armory.AddProfessions(ForeverRegion.US, "Low", "Bie", Profession("Tailoring", 197, 300, (2385, "Brown Linen Vest")));
        var crafters = Crafters();
        var source = new ForeverCraftSource(ForeverRegion.US, "synthetic-guild", Array.Empty<ForeverCraftCandidate>());
        Assert.Null(crafters.Current(1, source));

        var build = crafters.Build(1, source);
        var index = await build.Task;

        // Below level 10 the armory has nothing, so nobody is asked; a member it gave nothing for is counted, not listed.
        Assert.Equal((2, 3, 1, 0, false), (index.Crafters.Count, index.Asked, index.Unanswered, index.Skipped, index.RosterMissed));
        Assert.Equal((3, 3), (build.Done, build.Total));
        Assert.DoesNotContain("low-bie", _armory.ProfessionAsks.Keys);
        Assert.Equal(_clock.Now, index.BuiltAt);

        // Highest skill first, and a recipe without an id is never matched.
        Assert.Equal(new[] { ("Asha Brightvale", 150), ("Thalen Duskvale", 20) }, index.WhoKnows(2385).Select(w => (w.Crafter.FullName, w.Profession.Skill)));
        Assert.Equal("Blacksmithing", Assert.Single(index.WhoKnows(1631)).Profession.Name);
        Assert.Empty(index.WhoKnows(0));
        Assert.Empty(index.WhoKnows(99));

        // The answers are kept for six hours, for the same guild only.
        Assert.Same(index, crafters.Current(1, source));
        Assert.Null(crafters.Current(2, source));
        Assert.Null(crafters.Current(1, source with { GuildSlug = "other-guild" }));
        Assert.Null(crafters.Current(1, source with { Region = ForeverRegion.EU }));
        _clock.Now += TimeSpan.FromHours(7);
        Assert.Null(crafters.Current(1, source));
    }

    [Fact]
    public async Task Characters_members_put_on_the_roster_are_read_too_without_asking_twice()
    {
        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", members: FakeForeverArmory.Member("Asha", "Brightvale", level: 40));
        _armory.AddProfessions(ForeverRegion.US, "Asha", "Brightvale", Profession("Tailoring", 197, 150, (2385, "Brown Linen Vest")));
        _armory.AddProfessions(ForeverRegion.US, "Mira", "Dawn", Profession("Tailoring", 197, 90, (2385, "Brown Linen Vest")));
        var crafters = Crafters();
        var guildOnly = new ForeverCraftSource(ForeverRegion.US, "synthetic-guild", Array.Empty<ForeverCraftCandidate>());
        var first = await crafters.Build(1, guildOnly).Task;
        Assert.Single(first.Crafters);

        // Someone in another guild puts a character on this server's roster: the index no longer covers everyone.
        var withMira = guildOnly with { Shared = new[] { Candidate("Mira", "Dawn"), Candidate("Asha", "Brightvale") } };
        Assert.Null(crafters.Current(1, withMira));
        var second = await crafters.Build(1, withMira).Task;

        Assert.Equal(new[] { "Asha Brightvale", "Mira Dawn" }, second.WhoKnows(2385).Select(w => w.Crafter.FullName));
        // Asha is listed once, with the level the roster gave, and wasn't asked about again.
        Assert.Equal(40, second.Crafters.Single(c => c.FirstName == "Asha").Level);
        Assert.Equal(1, _armory.ProfessionAsks["asha-brightvale"]);
        Assert.Same(second, crafters.Current(1, withMira));
        Assert.Same(second, crafters.Current(1, guildOnly));

        // Without a guild on the armory, or when its roster doesn't come back, only those characters are read.
        var unmatched = await Crafters().Build(2, new ForeverCraftSource(ForeverRegion.US, null, new[] { Candidate("Mira", "Dawn") })).Task;
        Assert.Equal((1, false), (unmatched.Crafters.Count, unmatched.RosterMissed));
        var missed = await Crafters().Build(3, new ForeverCraftSource(ForeverRegion.US, "gone-guild", new[] { Candidate("Mira", "Dawn") })).Task;
        Assert.Equal((1, true), (missed.Crafters.Count, missed.RosterMissed));
    }

    [Fact]
    public async Task A_huge_guild_is_read_up_to_a_cap_from_the_highest_level_down_and_one_read_at_a_time()
    {
        var members = Enumerable.Range(0, ForeverCrafters.MaxCharacters + 50)
            .Select(i => FakeForeverArmory.Member("Member", "N" + i, level: 10 + i % 51))
            .ToArray();
        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", members: members);
        var crafters = Crafters();
        var source = new ForeverCraftSource(ForeverRegion.US, "synthetic-guild", new[] { Candidate("Shared", "Lowbie") });

        var build = crafters.Build(1, source);
        // Whoever asks while it runs joins the same read.
        Assert.Same(build, crafters.Build(1, source));
        var index = await build.Task;

        Assert.Equal((ForeverCrafters.MaxCharacters, 51), (index.Asked, index.Skipped));
        // A character a member put on the roster is never the one the cap drops.
        Assert.Contains("shared-lowbie", _armory.ProfessionAsks.Keys);
        Assert.All(_armory.ProfessionAsks.Values, asks => Assert.Equal(1, asks));
        Assert.NotSame(build, crafters.Build(1, source));
    }

    // ===== The pieces put together =====

    private static readonly ServiceProvider Ef = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    [Fact]
    public async Task The_workshop_reads_the_guild_once_and_says_who_put_each_character_on_the_roster()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<NinjaBotEntities>(o => o.UseSqlite(connection).UseInternalServiceProvider(Ef))
            .BuildServiceProvider();
        await using (var db = new NinjaBotEntities(new DbContextOptionsBuilder<NinjaBotEntities>().UseSqlite(connection).UseInternalServiceProvider(Ef).Options))
            await db.Database.EnsureCreatedAsync();
        var forever = new ForeverService(services.GetRequiredService<IServiceScopeFactory>(), _armory, NullLogger<ForeverService>.Instance);

        _armory.AddGuild(ForeverRegion.US, "Synthetic Guild", ForeverRuleset.PvP, ForeverFaction.Horde,
            FakeForeverArmory.Member("Asha", "Brightvale", level: 40), FakeForeverArmory.Member("Thalen", "Duskvale", level: 22));
        _armory.AddCharacter(ForeverRegion.US, "Asha", "Brightvale", ForeverClass.Mage, ForeverRuleset.PvP, ForeverFaction.Horde);
        _armory.AddProfessions(ForeverRegion.US, "Asha", "Brightvale", Profession("Tailoring", 197, 150, (2385, "Brown Linen Vest")));
        _armory.AddProfessions(ForeverRegion.US, "Thalen", "Duskvale", Profession("Tailoring", 197, 60, (2385, "Brown Linen Vest")));
        Assert.True((await forever.SetGuildAsync(1001, "Synthetic Guild", ForeverRegion.US, 1, "officer")).Ok);
        var asha = (await forever.AddCharacterAsync(501, 1001, "Asha", "Brightvale")).Value!.Character;
        Assert.True((await forever.ShareAsync(501, 1001, asha.Id)).Ok);
        var guild = (await forever.GetGuildAsync(1001))!;

        var catalog = new FakeForeverCatalog();
        var vest = catalog.Add(2385, "Brown Linen Vest", 197, "Tailoring", "Cloth Chest", craftedItemId: 2568, 1,
            new ForeverReagent(2996, "Bolt of Linen Cloth", 1), new ForeverReagent(2320, "Coarse Thread", 1));
        var prices = new FakeForeverPrices().Set(ForeverRegion.US, 2996, "Bolt of Linen Cloth", 60);
        var workshop = new ForeverWorkshop(forever, catalog, prices, Crafters());

        var sheet = await workshop.SheetAsync(vest, guild.Region);
        Assert.Equal((2568, 2, 60L), (sheet.Recipe!.CraftedItemId!.Value, sheet.Recipe.Reagents.Count, sheet.Prices!.Find(2996)!.MarketValue));

        var who = await workshop.CraftersAsync(guild, 1001, _ => true, id => id == 501 ? "Alice" : null);
        Assert.Equal(new[] { "Asha Brightvale", "Thalen Duskvale" }, who!.Index.WhoKnows(2385).Select(w => w.Crafter.FullName));
        // Only the character someone put on this server's roster carries a Discord name, starred as their main.
        Assert.Equal("★ Alice", who.SharedBy[(ForeverNames.Normalize("Asha"), ForeverNames.Normalize("Brightvale"))]);
        Assert.Single(who.SharedBy);

        // The next card is served from the same read.
        var again = await workshop.CraftersAsync(guild, 1001, _ => true, _ => "Alice");
        Assert.Same(who.Index, again!.Index);
        Assert.Equal(1, _armory.ProfessionAsks["asha-brightvale"]);

        // A recipe or prices that didn't come back leave a sheet with holes, not an error.
        catalog.Down = true;
        var holes = await new ForeverWorkshop(forever, catalog, new FakeForeverPrices(), Crafters()).SheetAsync(vest, ForeverRegion.EU);
        Assert.Equal((null, null, ForeverRegion.EU), (holes.Recipe, holes.Prices, holes.Region));
    }

    [Fact]
    public void A_recipe_or_item_is_found_by_its_id_its_name_or_the_only_name_that_fits()
    {
        var catalog = new FakeForeverCatalog();
        catalog.Add(2385, "Brown Linen Vest", 197, "Tailoring");
        catalog.Add(2386, "Linen Bag", 197, "Tailoring");
        catalog.Add(1631, "Rough Sharpening Stone", 164, "Blacksmithing");
        var recipes = catalog.RecipesIfLoaded;

        Assert.Equal("Linen Bag", ForeverWorkshop.FindRecipe(recipes, "2386")!.Name);
        Assert.Equal("Linen Bag", ForeverWorkshop.FindRecipe(recipes, " linen BAG ")!.Name);
        Assert.Equal("Rough Sharpening Stone", ForeverWorkshop.FindRecipe(recipes, "sharpening")!.Name);
        // "linen" fits two recipes, so it picks neither rather than guessing.
        Assert.Null(ForeverWorkshop.FindRecipe(recipes, "linen"));
        Assert.Null(ForeverWorkshop.FindRecipe(recipes, "0"));
        Assert.Null(ForeverWorkshop.FindRecipe(recipes, "999"));
        Assert.Null(ForeverWorkshop.FindRecipe(recipes, "  "));
        Assert.Null(ForeverWorkshop.FindRecipe(recipes, null));

        var prices = new FakeForeverPrices()
            .Set(ForeverRegion.US, 2589, "Linen Cloth", 27).Set(ForeverRegion.US, 2996, "Bolt of Linen Cloth", 60).Set(ForeverRegion.US, 286978, "", 7695)
            .IfLoaded(ForeverRegion.US);
        Assert.Equal(27, ForeverWorkshop.FindItem(prices, "2589")!.MarketValue);
        Assert.Equal(60, ForeverWorkshop.FindItem(prices, "bolt of linen cloth")!.MarketValue);
        Assert.Equal(60, ForeverWorkshop.FindItem(prices, "bolt")!.MarketValue);
        Assert.Null(ForeverWorkshop.FindItem(prices, "linen"));
        // An item TSM has no name for can only be found by its id.
        Assert.Equal(7695, ForeverWorkshop.FindItem(prices, "286978")!.MarketValue);
        Assert.Null(ForeverWorkshop.FindItem(prices, ""));
    }

    [Fact]
    public void Recipe_and_item_suggestions_put_names_that_start_with_the_text_first()
    {
        var catalog = new FakeForeverCatalog();
        catalog.Add(1, "Heavy Linen Bandage", 129, "First Aid");
        catalog.Add(2, "Linen Bandage", 129, "First Aid");
        catalog.Add(3, "Linen Bag", 197, "Tailoring");
        catalog.Add(4, "Copper Bar", 186, "Mining");
        catalog.Add(5, "Crafted Heavy Shot", 202, "Engineering");

        Assert.Equal(
            new[] { ("Linen Bag · Tailoring", "3"), ("Linen Bandage · First Aid", "2"), ("Heavy Linen Bandage · First Aid", "1") },
            ForeverSuggest.Recipes(catalog.RecipesIfLoaded, "linen").Select(s => (s.Name, (string)s.Value)));
        // A name that starts with the text leads even when a shorter name only contains it.
        Assert.Equal(new[] { "1", "5" }, ForeverSuggest.Recipes(catalog.RecipesIfLoaded, "heavy").Select(s => (string)s.Value));
        Assert.Equal((ForeverSuggest.RecipeHint, "0"), ForeverSuggest.Recipes(catalog.RecipesIfLoaded, "").Select(s => (s.Name, (string)s.Value)).Single());
        Assert.Equal((ForeverSuggest.LoadingHint, "0"), ForeverSuggest.Recipes(null, "linen").Select(s => (s.Name, (string)s.Value)).Single());
        Assert.Equal("No recipe has \"zzz\" in its name", Assert.Single(ForeverSuggest.Recipes(catalog.RecipesIfLoaded, "zzz")).Name);

        var many = new FakeForeverCatalog();
        for (var i = 1; i <= 60; i++) many.Add(i, "Recipe " + new string('x', 110) + i, 197, "Tailoring");
        var capped = ForeverSuggest.Recipes(many.RecipesIfLoaded, "recipe");
        Assert.Equal(25, capped.Count);
        Assert.All(capped, s => Assert.InRange(s.Name.Length, 1, 100));
        Assert.All(ForeverSuggest.Recipes(many.RecipesIfLoaded, new string('q', 200)), s => Assert.InRange(s.Name.Length, 1, 100));

        var prices = new FakeForeverPrices()
            .Set(ForeverRegion.US, 2589, "Linen Cloth", 27, soldPerDay: 12794)
            .Set(ForeverRegion.US, 2996, "Bolt of Linen Cloth", 60, soldPerDay: 900)
            .Set(ForeverRegion.US, 4343, "Linen Boots", 300, soldPerDay: 3)
            .Set(ForeverRegion.US, 286978, "", 7695, soldPerDay: 16)
            .IfLoaded(ForeverRegion.US);
        // Starts-with first, then what sells most; an unnamed item can't be typed, so it is never offered.
        Assert.Equal(
            new[] { ("Linen Cloth · 27c", "2589"), ("Linen Boots · 3s", "4343"), ("Bolt of Linen Cloth · 60c", "2996") },
            ForeverSuggest.Items(prices, "linen").Select(s => (s.Name, (string)s.Value)));
        Assert.Equal(ForeverSuggest.ItemHint, Assert.Single(ForeverSuggest.Items(prices, "")).Name);
        Assert.Equal(ForeverSuggest.LoadingHint, Assert.Single(ForeverSuggest.Items(null, "linen")).Name);
        Assert.StartsWith("No priced item has", Assert.Single(ForeverSuggest.Items(prices, "zzz")).Name);
    }

    // ===== The craft card =====

    private static readonly ForeverGuild Guild = new()
    {
        Name = "Synthetic *Guild*", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, ArmorySlug = "synthetic-guild"
    };

    private static readonly ForeverRecipeRef Boots = new(1353, "Handstitched Leather Boots", 165, "Leatherworking", "Leather Boots");

    private static ForeverCraftSheet Sheet(ForeverPriceSheet prices, double quantity = 1, bool noRecipe = false) => new(
        Boots,
        noRecipe ? null : new ForeverRecipe(1353, "Handstitched Leather Boots", 2302, "Handstitched Leather Boots", quantity, quantity,
            new[] { new ForeverReagent(2318, "Light Leather", 2), new ForeverReagent(2320, "Coarse [Thread]", 1) }),
        prices,
        ForeverRegion.US);

    private static ForeverCraftWho Who(params ForeverCrafter[] crafters) => new(
        new ForeverCraftIndex(crafters, DateTimeOffset.FromUnixTimeSeconds(1795000000), crafters.Length + 5, 5),
        new Dictionary<(string, string), string> { [(ForeverNames.Normalize("Asha"), ForeverNames.Normalize("Brightvale"))] = "★ Alice_A" });

    private static ForeverCrafter Crafter(string first, string last, int skill, int level = 30)
        => new(first, last, level, ForeverClass.Rogue, new[] { ForeverCrafterProfession.From(165, "Leatherworking", skill, 150, new[] { 1353 }) });

    [Fact]
    public void The_craft_card_prices_the_materials_against_buying_and_lists_who_can_make_it()
    {
        var prices = new FakeForeverPrices()
            .Set(ForeverRegion.US, 2318, "Light Leather", 44).Set(ForeverRegion.US, 2320, "Coarse Thread", 17).Set(ForeverRegion.US, 2302, "Handstitched Leather Boots", 250)
            .IfLoaded(ForeverRegion.US);

        var card = ForeverViews.Craft(Sheet(prices), Guild, Who(Crafter("Thalen", "Dusk_vale", 75), Crafter("Asha", "Brightvale", 150, level: 0)));

        Assert.Equal(new[]
        {
            "# 🛠️ Handstitched Leather Boots",
            "🥾 Leatherworking · Leather Boots",
            "**Makes** [Handstitched Leather Boots](https://www.wowhead.com/forever/item=2302)",
            "### Materials",
            "`2 ×` [Light Leather](https://www.wowhead.com/forever/item=2318) · 88c (44c each)",
            // A name with square brackets would end a link early, so it stays plain.
            "`1 ×` Coarse [Thread] · 17c",
            "**Materials** ≈ 1s 5c · **Buying it** ≈ 2s 50c",
            "Crafting comes out ≈ **1s 45c** cheaper.",
            $"-# US market value, scanned <t:{FakeForeverPrices.Scanned.ToUnixTimeSeconds()}:R>. Vendor prices, deposits and the auction house cut aren't counted.",
            "### Who can make it · 2",
            "🗡️ **Asha Brightvale** · Leatherworking 150/150 — ★ Alice\\_A",
            @"🗡️ **Thalen Dusk\_vale** · 30 · Leatherworking 75/150",
            "-# 2 of 7 characters had professions on the armory · read <t:1795000000:R>",
            "-# Pricing data from [TradeSkillMaster](https://tradeskillmaster.com) · a regional average, not your own auction house · Recipes and crafters from Blizzard's armory, which updates when a character logs out"
        }, Lines(card));
        Assert.Equal(new Color(200, 35, 35), Accent(card));
        Assert.Empty(Buttons(card));
        AssertFits(card);
    }

    [Fact]
    public void The_craft_card_says_so_when_buying_is_cheaper_or_a_price_is_missing()
    {
        var dear = new FakeForeverPrices()
            .Set(ForeverRegion.US, 2318, "Light Leather", 500).Set(ForeverRegion.US, 2320, "Coarse Thread", 17).Set(ForeverRegion.US, 2302, "Boots", 250)
            .IfLoaded(ForeverRegion.US);
        Assert.Contains("Buying comes out ≈ **7s 67c** cheaper.", Text(ForeverViews.Craft(Sheet(dear), Guild, Who())));

        // Five a craft: the comparison is per item.
        var batch = Text(ForeverViews.Craft(Sheet(dear, quantity: 5), Guild, Who()));
        Assert.Contains("**Makes** [Handstitched Leather Boots](https://www.wowhead.com/forever/item=2302) ×5", batch);
        Assert.Contains("**Materials** ≈ 10s 17c, so ≈ 2s 3c each · **Buying it** ≈ 2s 50c", batch);
        Assert.Contains("Crafting comes out ≈ **47c** cheaper each.", batch);

        // Within a tenth of each other, regional averages can't say which is cheaper.
        var close = new FakeForeverPrices()
            .Set(ForeverRegion.US, 2318, "Light Leather", 100).Set(ForeverRegion.US, 2320, "Coarse Thread", 40).Set(ForeverRegion.US, 2302, "Boots", 250)
            .IfLoaded(ForeverRegion.US);
        var even = Text(ForeverViews.Craft(Sheet(close), Guild, Who()));
        Assert.Contains("**Materials** ≈ 2s 40c · **Buying it** ≈ 2s 50c\nAbout the same either way.", even);
        Assert.DoesNotContain("cheaper", even);

        // One material without a price: no verdict on which is cheaper, since the bill isn't whole.
        var partial = new FakeForeverPrices().Set(ForeverRegion.US, 2318, "Light Leather", 44).Set(ForeverRegion.US, 2302, "Boots", 250).IfLoaded(ForeverRegion.US);
        var holes = Text(ForeverViews.Craft(Sheet(partial), Guild, Who()));
        Assert.Contains("`1 ×` Coarse [Thread] · no price", holes);
        Assert.Contains("**Materials** ≈ 88c plus 1 without a price · **Buying it** ≈ 2s 50c", holes);
        Assert.DoesNotContain("cheaper", holes);

        // Nothing priced at all, and no prices at all: neither shows TSM's credit, because nothing of theirs is shown.
        var unrelated = new FakeForeverPrices().Set(ForeverRegion.US, 1, "Else", 5).IfLoaded(ForeverRegion.US);
        var none = Text(ForeverViews.Craft(Sheet(unrelated), Guild, Who()));
        Assert.Contains("None of these is on the auction house price list", none);
        Assert.DoesNotContain("TradeSkillMaster", none);
        var down = Text(ForeverViews.Craft(Sheet(null), Guild, Who()));
        Assert.Contains("`2 ×` [Light Leather](https://www.wowhead.com/forever/item=2318)\n", down);
        Assert.Contains("-# TradeSkillMaster didn't send prices just now.", down);
        Assert.DoesNotContain("Pricing data from", down);

        Assert.Contains("Blizzard didn't send what this recipe takes just now", Text(ForeverViews.Craft(Sheet(dear, noRecipe: true), Guild, Who())));
    }

    [Fact]
    public void The_craft_card_says_where_the_list_of_crafters_stands()
    {
        var sheet = Sheet(null);

        var noGuild = ForeverViews.Craft(sheet, null, null);
        Assert.Contains("This server has no Forever guild yet. An officer can set one with `/forever guild set`", Text(noGuild));
        Assert.Equal(new Color(0xE0B04A), Accent(noGuild));

        var reading = Text(ForeverViews.Craft(sheet, Guild, null, new ForeverCraftProgress(120, 431)));
        Assert.Contains("⏳ Reading the guild's professions from the armory: **120 of 431** characters…", reading);
        Assert.Contains("kept for six hours", reading);
        Assert.Contains("⏳ Reading the guild's professions from the armory…", Text(ForeverViews.Craft(sheet, Guild, null, new ForeverCraftProgress(0, 0))));
        Assert.Contains("took too long", Text(ForeverViews.Craft(sheet, Guild, null)));

        var nobody = Text(ForeverViews.Craft(sheet, Guild, Who()));
        Assert.Contains(@"Nobody in **Synthetic \*Guild\*** knows this recipe yet.", nobody);
        Assert.Contains("### Who can make it\n", nobody);

        // A big guild is cut to what fits, and the caveats of the read are spelled out.
        var many = Enumerable.Range(1, 40).Select(i => Crafter("Member", "N" + new string('x', 20) + i, 150 - i)).ToArray();
        var index = new ForeverCraftIndex(many, DateTimeOffset.FromUnixTimeSeconds(1795000000), 600, 560, Skipped: 51, RosterMissed: true);
        var crowded = ForeverViews.Craft(sheet, Guild, new ForeverCraftWho(index, new Dictionary<(string, string), string>()));
        Assert.Contains("### Who can make it · 40", Text(crowded));
        Assert.Contains("…and 25 more", Text(crowded));
        Assert.Equal(15, Lines(crowded).Count(l => l.StartsWith("🗡️ **Member")));
        Assert.Contains("The armory didn't send the guild's roster", Text(crowded));
        Assert.Contains("the 51 lowest levels weren't read", Text(crowded));
        AssertFits(crowded);
    }

    [Fact]
    public void The_price_card_shows_what_tsm_knows_and_always_credits_it()
    {
        var sheet = new FakeForeverPrices().Set(ForeverRegion.EU, 2589, "Linen *Cloth*", 27, historical: 30, avgSalePrice: 25, saleRate: 0.95, soldPerDay: 12794.6).IfLoaded(ForeverRegion.EU);

        var card = ForeverViews.Price(sheet.Find(2589), sheet);

        Assert.Equal(new[]
        {
            @"# 💰 Linen \*Cloth\*",
            $"EU auction houses · scanned <t:{FakeForeverPrices.Scanned.ToUnixTimeSeconds()}:R>",
            "🏷️ **Market value** 27c",
            "🤝 **Usually sells for** 25c",
            "📈 **Its usual price** 30c · now **10% below** it",
            "📦 **12,795** sold a day · **95%** of listings sell",
            "-# Pricing data from [TradeSkillMaster](https://tradeskillmaster.com) · a regional average, not your own auction house"
        }, Lines(card));
        var link = Assert.Single(Buttons(card));
        Assert.Equal(("Wowhead", ButtonStyle.Link, "https://www.wowhead.com/forever/item=2589"), (link.Label, link.Style, link.Url));
        AssertFits(card);

        // A thin row: only what TSM has is shown, and an unnamed item is called by its id.
        var thin = new FakeForeverPrices().Set(ForeverRegion.US, 286978, "", 7695, historical: 7600, soldPerDay: 0.4).IfLoaded(ForeverRegion.US);
        Assert.Equal(new[]
        {
            "# 💰 Item 286978",
            $"US auction houses · scanned <t:{FakeForeverPrices.Scanned.ToUnixTimeSeconds()}:R>",
            "🏷️ **Market value** 76s 95c",
            "📈 **Its usual price** 76s",
            "📦 **0.4** sold a day"
        }, Lines(ForeverViews.Price(thin.Find(286978), thin)).Take(5));
        var risen = new FakeForeverPrices().Set(ForeverRegion.US, 1, "Ore", 150, historical: 100).IfLoaded(ForeverRegion.US);
        Assert.Contains("now **50% above** it", Text(ForeverViews.Price(risen.Find(1), risen)));
    }
}
