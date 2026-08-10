using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

// Type aliases to avoid naming conflict with Discord.Poll
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Shared fixture that starts the CommandsApiService on a random port.
    /// Used by all integration tests via IClassFixture.
    /// </summary>
    public class CommandsApiFixture : IAsyncLifetime
    {
        public HttpClient Client { get; private set; } = null!;
        public HttpClient UnauthClient { get; private set; } = null!;
        public ServiceProvider RootProvider { get; private set; } = null!;
        public int Port { get; private set; }
        public const string ApiKey = "test-api-key-integration-12345";

        private CommandsApiService _service = null!;
        private readonly string _dbName = $"CommandsApiTests_{Guid.NewGuid()}";

        public async Task InitializeAsync()
        {
            Port = Random.Shared.Next(15100, 15900);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CommandsApi:Enabled"] = "true",
                    ["CommandsApi:Port"] = Port.ToString(),
                    ["CommandsApi:Host"] = "127.0.0.1",
                    ["CommandsApi:ApiKey"] = ApiKey
                })
                .Build();

            // Build root service provider with in-memory DB and required services
            var services = new ServiceCollection();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase(_dbName)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddMemoryCache();
            services.AddScoped<WowCacheService>();
            services.AddSingleton<ILogger<WowCacheService>>(NullLogger<WowCacheService>.Instance);
            RootProvider = services.BuildServiceProvider();

            // Create simple dependencies for CommandsApiService constructor
            // HelpContentProvider: only used by /api/commands endpoints
            var helpProvider = new HelpContentProvider(
                NullLogger<HelpContentProvider>.Instance,
                config);

            // WowUtilities: only used by /api/guilds/refresh-roster endpoint.
            // It resolves heavy Discord/WoW services in its constructor, so we pass null
            // and skip testing that specific endpoint.
            _service = new CommandsApiService(
                NullLogger<CommandsApiService>.Instance,
                config,
                helpProvider,
                null!,
                RootProvider);

            await _service.StartAsync(CancellationToken.None);

            // Wait for server to be ready (fire-and-forget startup)
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };
            Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            UnauthClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };

            await WaitForServerReady();
        }

        private async Task WaitForServerReady()
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var response = await Client.GetAsync("/api/commands/health");
                    if (response.IsSuccessStatusCode) return;
                }
                catch { }
                await Task.Delay(100);
            }
            throw new TimeoutException($"CommandsApiService did not start on port {Port} within 3 seconds");
        }

        public async Task DisposeAsync()
        {
            Client?.Dispose();
            UnauthClient?.Dispose();
            try
            {
                await _service.StopAsync(CancellationToken.None);
            }
            catch { }
            _service?.Dispose();
            RootProvider?.Dispose();
        }

        /// <summary>
        /// Get a scoped DbContext for seeding test data.
        /// Caller must dispose the scope.
        /// </summary>
        public (IServiceScope Scope, NinjaBotEntities Db) CreateDbScope()
        {
            var scope = RootProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            return (scope, db);
        }
    }

    public class CommandsApiConfigurationTests
    {
        [Fact]
        public async Task StartAsync_WhenEnabledWithoutApiKey_FailsClosed()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CommandsApi:Enabled"] = "true",
                    ["CommandsApi:Host"] = "127.0.0.1",
                    ["CommandsApi:Port"] = Random.Shared.Next(15901, 16000).ToString(),
                    ["CommandsApi:ApiKey"] = " "
                })
                .Build();
            using var provider = new ServiceCollection().BuildServiceProvider();
            var helpProvider = new HelpContentProvider(
                NullLogger<HelpContentProvider>.Instance,
                config);
            using var service = new CommandsApiService(
                NullLogger<CommandsApiService>.Instance,
                config,
                helpProvider,
                null!,
                provider);

            Exception? error = null;
            try
            {
                await service.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            var invalid = Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("API key", invalid.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Integration tests for CommandsApiService endpoints.
    /// These tests verify routing, authentication, DB operations, and response structure.
    /// They serve as a safety net for refactoring the service into extension method groups.
    /// </summary>
    public class CommandsApiServiceTests : IClassFixture<CommandsApiFixture>
    {
        private readonly CommandsApiFixture _fixture;
        private readonly HttpClient _client;
        private readonly HttpClient _unauth;
        private static readonly JsonSerializerOptions SnakeCase = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        public CommandsApiServiceTests(CommandsApiFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.Client;
            _unauth = fixture.UnauthClient;
        }

        #region Authentication Tests

        [Fact]
        public async Task HealthCheck_NoAuth_Returns200()
        {
            var response = await _unauth.GetAsync("/api/commands/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("healthy", json.GetProperty("status").GetString());
        }

        [Fact]
        public async Task AuthenticatedEndpoint_NoApiKey_Returns401()
        {
            var response = await _unauth.GetAsync("/api/polls?guild_id=12345");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task AuthenticatedEndpoint_WrongApiKey_Returns401()
        {
            var client = new HttpClient { BaseAddress = _client.BaseAddress };
            client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

            var response = await client.GetAsync("/api/polls?guild_id=12345");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            client.Dispose();
        }

        [Fact]
        public async Task AuthenticatedEndpoint_CorrectApiKey_DoesNotReturn401()
        {
            var response = await _client.GetAsync("/api/polls?guild_id=99999");

            // Should be 200 (not 401) even if no data
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        #region Poll Endpoints

        [Fact]
        public async Task GetPolls_ReturnsEmptyList_WhenNoPollsExist()
        {
            var response = await _client.GetAsync("/api/polls?guild_id=111111");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(0, json.GetProperty("total_count").GetInt32());
        }

        [Fact]
        public async Task GetPolls_ReturnsPollsForGuild()
        {
            long guildId = 200001;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                db.Polls.Add(new DbPoll
                {
                    Question = "Test poll?",
                    PollType = "SingleChoice",
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                });
                await db.SaveChangesAsync();
            }

            var response = await _client.GetAsync($"/api/polls?guild_id={guildId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(1, json.GetProperty("total_count").GetInt32());
            Assert.Equal("Test poll?", json.GetProperty("polls")[0].GetProperty("question").GetString());
        }

        [Fact]
        public async Task GetPolls_InvalidGuildId_ReturnsBadRequest()
        {
            var response = await _client.GetAsync("/api/polls?guild_id=notanumber");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task GetPollById_ReturnsPollWithOptions()
        {
            long guildId = 200002;
            long pollId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Detailed poll?",
                    PollType = "SingleChoice",
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;

                db.PollOptions.AddRange(
                    new DbPollOption { PollId = pollId, OptionText = "Option A", DisplayOrder = 0 },
                    new DbPollOption { PollId = pollId, OptionText = "Option B", DisplayOrder = 1 }
                );
                await db.SaveChangesAsync();
            }

            var response = await _client.GetAsync($"/api/polls/{pollId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("Detailed poll?", json.GetProperty("poll").GetProperty("question").GetString());
            Assert.Equal(2, json.GetProperty("poll").GetProperty("options").GetArrayLength());
        }

        [Fact]
        public async Task GetPollById_NotFound_Returns404()
        {
            var response = await _client.GetAsync("/api/polls/999999");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task ClosePoll_CreatorWithoutCurrentGuildMembership_ReturnsForbidden()
        {
            const long pollGuildId = 200008;
            const long creatorId = 50008;
            long pollId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Guild-bound poll?",
                    PollType = "SingleChoice",
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = creatorId,
                    CreatedByName = "Creator",
                    GuildId = pollGuildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;
            }

            var response = await _client.PostAsJsonAsync($"/api/polls/{pollId}/close", new
            {
                UserId = creatorId.ToString()
            });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var (verifyScope, verifyDb) = _fixture.CreateDbScope();
            using (verifyScope)
            {
                var poll = await verifyDb.Polls.FindAsync(pollId);
                Assert.NotNull(poll);
                Assert.False(poll.IsClosed);
            }
        }

        [Fact]
        public async Task VotePoll_SingleChoice_RecordsVote()
        {
            long guildId = 200003;
            long pollId, optionId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Vote test?",
                    PollType = "SingleChoice",
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;

                var option = new DbPollOption { PollId = pollId, OptionText = "Yes", DisplayOrder = 0 };
                db.PollOptions.Add(option);
                await db.SaveChangesAsync();
                optionId = option.Id;
            }

            var response = await _client.PostAsJsonAsync($"/api/polls/{pollId}/vote", new
            {
                UserId = "50001",
                OptionId = optionId.ToString(),
                UserName = "Voter1"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("Vote recorded", json.GetProperty("message").GetString());
        }

        [Fact]
        public async Task VotePoll_ClosedPoll_ReturnsBadRequest()
        {
            long guildId = 200004;
            long pollId, optionId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Closed poll?",
                    PollType = "SingleChoice",
                    IsClosed = true,
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;

                var option = new DbPollOption { PollId = pollId, OptionText = "Yes", DisplayOrder = 0 };
                db.PollOptions.Add(option);
                await db.SaveChangesAsync();
                optionId = option.Id;
            }

            var response = await _client.PostAsJsonAsync($"/api/polls/{pollId}/vote", new
            {
                UserId = "50002",
                OptionId = optionId.ToString(),
                UserName = "Voter1"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task GetPollVoters_NonAnonymous_ReturnsVoters()
        {
            long guildId = 200005;
            long pollId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Who voted?",
                    PollType = "SingleChoice",
                    IsAnonymous = false,
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;

                var option = new DbPollOption { PollId = pollId, OptionText = "Yes", DisplayOrder = 0 };
                db.PollOptions.Add(option);
                await db.SaveChangesAsync();

                db.PollVotes.Add(new DbPollVote
                {
                    PollId = pollId,
                    OptionId = option.Id,
                    UserId = 50003,
                    UserName = "VoterA",
                    VotedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var response = await _client.GetAsync($"/api/polls/{pollId}/voters");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(1, json.GetProperty("poll").GetProperty("total_votes").GetInt32());
        }

        [Fact]
        public async Task GetPollVoters_Anonymous_Returns403()
        {
            long guildId = 200006;
            long pollId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Secret vote?",
                    PollType = "SingleChoice",
                    IsAnonymous = true,
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;
            }

            var response = await _client.GetAsync($"/api/polls/{pollId}/voters");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task GetPollResults_ReturnResults()
        {
            long guildId = 200007;
            long pollId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var poll = new DbPoll
                {
                    Question = "Results test?",
                    PollType = "SingleChoice",
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = 1,
                    CreatedByName = "TestUser",
                    GuildId = guildId,
                    ChannelId = 100,
                    MessageId = 100
                };
                db.Polls.Add(poll);
                await db.SaveChangesAsync();
                pollId = poll.Id;

                db.PollOptions.Add(new DbPollOption { PollId = pollId, OptionText = "A", DisplayOrder = 0 });
                await db.SaveChangesAsync();
            }

            var response = await _client.GetAsync($"/api/polls/{pollId}/results");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("Results test?", json.GetProperty("poll").GetProperty("question").GetString());
        }

        #endregion

        #region Poll Settings Endpoints

        [Fact]
        public async Task GetPollSettings_ReturnsDefaults_WhenNoneSet()
        {
            var response = await _client.GetAsync("/api/guilds/300001/poll-settings");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.False(json.GetProperty("settings").GetProperty("mention_voters_on_close").GetBoolean());
        }

        [Fact]
        public async Task PutPollSettings_CreatesAndReturnsSettings()
        {
            var response = await _client.PutAsJsonAsync("/api/guilds/300002/poll-settings", new
            {
                mention_voters_on_close = true,
                default_anonymous = true,
                user_id = "1001",
                user_name = "Admin"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("mention_voters_on_close").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("default_anonymous").GetBoolean());
        }

        [Fact]
        public async Task GetPollSettings_InvalidGuildId_ReturnsBadRequest()
        {
            var response = await _client.GetAsync("/api/guilds/notanumber/poll-settings");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region Log Monitoring Endpoints

        [Fact]
        public async Task GetLogMonitoring_ReturnsDefaults_WhenNoneSet()
        {
            var response = await _client.GetAsync("/api/guilds/400001/log-monitoring");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.False(json.GetProperty("settings").GetProperty("monitor_logs").GetBoolean());
        }

        [Fact]
        public async Task PutLogMonitoring_CreatesAndReturnsSettings()
        {
            var response = await _client.PutAsJsonAsync("/api/guilds/400002/log-monitoring", new
            {
                channel_id = "999",
                channel_name = "logs-channel",
                monitor_logs = true,
                server_name = "Test Server"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("monitor_logs").GetBoolean());
            Assert.Equal("999", json.GetProperty("settings").GetProperty("channel_id").GetString());
        }

        #endregion

        #region Greeting Settings Endpoints

        [Fact]
        public async Task GetGreetingSettings_ReturnsDefaults_WhenNoneSet()
        {
            var response = await _client.GetAsync("/api/guilds/500001/greeting-settings");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.False(json.GetProperty("settings").GetProperty("greet_users").GetBoolean());
            Assert.False(json.GetProperty("settings").GetProperty("part_users").GetBoolean());
        }

        [Fact]
        public async Task PutGreetingSettings_CreatesAndReturnsSettings()
        {
            var response = await _client.PutAsJsonAsync("/api/guilds/500002/greeting-settings", new
            {
                greet_users = true,
                part_users = true,
                greeting = "Welcome!",
                parting_message = "Goodbye!"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("greet_users").GetBoolean());
            Assert.Equal("Welcome!", json.GetProperty("settings").GetProperty("greeting").GetString());
        }

        #endregion

        #region Moderation Watcher Endpoints

        [Fact]
        public async Task GetModerationWatcher_ReturnsDefaults_WhenNoneSet()
        {
            var response = await _client.GetAsync("/api/guilds/600001/moderation-watcher");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.False(json.GetProperty("settings").GetProperty("watch_voice").GetBoolean());
            Assert.False(json.GetProperty("settings").GetProperty("watch_messages").GetBoolean());
        }

        [Fact]
        public async Task PutModerationWatcher_CreatesAndReturnsSettings()
        {
            var response = await _client.PutAsJsonAsync("/api/guilds/600002/moderation-watcher", new
            {
                channel_id = "888",
                channel_name = "mod-log",
                watch_voice = true,
                watch_messages = true,
                watch_bans = true,
                set_by_id = "1001",
                set_by_name = "Admin"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("watch_voice").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("watch_messages").GetBoolean());
            Assert.True(json.GetProperty("settings").GetProperty("watch_bans").GetBoolean());
        }

        #endregion

        #region WoW Association Endpoints

        [Fact]
        public async Task GetWowAssociation_ReturnsNull_WhenNoneSet()
        {
            var response = await _client.GetAsync("/api/guilds/700001/wow-association");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("association").ValueKind);
        }

        [Fact]
        public async Task PutWowAssociation_CreatesAssociation()
        {
            var response = await _client.PutAsJsonAsync("/api/guilds/700002/wow-association", new
            {
                wow_guild_name = "Test Guild",
                wow_realm = "Area 52",
                wow_realm_slug = "area-52",
                wow_region = "us",
                locale = "en_US"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("Test Guild", json.GetProperty("association").GetProperty("wow_guild_name").GetString());
            Assert.Equal("area-52", json.GetProperty("association").GetProperty("wow_realm_slug").GetString());
        }

        [Fact]
        public async Task PutWowAssociation_MissingRequired_ReturnsBadRequest()
        {
            var response = await _client.PutAsJsonAsync("/api/guilds/700003/wow-association", new
            {
                wow_guild_name = "Test Guild"
                // Missing wow_realm and wow_region
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region Character Endpoints

        [Fact]
        public async Task AddCharacter_CreatesCharacter()
        {
            var response = await _client.PostAsJsonAsync("/api/characters/add", new
            {
                DiscordUserId = "80001",
                CharacterName = "Phreeq",
                Realm = "Area 52",
                Region = "us"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("Phreeq", json.GetProperty("character").GetProperty("name").GetString());
            Assert.Equal("Area 52", json.GetProperty("character").GetProperty("realm").GetString());
        }

        [Fact]
        public async Task AddCharacter_Duplicate_ReturnsConflict()
        {
            // Add character first
            await _client.PostAsJsonAsync("/api/characters/add", new
            {
                DiscordUserId = "80002",
                CharacterName = "DupChar",
                Realm = "Stormrage",
                Region = "us"
            });

            // Try to add same character again
            var response = await _client.PostAsJsonAsync("/api/characters/add", new
            {
                DiscordUserId = "80002",
                CharacterName = "DupChar",
                Realm = "Stormrage",
                Region = "us"
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        [Fact]
        public async Task AddCharacter_MissingFields_ReturnsBadRequest()
        {
            var response = await _client.PostAsJsonAsync("/api/characters/add", new
            {
                DiscordUserId = "80003"
                // Missing CharacterName and Realm
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region Away Status Endpoints

        [Fact]
        public async Task GetAwayStatus_ReturnsNull_WhenNotSet()
        {
            var response = await _client.GetAsync("/api/users/900001/away-status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("status").ValueKind);
        }

        [Fact]
        public async Task PutAwayStatus_CreatesStatus()
        {
            var response = await _client.PutAsJsonAsync("/api/users/900002/away-status", new
            {
                user_name = "TestUser",
                is_away = true,
                message = "On vacation"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());

            // Verify via GET
            var getResponse = await _client.GetAsync("/api/users/900002/away-status");
            var getJson = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(getJson.GetProperty("status").GetProperty("is_away").GetBoolean());
            Assert.Equal("On vacation", getJson.GetProperty("status").GetProperty("message").GetString());
        }

        [Fact]
        public async Task GetAwayStatus_InvalidUserId_ReturnsBadRequest()
        {
            var response = await _client.GetAsync("/api/users/notanumber/away-status");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region Cache Invalidation Endpoint

        [Fact]
        public async Task CacheInvalidate_ReturnsSuccess()
        {
            var response = await _client.PostAsJsonAsync("/api/cache/wcl-invalidate", new
            {
                GuildName = "Test Guild",
                RealmSlug = "area-52",
                Region = "us"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task CacheInvalidate_MissingFields_ReturnsBadRequest()
        {
            var response = await _client.PostAsJsonAsync("/api/cache/wcl-invalidate", new
            {
                GuildName = "Test Guild"
                // Missing RealmSlug and Region
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region Realm Status Endpoint

        [Fact]
        public async Task GetRealmStatus_ReturnsEmptyWithMessage()
        {
            var response = await _client.GetAsync("/api/realms/us/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(0, json.GetProperty("statuses").GetArrayLength());
        }

        #endregion

        #region Sync Endpoints

        [Fact]
        public async Task SyncTrigger_QueuedType_CreatesSyncRequest()
        {
            var response = await _client.PostAsJsonAsync("/api/sync/trigger", new
            {
                sync_type = "achievements",
                user_id = "1001"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("achievements", json.GetProperty("sync_type").GetString());
            Assert.Equal("pending", json.GetProperty("status").GetString());
        }

        [Fact]
        public async Task SyncTrigger_DuplicatePending_ReturnsError()
        {
            // Seed a pending request
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                db.StaticDataSyncRequests.Add(new StaticDataSyncRequest
                {
                    SyncType = "pets",
                    Status = "pending",
                    RequestSource = "test",
                    RequestedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var response = await _client.PostAsJsonAsync("/api/sync/trigger", new
            {
                sync_type = "pets"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("pending_exists", json.GetProperty("error").GetString());
        }

        [Fact]
        public async Task SyncTrigger_InvalidType_ReturnsBadRequest()
        {
            var response = await _client.PostAsJsonAsync("/api/sync/trigger", new
            {
                sync_type = "nonexistent"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task GetSyncStatus_ReturnsStatusForAllTypes()
        {
            var response = await _client.GetAsync("/api/sync/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            // Should have entries for achievements, pets, mounts, items
            Assert.True(json.TryGetProperty("achievements", out _));
            Assert.True(json.TryGetProperty("pets", out _));
            Assert.True(json.TryGetProperty("mounts", out _));
            Assert.True(json.TryGetProperty("items", out _));
        }

        [Fact]
        public async Task GetSyncRequests_ReturnsRequestHistory()
        {
            var response = await _client.GetAsync("/api/sync/requests");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("requests", out _));
        }

        [Fact]
        public async Task DeleteSyncRequest_CancelsPending()
        {
            long requestId;
            var (scope, db) = _fixture.CreateDbScope();
            using (scope)
            {
                var request = new StaticDataSyncRequest
                {
                    SyncType = "mounts",
                    Status = "pending",
                    RequestSource = "test",
                    RequestedAt = DateTime.UtcNow
                };
                db.StaticDataSyncRequests.Add(request);
                await db.SaveChangesAsync();
                requestId = request.Id;
            }

            var response = await _client.DeleteAsync($"/api/sync/requests/{requestId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());

            // Verify it's cancelled in DB
            var (scope2, db2) = _fixture.CreateDbScope();
            using (scope2)
            {
                var updated = await db2.StaticDataSyncRequests.FindAsync(requestId);
                Assert.Equal("cancelled", updated?.Status);
            }
        }

        [Fact]
        public async Task DeleteSyncRequest_NotFound_Returns404()
        {
            var response = await _client.DeleteAsync("/api/sync/requests/999999");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        #endregion

        #region Commands Metadata Endpoints

        [Fact]
        public async Task GetCommands_ReturnsNotFound_WhenNoHelpContent()
        {
            // HelpContentProvider returns null since no commands are scanned in test
            var response = await _client.GetAsync("/api/commands");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task PostRegenerate_ReturnsSuccess()
        {
            var response = await _client.PostAsync("/api/commands/regenerate", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
        }

        #endregion

        #region Cross-Domain: Round-Trip Tests

        [Fact]
        public async Task LogMonitoring_RoundTrip_CreateUpdateRead()
        {
            // Create via PUT
            await _client.PutAsJsonAsync("/api/guilds/1100001/log-monitoring", new
            {
                channel_id = "111",
                channel_name = "logs",
                monitor_logs = false,
                server_name = "Test"
            });

            // Update via PUT (same endpoint, upsert)
            await _client.PutAsJsonAsync("/api/guilds/1100001/log-monitoring", new
            {
                monitor_logs = true
            });

            // Read via GET
            var response = await _client.GetAsync("/api/guilds/1100001/log-monitoring");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.True(json.GetProperty("settings").GetProperty("monitor_logs").GetBoolean());
            Assert.Equal("111", json.GetProperty("settings").GetProperty("channel_id").GetString());
        }

        [Fact]
        public async Task WowAssociation_RoundTrip_CreateThenRead()
        {
            // Create
            await _client.PutAsJsonAsync("/api/guilds/1100002/wow-association", new
            {
                wow_guild_name = "Round Trip Guild",
                wow_realm = "Stormrage",
                wow_realm_slug = "stormrage",
                wow_region = "us"
            });

            // Read
            var response = await _client.GetAsync("/api/guilds/1100002/wow-association");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("Round Trip Guild",
                json.GetProperty("association").GetProperty("wow_guild_name").GetString());
            Assert.Equal("stormrage",
                json.GetProperty("association").GetProperty("wow_realm_slug").GetString());
        }

        #endregion
    }
}
