using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// A server's WoW Forever guild on the commands API, for the web dashboard. Every server,
    /// guild and person here is SYNTHETIC, and the armory is a fake.
    /// </summary>
    public class ForeverGuildApiTests : IClassFixture<CommandsApiFixture>
    {
        private const string Officer = "900000000000000002";

        private readonly CommandsApiFixture _fixture;
        private readonly HttpClient _client;
        private readonly FakeForeverArmory _armory;

        public ForeverGuildApiTests(CommandsApiFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.Client;
            _armory = fixture.RootProvider.GetRequiredService<FakeForeverArmory>();
            _armory.Down = false;
        }

        private static string Url(long server) => $"/api/guilds/{server}/forever-guild";

        private Task<HttpResponseMessage> SetAsync(long server, string name, string region = "US", string userId = Officer)
            => _client.PutAsJsonAsync(Url(server), new { name, region, user_id = userId, username = "synthetic_officer" });

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        private string SavedName(long server)
        {
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                return db.ForeverGuilds.SingleOrDefault(g => g.DiscordGuildId == server)?.Name;
            }
        }

        [Fact]
        public async Task TheEndpointsNeedTheApiKey()
        {
            const long server = 700000000000000201;
            _armory.AddGuild(ForeverRegion.US, "Keyless Guild");

            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.UnauthClient.GetAsync(Url(server))).StatusCode);
            var put = await _fixture.UnauthClient.PutAsJsonAsync(Url(server), new { name = "Keyless Guild", region = "US", user_id = Officer });
            Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.UnauthClient.DeleteAsync(Url(server))).StatusCode);
            Assert.Null(SavedName(server));
        }

        [Fact]
        public async Task AServerWithNoGuildReadsAsNull()
        {
            var json = await _client.GetFromJsonAsync<JsonElement>(Url(700000000000000202));

            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("guild").ValueKind);
        }

        [Fact]
        public async Task SettingSavesTheGuildAsTheArmoryHasIt()
        {
            const long server = 700000000000000203;
            _armory.AddGuild(ForeverRegion.EU, "Synthetic Guild", ForeverRuleset.RoleplayPvP, ForeverFaction.Horde,
                FakeForeverArmory.Member("Asha", "Brightvale"), FakeForeverArmory.Member("Mira", "Dawnwhisper"));

            // Typed loosely: the armory's spelling, ruleset and faction are what get saved.
            var response = await SetAsync(server, "  synthetic guild ", "eu");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var set = (await JsonAsync(response)).GetProperty("guild");
            Assert.Equal(server.ToString(), set.GetProperty("guild_id").GetString());
            Assert.Equal("Synthetic Guild", set.GetProperty("name").GetString());
            Assert.Equal("EU", set.GetProperty("region").GetString());
            Assert.Equal("RoleplayPvP", set.GetProperty("ruleset").GetString());
            Assert.Equal("Roleplay PvP", set.GetProperty("ruleset_name").GetString());
            Assert.Equal("Horde", set.GetProperty("faction").GetString());
            Assert.Equal(2, set.GetProperty("member_count").GetInt32());
            Assert.Equal(Officer, set.GetProperty("set_by_id").GetString());
            Assert.Equal("synthetic_officer", set.GetProperty("set_by_name").GetString());
            Assert.EndsWith("Z", set.GetProperty("time_set").GetString());

            // Reading it back doesn't ask the armory, so there is no member count.
            _armory.Down = true;
            var read = (await _client.GetFromJsonAsync<JsonElement>(Url(server))).GetProperty("guild");
            Assert.Equal("Synthetic Guild", read.GetProperty("name").GetString());
            Assert.Equal("EU", read.GetProperty("region").GetString());
            Assert.Equal("Roleplay PvP", read.GetProperty("ruleset_name").GetString());
            Assert.Equal("Horde", read.GetProperty("faction").GetString());
            Assert.Equal(JsonValueKind.Null, read.GetProperty("member_count").ValueKind);
            Assert.EndsWith("Z", read.GetProperty("time_set").GetString());
        }

        [Fact]
        public async Task SettingAgainReplacesTheGuild()
        {
            const long server = 700000000000000204;
            _armory.AddGuild(ForeverRegion.US, "First Guild");
            _armory.AddGuild(ForeverRegion.US, "Second Guild", ForeverRuleset.PvP, ForeverFaction.Horde);

            Assert.Equal(HttpStatusCode.OK, (await SetAsync(server, "First Guild")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SetAsync(server, "Second Guild")).StatusCode);

            var read = (await _client.GetFromJsonAsync<JsonElement>(Url(server))).GetProperty("guild");
            Assert.Equal("Second Guild", read.GetProperty("name").GetString());
            Assert.Equal("PvP", read.GetProperty("ruleset_name").GetString());
            Assert.Equal("Horde", read.GetProperty("faction").GetString());
        }

        [Fact]
        public async Task AGuildTheArmoryDoesNotHaveIsRefusedInPlainWords()
        {
            const long server = 700000000000000205;

            var response = await SetAsync(server, "Nowhere Guild");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await JsonAsync(response);
            Assert.False(json.GetProperty("success").GetBoolean());
            var error = json.GetProperty("error").GetString();
            Assert.StartsWith("I couldn't find Nowhere Guild on the US armory.", error);
            // The command's wording is Discord markdown; a web page shows plain text.
            Assert.DoesNotContain("*", error);
            Assert.DoesNotContain("`", error);
            Assert.Null(SavedName(server));
        }

        [Fact]
        public async Task AnArmoryOutageIsNotABadRequestAndKeepsTheOldGuild()
        {
            const long server = 700000000000000206;
            _armory.AddGuild(ForeverRegion.US, "Kept Guild");
            _armory.AddGuild(ForeverRegion.US, "Other Guild");
            Assert.Equal(HttpStatusCode.OK, (await SetAsync(server, "Kept Guild")).StatusCode);

            _armory.Down = true;
            var response = await SetAsync(server, "Other Guild");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var json = await JsonAsync(response);
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("Blizzard's armory didn't answer. Try again in a few minutes.", json.GetProperty("error").GetString());
            Assert.Equal("Kept Guild", SavedName(server));
        }

        [Theory]
        [InlineData("", "Guild name is required.")]
        [InlineData("   ", "Guild name is required.")]
        [InlineData("Line\nBreak", "Guild name can't contain line breaks or control characters.")]
        public async Task ABadNameIsRefusedWithTheReason(string name, string expected)
        {
            const long server = 700000000000000207;

            var response = await SetAsync(server, name);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(expected, (await JsonAsync(response)).GetProperty("error").GetString());
            Assert.Null(SavedName(server));
        }

        [Theory]
        [InlineData("XX")]
        [InlineData("1")]
        [InlineData("")]
        [InlineData(null)]
        public async Task TheRegionMustBeOneTheGameHas(string region)
        {
            const long server = 700000000000000208;
            _armory.AddGuild(ForeverRegion.EU, "Region Guild");
            _armory.AddGuild(ForeverRegion.US, "Region Guild");

            var response = await _client.PutAsJsonAsync(Url(server), new { name = "Region Guild", region, user_id = Officer });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("region must be US, EU, KR or TW", (await JsonAsync(response)).GetProperty("error").GetString());
            Assert.Null(SavedName(server));
        }

        [Fact]
        public async Task BadRequestsAreRefused()
        {
            const long server = 700000000000000209;
            _armory.AddGuild(ForeverRegion.US, "Strict Guild");

            Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/guilds/not-a-number/forever-guild")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/guilds/0/forever-guild")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync("/api/guilds/-5/forever-guild")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/guilds/nope/forever-guild",
                new { name = "Strict Guild", region = "US", user_id = Officer })).StatusCode);

            // Who set it is recorded, so it has to be there.
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync(Url(server), new { name = "Strict Guild", region = "US" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(server, "Strict Guild", userId: "someone")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsync(Url(server), new StringContent("not json"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsync(Url(server),
                new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Null(SavedName(server));
        }

        [Fact]
        public async Task ReadingAGuildFromBeforeTheArmoryDoesNotAskTheArmory()
        {
            const long server = 700000000000000211;
            _armory.AddGuild(ForeverRegion.US, "Older Guild", ForeverRuleset.PvP, ForeverFaction.Horde);
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                db.ForeverGuilds.Add(new ForeverGuild
                {
                    DiscordGuildId = server, Name = "older guild", Region = ForeverRegion.US,
                    Ruleset = ForeverRuleset.Normal, Faction = ForeverFaction.Alliance
                });
                db.SaveChanges();
            }
            var asked = _armory.GuildAsks;

            var json = await _client.GetFromJsonAsync<JsonElement>(Url(server));

            // As stored: a page calls this on every load, so it doesn't wait on Blizzard to match the guild.
            Assert.Equal("older guild", json.GetProperty("guild").GetProperty("name").GetString());
            Assert.Equal("Alliance", json.GetProperty("guild").GetProperty("faction").GetString());
            Assert.Equal(asked, _armory.GuildAsks);
        }

        [Fact]
        public async Task RemovingUnlinksAndSaysWhetherThereWasOne()
        {
            const long server = 700000000000000210;
            _armory.AddGuild(ForeverRegion.US, "Leaving Guild");
            Assert.Equal(HttpStatusCode.OK, (await SetAsync(server, "Leaving Guild")).StatusCode);

            var first = await JsonAsync(await _client.DeleteAsync($"{Url(server)}?user_id={Officer}"));
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.True(first.GetProperty("removed").GetBoolean());
            Assert.Null(SavedName(server));

            var read = await _client.GetFromJsonAsync<JsonElement>(Url(server));
            Assert.Equal(JsonValueKind.Null, read.GetProperty("guild").ValueKind);

            // Nothing left to remove is not an error.
            var second = await JsonAsync(await _client.DeleteAsync(Url(server)));
            Assert.True(second.GetProperty("success").GetBoolean());
            Assert.False(second.GetProperty("removed").GetBoolean());
        }
    }
}
