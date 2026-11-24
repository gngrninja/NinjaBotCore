using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.WarcraftLogs;
using NinjaBotHelpers.Workers;
using Xunit;

namespace NinjaBotHelpers.Tests.Workers;

/// <summary>
/// Forever guilds in the log watcher. Warcraft Logs' Forever site is played by <see cref="Sites"/>,
/// which answers the three questions the watcher asks it: which servers there are, which of them
/// has a guild, and what a guild's newest log is.
/// </summary>
public class ForeverLogMonitoringTests : IDisposable
{
    private const string OneServerEach =
        @"{""data"":{""worldData"":{""regions"":[
            {""slug"":""US"",""servers"":{""total"":1,""data"":[{""slug"":""forever-us""}]}},
            {""slug"":""EU"",""servers"":{""total"":1,""data"":[{""slug"":""forever-eu""}]}}]}}}";

    private const string TwoUsServers =
        @"{""data"":{""worldData"":{""regions"":[
            {""slug"":""US"",""servers"":{""total"":2,""data"":[{""slug"":""normal""},{""slug"":""pvp""}]}}]}}}";

    private readonly Sites _sites = new();
    private readonly ServiceProvider _services;
    private readonly WarcraftLogsClient _wcl;
    private readonly LogMonitoringWorker _worker;
    private readonly Mock<ILogger<LogMonitoringWorker>> _log = new();

    public ForeverLogMonitoringTests()
    {
        var config = new HelpersConfiguration
        {
            WclClientId = "id",
            WclClientSecret = "secret",
            LogMonitoring = new LogMonitoringSettings
            {
                Enabled = true,
                Tier1ThresholdDays = 14,
                Tier2ThresholdDays = 30,
                Tier2IntervalHours = 3,
                Tier3IntervalHours = 24,
                // Posting a Forever log must not call the bot: it keeps no Forever caches.
                BotApiBaseUrl = "https://bot.invalid"
            }
        };
        _wcl = new WarcraftLogsClient(new HttpClient(_sites), NullLogger<WarcraftLogsClient>.Instance, config);
        var discordHttp = new HttpClient(_sites);
        discordHttp.DefaultRequestHeaders.Add("Authorization", "Bot test");
        var discord = new DiscordRestClient(discordHttp, NullLogger<DiscordRestClient>.Instance);

        var services = new ServiceCollection();
        var root = new InMemoryDatabaseRoot();
        services.AddDbContext<HelpersDbContext>(o => o.UseInMemoryDatabase("ForeverLogs", root)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        _services = services.BuildServiceProvider();

        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_sites, disposeHandler: false));
        _worker = new LogMonitoringWorker(_log.Object, _services.GetRequiredService<IServiceScopeFactory>(), config, discord, _wcl, http.Object);
    }

    public void Dispose() => _services.Dispose();

    // ===== The whole round =====

    [Fact]
    public async Task ALinkedGuildsNewLogIsPostedWhereWatchlogsWasTurnedOn()
    {
        await Seed(watch: Watch(1001, channel: 2001), guild: Guild(1001, "Hippies"));
        _sites.Servers = OneServerEach;
        _sites.Report = (name, slug, region) => name == "Hippies" && slug == "forever-us" && region == "us" ? "abcDEF123" : null;

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        var post = Assert.Single(_sites.Posts);
        Assert.EndsWith("/channels/2001/messages", post.Path);
        var embed = post.Body["embeds"]![0]!;
        Assert.Equal("New log for [Hippies] (Forever)!", embed.Value<string>("title"));
        Assert.Equal("[**Deadmines run** / **The Deadmines**](https://forever.warcraftlogs.com/reports/abcDEF123)", embed.Value<string>("description"));
        // WoWAnalyzer and WipeFest have nothing for a Forever report.
        Assert.DoesNotContain(embed["fields"]!, f => f.Value<string>("name") == "Links");
        Assert.DoesNotContain("wowanalyzer", post.Body.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(embed["fields"]!, f => f.Value<string>("name") == "Created By" && f.Value<string>("value") == "Uploader");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var watch = await db.LogMonitoring.SingleAsync();
        Assert.Equal("abcDEF123", watch.ForeverReportId);
        Assert.NotNull(watch.LatestLogForever);
        Assert.NotNull(watch.LastCheckedForever);
        // Nothing of the other games' state moves.
        Assert.Null(watch.RetailReportId);
        Assert.Null(watch.ClassicReportId);
        Assert.Null(watch.LastCheckedClassic);
        Assert.Equal("abcDEF123", (await db.WclPosted.SingleAsync()).ReportId);

        await Task.Delay(100);
        Assert.False(Asked("bot.invalid"));
        // Points are one budget, read from the main site: the Forever site is never asked about them.
        Assert.DoesNotContain(_sites.Requests, r => r.Url.Contains("forever.") && r.Query.Contains("rateLimitData"));
        Assert.Contains(_sites.Requests, r => r.Url.Contains("www.") && r.Query.Contains("rateLimitData"));
    }

    [Fact]
    public async Task TheSameLogIsNotPostedTwice()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "abcDEF123";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);
        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Single(_sites.Posts);
        // The server list is asked for once, not every round.
        Assert.Single(_sites.Requests, r => r.Query.Contains("worldData"));
    }

    [Fact]
    public async Task ARetailLogStillLinksToTheMainSiteWithItsTools()
    {
        await Seed(watch: Watch(1001), guild: null);
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowGuildAssociations.Add(new WowGuildAssociations { ServerId = 1001, WowGuild = "Retailers", LocalRealmSlug = "area-52", WowRegion = "us" });
            await db.SaveChangesAsync();
        }
        _sites.RetailReport = "retail1";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        var embed = Assert.Single(_sites.Posts).Body["embeds"]![0]!;
        Assert.Equal("New log for [Retailers]!", embed.Value<string>("title"));
        Assert.Contains("(https://www.warcraftlogs.com/reports/retail1)", embed.Value<string>("description"));
        Assert.Contains(embed["fields"]!, f => f.Value<string>("name") == "Links" && f.Value<string>("value")!.Contains("wowanalyzer.com/report/retail1"));
        // No Forever guild is linked, so the Forever site is left alone entirely.
        Assert.DoesNotContain(_sites.Requests, r => r.Url.Contains("forever."));
        // A retail post tells the bot to drop its caches; the Forever test above relies on seeing that here.
        for (var i = 0; i < 100 && !Asked("bot.invalid"); i++) await Task.Delay(20);
        Assert.True(Asked("bot.invalid"));
    }

    [Fact]
    public async Task AGuildIsAskedForInItsOwnRegion()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Les Amis", region: 1));
        _sites.Servers = OneServerEach;
        _sites.Report = (name, slug, region) => slug == "forever-eu" && region == "eu" ? "eu1" : null;

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Single(_sites.Posts);
    }

    [Fact]
    public async Task AGuildWarcraftLogsDoesNotHavePostsNothingAndIsStillMarkedChecked()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Nobody Uploads"));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => null;

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
        using var scope = _services.CreateScope();
        var watch = await scope.ServiceProvider.GetRequiredService<HelpersDbContext>().LogMonitoring.SingleAsync();
        Assert.NotNull(watch.LastCheckedForever);
        Assert.Null(watch.ForeverReportId);
    }

    // ===== Which server =====

    [Fact]
    public async Task WithSeveralServersTheOneThatHasTheGuildIsUsed()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        _sites.Servers = TwoUsServers;
        _sites.HasGuild = (name, slug, region) => name == "Hippies" && slug == "pvp" && region == "us";
        _sites.Report = (_, slug, _) => slug == "pvp" ? "pvp1" : "wrong";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Contains("/reports/pvp1)", Assert.Single(_sites.Posts).Body["embeds"]![0]!.Value<string>("description"));
        Assert.DoesNotContain(_sites.Requests, r => r.Query.Contains("reportData") && r.Query.Contains("\"normal\""));
    }

    [Fact]
    public async Task AGuildNameOnTwoServersIsLeftAloneRatherThanGuessedAt()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        _sites.Servers = TwoUsServers;
        _sites.HasGuild = (_, _, _) => true;
        _sites.Report = (_, _, _) => "someone-elses";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
        Assert.DoesNotContain(_sites.Requests, r => r.Query.Contains("reportData"));
        Assert.Contains(_log.Invocations, i => i.Method.Name == "Log"
            && (LogLevel)i.Arguments[0] == LogLevel.Warning && i.Arguments[2].ToString()!.Contains("normal, pvp"));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(ForeverLogAddresses.MaxServers + 1)]
    public async Task ARegionWithMoreServersThanCanBeLookedThroughIsSkipped(int total)
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        var nine = string.Join(",", Enumerable.Range(0, ForeverLogAddresses.MaxServers + 1).Select(i => $@"{{""slug"":""s{i}""}}"));
        _sites.Servers = $@"{{""data"":{{""worldData"":{{""regions"":[{{""slug"":""US"",""servers"":{{""total"":{total},""data"":[{nine}]}}}}]}}}}}}";
        _sites.Report = (_, _, _) => "x";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
        Assert.DoesNotContain(_sites.Requests, r => r.Query.Contains("guildData") || r.Query.Contains("reportData"));
        // The request asks for one more than the cap, which is how a region over it is noticed.
        Assert.Contains(_sites.Requests, r => r.Query.Contains($"servers(limit: {ForeverLogAddresses.MaxServers + 1})"));
    }

    [Fact]
    public async Task AListThatStopsShortOfTheTotalIsNotTrusted()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        _sites.Servers = @"{""data"":{""worldData"":{""regions"":[{""slug"":""US"",""servers"":{""total"":3,""data"":[{""slug"":""only-one-shown""}]}}]}}}";
        _sites.Report = (_, _, _) => "x";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
    }

    [Fact]
    public async Task ARegionWarcraftLogsHasNoServersForIsSkipped()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hanguk", region: 2));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "x";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
        Assert.DoesNotContain(_sites.Requests, r => r.Query.Contains("reportData"));
    }

    // ===== A site that isn't there =====

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AForeverSiteThatRefusesCostsTheOtherGamesNothing(HttpStatusCode refusal)
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowGuildAssociations.Add(new WowGuildAssociations { ServerId = 1001, WowGuild = "Retailers", LocalRealmSlug = "area-52", WowRegion = "us" });
            await db.SaveChangesAsync();
        }
        _sites.ForeverStatus = refusal;
        _sites.RetailReport = "retail1";

        // Forever is checked last in a round; the second round shows retail still works after its refusal.
        await _worker.CheckForNewLogsAsync(CancellationToken.None);
        _sites.RetailReport = "retail2";
        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Equal(2, _sites.Posts.Count);
        Assert.All(_sites.Posts, p => Assert.DoesNotContain("(Forever)", p.Body.ToString()));
        Assert.False(_wcl.AuthHealth.Unavailable);
        Assert.Equal(0, _wcl.AuthHealth.ConsecutiveAuthFailures);
        // One token for both rounds: the refusal didn't retire it.
        Assert.Single(_sites.Requests, r => r.Url.Contains("/oauth/token"));
        // And the site isn't asked again every round.
        Assert.Single(_sites.Requests, r => r.Url.Contains("forever."));
    }

    [Fact]
    public async Task ASiteThatAnswersNonsenseIsLeftAlone()
    {
        await Seed(watch: Watch(1001), guild: Guild(1001, "Hippies"));
        _sites.Servers = @"{""data"":{""worldData"":null},""errors"":[{""message"":""Cannot query field""}]}";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
        Assert.DoesNotContain(_sites.Requests, r => r.Query.Contains("reportData"));
    }

    // ===== When a guild is due =====

    [Fact]
    public async Task NothingIsAskedWhenNoGuildIsDue()
    {
        var now = DateTime.UtcNow;
        // Linked long ago, never had a log, checked an hour ago: the slow tier, not due for a day.
        await Seed(watch: Watch(1001, lastChecked: now.AddHours(-1)), guild: Guild(1001, "Sleepy", linked: now.AddDays(-90)));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "x";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.DoesNotContain(_sites.Requests, r => r.Url.Contains("forever."));
    }

    [Fact]
    public async Task AGuildLinkedLatelyIsCheckedEveryRoundBeforeItsFirstLog()
    {
        var now = DateTime.UtcNow;
        await Seed(watch: Watch(1001, lastChecked: now.AddMinutes(-5)), guild: Guild(1001, "Fresh", linked: now.AddDays(-2)));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "first";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Single(_sites.Posts);
    }

    [Fact]
    public async Task TurningWatchingOnCountsAsActiveEvenForAnOldLink()
    {
        var now = DateTime.UtcNow;
        await Seed(watch: Watch(1001, lastChecked: now.AddMinutes(-5), latestLog: now.AddDays(-1)), guild: Guild(1001, "Old Guard", linked: now.AddDays(-90)));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "first";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Single(_sites.Posts);
    }

    [Fact]
    public async Task AGuildLinkedAgainCountsAsActiveEvenWhenWatchingIsOld()
    {
        var now = DateTime.UtcNow;
        // Watching went on three months ago and nothing ever posted; yesterday an officer pointed the server at another guild.
        await Seed(watch: Watch(1001, lastChecked: now.AddMinutes(-5), latestLog: now.AddDays(-90)), guild: Guild(1001, "New Home", linked: now.AddDays(-1)));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "first";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Single(_sites.Posts);
    }

    [Fact]
    public async Task AnotherGamesCheckTimeDoesNotHoldForeverBack()
    {
        var now = DateTime.UtcNow;
        // Classic was looked at an hour ago; Forever never has been, so it is due however quiet the guild is.
        var watch = Watch(1001);
        watch.LastCheckedClassic = now.AddHours(-1);
        watch.LastCheckedRetail = now.AddHours(-1);
        watch.LastCheckedVanilla = now.AddHours(-1);
        await Seed(watch, guild: Guild(1001, "Sleepy", linked: now.AddDays(-90)));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "first";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Single(_sites.Posts);
    }

    [Fact]
    public async Task AServerWithWatchingOffIsNotChecked()
    {
        await Seed(watch: Watch(1001, on: false), guild: Guild(1001, "Hippies"));
        _sites.Servers = OneServerEach;
        _sites.Report = (_, _, _) => "x";

        await _worker.CheckForNewLogsAsync(CancellationToken.None);

        Assert.Empty(_sites.Posts);
        Assert.DoesNotContain(_sites.Requests, r => r.Url.Contains("forever."));
    }

    // ===== Placing guilds, on its own clock =====

    [Fact]
    public async Task AGuildFoundNowhereIsLookedForAgainLaterNotEveryRound()
    {
        var clock = new Clock();
        var addresses = new ForeverLogAddresses(_wcl, NullLogger.Instance, clock);
        _sites.Servers = TwoUsServers;
        _sites.HasGuild = (_, _, _) => false;
        var guilds = new[] { Guild(1001, "Not Yet") };

        Assert.Empty(await addresses.ResolveAsync(guilds, CancellationToken.None));
        Assert.Empty(await addresses.ResolveAsync(guilds, CancellationToken.None));
        Assert.Single(_sites.Requests, r => r.Query.Contains("guildData"));

        // Half an hour on, the guild has uploaded its first log.
        clock.Now += TimeSpan.FromMinutes(31);
        _sites.HasGuild = (_, slug, _) => slug == "normal";
        Assert.Equal("normal", (await addresses.ResolveAsync(guilds, CancellationToken.None))[1001]);

        // Once placed it stays placed without asking.
        Assert.Equal("normal", (await addresses.ResolveAsync(guilds, CancellationToken.None))[1001]);
        Assert.Equal(2, _sites.Requests.Count(r => r.Query.Contains("guildData")));
    }

    [Fact]
    public async Task TwoDiscordServersOnOneGuildAreLookedUpOnce()
    {
        var addresses = new ForeverLogAddresses(_wcl, NullLogger.Instance, new Clock());
        _sites.Servers = TwoUsServers;
        _sites.HasGuild = (_, slug, _) => slug == "pvp";

        var placed = await addresses.ResolveAsync(new[] { Guild(1001, "Hippies"), Guild(1002, "hippies") }, CancellationToken.None);

        Assert.Equal("pvp", placed[1001]);
        Assert.Equal("pvp", placed[1002]);
        var lookup = Assert.Single(_sites.Requests, r => r.Query.Contains("guildData"));
        Assert.Equal(2, Regex.Matches(lookup.Query, "guildData").Count);
    }

    [Fact]
    public async Task ADownSiteIsAskedAgainAfterHalfAnHourNotBefore()
    {
        var clock = new Clock();
        var addresses = new ForeverLogAddresses(_wcl, NullLogger.Instance, clock);
        _sites.ForeverStatus = HttpStatusCode.NotFound;
        var guilds = new[] { Guild(1001, "Hippies") };

        Assert.Empty(await addresses.ResolveAsync(guilds, CancellationToken.None));
        Assert.Empty(await addresses.ResolveAsync(guilds, CancellationToken.None));
        Assert.Single(_sites.Requests, r => r.Url.Contains("forever."));

        clock.Now += TimeSpan.FromMinutes(31);
        _sites.ForeverStatus = HttpStatusCode.OK;
        _sites.Servers = OneServerEach;
        Assert.Equal("forever-us", (await addresses.ResolveAsync(guilds, CancellationToken.None))[1001]);
    }

    [Fact]
    public async Task ANameWithQuotesIsSentAsANameNotAsQuery()
    {
        var addresses = new ForeverLogAddresses(_wcl, NullLogger.Instance, new Clock());
        _sites.Servers = TwoUsServers;
        _sites.HasGuild = (_, _, _) => false;

        await addresses.ResolveAsync(new[] { Guild(1001, "Say \"Cheese\" \\ now") }, CancellationToken.None);

        var lookup = Assert.Single(_sites.Requests, r => r.Query.Contains("guildData"));
        Assert.Contains(@"name: ""Say \""Cheese\"" \\ now""", lookup.Query);
    }

    [Theory]
    [InlineData(0, "us")]
    [InlineData(1, "eu")]
    [InlineData(2, "kr")]
    [InlineData(3, "tw")]
    [InlineData(4, null)]
    [InlineData(-1, null)]
    public void RegionsAreTheBotsFourAndNothingElse(int region, string? slug)
        => Assert.Equal(slug, ForeverLogAddresses.RegionSlug(region));

    [Fact]
    public void AForeverReportOpensOnTheForeverSite()
    {
        Assert.Equal("https://forever.warcraftlogs.com/reports/a1", WclSites.ReportUrl(WowGameVersion.Forever, "a1"));
        Assert.Equal("https://www.warcraftlogs.com/reports/a1", WclSites.ReportUrl(WowGameVersion.Retail, "a1"));
        Assert.Equal("https://www.warcraftlogs.com/reports/a1", WclSites.ReportUrl(WowGameVersion.Classic, "a1"));
    }

    // ===== Scaffolding =====

    private bool Asked(string host)
    {
        lock (_sites.Requests) return _sites.Requests.Any(r => r.Url.Contains(host));
    }

    private static LogMonitoring Watch(long server, long channel = 2001, bool on = true, DateTime? lastChecked = null, DateTime? latestLog = null)
        => new()
        {
            Id = server, ServerId = server, ChannelId = channel, ChannelName = "logs", ServerName = "Server",
            MonitorLogs = on, LastCheckedForever = lastChecked, LatestLogForever = latestLog
        };

    private static ForeverGuild Guild(long server, string name, int region = 0, DateTime? linked = null)
        => new() { DiscordGuildId = server, Name = name, Region = region, TimeSet = linked ?? DateTime.UtcNow.AddDays(-90) };

    private async Task Seed(LogMonitoring watch, ForeverGuild? guild)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        db.LogMonitoring.Add(watch);
        if (guild != null) db.ForeverGuilds.Add(guild);
        await db.SaveChangesAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Warcraft Logs' sites and Discord, answering from what a test set up.</summary>
    private sealed class Sites : HttpMessageHandler
    {
        public sealed record Request(string Url, string Query);

        public readonly List<Request> Requests = new();
        public readonly List<(string Path, JObject Body)> Posts = new();

        public HttpStatusCode ForeverStatus = HttpStatusCode.OK;
        public string Servers = @"{""data"":{""worldData"":{""regions"":[]}}}";
        public Func<string, string, string, bool> HasGuild = (_, _, _) => false;
        public Func<string, string, string, string?> Report = (_, _, _) => null;
        public string? RetailReport;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            var query = body.StartsWith('{') && JObject.Parse(body).Value<string>("query") is { } q ? q : string.Empty;
            lock (Requests) Requests.Add(new Request(url, query));

            if (url.Contains("/oauth/token")) return Json(@"{""access_token"":""token"",""expires_in"":3600}");
            if (request.RequestUri.Host == "discord.com")
            {
                lock (Posts) Posts.Add((request.RequestUri.AbsolutePath, JObject.Parse(body)));
                return Json(@"{""id"":""1""}");
            }
            if (request.RequestUri.Host == "forever.warcraftlogs.com")
            {
                if (ForeverStatus != HttpStatusCode.OK) return new HttpResponseMessage(ForeverStatus) { Content = new StringContent("no") };
                if (query.Contains("worldData")) return Json(Servers);
                if (query.Contains("guildData")) return Json(Guilds(query));
                return Json(Reports(query, Report));
            }
            if (request.RequestUri.Host == "www.warcraftlogs.com")
            {
                if (query.Contains("rateLimitData"))
                    return Json(@"{""data"":{""rateLimitData"":{""limitPerHour"":3600,""pointsSpentThisHour"":10,""pointsResetIn"":100}}}");
                return Json(Reports(query, (_, _, _) => RetailReport));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string Guilds(string query)
        {
            var data = new JObject();
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(query, @"(g\d+_\d+): guildData \{ guild\(name: ""((?:[^""\\]|\\.)*)"", serverSlug: ""([^""]*)"", serverRegion: ""([^""]*)""\)"))
            {
                data[m.Groups[1].Value] = new JObject
                {
                    ["guild"] = HasGuild(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value) ? new JObject { ["id"] = 77 } : JValue.CreateNull()
                };
            }
            return new JObject { ["data"] = data }.ToString();
        }

        private static string Reports(string query, Func<string, string, string, string?> report)
        {
            var data = new JObject();
            var errors = new JArray();
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(query,
                @"(guild_\d+): reportData \{\s*reports\(\s*guildName: ""([^""]*)"",\s*guildServerSlug: ""([^""]*)"",\s*guildServerRegion: ""([^""]*)"""))
            {
                var alias = m.Groups[1].Value;
                if (report(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value) is { } code)
                {
                    data[alias] = JObject.Parse($@"{{""reports"":{{""data"":[{{""code"":""{code}"",""title"":""Deadmines run"",
                        ""owner"":{{""name"":""Uploader""}},""startTime"":1795000000000,""endTime"":1795003600000,
                        ""zone"":{{""id"":1,""name"":""The Deadmines""}}}}]}}}}");
                }
                else
                {
                    // What Warcraft Logs sends for a guild it doesn't have.
                    data[alias] = new JObject { ["reports"] = JValue.CreateNull() };
                    errors.Add(new JObject { ["message"] = "No guild exists for the given name, server and region.", ["path"] = new JArray(alias, "reports") });
                }
            }
            var answer = new JObject { ["data"] = data };
            if (errors.Count > 0) answer["errors"] = errors;
            return answer.ToString();
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
