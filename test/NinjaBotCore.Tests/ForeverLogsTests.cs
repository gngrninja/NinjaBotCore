using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using Xunit;
using static NinjaBotCore.Tests.ForeverCardReader;

namespace NinjaBotCore.Tests;

/// <summary>
/// Warcraft Logs' Forever site, answering from what a test set up. It sorts a query the way the real
/// one would: the server list, or a guild asked for on each server it was given.
/// </summary>
internal sealed class FakeForeverLogSite : IForeverLogSite
{
    public readonly List<(string Query, IDictionary<string, object> Variables)> Asked = new();

    /// <summary>Region slug as the site writes it → its servers. A null list leaves the region's servers out of the answer.</summary>
    public Dictionary<string, (string[] Slugs, int? Total)> Servers = new() { ["US"] = (new[] { "forever-us" }, 1) };
    /// <summary>Server slugs where the guild lookup finds the guild.</summary>
    public HashSet<string> Known = new();
    /// <summary>Server slug → the reports the site has for the guild there, as JSON objects.</summary>
    public Dictionary<string, string[]> Reports = new();
    public Exception Fails;
    /// <summary>What the whole answer is replaced with, when a test wants something odd.</summary>
    public string Raw;

    public int ServerQuestions => Asked.Count(a => a.Query.Contains("worldData"));
    public int GuildQuestions => Asked.Count(a => a.Query.Contains("guildData"));

    public Task<JToken> AskAsync(string query, object variables, CancellationToken cancellationToken = default)
    {
        Asked.Add((query, variables as IDictionary<string, object> ?? new Dictionary<string, object>()));
        if (Fails != null) throw Fails;
        if (Raw != null) return Task.FromResult(JToken.Parse(Raw));

        if (query.Contains("worldData"))
        {
            var regions = new JArray(Servers.Select(r => new JObject
            {
                ["slug"] = r.Key,
                ["servers"] = new JObject
                {
                    ["total"] = r.Value.Total,
                    ["data"] = new JArray(r.Value.Slugs.Select(s => new JObject { ["slug"] = s }))
                }
            }));
            return Task.FromResult<JToken>(new JObject { ["worldData"] = new JObject { ["regions"] = regions } });
        }

        var asked = (IDictionary<string, object>)variables;
        var data = new JObject();
        for (var i = 0; asked.ContainsKey($"s{i}"); i++)
        {
            var slug = (string)asked[$"s{i}"];
            data[$"g{i}"] = new JObject { ["guild"] = Known.Contains(slug) ? new JObject { ["id"] = 77 } : JValue.CreateNull() };
            // A guild the site doesn't have: the reports are null, which is what an error beside the data leaves.
            data[$"r{i}"] = Reports.TryGetValue(slug, out var reports)
                ? new JObject { ["reports"] = new JObject { ["data"] = new JArray(reports.Select(JObject.Parse)) } }
                : new JObject { ["reports"] = JValue.CreateNull() };
        }
        return Task.FromResult<JToken>(data);
    }

    public static string Report(string code, string title, long startSeconds, int minutes = 90, string zone = "The Deadmines", string owner = "Uploader")
        => new JObject
        {
            ["code"] = code, ["title"] = title, ["startTime"] = startSeconds * 1000, ["endTime"] = (startSeconds + minutes * 60) * 1000,
            ["zone"] = zone == null ? JValue.CreateNull() : new JObject { ["id"] = 1, ["name"] = zone },
            ["owner"] = owner == null ? JValue.CreateNull() : new JObject { ["name"] = owner }
        }.ToString();
}

// A Forever guild's logs: finding the guild on Warcraft Logs, the card, and keeping an unknown site at arm's length.
// Every guild, uploader and report is SYNTHETIC.
public class ForeverLogsTests
{
    private const long Monday = 1795000000;

    private static readonly ForeverGuild Guild = new()
    {
        Name = "Synthetic Guild", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde,
        RosterEnabled = true, ArmorySlug = "synthetic-guild"
    };

    private static ForeverLogs Logs(FakeForeverLogSite site)
        => new(site, new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }), NullLogger<ForeverLogs>.Instance);

    // ===== Finding the guild =====

    [Fact]
    public async Task One_server_in_the_region_is_the_guilds_server()
    {
        var site = new FakeForeverLogSite
        {
            Known = { "forever-us" },
            Reports =
            {
                ["forever-us"] = new[]
                {
                    FakeForeverLogSite.Report("older1", "Stockade", Monday),
                    FakeForeverLogSite.Report("newer2", "Deadmines", Monday + 86400)
                }
            }
        };

        var answer = await Logs(site).LatestAsync("  Synthetic Guild ", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Found, answer.Status);
        Assert.Equal("forever-us", answer.Server);
        // Newest first, whatever order they came in.
        Assert.Equal(new[] { "newer2", "older1" }, answer.Reports.Select(r => r.Code));
        var newest = answer.Reports[0];
        Assert.Equal(("Deadmines", "The Deadmines", "Uploader"), (newest.Title, newest.Zone, newest.Owner));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Monday + 86400), newest.Start);
        Assert.Equal(TimeSpan.FromMinutes(90), newest.End - newest.Start);
        Assert.Equal("https://forever.warcraftlogs.com/reports/newer2", newest.Url);

        var asked = site.Asked.Single(a => a.Query.Contains("guildData"));
        Assert.Equal("Synthetic Guild", asked.Variables["name"]);
        Assert.Equal("us", asked.Variables["region"]);
        Assert.Equal(ForeverLogs.DefaultLimit, asked.Variables["limit"]);
        Assert.Equal("forever-us", asked.Variables["s0"]);
        Assert.False(asked.Variables.ContainsKey("s1"));
        // The name travels as a variable, never spliced into the query.
        Assert.DoesNotContain("Synthetic", asked.Query);
    }

    [Fact]
    public async Task The_server_list_is_asked_for_once_and_asks_for_one_past_the_cap()
    {
        var site = new FakeForeverLogSite { Known = { "forever-us" } };
        var logs = Logs(site);

        await logs.LatestAsync("Synthetic Guild", ForeverRegion.US);
        await logs.LatestAsync("Another Guild", ForeverRegion.US);

        Assert.Equal(1, site.ServerQuestions);
        Assert.Equal(2, site.GuildQuestions);
        Assert.Contains($"servers(limit: {ForeverLogs.MaxServers + 1})", site.Asked[0].Query);
    }

    [Fact]
    public async Task A_guild_on_the_site_with_no_logs_is_found_and_empty()
    {
        var site = new FakeForeverLogSite { Known = { "forever-us" }, Reports = { ["forever-us"] = Array.Empty<string>() } };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Found, answer.Status);
        Assert.Empty(answer.Reports);
    }

    [Fact]
    public async Task Logs_alone_prove_the_guild_is_there()
    {
        // The guild lookup comes back empty, but a log can't belong to a guild that doesn't exist.
        var site = new FakeForeverLogSite { Reports = { ["forever-us"] = new[] { FakeForeverLogSite.Report("only1", "Run", Monday) } } };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Found, answer.Status);
        Assert.Equal("only1", Assert.Single(answer.Reports).Code);
    }

    [Fact]
    public async Task A_guild_the_site_does_not_have_is_not_on_site()
    {
        var answer = await Logs(new FakeForeverLogSite()).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.NotOnSite, answer.Status);
        Assert.Equal("forever-us", answer.Server);
        Assert.Empty(answer.Reports);
    }

    [Fact]
    public async Task With_several_servers_the_one_that_has_the_guild_is_used()
    {
        var site = new FakeForeverLogSite
        {
            Servers = { ["US"] = (new[] { "normal", "pvp" }, 2) },
            Known = { "pvp" },
            Reports = { ["pvp"] = new[] { FakeForeverLogSite.Report("pvp1", "Run", Monday) } }
        };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Found, answer.Status);
        Assert.Equal("pvp", answer.Server);
        Assert.Equal("pvp1", Assert.Single(answer.Reports).Code);
        var asked = site.Asked.Single(a => a.Query.Contains("guildData"));
        Assert.Equal(("normal", "pvp"), (asked.Variables["s0"], asked.Variables["s1"]));
        Assert.Contains("$s1: String!", asked.Query);
    }

    [Fact]
    public async Task A_name_on_two_servers_is_ambiguous_and_shows_no_ones_logs()
    {
        var site = new FakeForeverLogSite
        {
            Servers = { ["US"] = (new[] { "normal", "pvp" }, 2) },
            Known = { "normal", "pvp" },
            Reports = { ["normal"] = new[] { FakeForeverLogSite.Report("theirs", "Run", Monday) } }
        };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Ambiguous, answer.Status);
        Assert.Empty(answer.Reports);
        Assert.Null(answer.Server);
        Assert.Equal(new[] { "normal", "pvp" }, answer.Servers);
    }

    [Fact]
    public async Task With_several_servers_and_the_guild_on_none_no_server_is_named()
    {
        var site = new FakeForeverLogSite { Servers = { ["US"] = (new[] { "normal", "pvp" }, 2) } };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.NotOnSite, answer.Status);
        Assert.Null(answer.Server);
    }

    [Theory]
    [InlineData(ForeverLogs.MaxServers + 1, ForeverLogs.MaxServers + 1)]
    [InlineData(1, 3)]
    public async Task A_region_with_more_servers_than_were_listed_or_wanted_is_not_searched(int listed, int total)
    {
        var slugs = Enumerable.Range(0, listed).Select(i => $"s{i}").ToArray();
        var site = new FakeForeverLogSite { Servers = { ["US"] = (slugs, total) }, Known = { "s0" } };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.TooManyServers, answer.Status);
        Assert.Equal(total, answer.ServerTotal);
        Assert.Equal(slugs, answer.Servers);
        Assert.Equal(0, site.GuildQuestions);
    }

    [Fact]
    public async Task The_cap_itself_is_still_searched()
    {
        var slugs = Enumerable.Range(0, ForeverLogs.MaxServers).Select(i => $"s{i}").ToArray();
        var site = new FakeForeverLogSite { Servers = { ["US"] = (slugs, slugs.Length) }, Known = { "s7" } };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Found, answer.Status);
        Assert.Equal("s7", answer.Server);
    }

    [Fact]
    public async Task A_region_the_site_has_no_servers_for_has_nowhere_to_look()
    {
        var site = new FakeForeverLogSite { Servers = { ["KR"] = (Array.Empty<string>(), 0) } };
        var logs = Logs(site);

        Assert.Equal(ForeverLogStatus.NoServers, (await logs.LatestAsync("Synthetic Guild", ForeverRegion.KR)).Status);
        Assert.Equal(ForeverLogStatus.NoServers, (await logs.LatestAsync("Synthetic Guild", ForeverRegion.TW)).Status);
        // The regions that do have one still work from the same answer.
        Assert.Equal(ForeverLogStatus.NotOnSite, (await logs.LatestAsync("Synthetic Guild", ForeverRegion.US)).Status);
        Assert.Equal(0, site.Asked.Count(a => a.Query.Contains("guildData") && (string)a.Variables["region"] != "us"));
    }

    [Fact]
    public async Task Each_region_is_asked_about_under_its_own_slug()
    {
        var site = new FakeForeverLogSite { Servers = { ["EU"] = (new[] { "forever-eu" }, 1) }, Known = { "forever-eu" } };

        var answer = await Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.EU);

        Assert.Equal("forever-eu", answer.Server);
        Assert.Equal("eu", site.Asked.Last().Variables["region"]);
    }

    // ===== A site that isn't there, or answers oddly =====

    public static TheoryData<Exception, string> Failures => new()
    {
        { new HttpRequestException("WCL Forever request failed with HTTP 404.", null, HttpStatusCode.NotFound), "it answered HTTP 404" },
        { new HttpRequestException("PRIVATE no route to host"), "it couldn't be reached" },
        { new TaskCanceledException("PRIVATE"), "it didn't answer in time" },
        { new InvalidOperationException("Warcraft Logs quota unavailable. Retry in 30s."), "NinjaBot is out of Warcraft Logs requests for the moment" },
        { new InvalidOperationException("PRIVATE Failed to parse"), "it sent an answer NinjaBot couldn't read" },
        { new FormatException("PRIVATE"), "something went wrong on the way" }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_site_that_fails_is_unreachable_in_words_of_our_own(Exception failure, string detail)
    {
        var site = new FakeForeverLogSite { Fails = failure };
        var logs = Logs(site);

        var answer = await logs.LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Unreachable, answer.Status);
        Assert.Equal(detail, answer.Detail);
        Assert.DoesNotContain("PRIVATE", Text(ForeverViews.Logs(Guild, answer, null)));

        // A failure isn't remembered: the next ask goes out again and works.
        site.Fails = null;
        site.Known.Add("forever-us");
        Assert.Equal(ForeverLogStatus.Found, (await logs.LatestAsync("Synthetic Guild", ForeverRegion.US)).Status);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var site = new FakeForeverLogSite { Fails = new OperationCanceledException(cancelled.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Logs(site).LatestAsync("Synthetic Guild", ForeverRegion.US, cancellationToken: cancelled.Token));
    }

    [Theory]
    [InlineData("null")]
    [InlineData(@"{""worldData"":null}")]
    [InlineData(@"{""worldData"":{""regions"":null}}")]
    [InlineData(@"""a string""")]
    public async Task A_server_list_that_is_not_one_is_unreachable(string raw)
    {
        var answer = await Logs(new FakeForeverLogSite { Raw = raw }).LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Unreachable, answer.Status);
        Assert.Equal("it sent an answer NinjaBot couldn't read", answer.Detail);
    }

    [Fact]
    public async Task Odd_values_in_an_answer_are_read_around_not_thrown_on()
    {
        var site = new FakeForeverLogSite { Known = { "forever-us" } };
        var logs = Logs(site);
        await logs.LatestAsync("Synthetic Guild", ForeverRegion.US);
        site.Raw = @"{
            ""g0"": null,
            ""r0"": { ""reports"": { ""data"": [
                null,
                { ""title"": ""no code"" },
                { ""code"": ""bare1"" },
                { ""code"": ""odd2"", ""title"": 7, ""startTime"": ""soon"", ""endTime"": 1e30, ""zone"": ""nowhere"", ""owner"": { ""name"": ""   "" } },
                { ""code"": ""good3"", ""title"": ""Fine"", ""startTime"": 1795000000000, ""endTime"": 1795000060000, ""zone"": { ""name"": ""Zone"" }, ""owner"": { ""name"": ""Up"" } }
            ] } }
        }";

        var answer = await logs.LatestAsync("Synthetic Guild", ForeverRegion.US);

        Assert.Equal(ForeverLogStatus.Found, answer.Status);
        Assert.Equal(new[] { "good3", "bare1", "odd2" }, answer.Reports.Select(r => r.Code));
        var odd = answer.Reports.Single(r => r.Code == "odd2");
        Assert.Equal(("Untitled", null, null), (odd.Title, odd.Zone, odd.Owner));
        Assert.Equal(DateTimeOffset.UnixEpoch, odd.Start);
        Assert.Equal(DateTimeOffset.UnixEpoch, odd.End);
        AssertFits(ForeverViews.Logs(Guild, answer, null));
    }

    [Fact]
    public void A_report_code_cannot_leave_its_place_in_the_link()
        => Assert.Equal("https://forever.warcraftlogs.com/reports/a%2F..%2Fb%3Fx", ForeverLogs.ReportUrl("a/../b?x"));

    // ===== The card =====

    private static ForeverLogAnswer Found(params ForeverLogReport[] reports)
        => new(ForeverLogStatus.Found, reports, Server: "forever-us", ServerTotal: 1);

    private static ForeverLogReport Run(string code = "abc123", string title = "Deadmines run", string zone = "The Deadmines", string owner = "Uploader", int minutes = 134)
        => new(code, title, zone, owner, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + minutes * 60));

    [Fact]
    public void The_card_lists_each_log_with_its_link_zone_time_length_and_uploader()
    {
        var card = ForeverViews.Logs(Guild, Found(Run(), Run("def456", "Stockade", null, null, 45)), new ForeverLogWatch(true, 2001));
        var lines = Lines(card);

        Assert.Equal("# ⚔️ Synthetic Guild · logs", lines[0]);
        Assert.Equal("-# WoW Forever · US · from Warcraft Logs", lines[1]);
        Assert.Equal("**[Deadmines run](https://forever.warcraftlogs.com/reports/abc123)** · The Deadmines", lines[2]);
        Assert.Equal($"<t:{Monday}:f> · 2h 14m · by Uploader", lines[3]);
        // No zone and no uploader: nothing is put in their place.
        Assert.Equal("**[Stockade](https://forever.warcraftlogs.com/reports/def456)**", lines[4]);
        Assert.Equal($"<t:{Monday}:f> · 45m", lines[5]);
        Assert.Equal("🔔 New logs are posted in <#2001>. An officer can turn that off with `/watchlogs`.", lines[6]);
        Assert.Equal("-# Warcraft Logs' Forever site · server forever-us", lines[7]);
        Assert.Equal(8, lines.Length);
        Assert.Equal(new Color(200, 35, 35), Accent(card));
        AssertFits(card);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Without_watching_the_card_says_how_to_turn_it_on(bool hasRow, bool on)
    {
        var text = Text(ForeverViews.Logs(Guild, Found(Run()), hasRow ? new ForeverLogWatch(on, 2001) : null));

        Assert.Contains("🔕 New logs aren't posted here. An officer can run `/watchlogs` in the channel they should go to.", text);
        Assert.DoesNotContain("<#", text);
    }

    [Fact]
    public void Odd_times_are_left_out_rather_than_shown_wrong()
    {
        var epoch = new ForeverLogReport("a1", "No times", null, "Uploader", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var backwards = new ForeverLogReport("b2", "Backwards", null, null, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday - 60));
        var endless = new ForeverLogReport("c3", "Endless", null, null, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 3 * 86400));
        var blink = new ForeverLogReport("d4", "Blink", null, null, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 20));
        var hour = new ForeverLogReport("e5", "Hour", null, null, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 3600));

        var lines = Lines(ForeverViews.Logs(Guild, Found(epoch, backwards, endless, blink, hour), null));

        Assert.Equal("by Uploader", lines[3]);
        Assert.Equal($"<t:{Monday}:f>", lines[5]);
        Assert.Equal($"<t:{Monday}:f>", lines[7]);
        Assert.Equal($"<t:{Monday}:f> · under a minute", lines[9]);
        Assert.Equal($"<t:{Monday}:f> · 1h 0m", lines[11]);
    }

    [Fact]
    public void Names_from_the_site_cannot_restyle_ping_or_break_the_card()
    {
        var hostile = new ForeverLogReport("x1", "**bold** `tick`\n# heading <@123>", "_Zone_\n## two", "@everyone *star*",
            DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 600));
        var bracketed = new ForeverLogReport("x2", "[Guild] run", null, null, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 600));
        var endless = new ForeverLogReport("x3", new string('T', 500), null, null, DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 600));
        var guild = new ForeverGuild { Name = "*Stars*\n# Big", Region = ForeverRegion.EU, Faction = ForeverFaction.Alliance };

        var card = ForeverViews.Logs(guild, Found(hostile, bracketed, endless) with { Server = "*srv*" }, null);
        var lines = Lines(card);

        Assert.Equal(@"# ⚔️ \*Stars\* # Big · logs", lines[0]);
        Assert.Equal(@"**[\*\*bold\*\* \`tick\` # heading <@123\>](https://forever.warcraftlogs.com/reports/x1)** · \_Zone\_ ## two", lines[2]);
        Assert.Equal($@"<t:{Monday}:f> · 10m · by @everyone \*star\*", lines[3]);
        // A bracket would end the link early, so that title stays plain text.
        Assert.Equal("**[Guild] run**", lines[4]);
        Assert.StartsWith("**[" + new string('T', 79) + "…](", lines[6]);
        Assert.Equal(@"-# Warcraft Logs' Forever site · server \*srv\*", lines[^1]);
        AssertFits(card);
    }

    [Fact]
    public void A_long_zone_or_uploader_is_cut_short()
    {
        var long_ = new ForeverLogReport("x1", "Run", new string('Z', 300), new string('O', 300),
            DateTimeOffset.FromUnixTimeSeconds(Monday), DateTimeOffset.FromUnixTimeSeconds(Monday + 600));

        var lines = Lines(ForeverViews.Logs(Guild, Found(long_), null));

        Assert.Equal("**[Run](https://forever.warcraftlogs.com/reports/x1)** · " + new string('Z', 59) + "…", lines[2]);
        Assert.Equal($"<t:{Monday}:f> · 10m · by " + new string('O', 39) + "…", lines[3]);
    }

    [Fact]
    public void A_full_page_of_long_logs_still_fits_a_card()
    {
        var reports = Enumerable.Range(0, 25)
            .Select(i => new ForeverLogReport($"code{i}", new string('W', 300), new string('Z', 300), new string('O', 300),
                DateTimeOffset.FromUnixTimeSeconds(Monday + i), DateTimeOffset.FromUnixTimeSeconds(Monday + i + 5000)))
            .ToArray();

        AssertFits(ForeverViews.Logs(Guild, Found(reports), new ForeverLogWatch(true, 2001)));
    }

    [Fact]
    public void A_guild_with_no_logs_says_so_and_still_offers_watching()
    {
        var lines = Lines(ForeverViews.Logs(Guild, Found(), null));

        Assert.Equal("**Synthetic Guild** is on Warcraft Logs, with no logs uploaded yet.", lines[2]);
        Assert.StartsWith("🔕", lines[3]);
    }

    [Fact]
    public void A_guild_not_on_the_site_says_how_it_gets_there()
    {
        var card = ForeverViews.Logs(Guild, new ForeverLogAnswer(ForeverLogStatus.NotOnSite, Array.Empty<ForeverLogReport>(), Server: "forever-us"), new ForeverLogWatch(true, 2001));
        var text = Text(card);

        Assert.Contains("Warcraft Logs has no guild called **Synthetic Guild** in US.", text);
        Assert.Contains("The name has to match the one in game.", text);
        // Watching can already be on: the first log posts once the guild exists.
        Assert.Contains("🔔 New logs are posted in <#2001>.", text);
        Assert.Equal(new Color(0xF0B132), Accent(card));
    }

    public static TheoryData<ForeverLogAnswer, string> Stuck => new()
    {
        { new ForeverLogAnswer(ForeverLogStatus.NoServers, Array.Empty<ForeverLogReport>()),
            "Warcraft Logs lists no Forever servers for US, so there is nowhere to look for **Synthetic Guild** yet." },
        { new ForeverLogAnswer(ForeverLogStatus.TooManyServers, Array.Empty<ForeverLogReport>(), Servers: new[] { "a", "b_c" }, ServerTotal: 1234),
            "Warcraft Logs lists 1,234 Forever servers for US, more than NinjaBot looks through for one guild.\n-# Servers: a, b\\_c" },
        { new ForeverLogAnswer(ForeverLogStatus.Ambiguous, Array.Empty<ForeverLogReport>(), Servers: new[] { "normal", "pvp" }),
            "Warcraft Logs has a guild called **Synthetic Guild** on more than one US server, and nothing says which is yours.\n-# Servers: normal, pvp" },
        { new ForeverLogAnswer(ForeverLogStatus.Unreachable, Array.Empty<ForeverLogReport>(), Detail: "it answered HTTP 404"),
            "Warcraft Logs' Forever site couldn't be read: it answered HTTP 404. Try again in a few minutes." },
        { new ForeverLogAnswer(ForeverLogStatus.Unreachable, Array.Empty<ForeverLogReport>()),
            "Warcraft Logs' Forever site couldn't be read: something went wrong on the way. Try again in a few minutes." }
    };

    [Theory]
    [MemberData(nameof(Stuck))]
    public void A_guild_that_cannot_be_found_gets_the_reason_and_no_promise_of_posts(ForeverLogAnswer answer, string body)
    {
        var card = ForeverViews.Logs(Guild, answer, new ForeverLogWatch(true, 2001));
        var text = Text(card);

        Assert.Contains(body, text);
        // Nothing can be posted for a guild that can't be found, so the card doesn't say anything will be.
        Assert.DoesNotContain("🔔", text);
        Assert.DoesNotContain("/watchlogs", text);
        Assert.EndsWith("-# Warcraft Logs' Forever site", text);
        Assert.Equal(new Color(0xF0B132), Accent(card));
        AssertFits(card);
    }

    // ===== An unknown site at arm's length =====

    private class Wcl : HttpMessageHandler
    {
        public HttpStatusCode Forever = HttpStatusCode.OK;
        public TimeSpan? ForeverRetryAfter;
        public int Tokens, ForeverCalls, MainCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("oauth"))
            {
                Interlocked.Increment(ref Tokens);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(@"{""access_token"":""token"",""expires_in"":3600}") };
            }
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri.Host == "forever.warcraftlogs.com")
            {
                Interlocked.Increment(ref ForeverCalls);
                Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
                var response = new HttpResponseMessage(Forever) { Content = new StringContent(@"{""data"":{""worldData"":{""regions"":[]}}}") };
                if (ForeverRetryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                return response;
            }
            Assert.Equal("www.warcraftlogs.com", request.RequestUri.Host);
            Interlocked.Increment(ref MainCalls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.Contains("rateLimitData")
                    ? @"{""data"":{""rateLimitData"":{""limitPerHour"":1000,""pointsSpentThisHour"":1,""pointsResetIn"":3600}}}"
                    : @"{""data"":{""worldData"":{""regions"":[{""slug"":""US""}]}}}")
            };
        }
    }

    private static WarcraftLogsV2Client Client(Wcl handler, ILogger<WarcraftLogsV2Client> logger = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        { ["WclClientId"] = "offline", ["WclClientSecret"] = "offline" }).Build();
        return new WarcraftLogsV2Client(config, factory.Object, logger ?? NullLogger<WarcraftLogsV2Client>.Instance);
    }

    private static List<LogLevel> Levels(Mock<ILogger<WarcraftLogsV2Client>> log)
        => log.Invocations.Where(i => i.Method.Name == nameof(ILogger.Log)).Select(i => (LogLevel)i.Arguments[0]).ToList();

    [Fact]
    public async Task A_forever_site_that_is_down_is_a_warning_and_the_main_site_down_is_still_an_error()
    {
        // Error lines are what an alert is raised on; a site this new being away isn't one.
        var log = new Mock<ILogger<WarcraftLogsV2Client>>();
        var client = Client(new Wcl { Forever = HttpStatusCode.NotFound }, log.Object);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryAsync("{ worldData { regions { slug } } }", null, WowGameVersion.Forever));

        Assert.Contains(LogLevel.Warning, Levels(log));
        Assert.DoesNotContain(LogLevel.Error, Levels(log));

        var mainLog = new Mock<ILogger<WarcraftLogsV2Client>>();
        var mainDown = Client(new MainIsDown(), mainLog.Object);
        await Assert.ThrowsAsync<HttpRequestException>(() => mainDown.QueryAsync("{ worldData { regions { slug } } }", null, WowGameVersion.Retail));
        Assert.Contains(LogLevel.Error, Levels(mainLog));
    }

    private sealed class MainIsDown : Wcl
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri!.AbsolutePath.Contains("oauth")
                ? base.SendAsync(request, cancellationToken)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 600)]
    [InlineData(HttpStatusCode.NotFound, null)]
    public async Task A_refusal_from_the_forever_site_stays_its_own(HttpStatusCode refusal, int? retryAfterSeconds)
    {
        var handler = new Wcl { Forever = refusal, ForeverRetryAfter = retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null };
        var client = Client(handler);
        const string query = "{ worldData { regions { slug } } }";

        for (var i = 0; i < 3; i++)
        {
            var failed = await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryAsync(query, null, WowGameVersion.Forever));
            Assert.Equal(refusal, failed.StatusCode);
        }

        // One attempt each, no replay with a fresh token, and the sign-in the other sites run on is untouched.
        Assert.Equal(3, handler.ForeverCalls);
        Assert.Equal(1, handler.Tokens);
        Assert.False(client.AuthHealth.Unavailable);
        Assert.Equal(0, client.AuthHealth.ConsecutiveAuthFailures);
        // Nor did a 429 or a Retry-After put the main site on hold.
        var main = await client.QueryAsync(query, null, WowGameVersion.Retail);
        Assert.Equal("US", main["worldData"]!["regions"]![0]!.Value<string>("slug"));

        // The owner's probe goes the same way and reports the refusal instead of throwing.
        var raw = await client.RawQueryAsync(query, null, WowGameVersion.Forever);
        Assert.Contains($"HTTP {(int)refusal}", raw);
        Assert.False(client.AuthHealth.Unavailable);
    }

    [Fact]
    public async Task The_main_site_still_gets_a_fresh_token_when_it_says_401()
    {
        // The isolation is the Forever site's alone: everywhere else a 401 still retires the token and replays.
        var handler = new MainRejectsOnce();
        var client = Client(handler);

        var data = await client.QueryAsync("{ worldData { regions { slug } } }", null, WowGameVersion.Retail);

        Assert.NotNull(data);
        Assert.Equal(2, handler.Tokens);
    }

    private sealed class MainRejectsOnce : Wcl
    {
        private int _seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => !request.RequestUri!.AbsolutePath.Contains("oauth") && Interlocked.Increment(ref _seen) == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") })
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task The_site_adapter_asks_the_forever_site()
    {
        var handler = new Wcl();
        var site = new WclForeverLogSite(Client(handler));

        var data = await site.AskAsync("{ worldData { regions { slug } } }", null);

        Assert.Equal(1, handler.ForeverCalls);
        Assert.Empty((JArray)data!["worldData"]!["regions"]!);
    }
}
