using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RaiderIOApiTransportTests
    {
        [Fact]
        public async Task RateLimit_RetriesUsingProviderDelayThenSucceeds()
        {
            var handler = new SequenceHandler(
                _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) },
                    Content = new StringContent("{\"message\":\"slow down\"}")
                },
                _ => Json(HttpStatusCode.OK, "{\"region\":\"us\",\"title\":\"Current\",\"affix_details\":[]}"));
            var api = CreateApi(handler);

            var result = await api.GetCurrentAffixAsync();

            Assert.Equal("Current", result.Title);
            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task LongRateLimitDelayFailsFastInsteadOfConsumingInteractionLifetime()
        {
            var handler = new SequenceHandler(
                _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1)) },
                    Content = new StringContent("{\"message\":\"slow down\"}")
                });
            var api = CreateApi(handler);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var error = await Assert.ThrowsAsync<RaiderIORateLimitException>(() =>
                api.GetCurrentAffixAsync(cancellationToken: cancellation.Token));

            Assert.Equal(TimeSpan.FromMinutes(1), error.RetryAfter);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task NotFound_ThrowsTypedExceptionWithoutRetrying()
        {
            var handler = new SequenceHandler(
                _ => Json(HttpStatusCode.NotFound, "{\"message\":\"missing\"}"));
            var api = CreateApi(handler);

            var error = await Assert.ThrowsAsync<RaiderIONotFoundException>(() =>
                api.GetCharMythicPlusInfoAsync("Missing", "Area 52", "us"));

            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task CharacterNotFoundBadRequest_ThrowsTypedExceptionWithoutWarning()
        {
            var logger = new CapturingLogger<RaiderIOApi>();
            var handler = new SequenceHandler(
                _ => Json(HttpStatusCode.BadRequest,
                    "{\"statusCode\":400,\"error\":\"Bad Request\",\"message\":\"Could not find requested character\"}"));
            var api = CreateApi(handler, logger: logger);

            var error = await Assert.ThrowsAsync<RaiderIONotFoundException>(() =>
                api.GetCharMythicPlusInfoAsync("Missing", "Area 52", "us"));

            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            Assert.Equal(1, handler.CallCount);
            Assert.DoesNotContain("RaiderIO API rejected request", logger.StructuredState, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GuildNotFoundBadRequest_ThrowsTypedExceptionWithoutWarning()
        {
            var logger = new CapturingLogger<RaiderIOApi>();
            var handler = new SequenceHandler(
                _ => Json(HttpStatusCode.BadRequest,
                    "{\"statusCode\":400,\"error\":\"Bad Request\",\"message\":\"Could not find requested guild\"}"));
            var api = CreateApi(handler, logger: logger);

            var error = await Assert.ThrowsAsync<RaiderIONotFoundException>(() =>
                api.GetRioGuildInfoAsync("Missing", "Area 52", "us"));

            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            Assert.Equal(1, handler.CallCount);
            Assert.DoesNotContain("RaiderIO API rejected request", logger.StructuredState, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UnknownRequestedResourceBadRequest_LogsDriftMessageOnceAndRemainsNoisy()
        {
            const string providerMessage = "Could not find requested raid";
            var logger = new CapturingLogger<RaiderIOApi>();
            Func<HttpRequestMessage, HttpResponseMessage> response =
                _ => Json(HttpStatusCode.BadRequest,
                    $"{{\"message\":\"{providerMessage}\"}}");
            var handler = new SequenceHandler(response, response);
            var api = CreateApi(handler, logger: logger);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = await Assert.ThrowsAsync<RaiderIOApiException>(() =>
                    api.GetRioGuildInfoAsync("Test", "Area 52", "us"));

                Assert.IsNotType<RaiderIONotFoundException>(error);
                Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            }

            Assert.Equal(2, handler.CallCount);
            Assert.Contains("RaiderIO API rejected request", logger.StructuredState, StringComparison.Ordinal);
            Assert.Equal(1, logger.CountEntries(
                Microsoft.Extensions.Logging.LogLevel.Information,
                providerMessage));
        }

        [Fact]
        public async Task OtherBadRequest_RemainsNoisyGenericRejection()
        {
            var logger = new CapturingLogger<RaiderIOApi>();
            var handler = new SequenceHandler(
                _ => Json(HttpStatusCode.BadRequest, "{\"message\":\"invalid region\"}"));
            var api = CreateApi(handler, logger: logger);

            var error = await Assert.ThrowsAsync<RaiderIOApiException>(() =>
                api.GetCharMythicPlusInfoAsync("Test", "Area 52", "invalid"));

            Assert.IsNotType<RaiderIONotFoundException>(error);
            Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            Assert.Contains("RaiderIO API rejected request", logger.StructuredState, StringComparison.Ordinal);
        }

        [Fact]
        public async Task QueryValuesAreEncodedAndEmptyAccessKeyIsOmitted()
        {
            Uri requested = null;
            var handler = new SequenceHandler(request =>
            {
                requested = request.RequestUri;
                return Json(HttpStatusCode.OK,
                    "{\"name\":\"Guild Name\",\"raid_rankings\":{},\"raid_progression\":{}}");
            });
            var api = CreateApi(handler, apiKey: "");

            await api.GetRioGuildInfoAsync("Guild & Friends", "Area 52", "us");

            Assert.NotNull(requested);
            Assert.Contains("name=Guild%20%26%20Friends", requested.Query);
            Assert.Contains("realm=Area%2052", requested.Query);
            Assert.DoesNotContain("access_key", requested.Query, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task StructuredLogStateNeverContainsTheAccessKey()
        {
            var logger = new CapturingLogger<RaiderIOApi>();
            var handler = new SequenceHandler(
                _ => Json(HttpStatusCode.OK, "{\"region\":\"us\",\"title\":\"Current\",\"affix_details\":[]}"));
            var api = CreateApi(handler, apiKey: "super-secret-rio-key", logger: logger);

            await api.GetCurrentAffixAsync();

            Assert.DoesNotContain("super-secret-rio-key", logger.StructuredState, StringComparison.Ordinal);
        }

        [Fact]
        public async Task InsightCharacterRequestIncludesDiscordCoachAndTalentFields()
        {
            Uri requested = null;
            var handler = new SequenceHandler(request =>
            {
                requested = request.RequestUri;
                return Json(HttpStatusCode.OK, "{}");
            });
            var api = CreateApi(handler);

            await api.GetCharInsightsInfoAsync("Odib", "Tichondrius", "us");

            var decodedQuery = Uri.UnescapeDataString(requested.Query);
            Assert.Contains("mythic_plus_best_runs:all", decodedQuery);
            Assert.Contains("mythic_plus_alternate_runs:all", decodedQuery);
            Assert.Contains("mythic_plus_dungeon_run_counts", decodedQuery);
            Assert.Contains("talents:categorized", decodedQuery);
        }

        [Fact]
        public async Task InsightMethodsUsePublishedPathsAndParameters()
        {
            var requests = new List<Uri>();
            var handler = new SequenceHandler(
                request => { requests.Add(request.RequestUri); return Json(HttpStatusCode.OK, "{\"rivals\":null}"); },
                request => { requests.Add(request.RequestUri); return Json(HttpStatusCode.OK, "{\"pastRuns\":[]}"); },
                request => { requests.Add(request.RequestUri); return Json(HttpStatusCode.OK, "{\"cutoffs\":{}}"); },
                request => { requests.Add(request.RequestUri); return Json(HttpStatusCode.OK, "{\"realmListing\":{\"realms\":[]}}"); });
            var api = CreateApi(handler);
            var run = new NinjaBotCore.Models.Wow.RaiderIOModels.MythicPlusRun
            {
                ZoneId = 16368,
                MythicLevel = 17,
                ClearTimeMs = 1709162,
                CompletedAt = DateTimeOffset.Parse("2026-08-23T05:32:10Z"),
                Spec = new NinjaBotCore.Models.Wow.RaiderIOModels.MythicPlusSpec { Id = 71 },
                Affixes = new[]
                {
                    new NinjaBotCore.Models.Wow.RaiderIOModels.AffixInfo { Id = 10 },
                    new NinjaBotCore.Models.Wow.RaiderIOModels.AffixInfo { Id = 9 }
                }
            };

            await api.GetCharacterRivalsAsync("Odib", "Tichondrius", "us", "region", 71);
            await api.GetRunReviewAsync("Odib", "Tichondrius", "us", run, "region");
            await api.GetSeasonCutoffsAsync("us", "season-mn-2");
            await api.GetLeaderboardCapacityAsync("us", "tichondrius");

            Assert.Equal(4, requests.Count);
            Assert.Equal("/api/v1/client/character-rivals", requests[0].AbsolutePath);
            Assert.Contains("specId=71", requests[0].Query);
            Assert.Equal("/api/v1/client/run-review", requests[1].AbsolutePath);
            Assert.Contains("dungeonId=16368", requests[1].Query);
            Assert.Contains("completedAt=", requests[1].Query);
            Assert.Equal("/api/v1/mythic-plus/season-cutoffs", requests[2].AbsolutePath);
            Assert.Contains("season=season-mn-2", requests[2].Query);
            Assert.Equal("/api/v1/mythic-plus/leaderboard-capacity", requests[3].AbsolutePath);
        }

        [Fact]
        public async Task RunReviewOmitsEmptyOptionalAffixes()
        {
            Uri requested = null;
            var handler = new SequenceHandler(request =>
            {
                requested = request.RequestUri;
                return Json(HttpStatusCode.OK, "{\"pastRuns\":[]}");
            });
            var api = CreateApi(handler);
            var run = new NinjaBotCore.Models.Wow.RaiderIOModels.MythicPlusRun
            {
                ZoneId = 16368,
                MythicLevel = 10,
                ClearTimeMs = 1000,
                CompletedAt = DateTimeOffset.Parse("2026-08-23T05:32:10Z"),
                Affixes = Array.Empty<NinjaBotCore.Models.Wow.RaiderIOModels.AffixInfo>()
            };

            await api.GetRunReviewAsync("Odib", "Tichondrius", "us", run);

            Assert.DoesNotContain("affixes=", requested.Query, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CutoffsAreCachedToProtectTheProviderAndDiscordInteractions()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
            var handler = new SequenceHandler(
                _ => Json(HttpStatusCode.OK, "{\"cutoffs\":{\"keystoneMaster\":{\"score\":2000}}}"));
            var api = CreateApi(handler, cache: cache);

            var first = await api.GetSeasonCutoffsAsync("us", "season-mn-2");
            var second = await api.GetSeasonCutoffsAsync("us", "season-mn-2");

            Assert.Equal(2000, first.Cutoffs.KeystoneMaster.Score);
            Assert.Equal(2000, second.Cutoffs.KeystoneMaster.Score);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task ConcurrentCutoffCacheMissesAreCoalesced()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
            var handler = new GatedHandler(
                "{\"cutoffs\":{\"keystoneMaster\":{\"score\":2000}}}");
            var api = CreateApi(handler, cache: cache);

            var first = api.GetSeasonCutoffsAsync("us", "season-mn-2");
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = api.GetSeasonCutoffsAsync("us", "season-mn-2");
            await Task.Delay(50);

            Assert.Equal(1, handler.CallCount);
            handler.Release.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(1, handler.CallCount);
        }

        private static RaiderIOApi CreateApi(
            HttpMessageHandler handler,
            string apiKey = "test-key",
            IMemoryCache cache = null,
            Microsoft.Extensions.Logging.ILogger<RaiderIOApi> logger = null)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["RioApi"] = apiKey
                })
                .Build();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(value => value.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return new RaiderIOApi(
                config,
                logger ?? NullLogger<RaiderIOApi>.Instance,
                factory.Object,
                cache);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };

        private sealed class GatedHandler : HttpMessageHandler
        {
            private readonly string _json;
            private int _callCount;

            public GatedHandler(string json) => _json = json;

            public int CallCount => Volatile.Read(ref _callCount);
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return Json(HttpStatusCode.OK, _json);
            }
        }

        private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
        {
            private readonly List<string> _state = new();
            private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Value)> _entries = new();

            public string StructuredState => string.Join("\n", _state);
            public int CountEntries(Microsoft.Extensions.Logging.LogLevel level, string value) =>
                _entries.Count(entry => entry.Level == level &&
                                        string.Equals(entry.Value, value, StringComparison.Ordinal));
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (state is IEnumerable<KeyValuePair<string, object>> values)
                {
                    foreach (var value in values.Select(entry => entry.Value?.ToString() ?? string.Empty))
                    {
                        _state.Add(value);
                        _entries.Add((logLevel, value));
                    }
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static NullScope Instance { get; } = new();
                public void Dispose() { }
            }
        }

        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

            public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            {
                _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
            }

            public int CallCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                CallCount++;
                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException("No response configured.");
                }

                return Task.FromResult(_responses.Dequeue()(request));
            }
        }
    }
}
