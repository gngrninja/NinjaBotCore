using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// The live raid recap's web endpoints: the owner's rollout on the admin page and each
    /// server's card on the dashboard. Its own fixture, because the rollout mode is shared state.
    /// Every server, guild and report here is SYNTHETIC.
    /// </summary>
    public class RaidRecapLiveApiTests : IClassFixture<CommandsApiFixture>
    {
        private const ulong Owner = 900000000000000001;
        private const ulong Officer = 900000000000000002;
        private const long LogChannel = 800000000000000031;

        private readonly CommandsApiFixture _fixture;
        private readonly HttpClient _client;
        private readonly Mock<IRaidRecapLiveDiscord> _discord;

        public RaidRecapLiveApiTests(CommandsApiFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.Client;
            _discord = Mock.Get(fixture.RootProvider.GetRequiredService<IRaidRecapLiveDiscord>());
            // The mock is shared by every test in the class; count each test's own calls.
            _discord.Invocations.Clear();
            _discord.Setup(d => d.OwnerIdsAsync()).ReturnsAsync(new[] { Owner });
            _discord.Setup(d => d.IsMemberAsync(It.IsAny<ulong>(), It.IsAny<ulong>())).ReturnsAsync(false);
            _discord.Setup(d => d.CanPost(It.IsAny<ulong>(), It.IsAny<ulong>())).Returns(true);
            _discord.Setup(d => d.SendAsync(It.IsAny<ulong>(), It.IsAny<MessageComponent>())).ReturnsAsync(4242UL);
            // Every other guild has no reports, as Warcraft Logs answers for a guild that hasn't logged lately.
            Mock.Get(fixture.RootProvider.GetRequiredService<IRaidRecapSource>())
                .Setup(s => s.GetRaidRecapReportsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<WclV2Report>());
        }

        private Task SetModeAsync(RaidRecapRolloutMode mode) =>
            _fixture.RootProvider.GetRequiredService<RaidRecapLiveGate>().SetModeAsync(mode);

        /// <summary>A server with a WoW guild and a /watchlogs channel that got a New log yesterday.</summary>
        private async Task SeedAsync(long server, string name, int logsPosted = 1)
        {
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                db.DiscordServers.Add(new DiscordServer { ServerId = server, ServerName = name, OwnerName = "" });
                db.WowGuildAssociations.Add(new WowGuildAssociations
                {
                    ServerId = server,
                    ServerName = name,
                    WowGuild = $"Synthetic Guild {server}",
                    WowRealm = "Area 52",
                    LocalRealmSlug = "area-52",
                    WowRegion = "us"
                });
                db.LogMonitoring.Add(new LogMonitoring
                {
                    ServerId = server,
                    ServerName = name,
                    ChannelId = LogChannel,
                    ChannelName = "logs",
                    MonitorLogs = true,
                    LatestLogRetail = DateTime.UtcNow.AddDays(-1)
                });
                for (var i = 0; i < logsPosted; i++)
                {
                    db.WclPosted.Add(new WclPosted { ServerId = server, ChannelId = LogChannel, ReportId = $"Synthetic{server}x{i}" });
                }

                await db.SaveChangesAsync();
            }
        }

        private async Task<RaidRecapLiveSettings> SettingsAsync(long server)
        {
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                return db.RaidRecapLiveSettings.AsQueryable().FirstOrDefault(s => s.DiscordGuildId == server);
            }
        }

        [Fact]
        public async Task RolloutEndpoints_NeedTheApiKey()
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.UnauthClient.GetAsync("/api/raid-recap-live/rollout")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.UnauthClient.GetAsync("/api/guilds/1/raid-recap-live")).StatusCode);
        }

        [Fact]
        public async Task Rollout_OnlyTheBotOwnerChangesTheMode()
        {
            await SetModeAsync(RaidRecapRolloutMode.OwnerServers);

            var refused = await _client.PutAsJsonAsync("/api/raid-recap-live/rollout", new { mode = "everyone", user_id = Officer.ToString() });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var bad = await _client.PutAsJsonAsync("/api/raid-recap-live/rollout", new { mode = "sideways", user_id = Owner.ToString() });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var response = await _client.PutAsJsonAsync("/api/raid-recap-live/rollout", new { mode = "selected", user_id = Owner.ToString() });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("mine", json.GetProperty("previous").GetString());
            var rollout = json.GetProperty("rollout");
            Assert.Equal("selected", rollout.GetProperty("mode").GetString());
            Assert.Equal("selected servers", rollout.GetProperty("mode_text").GetString());
            Assert.Equal(RaidRecapLiveSelection.MaxSelect, rollout.GetProperty("max_selected").GetInt32());

            var read = await _client.GetFromJsonAsync<JsonElement>("/api/raid-recap-live/rollout");
            Assert.Equal("selected", read.GetProperty("rollout").GetProperty("mode").GetString());
        }

        [Fact]
        public async Task Selection_AddsAServerTurnsItsCardOnAndRemovesIt()
        {
            const long server = 700000000000000101;
            await SeedAsync(server, "Synthetic add server");
            await SetModeAsync(RaidRecapRolloutMode.OwnerServers);

            var refused = await _client.PostAsJsonAsync("/api/raid-recap-live/selection", new { server_id = server.ToString(), user_id = Officer.ToString() });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            var response = await _client.PostAsJsonAsync("/api/raid-recap-live/selection",
                new { server_id = server.ToString(), user_id = Owner.ToString(), username = "owner" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            // Selecting moves "my servers" up, so the selected server is covered.
            Assert.Equal("mine", json.GetProperty("mode_changed_from").GetString());
            var result = Assert.Single(json.GetProperty("results").EnumerateArray());
            Assert.Equal(server.ToString(), result.GetProperty("server_id").GetString());
            Assert.Equal("Synthetic add server", result.GetProperty("name").GetString());
            Assert.Equal("turned_on", result.GetProperty("status").GetString());
            Assert.Equal("turned on, message posted", result.GetProperty("status_text").GetString());
            var rollout = json.GetProperty("rollout");
            Assert.Equal("selected", rollout.GetProperty("mode").GetString());
            var listed = rollout.GetProperty("selected").EnumerateArray().Single(s => s.GetProperty("server_id").GetString() == server.ToString());
            Assert.True(listed.GetProperty("enabled").GetBoolean());
            Assert.Equal(LogChannel.ToString(), listed.GetProperty("channel_id").GetString());
            Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("notice_sent_at").ValueKind);
            Assert.True(listed.GetProperty("bot_present").GetBoolean());
            _discord.Verify(d => d.SendAsync((ulong)LogChannel, It.IsAny<MessageComponent>()), Times.Once);

            var bad = await _client.DeleteAsync($"/api/raid-recap-live/selection/{server}?user_id={Officer}");
            Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
            var removed = await _client.DeleteAsync($"/api/raid-recap-live/selection/{server}?user_id={Owner}");
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.True((await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetBoolean());
            Assert.False((await SettingsAsync(server)).Selected);
            // Its own setting is kept.
            Assert.True((await SettingsAsync(server)).Enabled);
        }

        [Fact]
        public async Task Selection_IsRefusedWhileTheKillSwitchIsOn()
        {
            const long server = 700000000000000102;
            await SeedAsync(server, "Synthetic kill switch server");
            await SetModeAsync(RaidRecapRolloutMode.Off);

            var response = await _client.PostAsJsonAsync("/api/raid-recap-live/selection",
                new { server_id = server.ToString(), user_id = Owner.ToString(), username = "owner" });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("kill_switch", json.GetProperty("reason").GetString());
            Assert.Null(await SettingsAsync(server));
            _discord.Verify(d => d.SendAsync(It.IsAny<ulong>(), It.IsAny<MessageComponent>()), Times.Never);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("00000000000000000")]
        public async Task Selection_RefusesAZeroServerIdInsteadOfSelectingTheMostActive(string serverId)
        {
            await SetModeAsync(RaidRecapRolloutMode.Selected);

            var response = await _client.PostAsJsonAsync("/api/raid-recap-live/selection",
                new { server_id = serverId, user_id = Owner.ToString(), username = "owner" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            _discord.Verify(d => d.SendAsync(It.IsAny<ulong>(), It.IsAny<MessageComponent>()), Times.Never);
        }

        [Fact]
        public async Task Candidates_PreviewChangesNothing()
        {
            const long server = 700000000000000103;
            // The most logs posted, so it is ranked first whatever the other tests seeded.
            await SeedAsync(server, "Synthetic preview server", logsPosted: 5);
            Mock.Get(_fixture.RootProvider.GetRequiredService<IRaidRecapSource>())
                .Setup(s => s.GetRaidRecapReportsAsync($"Synthetic Guild {server}", "area-52", "us", RaidRecapLiveCoordinator.DiscoveryReports))
                .ReturnsAsync(new[] { new WclV2Report { Code = "SyntheticPreview1", EndTime = DateTimeOffset.UtcNow.AddHours(-20).ToUnixTimeMilliseconds() } });

            var response = await _client.GetAsync("/api/raid-recap-live/candidates?count=25");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
            var json = JsonDocument.Parse(body).RootElement;

            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("active").GetInt32() >= 1);
            var candidate = json.GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("server_id").GetString() == server.ToString());
            Assert.Equal("ready", candidate.GetProperty("status").GetString());
            Assert.Equal(5, candidate.GetProperty("logs_posted").GetInt32());
            Assert.Equal(1, candidate.GetProperty("reports_this_week").GetInt32());
            Assert.False(candidate.GetProperty("more_reports").GetBoolean());
            Assert.Null(await SettingsAsync(server));
        }

        [Fact]
        public async Task GuildLiveCard_TurnsOnInTheLogChannelAndOff()
        {
            const long server = 700000000000000104;
            await SeedAsync(server, "Synthetic officer server");
            await SetModeAsync(RaidRecapRolloutMode.Everyone);

            var before = await _client.GetFromJsonAsync<JsonElement>($"/api/guilds/{server}/raid-recap-live");
            var settings = before.GetProperty("settings");
            Assert.True(settings.GetProperty("available").GetBoolean());
            Assert.False(settings.GetProperty("enabled").GetBoolean());
            Assert.False(settings.GetProperty("turned_off").GetBoolean());
            Assert.Equal(LogChannel.ToString(), settings.GetProperty("log_channel_id").GetString());
            Assert.Equal($"Synthetic Guild {server}", settings.GetProperty("wow_guild").GetString());

            var on = await _client.PutAsJsonAsync($"/api/guilds/{server}/raid-recap-live",
                new { enabled = true, user_id = Officer.ToString(), username = "officer" });
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
            settings = (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settings");
            Assert.True(settings.GetProperty("enabled").GetBoolean());
            Assert.Equal(LogChannel.ToString(), settings.GetProperty("channel_id").GetString());
            Assert.Equal("officer", settings.GetProperty("set_by_name").GetString());
            Assert.Equal(JsonValueKind.Null, settings.GetProperty("live").ValueKind);

            var off = await _client.PutAsJsonAsync($"/api/guilds/{server}/raid-recap-live",
                new { enabled = false, user_id = Officer.ToString(), username = "officer" });
            settings = (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settings");
            Assert.False(settings.GetProperty("enabled").GetBoolean());
            Assert.True(settings.GetProperty("turned_off").GetBoolean());
            var saved = await SettingsAsync(server);
            Assert.Equal(((long?)Officer, false), (saved.SetById, saved.Enabled));
        }

        [Fact]
        public async Task GuildLiveCard_SaysWhyItCantTurnOn()
        {
            const long server = 700000000000000105;
            await SetModeAsync(RaidRecapRolloutMode.Everyone);

            var noGuild = await _client.PutAsJsonAsync($"/api/guilds/{server}/raid-recap-live",
                new { enabled = true, channel_id = LogChannel.ToString(), user_id = Officer.ToString() });
            Assert.Equal(HttpStatusCode.Conflict, noGuild.StatusCode);
            Assert.Equal("no_guild", (await noGuild.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

            await SetModeAsync(RaidRecapRolloutMode.OwnerServers);
            var limited = await _client.PutAsJsonAsync($"/api/guilds/{server}/raid-recap-live",
                new { enabled = true, user_id = Officer.ToString() });
            var json = await limited.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_available", json.GetProperty("reason").GetString());
            Assert.Contains("limited rollout", json.GetProperty("error").GetString());

            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/api/guilds/{server}/raid-recap-live",
                new { enabled = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/api/guilds/{server}/raid-recap-live",
                new { enabled = true, channel_id = "general", user_id = Officer.ToString() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/guilds/not-a-server/raid-recap-live")).StatusCode);
            Assert.Null(await SettingsAsync(server));
        }
    }
}
