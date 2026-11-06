using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Services.Api;
using NinjaBotCore.Services.DeliveryProblems;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// The owner's Actionable Alerts: recording features Discord refused, what the page shows
    /// about them, and the owner's actions. Every server, channel and person here is SYNTHETIC.
    /// </summary>
    public class DeliveryProblemTests : IDisposable
    {
        private const ulong Server = 700000000000000001;
        private const ulong OtherServer = 700000000000000002;
        private const ulong Channel = 710000000000000001;
        private const ulong Setter = 720000000000000001;
        private const ulong ServerOwner = 720000000000000002;
        private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        private readonly ServiceProvider _services;
        private readonly FakeDiscord _discord = new();
        private DateTimeOffset _now = Start;

        public DeliveryProblemTests()
        {
            var name = $"DeliveryProblems_{Guid.NewGuid()}";
            _services = new ServiceCollection()
                .AddDbContext<NinjaBotEntities>(o => o.UseInMemoryDatabase(name)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)))
                .BuildServiceProvider();
        }

        public void Dispose() => _services.Dispose();

        private DeliveryProblemRecorder Recorder() =>
            new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DeliveryProblemRecorder>.Instance, () => _now);

        private DeliveryProblemService Service() =>
            new(_services.GetRequiredService<IServiceScopeFactory>(), _discord, NullLogger<DeliveryProblemService>.Instance, () => _now);

        private async Task<T> DbAsync<T>(Func<NinjaBotEntities, Task<T>> work)
        {
            using var scope = _services.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<NinjaBotEntities>());
        }

        private Task<List<DeliveryProblem>> RowsAsync() => DbAsync(db => db.DeliveryProblems.AsNoTracking().OrderBy(p => p.Id).ToListAsync());

        /// <summary>Every feature on and posting in <see cref="Channel"/>, so a problem there still applies.</summary>
        private Task FeaturesOnAsync(ulong server = Server, long? setter = null) => DbAsync(async db =>
        {
            var id = (long)server;
            db.LogMonitoring.Add(new LogMonitoring { ServerId = id, ChannelId = (long)Channel, ChannelName = "raid-logs", MonitorLogs = true, SetById = setter });
            db.ServerGreetings.Add(new ServerGreeting { DiscordGuildId = id, GreetUsers = true, PartUsers = true, GreetingChannelId = (long)Channel, SetById = setter });
            db.ModerationWatcher.Add(new ModerationWatcher { DiscordGuildId = id, ChannelId = (long)Channel, WatchMessages = true, SetById = setter });
            db.RealmWatchSubscriptions.Add(new RealmWatchSubscription { GuildId = id, ChannelId = (long)Channel, UserId = setter ?? 1, RealmSlug = "synthetic", RealmName = "Synthetic", Region = "us" });
            db.WordList.Add(new WordList { ServerId = id, ServerName = "Synthetic", Word = "synthetic", SetById = setter ?? 0 });
            return await db.SaveChangesAsync();
        });

        private async Task<long> SeedProblemAsync(string feature = DeliveryFeature.NewLog, string problem = DeliveryProblemKind.NoAccess, ulong server = Server)
        {
            await Recorder().FailedAsync(server, feature, Channel, problem, problem == DeliveryProblemKind.NoAccess ? 50001 : 10003);
            return (await RowsAsync()).Single(p => p.ServerId == (long)server && p.Feature == feature).Id;
        }

        // ===== Recording =====

        [Fact]
        public async Task ABrokenChannelIsWrittenOnceAnHourHoweverOftenItFails()
        {
            var recorder = Recorder();
            for (var i = 0; i < 5; i++)
            {
                await recorder.FailedAsync(Server, DeliveryFeature.NewLog, Channel, 50001);
            }

            var row = Assert.Single(await RowsAsync());
            Assert.Equal((Server, DeliveryFeature.NewLog, Channel), ((ulong)row.ServerId, row.Feature, (ulong)row.ChannelId));
            Assert.Equal(DeliveryProblemKind.NoAccess, row.Problem);
            Assert.Equal(50001, row.DiscordCode);
            Assert.Equal(1, row.Failures);

            // The failures in between are counted on the next write, an hour later.
            _now = Start + DeliveryProblemRecorder.WriteInterval;
            await recorder.FailedAsync(Server, DeliveryFeature.NewLog, Channel, 50001);
            row = Assert.Single(await RowsAsync());
            Assert.Equal(6, row.Failures);
            Assert.Equal(Start.UtcDateTime, row.FirstSeenAt);
            Assert.Equal(_now.UtcDateTime, row.LastSeenAt);
        }

        [Fact]
        public async Task ADifferentProblemIsWrittenAtOnce()
        {
            var recorder = Recorder();
            await recorder.FailedAsync(Server, DeliveryFeature.NewLog, Channel, 50013);
            await recorder.FailedAsync(Server, DeliveryFeature.NewLog, Channel, 10003);

            var row = Assert.Single(await RowsAsync());
            Assert.Equal(DeliveryProblemKind.ChannelMissing, row.Problem);
            Assert.Equal(2, row.Failures);
        }

        [Fact]
        public async Task APostThatWorksResolvesTheProblemAndABreakAfterThatStartsAfresh()
        {
            var recorder = Recorder();
            await recorder.FailedAsync(Server, DeliveryFeature.Welcome, Channel, 50001);
            await DbAsync(async db =>
            {
                var row = await db.DeliveryProblems.SingleAsync();
                row.DismissedAt = Start.UtcDateTime;
                row.NotifiedAt = Start.UtcDateTime;
                row.LastAttemptResult = DeliveryNotifyResult.Sent;
                return await db.SaveChangesAsync();
            });

            _now = Start.AddMinutes(5);
            await recorder.DeliveredAsync(Server, DeliveryFeature.Welcome, Channel);
            var resolved = Assert.Single(await RowsAsync());
            Assert.Equal(_now.UtcDateTime, resolved.ResolvedAt);
            Assert.Equal(DeliveryResolution.Delivered, resolved.Resolution);

            // Within the hour of the first write, but it worked in between, so it is written now.
            _now = Start.AddMinutes(10);
            await recorder.FailedAsync(Server, DeliveryFeature.Welcome, Channel, 50013);
            var again = Assert.Single(await RowsAsync());
            Assert.Null(again.ResolvedAt);
            Assert.Null(again.Resolution);
            Assert.Null(again.DismissedAt);
            Assert.Null(again.NotifiedAt);
            Assert.Null(again.LastAttemptResult);
            Assert.Equal(_now.UtcDateTime, again.FirstSeenAt);
            Assert.Equal(1, again.Failures);
            Assert.Equal(DeliveryProblemKind.MissingPermissions, again.Problem);
        }

        [Fact]
        public async Task FeaturesAndChannelsAreTrackedSeparately()
        {
            var recorder = Recorder();
            await recorder.FailedAsync(Server, DeliveryFeature.Welcome, Channel, 50001);
            await recorder.FailedAsync(Server, DeliveryFeature.Leave, Channel, 50001);
            await recorder.FailedAsync(Server, DeliveryFeature.Welcome, Channel + 1, 50001);
            await recorder.FailedAsync(OtherServer, DeliveryFeature.Welcome, Channel, 50001);
            await recorder.DeliveredAsync(Server, DeliveryFeature.Leave, Channel);

            var rows = await RowsAsync();
            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { DeliveryFeature.Leave }, rows.Where(r => r.ResolvedAt != null).Select(r => r.Feature));
        }

        [Fact]
        public async Task RecordingNeverThrows()
        {
            // The database is gone, as when it is down.
            var recorder = Recorder();
            _services.Dispose();
            await recorder.FailedAsync(Server, DeliveryFeature.NewLog, Channel, 50001);
            await recorder.DeliveredAsync(Server, DeliveryFeature.NewLog, Channel);
        }

        // ===== What the page shows =====

        [Fact]
        public async Task ThePageShowsWhatIsWrongNowAndWhoToTell()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(true, "raid-logs", new[] { "View Channel", "Embed Links" });
            _discord.Members.Add((Server, Setter));
            await DbAsync(async db =>
            {
                db.LogMonitoring.Add(new LogMonitoring { ServerId = (long)Server, ChannelId = (long)Channel, ChannelName = "raid-logs", MonitorLogs = true, SetById = (long)Setter, SetByName = "synthetic_officer" });
                return await db.SaveChangesAsync();
            });
            await SeedProblemAsync();

            var (problems, counts) = await Service().ListAsync();
            var view = Assert.Single(problems);
            Assert.True(view.BotPresent);
            Assert.Equal("Synthetic Server", view.ServerName);
            Assert.Equal("raid-logs", view.ChannelName);
            Assert.True(view.ChannelExists);
            Assert.Equal(new[] { "View Channel", "Embed Links" }, view.MissingPermissions);
            Assert.False(view.FixedNow);
            Assert.Equal("open", view.State);
            Assert.Equal("NinjaBot can't see #raid-logs", view.ProblemText);
            Assert.Equal(new DeliveryPerson(Setter, "synthetic_officer", true, DeliveryProblemText.Setter), view.Setter);
            Assert.Equal(new DeliveryPerson(ServerOwner, "synthetic_owner", true, DeliveryProblemText.Owner), view.Owner);
            Assert.Contains("View Channel and Embed Links", view.DmPreview);
            Assert.Contains("set this up", view.DmPreview);
            Assert.Equal(new DeliveryProblemCounts(1, 0, 0, 0, 0), counts);
        }

        [Fact]
        public async Task NewLogFallsBackToWhoeverLinkedTheGuild()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            await DbAsync(async db =>
            {
                db.LogMonitoring.Add(new LogMonitoring { ServerId = (long)Server, ChannelId = (long)Channel, MonitorLogs = true });
                db.WowGuildAssociations.Add(new WowGuildAssociations { ServerId = (long)Server, SetById = (long)Setter, SetBy = "synthetic_guild_linker" });
                return await db.SaveChangesAsync();
            });
            await SeedProblemAsync();

            var view = Assert.Single((await Service().ListAsync()).Problems);
            Assert.Equal(Setter, view.Setter!.UserId);
            Assert.Equal("synthetic_guild_linker", view.Setter.Name);
            // They linked the guild; they may never have run /watchlogs, so the DM doesn't say they did.
            Assert.Equal(DeliveryProblemText.GuildLinker, view.Setter.Via);
            Assert.Contains("because you linked Synthetic Server's WoW guild", view.DmPreview);
        }

        [Theory]
        [InlineData(DeliveryFeature.Welcome)]
        [InlineData(DeliveryFeature.Leave)]
        [InlineData(DeliveryFeature.ModerationLog)]
        [InlineData(DeliveryFeature.RealmAlert)]
        [InlineData(DeliveryFeature.WordFilter)]
        public async Task EachFeatureKnowsWhoSetItUp(string feature)
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            await DbAsync(async db =>
            {
                db.ServerGreetings.Add(new ServerGreeting { DiscordGuildId = (long)Server, GreetUsers = true, PartUsers = true, SetById = (long)Setter, SetByName = "greeter" });
                db.ModerationWatcher.Add(new ModerationWatcher { DiscordGuildId = (long)Server, ChannelId = (long)Channel, WatchMessages = true, SetById = (long)Setter, SetByName = "watcher" });
                db.RealmWatchSubscriptions.Add(new RealmWatchSubscription { GuildId = (long)Server, ChannelId = (long)Channel, UserId = (long)Setter, RealmSlug = "synthetic", RealmName = "Synthetic", Region = "us" });
                db.WordList.Add(new WordList { ServerId = (long)Server, ServerName = "Synthetic Server", Word = "synthetic", SetById = (long)Setter });
                return await db.SaveChangesAsync();
            });
            await SeedProblemAsync(feature);

            var view = Assert.Single((await Service().ListAsync()).Problems);
            Assert.Equal(Setter, view.Setter?.UserId);
        }

        [Fact]
        public async Task AServerTheBotLeftIsShownWithTheOwnerItRemembers()
        {
            await DbAsync(async db =>
            {
                db.DiscordServers.Add(new DiscordServer { ServerId = (long)Server, ServerName = "Gone Server", OwnerId = (long)ServerOwner, OwnerName = "remembered_owner", BotPresent = false });
                return await db.SaveChangesAsync();
            });
            await FeaturesOnAsync();
            await SeedProblemAsync();

            var (problems, counts) = await Service().ListAsync();
            var view = Assert.Single(problems);
            Assert.False(view.BotPresent);
            Assert.Equal("Gone Server", view.ServerName);
            Assert.Null(view.ChannelExists);
            Assert.False(view.FixedNow);
            Assert.Equal(new DeliveryPerson(ServerOwner, "remembered_owner", null, DeliveryProblemText.Owner), view.Owner);
            Assert.Equal(1, counts.BotLeft);
        }

        [Fact]
        public async Task OldResolvedProblemsDropOffThePage()
        {
            await SeedProblemAsync();
            await Recorder().DeliveredAsync(Server, DeliveryFeature.NewLog, Channel);
            Assert.Equal("resolved", Assert.Single((await Service().ListAsync()).Problems).State);

            _now = Start + DeliveryProblemService.ResolvedWindow + TimeSpan.FromMinutes(1);
            Assert.Empty((await Service().ListAsync()).Problems);
        }

        [Fact]
        public async Task AChannelThatLooksFixedIsFlagged()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(true, "raid-logs", Array.Empty<string>());
            await FeaturesOnAsync();
            await SeedProblemAsync();

            Assert.True(Assert.Single((await Service().ListAsync()).Problems).FixedNow);
        }

        // ===== DMs =====

        [Fact]
        public async Task DmGoesToWhoSetItUpAndIsRecorded()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Members.Add((Server, Setter));
            await DbAsync(async db =>
            {
                db.ServerGreetings.Add(new ServerGreeting { DiscordGuildId = (long)Server, GreetUsers = true, SetById = (long)Setter });
                return await db.SaveChangesAsync();
            });
            var id = await SeedProblemAsync(DeliveryFeature.Welcome);

            var (result, view) = await Service().SendDmAsync(id, DeliveryProblemText.Setter);
            Assert.Equal(DeliveryDmResult.Sent, result);
            var (to, text) = Assert.Single(_discord.Sent);
            Assert.Equal(Setter, to);
            Assert.Contains("NinjaBot can't post welcome messages in Synthetic Server", text);
            Assert.Contains("you set this up", text);
            Assert.Equal("notified", view!.State);
            var row = Assert.Single(await RowsAsync());
            Assert.Equal(_now.UtcDateTime, row.NotifiedAt);
            Assert.Equal((long)Setter, row.NotifiedUserId);
            Assert.Equal(DeliveryProblemText.Setter, row.NotifiedRole);
            Assert.Equal(DeliveryNotifyResult.Sent, row.LastAttemptResult);
            Assert.Equal(DeliveryProblemText.Setter, row.LastAttemptRole);
        }

        [Fact]
        public async Task DmToTheOwnerSaysWhyTheyGotIt()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Members.Add((Server, ServerOwner));
            await FeaturesOnAsync();
            var id = await SeedProblemAsync();

            Assert.Equal(DeliveryDmResult.Sent, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);
            var (to, text) = Assert.Single(_discord.Sent);
            Assert.Equal(ServerOwner, to);
            Assert.Contains("because you own Synthetic Server", text);
        }

        [Fact]
        public async Task NoDmIsSentToSomeoneWhoLeftOrAboutAServerTheBotLeft()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            await DbAsync(async db =>
            {
                db.ServerGreetings.Add(new ServerGreeting { DiscordGuildId = (long)Server, GreetUsers = true, SetById = (long)Setter });
                return await db.SaveChangesAsync();
            });
            var id = await SeedProblemAsync(DeliveryFeature.Welcome);

            Assert.Equal(DeliveryDmResult.NotInServer, (await Service().SendDmAsync(id, DeliveryProblemText.Setter)).Result);
            var row = Assert.Single(await RowsAsync());
            Assert.Null(row.NotifiedAt);
            Assert.Equal(DeliveryNotifyResult.NotInServer, row.LastAttemptResult);

            await FeaturesOnAsync(OtherServer);
            var gone = await SeedProblemAsync(server: OtherServer);
            Assert.Equal(DeliveryDmResult.BotLeft, (await Service().SendDmAsync(gone, DeliveryProblemText.Owner)).Result);
            Assert.Null((await Service().SendDmAsync(gone + 1000, DeliveryProblemText.Owner)).View);
            Assert.Empty(_discord.Sent);
        }

        [Fact]
        public async Task ClosedDmsAreShownButDontCountAsTold()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Members.Add((Server, ServerOwner));
            _discord.Outcome = DeliveryDmOutcome.DmsClosed;
            await FeaturesOnAsync();
            var id = await SeedProblemAsync();

            var (result, view) = await Service().SendDmAsync(id, DeliveryProblemText.Owner);
            Assert.Equal(DeliveryDmResult.DmsClosed, result);
            Assert.Equal("open", view!.State);
            Assert.Equal(DeliveryNotifyResult.DmsClosed, view.Row.LastAttemptResult);
            Assert.Equal(DeliveryProblemText.Owner, view.Row.LastAttemptRole);
            Assert.Null(view.Row.NotifiedRole);
        }

        [Fact]
        public async Task ThereIsNobodyToDmWhenNoSetterIsOnRecord()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            await FeaturesOnAsync();
            var id = await SeedProblemAsync(DeliveryFeature.Welcome);

            Assert.Equal(DeliveryDmResult.NoRecipient, (await Service().SendDmAsync(id, DeliveryProblemText.Setter)).Result);
            Assert.Empty(_discord.Sent);
        }

        // ===== Re-check, turn off, dismiss =====

        [Fact]
        public async Task RecheckResolvesOnlyWhenTheBotCanPostAgain()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(true, "raid-logs", new[] { "Send Messages" });
            await FeaturesOnAsync();
            var id = await SeedProblemAsync();

            Assert.False((await Service().RecheckAsync(id)).Fixed);
            Assert.Null(Assert.Single(await RowsAsync()).ResolvedAt);

            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(true, "raid-logs", Array.Empty<string>());
            var (isFixed, view) = await Service().RecheckAsync(id);
            Assert.True(isFixed);
            Assert.Equal("resolved", view!.State);
            Assert.Equal(DeliveryResolution.Fixed, Assert.Single(await RowsAsync()).Resolution);
        }

        [Fact]
        public async Task RecheckNeverResolvesADeletedChannel()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(false, null, Array.Empty<string>());
            await FeaturesOnAsync();
            var id = await SeedProblemAsync(problem: DeliveryProblemKind.ChannelMissing);

            Assert.False((await Service().RecheckAsync(id)).Fixed);
        }

        [Fact]
        public async Task TurnOffSwitchesEachFeatureOffTheWayItsCommandDoes()
        {
            var other = (long)Channel + 5;
            await DbAsync(async db =>
            {
                db.LogMonitoring.Add(new LogMonitoring { ServerId = (long)Server, ChannelId = (long)Channel, MonitorLogs = true });
                db.ServerGreetings.Add(new ServerGreeting { DiscordGuildId = (long)Server, GreetUsers = true, PartUsers = true });
                db.ModerationWatcher.Add(new ModerationWatcher { DiscordGuildId = (long)Server, ChannelId = (long)Channel, WatchMessages = true, WatchBans = true });
                db.RealmWatchSubscriptions.Add(new RealmWatchSubscription { GuildId = (long)Server, ChannelId = (long)Channel, UserId = 1, RealmSlug = "a", RealmName = "A", Region = "us" });
                db.RealmWatchSubscriptions.Add(new RealmWatchSubscription { GuildId = (long)Server, ChannelId = other, UserId = 2, RealmSlug = "b", RealmName = "B", Region = "us" });
                return await db.SaveChangesAsync();
            });
            var ids = new List<long>();
            foreach (var feature in new[] { DeliveryFeature.NewLog, DeliveryFeature.Welcome, DeliveryFeature.Leave, DeliveryFeature.ModerationLog, DeliveryFeature.RealmAlert, DeliveryFeature.WordFilter })
            {
                ids.Add(await SeedProblemAsync(feature));
            }

            var results = await Service().TurnOffAsync(ids.Append(99999).ToList());
            Assert.Equal(
                new[] { "turned_off", "turned_off", "turned_off", "turned_off", "turned_off", "not_supported", "not_found" },
                results.Select(r => r.Status));

            await DbAsync(async db =>
            {
                Assert.False((await db.LogMonitoring.SingleAsync()).MonitorLogs);
                var greeting = await db.ServerGreetings.SingleAsync();
                Assert.False(greeting.GreetUsers);
                Assert.False(greeting.PartUsers);
                var watcher = await db.ModerationWatcher.SingleAsync();
                Assert.False(watcher.WatchMessages);
                Assert.False(watcher.WatchBans);
                // Only the alerts posting to the broken channel go.
                Assert.Equal(other, (await db.RealmWatchSubscriptions.SingleAsync()).ChannelId);
                return 0;
            });

            var rows = await RowsAsync();
            Assert.All(rows.Where(r => r.Feature != DeliveryFeature.WordFilter), r => Assert.Equal(DeliveryResolution.TurnedOff, r.Resolution));
            Assert.Null(rows.Single(r => r.Feature == DeliveryFeature.WordFilter).ResolvedAt);

            // A second time, they're resolved and nothing is touched.
            Assert.All(await Service().TurnOffAsync(ids.Take(5).ToList()), r => Assert.Equal("resolved", r.Status));
        }

        [Fact]
        public async Task DismissHidesOpenProblemsOnly()
        {
            await FeaturesOnAsync();
            var open = await SeedProblemAsync(DeliveryFeature.NewLog);
            var resolved = await SeedProblemAsync(DeliveryFeature.Welcome);
            await Recorder().DeliveredAsync(Server, DeliveryFeature.Welcome, Channel);

            Assert.Equal(1, await Service().DismissAsync(new[] { open, resolved, open }));
            var rows = await RowsAsync();
            Assert.Equal(Start.UtcDateTime, rows.Single(r => r.Id == open).DismissedAt);
            Assert.Null(rows.Single(r => r.Id == resolved).DismissedAt);
            Assert.Equal("dismissed", (await Service().GetAsync(open))!.State);
        }

        // ===== After the review =====

        [Fact]
        public async Task AFeatureTheServerMovedResolvesItselfAndTurnOffLeavesItAlone()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            await FeaturesOnAsync();
            var id = await SeedProblemAsync(DeliveryFeature.Welcome, DeliveryProblemKind.ChannelMissing);
            await DbAsync(async db =>
            {
                // The officer followed the DM and turned greetings on in a new channel.
                (await db.ServerGreetings.SingleAsync()).GreetingChannelId = (long)Channel + 9;
                return await db.SaveChangesAsync();
            });

            Assert.Equal(DeliveryTurnOffStatus.Resolved, Assert.Single(await Service().TurnOffAsync(new[] { id })).Status);
            Assert.True(await DbAsync(async db => (await db.ServerGreetings.SingleAsync()).GreetUsers == true));
            var view = Assert.Single((await Service().ListAsync()).Problems);
            Assert.Equal("resolved", view.State);
            Assert.Equal(DeliveryResolution.Moved, view.Row.Resolution);
        }

        [Theory]
        [InlineData(DeliveryFeature.NewLog)]
        [InlineData(DeliveryFeature.Welcome)]
        [InlineData(DeliveryFeature.Leave)]
        [InlineData(DeliveryFeature.ModerationLog)]
        [InlineData(DeliveryFeature.RealmAlert)]
        [InlineData(DeliveryFeature.WordFilter)]
        public async Task AFeatureTheServerTurnedOffResolvesItself(string feature)
        {
            await SeedProblemAsync(feature);

            var view = Assert.Single((await Service().ListAsync()).Problems);
            Assert.Equal(DeliveryResolution.FeatureOff, view.Row.Resolution);
            Assert.Equal(0, (await Service().ListAsync()).Counts.Open);
        }

        [Fact]
        public async Task LeaveMessagesFollowThePartingChannel()
        {
            await FeaturesOnAsync();
            await SeedProblemAsync(DeliveryFeature.Leave);
            await DbAsync(async db =>
            {
                (await db.ServerGreetings.SingleAsync()).PartingChannelId = (long)Channel + 3;
                return await db.SaveChangesAsync();
            });

            Assert.Equal(DeliveryResolution.Moved, Assert.Single((await Service().ListAsync()).Problems).Row.Resolution);
        }

        [Fact]
        public async Task NoDmWhenThereIsNothingLeftToFixOrTheSamePersonWasJustTold()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Members.Add((Server, ServerOwner));
            await FeaturesOnAsync();
            var id = await SeedProblemAsync();

            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(true, "raid-logs", Array.Empty<string>());
            Assert.Equal(DeliveryDmResult.LooksFixed, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);

            _discord.Channels[(Server, Channel)] = new DeliveryChannelInfo(true, "raid-logs", new[] { "Embed Links" });
            Assert.Equal(DeliveryDmResult.Sent, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);
            _now = Start + DeliveryProblemService.DmCooldown - TimeSpan.FromSeconds(1);
            Assert.Equal(DeliveryDmResult.RecentlySent, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);
            _now = Start + DeliveryProblemService.DmCooldown;
            Assert.Equal(DeliveryDmResult.Sent, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);
            Assert.Equal(2, _discord.Sent.Count);

            await Recorder().DeliveredAsync(Server, DeliveryFeature.NewLog, Channel);
            Assert.Equal(DeliveryDmResult.Resolved, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);
            Assert.Equal(2, _discord.Sent.Count);
        }

        [Fact]
        public async Task ADoubleClickSendsOneDm()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Members.Add((Server, ServerOwner));
            _discord.Delay = TimeSpan.FromMilliseconds(200);
            await FeaturesOnAsync();
            var id = await SeedProblemAsync();
            var service = Service();

            var results = await Task.WhenAll(
                service.SendDmAsync(id, DeliveryProblemText.Owner),
                service.SendDmAsync(id, DeliveryProblemText.Owner));
            Assert.Single(_discord.Sent);
            Assert.Equal(new[] { DeliveryDmResult.RecentlySent, DeliveryDmResult.Sent }, results.Select(r => r.Result).OrderBy(r => r));
        }

        [Fact]
        public async Task ADiscordHiccupIsNeverReportedAsLeftTheServer()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.MemberCheck = null;
            await FeaturesOnAsync();
            var id = await SeedProblemAsync();

            Assert.Equal(DeliveryDmResult.CheckFailed, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);
            var row = Assert.Single(await RowsAsync());
            Assert.Null(row.LastAttemptResult);
            Assert.Empty(_discord.Sent);
        }

        [Fact]
        public async Task AFailedDmNeverHidesWhoWasTold()
        {
            _discord.AddServer(Server, "Synthetic Server", ServerOwner, "synthetic_owner");
            _discord.Members.Add((Server, Setter));
            _discord.Members.Add((Server, ServerOwner));
            await FeaturesOnAsync(setter: (long)Setter);
            var id = await SeedProblemAsync(DeliveryFeature.Welcome);

            Assert.Equal(DeliveryDmResult.Sent, (await Service().SendDmAsync(id, DeliveryProblemText.Setter)).Result);
            _discord.Outcome = DeliveryDmOutcome.DmsClosed;
            _now = Start.AddHours(2);
            Assert.Equal(DeliveryDmResult.DmsClosed, (await Service().SendDmAsync(id, DeliveryProblemText.Owner)).Result);

            var row = Assert.Single(await RowsAsync());
            Assert.Equal((Start.UtcDateTime, (long)Setter, DeliveryProblemText.Setter), (row.NotifiedAt!.Value, row.NotifiedUserId!.Value, row.NotifiedRole));
            Assert.Equal((_now.UtcDateTime, DeliveryProblemText.Owner, DeliveryNotifyResult.DmsClosed), (row.LastAttemptAt!.Value, row.LastAttemptRole, row.LastAttemptResult));
        }

        [Fact]
        public void TheRealmAlertDmSaysToPickTheChannelAgain()
        {
            var text = DeliveryProblemText.Dm(DeliveryFeature.RealmAlert, DeliveryProblemKind.ChannelMissing, "Synthetic", Channel, "alerts", Array.Empty<string>(), DeliveryProblemText.Setter);
            Assert.Contains("`/realm-watch add` and pick the new `channel` (needs Administrator)", text);
        }

        // ===== Words =====

        [Theory]
        [InlineData(DeliveryFeature.NewLog, "`/watchlogs`")]
        [InlineData(DeliveryFeature.Welcome, "`/toggle-greetings`")]
        [InlineData(DeliveryFeature.Leave, "`/set-parting-channel`")]
        [InlineData(DeliveryFeature.ModerationLog, "`/watch action:Enable`")]
        [InlineData(DeliveryFeature.RealmAlert, "`/realm-watch remove`")]
        public void TheDmSaysHowToMoveOrStopIt(string feature, string command)
        {
            foreach (var problem in new[] { DeliveryProblemKind.ChannelMissing, DeliveryProblemKind.NoAccess, DeliveryProblemKind.MissingPermissions })
            {
                var text = DeliveryProblemText.Dm(feature, problem, "Synthetic", Channel, "logs", Array.Empty<string>(), DeliveryProblemText.Setter);
                Assert.Contains(command, text);
                Assert.Contains("**To fix it:**", text);
                // A deleted channel can't be linked; the others are.
                Assert.Equal(problem != DeliveryProblemKind.ChannelMissing, text.Contains($"<#{Channel}>"));
            }
        }

        [Fact]
        public void TheWordFilterDmAsksForManageMessages()
        {
            var text = DeliveryProblemText.Dm(DeliveryFeature.WordFilter, DeliveryProblemKind.MissingPermissions, "Synthetic", Channel, "general", Array.Empty<string>(), DeliveryProblemText.Owner);
            Assert.Contains("can't remove blocked words", text);
            Assert.Contains("View Channel and Manage Messages", text);
            Assert.Contains("`/remove-word`", text);
            Assert.False(DeliveryProblemText.CanTurnOff(DeliveryFeature.WordFilter));
        }

        [Fact]
        public void ServerAndChannelNamesCantPingOrFormat()
        {
            var text = DeliveryProblemText.Dm(
                DeliveryFeature.NewLog, DeliveryProblemKind.ChannelMissing, "@everyone **Big** <@&1>", Channel, "logs_`x`", Array.Empty<string>(), DeliveryProblemText.Setter);
            Assert.DoesNotContain("@everyone", text);
            Assert.DoesNotContain("<@&1>", text);
            Assert.DoesNotContain("**Big**", text);
            Assert.DoesNotContain("`x`", text);
        }

        [Theory]
        [InlineData(DeliveryProblemKind.ChannelMissing, "logs", "#logs was deleted")]
        [InlineData(DeliveryProblemKind.ChannelMissing, null, "Channel deleted")]
        [InlineData(DeliveryProblemKind.NoAccess, "logs", "NinjaBot can't see #logs")]
        [InlineData(DeliveryProblemKind.MissingPermissions, null, "NinjaBot is missing permissions in the channel")]
        public void TheProblemLineIsShort(string problem, string channel, string expected) =>
            Assert.Equal(expected, DeliveryProblemText.Problem(problem, channel, null));

        [Fact]
        public void BatchIdsMustAllBeProblemIds()
        {
            Assert.Equal(new long[] { 1, 2 }, DeliveryProblemEndpoints.Ids(new List<string> { "1", "2" }));
            Assert.Null(DeliveryProblemEndpoints.Ids(null));
            Assert.Null(DeliveryProblemEndpoints.Ids(new List<string>()));
            Assert.Null(DeliveryProblemEndpoints.Ids(new List<string> { "1", "x" }));
            Assert.Null(DeliveryProblemEndpoints.Ids(new List<string> { "0" }));
            Assert.Null(DeliveryProblemEndpoints.Ids(Enumerable.Range(1, DeliveryProblemService.MaxBatch + 1).Select(i => i.ToString()).ToList()));
        }

        private sealed class FakeDiscord : IDeliveryProblemDiscord
        {
            public Dictionary<ulong, DeliveryServerInfo> Servers { get; } = new();
            public Dictionary<(ulong, ulong), DeliveryChannelInfo> Channels { get; } = new();
            public HashSet<(ulong, ulong)> Members { get; } = new();
            public List<(ulong, string)> Sent { get; } = new();
            public DeliveryDmOutcome Outcome { get; set; } = DeliveryDmOutcome.Sent;
            public TimeSpan Delay { get; set; }

            public void AddServer(ulong id, string name, ulong owner, string ownerName) =>
                Servers[id] = new DeliveryServerInfo(true, name, owner, ownerName);

            public DeliveryServerInfo Server(ulong serverId) =>
                Servers.TryGetValue(serverId, out var server) ? server : new DeliveryServerInfo(false, null, null, null);

            public DeliveryChannelInfo Channel(ulong serverId, ulong channelId, IReadOnlyList<ChannelPermission> needed)
            {
                if (!Servers.ContainsKey(serverId))
                {
                    return null;
                }

                return Channels.TryGetValue((serverId, channelId), out var channel)
                    ? channel
                    : new DeliveryChannelInfo(false, null, Array.Empty<string>());
            }

            public bool? InServer(ulong serverId, ulong userId) =>
                Servers.ContainsKey(serverId) ? Members.Contains((serverId, userId)) : null;

            public string MemberName(ulong serverId, ulong userId) => null;

            public bool? MemberCheck { get; set; } = true;

            public Task<bool?> IsMemberAsync(ulong serverId, ulong userId) =>
                Task.FromResult(MemberCheck == null ? null : (bool?)Members.Contains((serverId, userId)));

            public async Task<DeliveryDmOutcome> SendDmAsync(ulong userId, string text)
            {
                await Task.Delay(Delay);
                if (Outcome == DeliveryDmOutcome.Sent)
                {
                    lock (Sent)
                    {
                        Sent.Add((userId, text));
                    }
                }

                return Outcome;
            }
        }
    }
}
