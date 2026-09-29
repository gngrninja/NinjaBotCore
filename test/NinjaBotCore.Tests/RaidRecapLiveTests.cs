using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

// The live raid recap card. Every guild, player and report here is SYNTHETIC.
public class RaidRecapLiveTests
{
    private const string Code = "AbCdEfGh12345678";
    private const ulong Owner = 900;
    private const ulong Server = 2;
    private const ulong Channel = 3;
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private sealed class FakeDiscord : IRaidRecapLiveDiscord
    {
        public HashSet<ulong> Owners { get; } = new() { Owner };
        public HashSet<ulong> OwnerServers { get; } = new() { Server };
        public bool Postable { get; set; } = true;
        public RaidRecapLiveEdit EditResult { get; set; } = RaidRecapLiveEdit.Updated;
        public bool SendUnavailable { get; set; }
        public Exception SendThrows { get; set; }
        public List<(ulong Channel, MessageComponent Card)> Sent { get; } = new();
        public List<(ulong Channel, ulong Message, MessageComponent Card)> Edits { get; } = new();
        public int MembershipChecks { get; private set; }

        public Task<IReadOnlyCollection<ulong>> OwnerIdsAsync() => Task.FromResult<IReadOnlyCollection<ulong>>(Owners);

        public Task<bool> IsMemberAsync(ulong guildId, ulong userId)
        {
            MembershipChecks++;
            return Task.FromResult(Owners.Contains(userId) && OwnerServers.Contains(guildId));
        }

        public string GuildIconUrl(ulong guildId) => "https://cdn.discordapp.com/icons/2/synthetic.png";

        public bool CanPost(ulong guildId, ulong channelId) => Postable;

        public Task<ulong?> SendAsync(ulong channelId, MessageComponent card)
        {
            if (SendThrows != null) throw SendThrows;
            if (SendUnavailable) return Task.FromResult<ulong?>(null);
            Sent.Add((channelId, card));
            return Task.FromResult<ulong?>((ulong)(4200 + Sent.Count));
        }

        public Task<RaidRecapLiveEdit> EditAsync(ulong channelId, ulong messageId, MessageComponent card)
        {
            Edits.Add((channelId, messageId, card));
            return Task.FromResult(EditResult);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _efServices = new ServiceCollection()
            .AddEntityFrameworkInMemoryDatabase()
            .BuildServiceProvider();

        public ServiceProvider Services { get; }
        public FakeDiscord Discord { get; } = new();
        public Mock<IRaidRecapSource> Source { get; } = new(MockBehavior.Strict);
        public Mock<IRaidRecapPlayerSource> Players { get; }
        public RaidRecapLiveGate Gate { get; }
        public RaidRecapLiveCoordinator Coordinator { get; }
        public RaidRecapService Service { get; }
        public DateTimeOffset Now { get; set; } = Start.AddMinutes(20);
        public RaidRecapReport Report { get; set; }
        public List<WclV2Report> Listed { get; } = new();

        public Rig()
        {
            var database = Guid.NewGuid().ToString();
            Services = new ServiceCollection()
                .AddDbContext<NinjaBotEntities>(options => options
                    .UseInMemoryDatabase(database)
                    .UseInternalServiceProvider(_efServices))
                .BuildServiceProvider();
            Players = Source.As<IRaidRecapPlayerSource>();
            var scopes = Services.GetRequiredService<IServiceScopeFactory>();
            // The report cache has a 30 second lifetime; use the test clock so refreshes re-read.
            var cache = new RaidRecapCache(() => Now);
            Service = new RaidRecapService(Source.Object, cache);
            Gate = new RaidRecapLiveGate(scopes, Discord);
            Coordinator = new RaidRecapLiveCoordinator(scopes, Source.Object, Service, cache, Gate, Discord,
                NullLogger<RaidRecapLiveCoordinator>.Instance, () => Now);

            Report = Build(Wipe(1, 0, 62));
            Source.Setup(x => x.GetRaidRecapReportsAsync("Synthetic Guild", "area-52", "us")).ReturnsAsync(() => Listed.ToArray());
            Source.Setup(x => x.GetRaidRecapReportAsync(Code)).ReturnsAsync(() => Report);
            Source.Setup(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RaidRecapAnalysis("deaths", true, null)
                {
                    Deaths = new[]
                    {
                        new RaidRecapDeath(1, "PrivateAlpha", 42000, "Synthetic Blast"),
                        new RaidRecapDeath(2, "PrivateBeta", 42000, "Synthetic Blast"),
                        new RaidRecapDeath(3, "PrivateGamma", 95000, "Synthetic Spin"),
                        new RaidRecapDeath(1, "PrivateAlpha", 120000, "Melee")
                    }
                });
            Players.Setup(x => x.GetRaidRecapRosterAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((RaidRecapReport r, RaidRecapFight f, CancellationToken _) => new RaidRecapRoster(r.SnapshotKey, f.Id, new[]
                {
                    new RaidRecapPlayer(1, "PrivateAlpha", "Warrior", "Fury", "dps", "Realm", "US", true),
                    new RaidRecapPlayer(2, "PrivateBeta", "Mage", "Frost", "dps", "Realm", "US", true),
                    new RaidRecapPlayer(3, "PrivateGamma", "Priest", "Holy", "healers", "Realm", "US", true)
                }, true));
            Source.Setup(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()))
                .ReturnsAsync(JObject.Parse("{data:{entries:[{id:1,name:'PrivateAlpha',total:6000000},{id:3,name:'PrivateGamma',total:3000000}]}}"));
            Players.Setup(x => x.GetRaidRecapParsesAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>(), It.IsAny<RaidRecapRoster>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((RaidRecapReport r, RaidRecapFight f, bool healing, RaidRecapRoster _, CancellationToken _) =>
                    new RaidRecapParses(r.SnapshotKey, f.Id, healing ? "hps" : "dps", "Parses", "Today", 1, Start,
                        new[] { new RaidRecapParse(1, 1001, 96.4, 500, 60, 1, 640) }));
        }

        public RaidRecapReport Build(params RaidRecapFight[] fights)
        {
            var end = fights.Length == 0 ? 0 : fights.Max(f => f.EndMs ?? 0);
            return new RaidRecapReport(Code, "Synthetic raid night", 1, Start.ToUnixTimeMilliseconds(),
                Start.ToUnixTimeMilliseconds() + end, Now, fights);
        }

        /// <summary>The guild's newest log, last touched the given number of minutes ago.</summary>
        public void List(double minutesSinceLastEvent, string code = Code)
        {
            Listed.Clear();
            Listed.Add(new WclV2Report
            {
                Code = code,
                Title = "Synthetic raid night",
                StartTime = Start.ToUnixTimeMilliseconds(),
                EndTime = Now.AddMinutes(-minutesSinceLastEvent).ToUnixTimeMilliseconds(),
                Zone = new WclV2Zone { Name = "Synthetic Spire" }
            });
        }

        public async Task EnrollAsync(ulong server = Server, bool association = true, bool enabled = true)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            if (association)
            {
                db.WowGuildAssociations.Add(new WowGuildAssociations
                {
                    ServerId = (long)server,
                    ServerName = "Synthetic server",
                    WowGuild = "Synthetic Guild",
                    WowRealm = "Area 52",
                    LocalRealmSlug = "area-52",
                    WowRegion = "us"
                });
            }

            db.RaidRecapLiveSettings.Add(new RaidRecapLiveSettings
            {
                DiscordGuildId = (long)server,
                Enabled = enabled,
                ChannelId = (long)Channel
            });
            await db.SaveChangesAsync();
        }

        public async Task<List<RaidRecapLiveCard>> CardsAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<NinjaBotEntities>()
                .RaidRecapLiveCards.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        }

        public Task SweepAsync() => Coordinator.RunSweepAsync(CancellationToken.None);

        public void Dispose()
        {
            try { Services.Dispose(); }
            finally { _efServices.Dispose(); }
        }
    }

    private static RaidRecapFight Wipe(int id, double startMinute, double left, int difficulty = 4) =>
        new(id, 3001, difficulty, "Synthetic Boss", false, false, startMinute * 60000, startMinute * 60000 + 300000, left);

    private static RaidRecapFight Kill(int id, double startMinute, int encounter = 3001) =>
        new(id, encounter, 4, encounter == 3001 ? "Synthetic Boss" : "Second Boss", true, false, startMinute * 60000, startMinute * 60000 + 300000, null);

    private static string Text(MessageComponent card) => RaidRecapPanelTests.Text(card);

    // ===== Rollout gate =====

    [Fact]
    public async Task GateDefaultsToOwnerServersAndFollowsTheOwnersMembership()
    {
        using var rig = new Rig();
        Assert.Equal(RaidRecapRolloutMode.OwnerServers, await rig.Gate.ModeAsync());
        Assert.True(await rig.Gate.AllowsAsync(Server));
        Assert.False(await rig.Gate.AllowsAsync(77));

        // Joining a server makes it eligible; leaving removes it. No list to maintain.
        rig.Discord.OwnerServers.Add(77);
        Assert.True(await rig.Gate.AllowsAsync(77));
        rig.Discord.OwnerServers.Remove(Server);
        Assert.False(await rig.Gate.AllowsAsync(Server));
    }

    [Fact]
    public async Task GateModesPersistAndOffAllowsNobody()
    {
        using var rig = new Rig();
        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Everyone);
        Assert.Equal(RaidRecapRolloutMode.Everyone, await rig.Gate.ModeAsync());
        Assert.True(await rig.Gate.AllowsAsync(77));
        Assert.Equal(0, rig.Discord.MembershipChecks);

        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Off);
        Assert.False(await rig.Gate.AllowsAsync(Server));
        Assert.False(await rig.Gate.AllowsAsync(77));

        using var scope = rig.Services.CreateScope();
        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<NinjaBotEntities>().RaidRecapRollout.ToListAsync());
        Assert.Equal(RaidRecapRollout.SingletonId, row.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Gate.SetModeAsync((RaidRecapRolloutMode)42));
    }

    [Fact]
    public async Task GateWithNoKnownOwnerAllowsNobody()
    {
        using var rig = new Rig();
        rig.Discord.Owners.Clear();
        Assert.False(await rig.Gate.AllowsAsync(Server));
    }

    // ===== Found =====

    [Fact]
    public async Task LiveLogIsPostedOnceInTheServersChannel()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 2);

        await rig.SweepAsync();

        var sent = Assert.Single(rig.Discord.Sent);
        Assert.Equal(Channel, sent.Channel);
        Assert.Contains("🔴 **LIVE**", Text(sent.Card));
        var card = Assert.Single(await rig.CardsAsync());
        Assert.Equal(RaidRecapLiveState.Live, card.State);
        Assert.Equal(Code, card.ReportCode);
        Assert.Equal(4201, card.MessageId);
        Assert.Equal((long)Channel, card.ChannelId);
        Assert.Equal("Synthetic Guild", card.GuildName);
        Assert.Equal("Synthetic Spire", card.ZoneName);

        // Later sweeps never post a second card for the same log.
        for (var i = 0; i < 5; i++)
        {
            rig.Now = rig.Now.AddMinutes(6);
            rig.List(minutesSinceLastEvent: 1);
            rig.Report = rig.Build(Wipe(1, 0, 62));
            await rig.SweepAsync();
        }

        Assert.Single(rig.Discord.Sent);
        Assert.Single(await rig.CardsAsync());
    }

    [Theory]
    [InlineData("kill-switch")]
    [InlineData("not-owner-server")]
    [InlineData("server-off")]
    [InlineData("cannot-post")]
    [InlineData("no-guild")]
    [InlineData("log-is-old")]
    [InlineData("no-reports")]
    [InlineData("no-boss-pull")]
    [InlineData("dungeon-log")]
    public async Task NothingIsPostedUnlessEveryConditionHolds(string missing)
    {
        using var rig = new Rig();
        await rig.EnrollAsync(association: missing != "no-guild", enabled: missing != "server-off");
        rig.List(minutesSinceLastEvent: missing == "log-is-old" ? 16 : 2);
        if (missing == "kill-switch") await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Off);
        if (missing == "not-owner-server") rig.Discord.OwnerServers.Clear();
        if (missing == "cannot-post") rig.Discord.Postable = false;
        if (missing == "no-reports") rig.Listed.Clear();
        if (missing == "no-boss-pull") rig.Report = rig.Build();
        if (missing == "dungeon-log") rig.Report = rig.Build(Wipe(1, 0, 40, difficulty: 10));

        await rig.SweepAsync();

        Assert.Empty(rig.Discord.Sent);
        Assert.Empty(rig.Discord.Edits);
        Assert.Empty(await rig.CardsAsync());
        if (missing is "kill-switch" or "not-owner-server" or "server-off" or "cannot-post" or "no-guild")
        {
            // Refused before any WarcraftLogs call is spent.
            rig.Source.Verify(x => x.GetRaidRecapReportsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            rig.Source.Verify(x => x.GetRaidRecapReportAsync(It.IsAny<string>()), Times.Never);
        }
    }

    [Fact]
    public async Task ServersWithoutALiveCardAreCheckedEveryFiveMinutesNotEverySweep()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 60);

        for (var i = 0; i < 9; i++)
        {
            await rig.SweepAsync();
            rig.Now = rig.Now.AddSeconds(30);
        }

        rig.Source.Verify(x => x.GetRaidRecapReportsAsync("Synthetic Guild", "area-52", "us"), Times.Once);
        rig.Now = rig.Now.AddMinutes(1);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportsAsync("Synthetic Guild", "area-52", "us"), Times.Exactly(2));
    }

    [Fact]
    public async Task ALogThatAlreadyHadACardIsNeverPostedAgain()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            db.RaidRecapLiveCards.Add(new RaidRecapLiveCard
            {
                DiscordGuildId = (long)Server, ChannelId = (long)Channel, MessageId = 1, ReportCode = Code,
                State = RaidRecapLiveState.Stopped, StartedAt = rig.Now.UtcDateTime,
                LastChangedAt = rig.Now.UtcDateTime, LastCheckedAt = rig.Now.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        Assert.Empty(rig.Discord.Sent);
        Assert.Empty(rig.Discord.Edits);
    }

    // ===== Watching =====

    [Fact]
    public async Task CardIsEditedInPlaceOnlyWhenAPullChanges()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(6);             // the first pull ended a minute ago
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Wipe(1, 0, 62));
        await rig.SweepAsync();
        rig.Source.Invocations.Clear();

        // Too soon: nothing is read.
        rig.Now = rig.Now.AddSeconds(60);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Never);

        // Due, but the log has not changed: read once, no edit.
        rig.Now = rig.Now.AddSeconds(31);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Once);
        Assert.Empty(rig.Discord.Edits);

        // A new pull lands: the same message is edited.
        rig.Now = Start.AddMinutes(12);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 41));
        await rig.SweepAsync();
        var edit = Assert.Single(rig.Discord.Edits);
        Assert.Equal(Channel, edit.Channel);
        Assert.Equal(4201ul, edit.Message);
        Assert.Contains("`62 · 41`", Text(edit.Card));
        Assert.Contains("2 pulls", Text(edit.Card));
        Assert.Single(rig.Discord.Sent);

        var card = Assert.Single(await rig.CardsAsync());
        Assert.Equal(RaidRecapLiveState.Live, card.State);
        Assert.Equal(0, card.Failures);
    }

    [Fact]
    public async Task RefreshSlowsDownAfterTenQuietMinutesAndSpeedsUpWithTheNextPull()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Wipe(1, 0, 62));   // pull ended 5 minutes after the log began
        rig.Now = Start.AddMinutes(6);
        await rig.SweepAsync();
        rig.Now = rig.Now.AddSeconds(91);
        await rig.SweepAsync();                    // learns when the last pull ended
        rig.Source.Invocations.Clear();

        // More than ten minutes since that pull: a 91 second wait is no longer enough.
        rig.Now = Start.AddMinutes(16);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Once);
        rig.Now = rig.Now.AddSeconds(91);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Once);
        rig.Now = rig.Now.AddSeconds(91);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Exactly(2));

        // The next pull lands: back to the fast pace.
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 14, 41));   // ended at minute 19
        rig.Now = Start.AddMinutes(23);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Exactly(3));
        rig.Now = rig.Now.AddSeconds(91);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Exactly(4));
    }

    [Fact]
    public async Task ANewWipeDoesNotReadTheLastKillAgain()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Kill(1, 0), Wipe(2, 6, 62) with { EncounterId = 3002, Name = "Second Boss" });
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()), Times.Exactly(2));
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()), Times.Once);

        rig.Now = rig.Now.AddMinutes(10);
        rig.Report = rig.Build(Kill(1, 0),
            Wipe(2, 6, 62) with { EncounterId = 3002, Name = "Second Boss" },
            Wipe(3, 12, 40) with { EncounterId = 3002, Name = "Second Boss" });
        await rig.SweepAsync();

        Assert.Single(rig.Discord.Edits);
        rig.Source.Verify(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()), Times.Exactly(2));
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CardIsStillPostedWhenDeathsAndOutputCannotBeLoaded()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Kill(1, 0), Wipe(2, 6, 62) with { EncounterId = 3002, Name = "Second Boss" });
        rig.Source.Setup(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PRIVATE provider detail"));
        rig.Source.Setup(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()))
            .ThrowsAsync(new TaskCanceledException("PRIVATE provider timeout"));

        await rig.SweepAsync();

        var text = Text(Assert.Single(rig.Discord.Sent).Card);
        Assert.Contains("🔴 **LIVE**", text);
        Assert.Contains("✅ 1 kill · 💀 1 wipe", text);
        Assert.DoesNotContain("first deaths", text);
        Assert.DoesNotContain("Top damage", text);
        Assert.DoesNotContain("PRIVATE", text);
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()), Times.Once);
        rig.Source.Verify(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()), Times.Once);
        Assert.Equal(RaidRecapLiveState.Live, Assert.Single(await rig.CardsAsync()).State);
    }

    // ===== One card's trouble never holds up another =====

    [Fact]
    public async Task ProviderTimeoutCountsAsAFailureAndTheOtherCardsStillRefresh()
    {
        using var rig = new Rig();
        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Everyone);
        const string slow = "SlowSlowSlow0001";
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            foreach (var (guild, code) in new[] { (11L, slow), (12L, Code) })
            {
                db.RaidRecapLiveSettings.Add(new RaidRecapLiveSettings { DiscordGuildId = guild, Enabled = true, ChannelId = (long)Channel });
                db.RaidRecapLiveCards.Add(new RaidRecapLiveCard
                {
                    DiscordGuildId = guild, ChannelId = (long)Channel, MessageId = 7000 + guild, ReportCode = code,
                    State = RaidRecapLiveState.Live, StartedAt = rig.Now.UtcDateTime,
                    LastChangedAt = rig.Now.UtcDateTime, LastCheckedAt = rig.Now.AddMinutes(-5).UtcDateTime
                });
            }

            await db.SaveChangesAsync();
        }

        // A provider timeout looks like a cancellation nobody asked for.
        rig.Source.Setup(x => x.GetRaidRecapReportAsync(slow)).ThrowsAsync(new TaskCanceledException("PRIVATE timeout"));
        rig.Report = rig.Build(Wipe(1, 0, 62)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };

        await rig.SweepAsync();   // must not throw

        var cards = await rig.CardsAsync();
        Assert.Equal(1, cards.Single(c => c.ReportCode == slow).Failures);
        Assert.Equal(RaidRecapLiveState.Live, cards.Single(c => c.ReportCode == slow).State);
        Assert.Equal(0, cards.Single(c => c.ReportCode == Code).Failures);
        Assert.Equal(7012ul, Assert.Single(rig.Discord.Edits).Message);

        // The slow card waits its turn like any other; it is not retried on the very next sweep.
        rig.Source.Invocations.Clear();
        rig.Now = rig.Now.AddSeconds(30);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(slow), Times.Never);
    }

    [Fact]
    public async Task ShutdownCancellationStopsTheSweep()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Coordinator.RunSweepAsync(cts.Token));
        Assert.Empty(rig.Discord.Sent);
    }

    [Theory]
    [InlineData("Warcraft Logs quota unavailable. Retry in 60s.")]
    [InlineData("Warcraft Logs quota check timed out. Try again later.")]
    [InlineData("429")]
    public async Task SpentApiBudgetNeverStopsACard(string signal)
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        Exception refused = signal == "429"
            ? new System.Net.Http.HttpRequestException("WCL request failed", null, System.Net.HttpStatusCode.TooManyRequests)
            : new InvalidOperationException(signal);
        rig.Source.Setup(x => x.GetRaidRecapReportAsync(Code)).ThrowsAsync(refused);
        for (var i = 0; i < RaidRecapLiveCoordinator.MaxFailures * 2; i++)
        {
            rig.Now = rig.Now.AddMinutes(4);
            await rig.SweepAsync();
        }

        var card = Assert.Single(await rig.CardsAsync());
        Assert.Equal(RaidRecapLiveState.Live, card.State);
        Assert.Equal(0, card.Failures);
        Assert.Empty(rig.Discord.Edits);

        // The budget resets and the card carries on.
        rig.Source.Setup(x => x.GetRaidRecapReportAsync(Code)).ReturnsAsync(() => rig.Report);
        rig.Now = rig.Now.AddMinutes(4);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 41)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
        await rig.SweepAsync();
        Assert.Single(rig.Discord.Edits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPostLeavesNoClaimSoTheNextCheckPostsExactlyOnce(bool throws)
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        if (throws) rig.Discord.SendThrows = new InvalidOperationException("PRIVATE discord failure");
        else rig.Discord.SendUnavailable = true;

        await rig.SweepAsync();   // must not throw
        Assert.Empty(rig.Discord.Sent);
        Assert.Empty(await rig.CardsAsync());

        rig.Discord.SendThrows = null;
        rig.Discord.SendUnavailable = false;
        rig.Now = rig.Now.AddMinutes(6);
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();
        Assert.Single(rig.Discord.Sent);
        var card = Assert.Single(await rig.CardsAsync());
        Assert.Equal(4201, card.MessageId);
        Assert.Equal(RaidRecapLiveState.Live, card.State);
    }

    [Fact]
    public async Task ClaimLeftByAnInterruptedPostIsPostedOnTheNextRefresh()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            db.RaidRecapLiveCards.Add(new RaidRecapLiveCard
            {
                DiscordGuildId = (long)Server, ChannelId = (long)Channel, MessageId = 0, ReportCode = Code,
                State = RaidRecapLiveState.Live, StartedAt = rig.Now.UtcDateTime,
                LastChangedAt = rig.Now.UtcDateTime, LastCheckedAt = rig.Now.AddMinutes(-5).UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        rig.Report = rig.Build(Wipe(1, 0, 62)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
        await rig.SweepAsync();

        Assert.Single(rig.Discord.Sent);
        Assert.Empty(rig.Discord.Edits);
        Assert.Equal(4201, Assert.Single(await rig.CardsAsync()).MessageId);
    }

    [Fact]
    public async Task CardOutsideTheRolloutStopsOnceQuietAndNeverUsesASlot()
    {
        using var rig = new Rig();
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            for (var i = 0; i < RaidRecapLiveCoordinator.MaxLiveCards; i++)
            {
                // Posted while the rollout was open to everyone; the owner is in none of these.
                db.RaidRecapLiveSettings.Add(new RaidRecapLiveSettings { DiscordGuildId = 1000 + i, Enabled = true, ChannelId = (long)Channel });
                db.RaidRecapLiveCards.Add(new RaidRecapLiveCard
                {
                    DiscordGuildId = 1000 + i, ChannelId = (long)Channel, MessageId = 5000 + i, ReportCode = Code,
                    State = RaidRecapLiveState.Live, StartedAt = rig.Now.UtcDateTime,
                    LastChangedAt = rig.Now.UtcDateTime, LastCheckedAt = rig.Now.UtcDateTime
                });
            }

            await db.SaveChangesAsync();
        }

        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        // The owner's own server still gets its card: the others do not hold the slots.
        Assert.Single(rig.Discord.Sent);
        Assert.Empty(rig.Discord.Edits);
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Once);

        rig.Now = rig.Now.AddMinutes(31);
        rig.Report = rig.Build(Wipe(1, 0, 62)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
        await rig.SweepAsync();

        var cards = await rig.CardsAsync();
        Assert.All(cards.Where(c => c.DiscordGuildId >= 1000), c => Assert.Equal(RaidRecapLiveState.Stopped, c.State));
        Assert.Equal(RaidRecapLiveState.Live, cards.Single(c => c.DiscordGuildId == (long)Server).State);
        var notices = rig.Discord.Edits.Where(e => e.Message >= 5000).ToArray();
        Assert.Equal(RaidRecapLiveCoordinator.MaxLiveCards, notices.Length);
        Assert.All(notices, e => Assert.Contains("Live updates stopped", Text(e.Card)));
    }

    [Fact]
    public async Task NewLogChecksAreSpreadOutLongestWaitingFirst()
    {
        using var rig = new Rig();
        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Everyone);
        const int servers = 12;
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            for (var i = 0; i < servers; i++)
            {
                db.RaidRecapLiveSettings.Add(new RaidRecapLiveSettings { DiscordGuildId = 2000 + i, Enabled = true, ChannelId = (long)Channel });
                db.WowGuildAssociations.Add(new WowGuildAssociations
                {
                    ServerId = 2000 + i, ServerName = "Synthetic", WowGuild = "Guild" + i,
                    WowRealm = "Area 52", LocalRealmSlug = "area-52", WowRegion = "us"
                });
            }

            await db.SaveChangesAsync();
        }

        var asked = new List<string>();
        rig.Source.Setup(x => x.GetRaidRecapReportsAsync(It.IsAny<string>(), "area-52", "us"))
            .Callback<string, string, string>((guild, _, _) => asked.Add(guild))
            .ReturnsAsync(Array.Empty<WclV2Report>());

        // Even straight after a restart, one sweep checks only a few servers.
        await rig.SweepAsync();
        Assert.Equal(RaidRecapLiveCoordinator.MaxDiscoveriesPerSweep, asked.Count);

        rig.Now = rig.Now.AddSeconds(30);
        await rig.SweepAsync();
        rig.Now = rig.Now.AddSeconds(30);
        await rig.SweepAsync();
        Assert.Equal(servers, asked.Count);
        Assert.Equal(servers, asked.Distinct().Count());

        rig.Now = rig.Now.AddSeconds(30);
        await rig.SweepAsync();
        Assert.Equal(servers, asked.Count);   // nobody is due again yet
    }

    [Fact]
    public async Task DungeonLogStartedAfterTheRaidLogDoesNotHideTheRaid()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        const string dungeon = "DungeonLog000001";
        rig.Listed.Add(new WclV2Report
        {
            Code = dungeon, Title = "Keys", Zone = new WclV2Zone { Name = "Mythic+ Dungeons" },
            StartTime = Start.AddMinutes(10).ToUnixTimeMilliseconds(), EndTime = rig.Now.AddMinutes(-1).ToUnixTimeMilliseconds()
        });
        rig.Listed.Add(new WclV2Report
        {
            Code = Code, Title = "Raid", Zone = new WclV2Zone { Name = "Synthetic Spire" },
            StartTime = Start.ToUnixTimeMilliseconds(), EndTime = rig.Now.AddMinutes(-2).ToUnixTimeMilliseconds()
        });
        rig.Source.Setup(x => x.GetRaidRecapReportAsync(dungeon)).ReturnsAsync(() =>
            new RaidRecapReport(dungeon, "Keys", 1, Start.ToUnixTimeMilliseconds(), rig.Now.ToUnixTimeMilliseconds(), rig.Now,
                new[] { Wipe(1, 0, 40, difficulty: 10) }));

        await rig.SweepAsync();

        Assert.Contains("Synthetic Spire", Text(Assert.Single(rig.Discord.Sent).Card));
        Assert.Equal(Code, Assert.Single(await rig.CardsAsync()).ReportCode);
    }

    // ===== Ended and stopped =====

    [Fact]
    public async Task RaidThatReturnsFromALongBreakResumesTheSameCard()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(6);
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Wipe(1, 0, 62));
        await rig.SweepAsync();

        rig.Now = Start.AddMinutes(40);   // quiet since minute 5
        await rig.SweepAsync();
        Assert.Equal(RaidRecapLiveState.Ended, Assert.Single(await rig.CardsAsync()).State);
        Assert.Contains("Raid ended", Text(rig.Discord.Edits.Last().Card));

        // Back from the break: a new pull, and the log is live again.
        rig.Now = Start.AddMinutes(60);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 50, 41));
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        Assert.Single(rig.Discord.Sent);
        var card = Assert.Single(await rig.CardsAsync());
        Assert.Equal(RaidRecapLiveState.Live, card.State);
        Assert.Equal(4201, card.MessageId);
        var resumed = rig.Discord.Edits.Last();
        Assert.Equal(4201ul, resumed.Message);
        Assert.Contains("🔴 **LIVE**", Text(resumed.Card));
        Assert.Contains("`62 · 41`", Text(resumed.Card));
    }


    [Fact]
    public async Task QuietLogBecomesAFinalCardAndIsNotWatchedAgain()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Wipe(1, 0, 62), Kill(2, 6));
        rig.Now = Start.AddMinutes(12);
        await rig.SweepAsync();

        rig.Now = Start.AddMinutes(11 + 31);   // the log's last event was at minute 11
        await rig.SweepAsync();

        var text = Text(Assert.Single(rig.Discord.Edits).Card);
        Assert.Contains("🏁 **Raid ended**", text);
        Assert.Contains("· final", text);
        Assert.DoesNotContain("LIVE", text);
        Assert.Equal(RaidRecapLiveState.Ended, Assert.Single(await rig.CardsAsync()).State);

        rig.Source.Invocations.Clear();
        rig.Now = rig.Now.AddHours(1);
        rig.List(minutesSinceLastEvent: 90);
        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapReportAsync(Code), Times.Never);
        Assert.Single(rig.Discord.Edits);
    }

    [Fact]
    public async Task NoCardIsWatchedForMoreThanEightHours()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        rig.Now = rig.Now.AddHours(8).AddMinutes(1);
        rig.Report = rig.Build(Wipe(1, 0, 62)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
        await rig.SweepAsync();

        Assert.Equal(RaidRecapLiveState.Ended, Assert.Single(await rig.CardsAsync()).State);
    }

    [Fact]
    public async Task DeletedCardStopsWatchingWithoutReposting()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        rig.Discord.EditResult = RaidRecapLiveEdit.Missing;
        rig.Now = rig.Now.AddSeconds(91);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 41));
        await rig.SweepAsync();
        Assert.Equal(RaidRecapLiveState.Stopped, Assert.Single(await rig.CardsAsync()).State);

        rig.Now = rig.Now.AddMinutes(10);
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();
        Assert.Single(rig.Discord.Sent);
        Assert.Single(rig.Discord.Edits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedFailuresStopWatchingAfterTheLimit(bool providerFails)
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        if (providerFails)
        {
            rig.Source.Setup(x => x.GetRaidRecapReportAsync(Code)).ThrowsAsync(new InvalidOperationException("PRIVATE provider detail"));
        }
        else
        {
            rig.Discord.EditResult = RaidRecapLiveEdit.Unavailable;
        }

        for (var attempt = 1; attempt <= RaidRecapLiveCoordinator.MaxFailures; attempt++)
        {
            rig.Now = rig.Now.AddMinutes(4);
            rig.Report = rig.Build(Enumerable.Range(1, attempt + 1).Select(i => Wipe(i, i * 6, 50)).ToArray());
            await rig.SweepAsync();
            var card = Assert.Single(await rig.CardsAsync());
            Assert.Equal(attempt, card.Failures);
            Assert.Equal(attempt < RaidRecapLiveCoordinator.MaxFailures ? RaidRecapLiveState.Live : RaidRecapLiveState.Stopped, card.State);
        }

        Assert.Single(rig.Discord.Sent);
        Assert.Contains("Live updates stopped", Text(rig.Discord.Edits.Last().Card));
        Assert.DoesNotContain("PRIVATE", string.Concat(rig.Discord.Edits.Select(e => Text(e.Card))));
    }

    [Fact]
    public async Task OneGoodRefreshClearsEarlierFailures()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        rig.Source.Setup(x => x.GetRaidRecapReportAsync(Code)).ThrowsAsync(new InvalidOperationException());
        rig.Now = rig.Now.AddMinutes(4);
        await rig.SweepAsync();
        Assert.Equal(1, Assert.Single(await rig.CardsAsync()).Failures);

        rig.Source.Setup(x => x.GetRaidRecapReportAsync(Code)).ReturnsAsync(() => rig.Report);
        rig.Now = rig.Now.AddMinutes(4);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 41));
        await rig.SweepAsync();
        var card = Assert.Single(await rig.CardsAsync());
        Assert.Equal(0, card.Failures);
        Assert.Equal(RaidRecapLiveState.Live, card.State);
    }

    [Fact]
    public async Task TurningTheServerOffStopsItsCardAndSaysSo()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        await rig.Coordinator.StopServerAsync(Server);

        Assert.Equal(RaidRecapLiveState.Stopped, Assert.Single(await rig.CardsAsync()).State);
        var text = Text(Assert.Single(rig.Discord.Edits).Card);
        Assert.Contains("Live updates stopped", text);
        Assert.DoesNotContain("LIVE**", text);
    }

    [Fact]
    public async Task KillSwitchSpendsNothingAndWatchingResumesWhenItIsLifted()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();
        rig.Source.Invocations.Clear();

        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Off);
        rig.Now = rig.Now.AddMinutes(5);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 41));
        await rig.SweepAsync();
        Assert.Empty(rig.Source.Invocations);
        Assert.Empty(rig.Discord.Edits);
        Assert.Equal(RaidRecapLiveState.Live, Assert.Single(await rig.CardsAsync()).State);

        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.OwnerServers);
        await rig.SweepAsync();
        Assert.Single(rig.Discord.Edits);
    }

    [Fact]
    public async Task OwnerLeavingAServerPausesItsCardWithoutSpendingCalls()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();
        rig.Source.Invocations.Clear();

        rig.Discord.OwnerServers.Clear();
        rig.Now = rig.Now.AddMinutes(5);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 41));
        await rig.SweepAsync();

        Assert.Empty(rig.Source.Invocations);
        Assert.Empty(rig.Discord.Edits);
    }

    [Fact]
    public async Task LiveCardsAreCappedAcrossAllServers()
    {
        using var rig = new Rig();
        await rig.Gate.SetModeAsync(RaidRecapRolloutMode.Everyone);
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            for (var i = 0; i < RaidRecapLiveCoordinator.MaxLiveCards; i++)
            {
                db.RaidRecapLiveSettings.Add(new RaidRecapLiveSettings { DiscordGuildId = 1000 + i, Enabled = true, ChannelId = (long)Channel });
                db.RaidRecapLiveCards.Add(new RaidRecapLiveCard
                {
                    DiscordGuildId = 1000 + i, ChannelId = (long)Channel, MessageId = 5000 + i, ReportCode = Code,
                    State = RaidRecapLiveState.Live, StartedAt = rig.Now.UtcDateTime,
                    LastChangedAt = rig.Now.UtcDateTime, LastCheckedAt = rig.Now.UtcDateTime
                });
            }

            await db.SaveChangesAsync();
        }

        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        await rig.SweepAsync();

        Assert.Empty(rig.Discord.Sent);
        rig.Source.Verify(x => x.GetRaidRecapReportsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ===== The card =====

    [Fact]
    public async Task PublicCardNamesTopPerformersAndKeepsDeathsBySpecOnly()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Kill(1, 0), Wipe(2, 6, 62) with { EncounterId = 3002, Name = "Second Boss" },
            Wipe(3, 12, 12.1) with { EncounterId = 3002, Name = "Second Boss" });
        await rig.SweepAsync();

        var card = Assert.Single(rig.Discord.Sent).Card;
        var text = Text(card);
        Assert.StartsWith("# 📊 Raid Recap · Synthetic Guild", text);
        Assert.Contains("Synthetic Spire", text);
        Assert.Contains("✅ 1 kill · 💀 2 wipes", text);
        Assert.Contains("**Now · Second Boss · H** · 2 pulls", text);
        Assert.Contains("Last pull 💀 Wipe · 5:00 · **12.1%** · Best **12.1%**", text);
        Assert.Contains("`62 · 12`", text);

        Assert.Contains("**💀 Last wipe · first deaths** · #3", text);
        Assert.Contains("`0:42` ⚔️ Fury Warrior · Synthetic Blast", text);
        Assert.Contains("`0:42` ⚔️ Frost Mage · Synthetic Blast", text);
        Assert.Contains("`1:35` 💚 Holy Priest · Synthetic Spin", text);
        Assert.Contains("-# 4 deaths on this pull", text);
        Assert.Contains("**✅ Synthetic Boss kill** · 5:00", text);
        // Top damage and healing are praise, so they are named and linked.
        Assert.Contains("🥇 **[PrivateAlpha](https://www.warcraftlogs.com/reports/AbCdEfGh12345678#fight=1&source=1)** · ⚔️ Fury Warrior · **20.0K** DPS · 🟠 **96**", text);
        Assert.Contains("🥈 **[PrivateGamma](https://www.warcraftlogs.com/reports/AbCdEfGh12345678#fight=1&source=3)** · 💚 Holy Priest · **10.0K** DPS", text);

        // Deaths never are: spec and class only, no name and no player link.
        var normalized = text.Replace("\r\n", "\n");
        var from = normalized.IndexOf("**💀 Last wipe", StringComparison.Ordinal);
        var deaths = normalized.Substring(from, normalized.IndexOf("**✅ Synthetic Boss kill**", StringComparison.Ordinal) - from);
        Assert.Contains("Fury Warrior", deaths);
        Assert.DoesNotContain("Private", deaths);
        Assert.DoesNotContain("&source=", deaths);
        Assert.DoesNotContain("PrivateBeta", text);   // died, but was not a top performer
        Assert.Contains("refreshes while the raid is live", text);

        Assert.DoesNotContain("Synthetic raid night", text);   // the uploader's free-text title
        var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(card);
        Assert.DoesNotContain("@everyone", serialized);

        var parts = RaidRecapPanelTests.Flatten(card.Components).ToArray();
        Assert.InRange(parts.Length, 1, 40);
        Assert.InRange(text.Length, 1, 3800);
        Assert.Single(parts.OfType<ThumbnailComponent>());
        var buttons = parts.OfType<ButtonComponent>().ToArray();
        Assert.Equal(new[] { "WarcraftLogs", "Open my recap" }, buttons.Select(b => b.Label));
        Assert.Equal(ButtonStyle.Link, buttons[0].Style);
        Assert.Equal("rrlive_open~" + Code, buttons[1].CustomId);
        Assert.Empty(parts.OfType<SelectMenuComponent>());
    }

    [Fact]
    public void HostileNamesAndLongNightsStayInsideDiscordLimits()
    {
        var hostile = string.Concat(Enumerable.Repeat("😀**@everyone[]\\\n‮", 300));
        var fights = Enumerable.Range(1, 60)
            .Select(i => new RaidRecapFight(i, 3000 + i % 12, 4, hostile, i % 12 == 0, false, i * 400000d, i * 400000d + 300000, 50))
            .ToArray();
        var report = new RaidRecapReport(Code, hostile, 1, 1_790_000_000_000, 1_790_030_000_000, Start, fights);
        var info = new RaidRecapLiveInfo(hostile, "us", hostile, "https://cdn.discordapp.com/icons/2/synthetic.png");
        var highlights = new RaidRecapLiveHighlights
        {
            Wipe = fights[^1],
            FirstDeaths = Enumerable.Range(1, 3).Select(i => new RaidRecapLiveDeath(i * 1000, "⚔️ Fury Warrior", hostile)).ToArray(),
            TotalDeaths = 20,
            DeathsComplete = false,
            Kill = fights[11],
            TopDamage = Enumerable.Range(1, 3).Select(i => new RaidRecapLivePerformer("⚔️ Fury Warrior", double.MaxValue, 100) { Name = hostile, ActorId = i }).ToArray(),
            TopHealing = Enumerable.Range(1, 3).Select(i => new RaidRecapLivePerformer("💚 Holy Priest", 1, null) { Name = hostile }).ToArray(),
            Pattern = new RaidRecapLiveWipePattern(hostile, 10, 10),
            Quality = new RaidRecapLiveKillQuality(99.9, 20, 99.9, 5, 99.9, 99.9),
            Mvps = Enumerable.Range(1, 3).Select(i => new RaidRecapLiveMvp(hostile, 12)).ToArray()
        };
        Assert.NotNull(RaidRecapLiveHighlights.BeatenBest(report with
        {
            Fights = fights.Select((f, i) => f with { Kill = false, Remaining = 100 - i }).ToArray()
        }));

        foreach (var ended in new[] { false, true })
        {
            // Every optional line at once: record pull, pattern, raid standing and the night summary.
            var busiest = report with { Fights = fights.Select((f, i) => f with { Kill = false, Remaining = 100 - i }).ToArray() };
            var card = RaidRecapView.Live(busiest, info, ended, Start, highlights with { Wipe = busiest.Fights[^1], Kill = busiest.Fights[11] });
            Assert.Contains("New best pull", Text(card));
            Assert.Contains("📌 First death was", Text(card));
            Assert.Contains("-# Raid:", Text(card));
            Assert.Equal(ended, Text(card).Contains("top-3 finishes"));
            var parts = RaidRecapPanelTests.Flatten(card.Components).ToArray();
            Assert.InRange(parts.Length, 1, 40);
            Assert.InRange(Text(card).Length, 1, 3800);
            Assert.DoesNotContain("@everyone", Newtonsoft.Json.JsonConvert.SerializeObject(card));
            Assert.Contains("at least 20 deaths", Text(card));
            Assert.All(parts.OfType<ButtonComponent>().Where(b => b.Style != ButtonStyle.Link), b => Assert.InRange(b.CustomId.Length, 1, 100));
        }
    }

    [Fact]
    public void BossIsCalledNowOnlyWhileTheRaidIsStillWorkingOnIt()
    {
        var info = new RaidRecapLiveInfo("Guild", "us", "Zone", "");
        RaidRecapReport Report(params RaidRecapFight[] fights) =>
            new(Code, "Synthetic", 1, 1_790_000_000_000, 1_790_000_900_000, Start, fights);

        Assert.Contains("**Now · Synthetic Boss · H**", Text(RaidRecapView.Live(Report(Wipe(1, 0, 40)), info, false, Start)));
        var killed = Text(RaidRecapView.Live(Report(Wipe(1, 0, 40), Kill(2, 6)), info, false, Start));
        Assert.Contains("**Latest · Synthetic Boss · H**", killed);
        Assert.DoesNotContain("**Now ·", killed);
        Assert.Contains("**Last boss · Synthetic Boss · H**", Text(RaidRecapView.Live(Report(Kill(1, 0)), info, true, Start)));
    }

    [Fact]
    public void UnmatchedTopPerformerIsNamedButNeverLinked()
    {
        var kill = Kill(1, 0);
        var report = new RaidRecapReport(Code, "Synthetic", 1, 1_790_000_000_000, 1_790_000_300_000, Start, new[] { kill });
        var highlights = new RaidRecapLiveHighlights
        {
            Kill = kill,
            TopDamage = new[]
            {
                new RaidRecapLivePerformer("⚔️ Fury Warrior", 1000, 80) { Name = "Matched", ActorId = 7 },
                new RaidRecapLivePerformer("👤 Class unknown", 900, null) { Name = "Un[matched](x) @everyone" }
            }
        };
        var text = Text(RaidRecapView.Live(report, new RaidRecapLiveInfo("Guild", "us", "Zone", ""), false, Start, highlights));
        Assert.Contains("🥇 **[Matched](https://www.warcraftlogs.com/reports/AbCdEfGh12345678#fight=1&source=7)** · ⚔️ Fury Warrior", text);
        Assert.Contains("🥈 **Un\\[matched\\]\\(x\\) ＠everyone** · 👤 Class unknown", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "&source="));
        Assert.DoesNotContain("@everyone", text);
    }

    [Theory]
    [InlineData(new double[] { 62, 55.4, 41, 12.1, 0.4 }, false, "`62 · 55 · 41 · 12 · <1`")]
    [InlineData(new double[] { 38 }, true, "`38 · ✅`")]
    public void PullStripReadsLeftToRightAndEndsOnTheKill(double[] wipes, bool killed, string expected)
    {
        var fights = wipes.Select((left, i) => Wipe(i + 1, i * 6, left)).ToList();
        if (killed) fights.Add(Kill(fights.Count + 1, fights.Count * 6));
        var boss = new RaidRecapBoss(3001, 4, "Synthetic Boss", fights);
        Assert.Equal(expected, RaidRecapView.PullStrip(boss));
    }

    [Fact]
    public void PullStripKeepsTheLatestTwelvePulls()
    {
        var boss = new RaidRecapBoss(3001, 4, "Synthetic Boss", Enumerable.Range(1, 15).Select(i => Wipe(i, i * 6, 100 - i)).ToArray());
        var strip = RaidRecapView.PullStrip(boss);
        Assert.StartsWith("`… 96 · 95", strip);
        Assert.EndsWith("86 · 85`", strip);
        Assert.Equal(11, strip.Count(c => c == '·'));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(15.1, false)]
    [InlineData(600, false)]
    [InlineData(-20, false)]
    public void LogCountsAsLiveOnlyWhenItsLastEventIsRecent(double minutesAgo, bool live)
    {
        var report = new WclV2Report { Code = Code, EndTime = Start.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds() };
        Assert.Equal(live, RaidRecapLiveCoordinator.IsLive(report, Start));
        Assert.False(RaidRecapLiveCoordinator.IsLive(new WclV2Report { Code = Code, EndTime = 0 }, Start));
    }

    [Fact]
    public void HelpListsTheServerSwitchAndHidesTheOwnerSwitch()
    {
        using var help = new NinjaBotCore.Services.HelpContentProvider(
            NullLogger<NinjaBotCore.Services.HelpContentProvider>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        help.RegenerateHelpContent();
        var commands = help.GetHelpContent().Categories.SelectMany(c => c.Commands).ToArray();
        Assert.Contains(commands, c => c.Name == "raid-recap-live");
        Assert.DoesNotContain(commands, c => c.Name == "raid-recap-rollout");
    }

    [Fact]
    public void RealStartupWiringResolvesEveryLivePiece()
    {
        // The bot registers its settings as IConfigurationRoot. Build the same container it
        // builds, with the real Discord adapter, so a wiring mistake fails here and not at boot.
        var client = RaidRecapTransportTests.Client(new RaidRecapTransportTests.Handler());
        Microsoft.Extensions.Configuration.IConfigurationRoot config =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        using var shards = new Discord.WebSocket.DiscordShardedClient();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(shards)
            .AddSingleton(config)
            .AddSingleton(client)
            .AddRaidRecap()
            .AddSingleton<NinjaBotCore.Services.RaidRecapLiveService>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<RaidRecapLiveDiscord>(services.GetRequiredService<IRaidRecapLiveDiscord>());
        Assert.NotNull(services.GetRequiredService<RaidRecapLiveGate>());
        Assert.NotNull(services.GetRequiredService<RaidRecapLiveCoordinator>());
        using var timer = services.GetRequiredService<NinjaBotCore.Services.RaidRecapLiveService>();
        Assert.NotNull(timer);
        Assert.NotNull(ActivatorUtilities.CreateInstance<RaidRecapLiveCommands>(services));
    }

    [Theory]
    [InlineData("Warcraft Logs quota unavailable. Retry in 60s.", true)]
    [InlineData("Report unavailable or incomplete. It may be private or deleted.", false)]
    public void OnlyBudgetRefusalsAreTreatedAsQuota(string message, bool quota)
    {
        Assert.Equal(quota, RaidRecapLiveCoordinator.IsQuota(new InvalidOperationException(message)));
        Assert.True(RaidRecapLiveCoordinator.IsQuota(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.TooManyRequests)));
        Assert.False(RaidRecapLiveCoordinator.IsQuota(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.InternalServerError)));
        Assert.False(RaidRecapLiveCoordinator.IsQuota(new TaskCanceledException()));
    }

    [Fact]
    public void RolloutCommandSaysChangedOnlyWhenTheModeReallyChanged()
    {
        const RaidRecapRolloutMode mine = RaidRecapRolloutMode.OwnerServers;
        Assert.Equal("Rollout is **limited to servers you are a member of**.",
            RaidRecapLiveCommands.RolloutLine(mine, mine, requested: false));
        var same = RaidRecapLiveCommands.RolloutLine(mine, mine, requested: true);
        Assert.Contains("already set", same);
        Assert.DoesNotContain("Changed from", same);
        var changed = RaidRecapLiveCommands.RolloutLine(RaidRecapRolloutMode.Off, mine, requested: true);
        Assert.Equal("Rollout is **off everywhere**. Changed from limited to servers you are a member of.", changed);
    }

    // ===== Insights =====

    [Theory]
    [InlineData("Blast,Blast,Blast,Spin,Melee", "Blast", 3, 5)]      // more than half
    [InlineData("Blast,Blast,Spin,Melee", "Blast", 2, 4)]            // exactly half, and clearly ahead
    [InlineData("Blast,Spin,Blast,Spin", null, 0, 0)]                // a tie is not a pattern
    [InlineData("Blast,Blast,,Unknown killing ability", "Blast", 2, 4)]
    [InlineData("Blast,Blast", null, 0, 0)]                          // too few wipes to call it a pattern
    [InlineData("Blast,Spin,Melee", null, 0, 0)]                     // nothing repeats
    [InlineData("Blast,Blast,Spin,Melee,Fire", null, 0, 0)]          // repeats, but under half
    [InlineData(",,", null, 0, 0)]
    public void WipePatternNeedsThreeWipesAndHalfOfThem(string blows, string ability, int count, int wipes)
    {
        var pattern = RaidRecapLiveHighlights.FindPattern(blows.Split(',').Select(b => b.Length == 0 ? null : b).ToArray());
        if (ability == null)
        {
            Assert.Null(pattern);
            return;
        }

        Assert.Equal(new RaidRecapLiveWipePattern(ability, count, wipes), pattern);
    }

    [Fact]
    public void NewBestPullIsCalledOnlyWhenTheLatestWipeBeatsEveryEarlierOne()
    {
        RaidRecapReport Night(params RaidRecapFight[] fights) =>
            new(Code, "Synthetic", 1, 1_790_000_000_000, 1_790_003_000_000, Start, fights);

        var record = RaidRecapLiveHighlights.BeatenBest(Night(Wipe(1, 0, 62), Wipe(2, 6, 38), Wipe(3, 12, 12.1)));
        Assert.Equal(38, record.Value.Previous);
        Assert.Equal(3, record.Value.Fight.Id);
        Assert.Null(RaidRecapLiveHighlights.BeatenBest(Night(Wipe(1, 0, 12), Wipe(2, 6, 38))));          // worse
        Assert.Null(RaidRecapLiveHighlights.BeatenBest(Night(Wipe(1, 0, 38), Wipe(2, 6, 38))));          // equal
        Assert.Null(RaidRecapLiveHighlights.BeatenBest(Night(Wipe(1, 0, 38))));                          // first pull
        Assert.Null(RaidRecapLiveHighlights.BeatenBest(Night(Wipe(1, 0, 38), Kill(2, 6))));              // a kill, not a wipe
        // Another boss's pulls do not count.
        Assert.Null(RaidRecapLiveHighlights.BeatenBest(Night(Wipe(1, 0, 62), Wipe(2, 6, 12) with { EncounterId = 3002 })));

        var info = new RaidRecapLiveInfo("Guild", "us", "Zone", "");
        var text = Text(RaidRecapView.Live(Night(Wipe(1, 0, 62), Wipe(2, 6, 38), Wipe(3, 12, 12.1)), info, false, Start));
        Assert.Contains("🔥 **New best pull** · 12.1%, was 38%", text);
        Assert.DoesNotContain("New best pull", Text(RaidRecapView.Live(Night(Wipe(1, 0, 12), Wipe(2, 6, 38)), info, false, Start)));
    }

    [Theory]
    [InlineData(3001)]   // the next pull on the same boss has begun
    [InlineData(3002)]   // the raid has moved to another boss
    public void PullThatHasJustBegunNeverBorrowsTheLastPullsNumbers(int encounter)
    {
        var begun = new RaidRecapFight(4, encounter, 4, encounter == 3001 ? "Synthetic Boss" : "Second Boss", null, true, 1_080_000, null, 97);
        var report = new RaidRecapReport(Code, "Synthetic", 1, 1_790_000_000_000, 1_790_001_100_000, Start,
            new[] { Wipe(1, 0, 62), Wipe(2, 6, 38), Wipe(3, 12, 12.1), begun });
        var highlights = new RaidRecapLiveHighlights
        {
            Wipe = report.Fights[2],
            FirstDeaths = new[] { new RaidRecapLiveDeath(42000, "⚔️ Fury Warrior", "Synthetic Blast") },
            TotalDeaths = 1,
            DeathsComplete = true,
            Pattern = new RaidRecapLiveWipePattern("Synthetic Blast", 3, 3)
        };

        var text = Text(RaidRecapView.Live(report, new RaidRecapLiveInfo("Guild", "us", "Zone", ""), false, Start, highlights));

        Assert.Contains("Last pull ⏳ In progress", text);
        // The record belongs to pull 3, which is no longer the pull on show.
        Assert.DoesNotContain("New best pull", text);
        Assert.DoesNotContain("97%", text);
        if (encounter == 3001)
        {
            Assert.Contains("**Now · Synthetic Boss · H**", text);
            Assert.Contains("📌 First death was **Synthetic Blast** on 3 of 3 wipes", text);
        }
        else
        {
            // Advice about the last boss is not shown under the new one.
            Assert.Contains("**Now · Second Boss · H**", text);
            Assert.DoesNotContain("📌", text);
        }
    }

    [Fact]
    public void KillQualityAveragesDamageDealersAndHealersSeparatelyAndLeavesTanksOut()
    {
        RaidRecapStanding Row(int id, string role, double? parse) => new("P" + id, 1000, 100)
        {
            ActorId = id,
            Player = new(id, "P" + id, "Warrior", "Fury", role, "Realm", "US", true),
            Parse = parse.HasValue ? new(id, 1000 + id, parse.Value, 100, null, null, null) : null
        };
        var rows = new[] { Row(1, "dps", 90), Row(2, "dps", 70), Row(3, "dps", null), Row(4, "tanks", 10), Row(5, "healers", 60) };
        RaidRecapOutput Output(string metric, double? speed, double? execution) => new(rows,
            new RaidRecapParses("k", 1, metric, "Parses", "Today", 1, Start, Array.Empty<RaidRecapParse>())
            { SpeedPercent = speed, ExecutionPercent = execution }, null);

        var quality = RaidRecapLiveHighlights.FindQuality(Output("dps", 72.4, 64), Output("hps", null, null));
        Assert.Equal(80, quality.DamageAverage);
        Assert.Equal(2, quality.DamageParses);
        Assert.Equal(60, quality.HealingAverage);
        Assert.Equal(1, quality.HealingParses);
        Assert.Equal(72.4, quality.Speed);
        Assert.Equal(64, quality.Execution);

        // Nothing to say is nothing shown, never a zero.
        Assert.Null(RaidRecapLiveHighlights.FindQuality(new(Array.Empty<RaidRecapStanding>(), null, null), null));

        var kill = Kill(1, 0);
        var report = new RaidRecapReport(Code, "Synthetic", 1, 1_790_000_000_000, 1_790_000_300_000, Start, new[] { kill });
        var info = new RaidRecapLiveInfo("Guild", "us", "Zone", "");
        var shown = Text(RaidRecapView.Live(report, info, false, Start, new RaidRecapLiveHighlights
        {
            Kill = kill,
            TopDamage = new[] { new RaidRecapLivePerformer("⚔️ Fury Warrior", 1000, 90) { Name = "P1" } },
            Quality = quality
        }));
        Assert.Contains("-# Raid: damage parse avg 🟣 **80** · healing avg 🔵 **60** · speed 🔵 **72** · execution 🔵 **64**", shown);

        var partial = Text(RaidRecapView.Live(report, info, false, Start, new RaidRecapLiveHighlights
        {
            Kill = kill,
            TopDamage = new[] { new RaidRecapLivePerformer("⚔️ Fury Warrior", 1000, 90) { Name = "P1" } },
            Quality = new RaidRecapLiveKillQuality(80, 2, null, 0, null, null)
        }));
        Assert.Contains("-# Raid: damage parse avg 🟣 **80**", partial);
        Assert.DoesNotContain("speed", partial);
        Assert.DoesNotContain("healing avg", partial);
    }

    [Theory]
    [InlineData("{data:[{speed:{rankPercent:72.5},execution:{rankPercent:64}}]}", 72.5, 64d)]
    [InlineData("{data:[{speed:88,execution:12}]}", 88d, 12d)]
    [InlineData("{data:[{speed:{rankPercent:0}}]}", 0d, null)]
    [InlineData("{data:[{speed:{rankPercent:101},execution:{rankPercent:-1}}]}", null, null)]
    [InlineData("{data:[{speed:'fast',execution:{rank:3}}]}", null, null)]
    [InlineData("{data:[{}]}", null, null)]
    [InlineData("{data:[{speed:50},{speed:60}]}", null, null)]
    [InlineData("{}", null, null)]
    public void SpeedAndExecutionAreOptionalAndOnlyAcceptedAsPercentages(string json, double? speed, double? execution)
    {
        var ranking = JObject.Parse(json);
        Assert.Equal(speed, RaidRecapPlayerRules.FightPercent(ranking, "speed"));
        Assert.Equal(execution, RaidRecapPlayerRules.FightPercent(ranking, "execution"));
        Assert.Null(RaidRecapPlayerRules.FightPercent(null, "speed"));
    }

    [Fact]
    public void FinalCardAddsPaceAndStandoutsAndTheLiveCardDoesNot()
    {
        // Gaps between pulls: 1, 1 and 4 minutes. The middle one is 1 minute.
        var fights = new[]
        {
            new RaidRecapFight(1, 3001, 4, "A", true, false, 0, 300000, null),
            new RaidRecapFight(2, 3002, 4, "B", true, false, 360000, 660000, null),
            new RaidRecapFight(3, 3003, 4, "C", true, false, 720000, 1020000, null),
            new RaidRecapFight(4, 3004, 4, "D", true, false, 1260000, 1560000, null)
        };
        var report = new RaidRecapReport(Code, "Synthetic", 1, 1_790_000_000_000, 1_790_001_560_000, Start, fights);
        Assert.Equal(60000, RaidRecapLiveHighlights.MedianGapMs(report));
        // An even number of gaps takes the middle of the two middle ones: 1 and 1 minute.
        Assert.Equal(60000, RaidRecapLiveHighlights.MedianGapMs(report with { Fights = fights.Take(3).ToArray() }));
        var uneven = fights.Take(2).Append(fights[2] with { StartMs = 840000, EndMs = 1140000 }).ToArray();   // gaps of 1 and 3 minutes
        Assert.Equal(120000, RaidRecapLiveHighlights.MedianGapMs(report with { Fights = uneven }));
        Assert.Null(RaidRecapLiveHighlights.MedianGapMs(report with { Fights = fights.Take(2).ToArray() }));

        RaidRecapLivePerformer P(string name, int? actor = null) => new("⚔️ Fury Warrior", 1000, 90) { Name = name, ActorId = actor };
        const string hostile = "Un**safe** @everyone";
        var mvps = RaidRecapLiveHighlights.FindMvps(new[]
        {
            // One list per kill: that kill's top damage and top healing together.
            new[] { P("Ann", 1), P("Bo", 2), P(hostile, 9), P("Ann", 1) },   // Ann topped both lists: still one finish
            new[] { P("Ann", 1), P("Bo", 2), P("Di", 4), P(hostile, 9) },
            new[] { P("Bo", 2), P("Eve", 5), P("Ann", 1) }
        });
        Assert.Equal(new[] { new RaidRecapLiveMvp("Ann", 3), new RaidRecapLiveMvp("Bo", 3), new RaidRecapLiveMvp(hostile, 2) }, mvps);

        // One kill is not enough, however many lists a player topped.
        Assert.Empty(RaidRecapLiveHighlights.FindMvps(new[] { new[] { P("Ann", 1), P("Ann", 1) } }));
        // Two players who share a name are told apart by their id in the log.
        Assert.Empty(RaidRecapLiveHighlights.FindMvps(new[] { new[] { P("Same", 1) }, new[] { P("Same", 2) } }));
        Assert.Equal(new[] { new RaidRecapLiveMvp("Same", 2) },
            RaidRecapLiveHighlights.FindMvps(new[] { new[] { P("Same", 1) }, new[] { P("Same", 1) } }));

        var info = new RaidRecapLiveInfo("Guild", "us", "Zone", "");
        var highlights = new RaidRecapLiveHighlights { Kill = fights[3], Mvps = mvps };
        var final = Text(RaidRecapView.Live(report, info, true, Start, highlights));
        Assert.Contains("⏳ 1:00 between pulls, typically", final);
        Assert.Contains("🏆 Most top-3 finishes: **Ann** 3 · **Bo** 3 · **Un\\*\\*safe\\*\\* ＠everyone** 2", final);
        Assert.DoesNotContain("@everyone", final);
        var live = Text(RaidRecapView.Live(report, info, false, Start, highlights));
        Assert.DoesNotContain("between pulls", live);
        Assert.DoesNotContain("top-3 finishes", live);
    }

    [Fact]
    public async Task WipePatternAppearsAfterThreeWipesAndNamesNoPlayer()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(6);
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Wipe(1, 0, 62));
        await rig.SweepAsync();
        Assert.DoesNotContain("📌", Text(rig.Discord.Sent.Single().Card));

        rig.Now = Start.AddMinutes(12);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 55));
        await rig.SweepAsync();
        Assert.DoesNotContain("📌", Text(rig.Discord.Edits.Last().Card));

        rig.Now = Start.AddMinutes(18);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 55), Wipe(3, 12, 41));
        await rig.SweepAsync();
        var text = Text(rig.Discord.Edits.Last().Card);
        Assert.Contains("📌 First death was **Synthetic Blast** on 3 of 3 wipes", text);
        Assert.DoesNotContain("Private", text);

        // Each wipe was read once, when it happened.
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()), Times.Exactly(3));

        // The boss dies: the pattern is history, not advice.
        rig.Now = Start.AddMinutes(24);
        rig.Report = rig.Build(Wipe(1, 0, 62), Wipe(2, 6, 55), Wipe(3, 12, 41), Kill(4, 18));
        await rig.SweepAsync();
        Assert.DoesNotContain("📌", Text(rig.Discord.Edits.Last().Card));
    }

    [Fact]
    public async Task CatchingUpIsSpreadAcrossRedrawsAndThePatternWaitsForAllOfIt()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(50);
        rig.List(minutesSinceLastEvent: 1);
        // The bot joins a raid that already has eight wipes on this boss.
        var wipes = Enumerable.Range(1, 8).Select(i => Wipe(i, (i - 1) * 6, 90 - i)).ToArray();
        rig.Report = rig.Build(wipes);

        await rig.SweepAsync();
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()),
            Times.Exactly(RaidRecapLiveCoordinator.MaxPullReadsPerRedraw));
        // Three of eight wipes read: too early to call anything a pattern.
        Assert.DoesNotContain("📌", Text(rig.Discord.Sent.Single().Card));
        // Only the wipe on show needs specs. The earlier ones are read without the roster.
        rig.Players.Verify(x => x.GetRaidRecapRosterAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<CancellationToken>()), Times.Once);

        rig.Now = Start.AddMinutes(56);
        rig.Report = rig.Build(wipes.Append(Wipe(9, 48, 70)).ToArray());
        await rig.SweepAsync();
        Assert.DoesNotContain("📌", Text(rig.Discord.Edits.Last().Card));

        rig.Now = Start.AddMinutes(62);
        rig.Report = rig.Build(wipes.Append(Wipe(9, 48, 70)).Append(Wipe(10, 54, 60)).ToArray());
        await rig.SweepAsync();
        rig.Now = Start.AddMinutes(68);
        rig.Report = rig.Build(wipes.Append(Wipe(9, 48, 70)).Append(Wipe(10, 54, 60)).Append(Wipe(11, 60, 50)).ToArray());
        await rig.SweepAsync();

        // The pattern covers the latest ten wipes, and each of those was read exactly once.
        // The very first wipe has dropped out of that window, so it is never read at all.
        Assert.Contains("📌 First death was **Synthetic Blast** on 10 of 10 wipes", Text(rig.Discord.Edits.Last().Card));
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()),
            Times.Exactly(10));
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.Is<RaidRecapFight>(f => f.Id == 1), "deaths", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EarlierKillsAreReadFromTheTableAloneAndNothingIsReadTwice()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(20);
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Kill(1, 0), Kill(2, 6, encounter: 3002), Kill(3, 12, encounter: 3003));

        await rig.SweepAsync();

        // Three kills, damage and healing each: six table reads.
        rig.Source.Verify(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()), Times.Exactly(6));
        // Parses only for the kill on show.
        rig.Players.Verify(x => x.GetRaidRecapParsesAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>(), It.IsAny<RaidRecapRoster>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        rig.Players.Verify(x => x.GetRaidRecapParsesAsync(It.IsAny<RaidRecapReport>(), It.Is<RaidRecapFight>(f => f.Id != 3), It.IsAny<bool>(), It.IsAny<RaidRecapRoster>(), It.IsAny<CancellationToken>()), Times.Never);

        // Twenty more redraws with nothing new: no pull is read again, and nothing is forgotten.
        rig.Source.Invocations.Clear();
        rig.Players.Invocations.Clear();
        for (var i = 1; i <= 20; i++)
        {
            rig.Now = rig.Now.AddMinutes(5);
            rig.Report = rig.Build(Kill(1, 0), Kill(2, 6, encounter: 3002), Kill(3, 12, encounter: 3003)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
            await rig.SweepAsync();
        }

        Assert.Equal(20, rig.Discord.Edits.Count);
        rig.Source.Verify(x => x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>()), Times.Never);
        rig.Players.Verify(x => x.GetRaidRecapParsesAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>(), It.IsAny<RaidRecapRoster>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PullThatKeepsFailingIsGivenUpOnAfterThreeTries()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(6);
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Wipe(1, 0, 62));
        rig.Source.Setup(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PRIVATE provider detail"));

        await rig.SweepAsync();
        for (var i = 1; i <= 10; i++)
        {
            rig.Now = rig.Now.AddMinutes(5);
            rig.Report = rig.Build(Wipe(1, 0, 62)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
            await rig.SweepAsync();
        }

        Assert.Equal(10, rig.Discord.Edits.Count);
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()),
            Times.Exactly(RaidRecapLiveCoordinator.MaxPullAttempts));
        Assert.DoesNotContain("PRIVATE", string.Concat(rig.Discord.Edits.Select(e => Text(e.Card))));
        Assert.Equal(RaidRecapLiveState.Live, Assert.Single(await rig.CardsAsync()).State);
    }

    [Fact]
    public async Task KillWhoseParsesFailedIsReadAgainUntilTheyArrive()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(6);
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Kill(1, 0));
        var failing = true;
        rig.Players.Setup(x => x.GetRaidRecapParsesAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>(), It.IsAny<RaidRecapRoster>(), It.IsAny<CancellationToken>()))
            .Returns((RaidRecapReport r, RaidRecapFight f, bool healing, RaidRecapRoster _, CancellationToken _) => failing
                ? Task.FromException<RaidRecapParses>(new TimeoutException())
                : Task.FromResult(new RaidRecapParses(r.SnapshotKey, f.Id, healing ? "hps" : "dps", "Parses", "Today", 1, Start,
                    new[] { new RaidRecapParse(1, 1001, 96.4, 500, 60, 1, 640) })));

        await rig.SweepAsync();
        var first = Text(rig.Discord.Sent.Single().Card);
        Assert.Contains("**[PrivateAlpha]", first);     // output is shown even without parses
        Assert.DoesNotContain("-# Raid:", first);

        failing = false;
        rig.Now = rig.Now.AddMinutes(5);
        rig.Report = rig.Build(Kill(1, 0)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
        await rig.SweepAsync();
        Assert.Contains("-# Raid: damage parse avg 🟠 **96**", Text(rig.Discord.Edits.Last().Card));

        // Complete now, so it is left alone.
        rig.Players.Invocations.Clear();
        rig.Now = rig.Now.AddMinutes(5);
        rig.Report = rig.Build(Kill(1, 0)) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
        await rig.SweepAsync();
        rig.Players.Verify(x => x.GetRaidRecapParsesAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), It.IsAny<bool>(), It.IsAny<RaidRecapRoster>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SpentBudgetStopsReadingPullsAndDoesNotCountAgainstThem()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(30);
        rig.List(minutesSinceLastEvent: 1);
        var wipes = Enumerable.Range(1, 4).Select(i => Wipe(i, (i - 1) * 6, 90 - i)).ToArray();
        rig.Report = rig.Build(wipes);
        rig.Source.Setup(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Warcraft Logs quota unavailable. Retry in 60s."));

        await rig.SweepAsync();
        for (var i = 1; i <= 5; i++)
        {
            rig.Now = rig.Now.AddMinutes(5);
            rig.Report = rig.Build(wipes) with { EndTime = rig.Now.ToUnixTimeMilliseconds() };
            await rig.SweepAsync();
        }

        // One refused read per redraw, never three, and never given up on.
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.IsAny<RaidRecapFight>(), "deaths", It.IsAny<CancellationToken>()), Times.Exactly(6));
        rig.Source.Verify(x => x.GetRaidRecapAnalysisAsync(It.IsAny<RaidRecapReport>(), It.Is<RaidRecapFight>(f => f.Id != 4), "deaths", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FinalCardNamesStandoutsFromTheKillsItSaw()
    {
        using var rig = new Rig();
        await rig.EnrollAsync();
        rig.Now = Start.AddMinutes(6);
        rig.List(minutesSinceLastEvent: 1);
        rig.Report = rig.Build(Kill(1, 0));
        await rig.SweepAsync();
        rig.Now = Start.AddMinutes(12);
        rig.Report = rig.Build(Kill(1, 0), Kill(2, 6, encounter: 3002));
        await rig.SweepAsync();
        rig.Now = Start.AddMinutes(18);
        rig.Report = rig.Build(Kill(1, 0), Kill(2, 6, encounter: 3002), Kill(3, 12, encounter: 3003));
        await rig.SweepAsync();
        Assert.DoesNotContain("top-3 finishes", Text(rig.Discord.Edits.Last().Card));

        rig.Now = Start.AddMinutes(17 + 31);
        await rig.SweepAsync();

        var text = Text(rig.Discord.Edits.Last().Card);
        Assert.Contains("🏁 **Raid ended**", text);
        // Each kill ran five minutes and the next began a minute later.
        Assert.Contains("⏳ 1:00 between pulls, typically", text);
        // Both players were in the top three of all three kills. Topping damage and healing
        // on the same kill still counts once.
        Assert.Contains("🏆 Most top-3 finishes: **PrivateAlpha** 3 · **PrivateGamma** 3", text);
        Assert.Contains("-# Raid: damage parse avg 🟠 **96**", text);
    }

    // ===== Open my recap =====

    [Fact]
    public async Task OpenMyRecapRoutesByReportCodeAndNeedsNoSession()
    {
        using var client = new DiscordRestClient();
        using var router = new InteractionService(client);
        using var deps = new ServiceCollection()
            .AddSingleton(new RaidRecapService(Mock.Of<IRaidRecapSource>(), new RaidRecapCache()))
            .AddSingleton(new RaidRecapSessions())
            .AddSingleton(Mock.Of<IRaidRecapDiscord>())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<RaidRecapCommands>>(NullLogger<RaidRecapCommands>.Instance)
            .BuildServiceProvider();
        await router.AddModuleAsync<RaidRecapCommands>(deps);

        var id = "rrlive_open~" + Code;
        var found = router.SearchComponentCommand(Mock.Of<IComponentInteraction>(x => x.Data == Mock.Of<IComponentInteractionData>(d => d.CustomId == id)));
        Assert.True(found.IsSuccess);
        Assert.Equal(nameof(RaidRecapCommands.OpenFromLiveCardAsync), found.Command.MethodName);
    }
}
