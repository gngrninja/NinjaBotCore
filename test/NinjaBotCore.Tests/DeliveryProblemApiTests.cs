using System;
using System.Collections.Generic;
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
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services.DeliveryProblems;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// The Actionable Alerts endpoints on the owner's admin page. Every server, channel and
    /// person here is SYNTHETIC.
    /// </summary>
    public class DeliveryProblemApiTests : IClassFixture<CommandsApiFixture>
    {
        private const ulong Owner = 900000000000000001;
        private const ulong Officer = 900000000000000002;
        private const ulong Server = 700000000000000011;
        private const ulong Channel = 710000000000000011;

        private readonly CommandsApiFixture _fixture;
        private readonly HttpClient _client;
        private readonly Mock<IDeliveryProblemDiscord> _discord;

        public DeliveryProblemApiTests(CommandsApiFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.Client;
            Mock.Get(fixture.RootProvider.GetRequiredService<IRaidRecapLiveDiscord>())
                .Setup(d => d.OwnerIdsAsync()).ReturnsAsync(new[] { Owner });
            _discord = Mock.Get(fixture.RootProvider.GetRequiredService<IDeliveryProblemDiscord>());
            _discord.Invocations.Clear();
            _discord.Setup(d => d.Server(It.IsAny<ulong>())).Returns(new DeliveryServerInfo(true, "Synthetic Server", Owner + 10, "synthetic_owner"));
            _discord.Setup(d => d.Channel(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<IReadOnlyList<ChannelPermission>>()))
                .Returns(new DeliveryChannelInfo(true, "raid-logs", new[] { "Embed Links" }));
            _discord.Setup(d => d.IsMemberAsync(It.IsAny<ulong>(), It.IsAny<ulong>())).ReturnsAsync((bool?)true);
            _discord.Setup(d => d.SendDmAsync(It.IsAny<ulong>(), It.IsAny<string>())).ReturnsAsync(DeliveryDmOutcome.Sent);
        }

        private async Task<long> SeedAsync(string feature)
        {
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                // Every feature still on in the channel, so the problem still applies.
                var server = (long)Server;
                if (!db.ModerationWatcher.Any(m => m.DiscordGuildId == server))
                {
                    db.ModerationWatcher.Add(new ModerationWatcher { DiscordGuildId = server, ChannelId = (long)Channel, WatchMessages = true });
                    db.ServerGreetings.Add(new ServerGreeting { DiscordGuildId = server, GreetUsers = true, PartUsers = true, GreetingChannelId = (long)Channel });
                    db.WordList.Add(new WordList { ServerId = server, ServerName = "Synthetic", Word = "synthetic" });
                }

                var row = new DeliveryProblem
                {
                    ServerId = (long)Server,
                    Feature = feature,
                    ChannelId = (long)Channel,
                    Problem = DeliveryProblemKind.MissingPermissions,
                    DiscordCode = 50013,
                    FirstSeenAt = DateTime.UtcNow.AddDays(-2),
                    LastSeenAt = DateTime.UtcNow,
                    Failures = 3
                };
                db.DeliveryProblems.Add(row);
                await db.SaveChangesAsync();
                return row.Id;
            }
        }

        [Fact]
        public async Task TheEndpointsNeedTheApiKey()
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.UnauthClient.GetAsync("/api/admin/delivery-problems")).StatusCode);
            var post = await _fixture.UnauthClient.PostAsJsonAsync("/api/admin/delivery-problems/dismiss", new { ids = new[] { "1" }, user_id = Owner.ToString() });
            Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        }

        [Fact]
        public async Task TheListSaysWhatIsWrongInSnakeCase()
        {
            var id = await SeedAsync(DeliveryFeature.ModerationLog);

            var json = await _client.GetFromJsonAsync<JsonElement>("/api/admin/delivery-problems");
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("counts").GetProperty("open").GetInt32() >= 1);
            var problem = json.GetProperty("problems").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id.ToString());
            Assert.Equal(Server.ToString(), problem.GetProperty("server_id").GetString());
            Assert.Equal("Moderation log", problem.GetProperty("feature_text").GetString());
            Assert.Equal(Channel.ToString(), problem.GetProperty("channel_id").GetString());
            Assert.Equal("missing_permissions", problem.GetProperty("problem").GetString());
            Assert.Equal("Embed Links", problem.GetProperty("missing_permissions")[0].GetString());
            Assert.Equal("open", problem.GetProperty("state").GetString());
            Assert.Equal((Owner + 10).ToString(), problem.GetProperty("owner").GetProperty("user_id").GetString());
            Assert.True(problem.GetProperty("can_turn_off").GetBoolean());
            Assert.EndsWith("Z", problem.GetProperty("last_seen_at").GetString());
            Assert.Contains("Embed Links", problem.GetProperty("dm_preview").GetString());
        }

        [Fact]
        public async Task OnlyTheBotOwnerActs()
        {
            var id = await SeedAsync(DeliveryFeature.Welcome);
            var officer = Officer.ToString();

            Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync($"/api/admin/delivery-problems/{id}/dm", new { to = "owner", user_id = officer })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync($"/api/admin/delivery-problems/{id}/recheck", new { user_id = officer })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/turn-off", new { ids = new[] { id.ToString() }, user_id = officer })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/dismiss", new { ids = new[] { id.ToString() }, user_id = officer })).StatusCode);
            _discord.Verify(d => d.SendDmAsync(It.IsAny<ulong>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task BadRequestsAreRefused()
        {
            var owner = Owner.ToString();
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/1/dm", new { to = "everyone", user_id = owner })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/999999/dm", new { to = "owner", user_id = owner })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/dismiss", new { ids = Array.Empty<string>(), user_id = owner })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/turn-off", new { ids = new[] { "1", "nope" }, user_id = owner })).StatusCode);
            var tooMany = Enumerable.Range(1, DeliveryProblemService.MaxBatch + 1).Select(i => i.ToString()).ToArray();
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/admin/delivery-problems/dismiss", new { ids = tooMany, user_id = owner })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/admin/delivery-problems/dismiss", new StringContent("not json"))).StatusCode);
        }

        [Fact]
        public async Task TheOwnerDmsTurnsOffAndDismisses()
        {
            var owner = Owner.ToString();
            var dmId = await SeedAsync(DeliveryFeature.Leave);

            var dm = await _client.PostAsJsonAsync($"/api/admin/delivery-problems/{dmId}/dm", new { to = "owner", user_id = owner });
            var dmJson = await dm.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("sent", dmJson.GetProperty("result").GetString());
            Assert.Equal("DM sent.", dmJson.GetProperty("result_text").GetString());
            Assert.Equal("notified", dmJson.GetProperty("problem").GetProperty("state").GetString());
            _discord.Verify(d => d.SendDmAsync(Owner + 10, It.Is<string>(t => t.Contains("because you own"))), Times.Once);

            var offId = await SeedAsync(DeliveryFeature.WordFilter);
            var off = await (await _client.PostAsJsonAsync("/api/admin/delivery-problems/turn-off", new { ids = new[] { offId.ToString() }, user_id = owner }))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_supported", off.GetProperty("results")[0].GetProperty("status").GetString());

            var dismissed = await (await _client.PostAsJsonAsync("/api/admin/delivery-problems/dismiss", new { ids = new[] { offId.ToString() }, user_id = owner }))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, dismissed.GetProperty("dismissed").GetInt32());
        }
    }
}
