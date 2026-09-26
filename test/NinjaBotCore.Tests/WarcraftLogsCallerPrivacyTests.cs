using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Xunit;

namespace NinjaBotCore.Tests;

public class WarcraftLogsCallerPrivacyTests
{
    private const string Sentinel = "PRIVATE-provider-report-or-token-do-not-log";

    // The scalar cases reproduce the independent review probes through the real /char catch blocks.
    [Theory]
    [InlineData("zone", "scalar")]
    [InlineData("encounter", "scalar")]
    [InlineData("zone", "value")]
    [InlineData("encounter", "value")]
    [InlineData("zone", "path")]
    [InlineData("encounter", "path")]
    public async Task LegacyProductionCallerDoesNotRenderNestedProviderException(string kind, string shape)
    {
        var payload = MalformedRanking(kind, shape);
        var body = Envelope(kind, payload);
        // Prove these fixtures would expose private values/property paths via Newtonsoft diagnostics.
        var original = Assert.ThrowsAny<JsonException>(() =>
        {
            if (kind == "zone") payload.ToObject<WclV2ZoneRankingsData>();
            else payload.ToObject<WclV2EncounterRankingsData>();
        });
        Assert.Contains(Sentinel, original.ToString());
        if (shape == "path") Assert.Contains(Sentinel, Assert.IsType<JsonSerializationException>(original).Path);

        using var fixture = new Fixture(body);
        Assert.Null(await fixture.CallProductionCaller(kind));
        Assert.Equal(1, fixture.Handler.RankingsRequests);
        Assert.Contains(kind + "Rankings", fixture.Handler.LastRankingsQuery);
        var warning = Assert.Single(fixture.Sink.Events, e => e.Level == LogEventLevel.Warning && e.Exception != null);
        Assert.Contains(kind == "zone" ? "Failed to fetch WCL V2 data with filters" :
            "Failed to fetch character encounter rankings", warning.RenderMessage());
        Assert.Equal(typeof(CharCommands).FullName, ((ScalarValue)warning.Properties["SourceContext"]).Value);
        Assert.DoesNotContain(Sentinel, fixture.Sink.Text.ToString());
        Assert.Null(warning.Exception.InnerException);
    }

    [Theory]
    [InlineData("zone", "scalar")]
    [InlineData("encounter", "scalar")]
    [InlineData("zone", "value")]
    [InlineData("encounter", "value")]
    [InlineData("zone", "path")]
    [InlineData("encounter", "path")]
    public async Task EscapingConversionFailureContainsNoProviderPayloadOrInnerException(string kind, string shape)
    {
        using var fixture = new Fixture(Envelope(kind, MalformedRanking(kind, shape)));
        var error = await Record.ExceptionAsync(() => CallClient(fixture.Client, kind));
        Assert.NotNull(error);
        Assert.DoesNotContain(Sentinel, error.ToString());
        Assert.DoesNotContain("characterData.character", error.ToString());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
        Assert.Equal(1, fixture.Handler.RankingsRequests);
    }

    [Theory]
    [InlineData("zone", false)]
    [InlineData("encounter", false)]
    [InlineData("zone", true)]
    [InlineData("encounter", true)]
    public async Task NullOrMissingRankingsRemainNullWithoutCallerException(string kind, bool missing)
    {
        using var fixture = new Fixture(Envelope(kind, missing ? null : JValue.CreateNull()));
        Assert.Null(await fixture.CallProductionCaller(kind));
        Assert.Equal(1, fixture.Handler.RankingsRequests);
        Assert.DoesNotContain(fixture.Sink.Events, e => e.Exception != null || e.Level == LogEventLevel.Error);
    }

    [Theory]
    [InlineData("zone")]
    [InlineData("encounter")]
    public async Task ValidRankingsRemainTypedAndCachedByProductionCaller(string kind)
    {
        var payload = kind == "zone"
            ? JObject.Parse("""{"zone":1,"bestPerformanceAverage":91.5,"rankings":[{"totalKills":3,"encounter":{"id":1,"name":"Synthetic"}}]}""")
            : JObject.Parse("""{"totalKills":3,"ranks":[{"duration":123,"amount":45.5}]}""");
        using var fixture = new Fixture(Envelope(kind, payload));
        var result = await fixture.CallProductionCaller(kind);
        if (kind == "zone")
        {
            var rankings = Assert.IsType<WclV2ZoneRankingsData>(result);
            Assert.Equal(91.5, rankings.BestPerformanceAverage);
            Assert.Equal(3, Assert.Single(rankings.Rankings).TotalKills);
        }
        else
        {
            var rankings = Assert.IsType<WclV2EncounterRankingsData>(result);
            Assert.Equal(3, rankings.TotalKills);
            Assert.Equal(123, Assert.Single(rankings.Ranks).Duration);
        }
        Assert.Same(result, await fixture.CallProductionCaller(kind));
        Assert.Equal(1, fixture.Handler.RankingsRequests);
        Assert.DoesNotContain(fixture.Sink.Events, e => e.Exception != null || e.Level >= LogEventLevel.Warning);
    }

    [Theory]
    [InlineData("zone", "http")]
    [InlineData("encounter", "http")]
    [InlineData("zone", "cancel")]
    [InlineData("encounter", "cancel")]
    public async Task NonParsingFailuresKeepTheirExistingType(string kind, string failure)
    {
        using var fixture = new Fixture(Envelope(kind, new JObject()));
        fixture.Handler.Failure = failure;
        var error = await Record.ExceptionAsync(() => CallClient(fixture.Client, kind));
        if (failure == "http")
        {
            var http = Assert.IsType<HttpRequestException>(error);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, http.StatusCode);
        }
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(1, fixture.Handler.RankingsRequests);
    }

    private static JToken MalformedRanking(string kind, string shape) => shape switch
    {
        "scalar" => new JValue(Sentinel),
        "value" => kind == "zone"
            ? new JObject { ["rankings"] = new JArray(new JObject { ["totalKills"] = Sentinel }) }
            : new JObject { ["ranks"] = new JArray(new JObject { ["duration"] = Sentinel }) },
        "path" => new JObject { [kind == "zone" ? "rankings" : "ranks"] = new JObject { [Sentinel] = new JArray() } },
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static string Envelope(string kind, JToken payload)
    {
        var character = new JObject();
        if (payload != null) character[kind + "Rankings"] = payload;
        return new JObject { ["data"] = new JObject { ["characterData"] = new JObject { ["character"] = character } } }.ToString();
    }

    private static async Task<object> CallClient(WarcraftLogsV2Client client, string kind) => kind == "zone"
        ? await client.GetCharacterZoneRankingsAsync("Synthetic", "realm", "us", 1)
        : await client.GetCharacterEncounterRankingsAsync("Synthetic", "realm", "us", 1);

    private sealed class Fixture : IDisposable
    {
        public readonly OfflineHandler Handler;
        public readonly RenderedSink Sink = new();
        public readonly WarcraftLogsV2Client Client;
        private readonly Logger _logger;
        private readonly ILoggerFactory _factory;
        private readonly MemoryCache _memory = new(new MemoryCacheOptions());
        private readonly CharCommands _module;

        public Fixture(string body)
        {
            Handler = new OfflineHandler(body);
            _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(Sink).CreateLogger();
            _factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddSerilog(_logger));
            var http = new Mock<IHttpClientFactory>();
            http.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(Handler, false));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            { ["WclClientId"] = "offline", ["WclClientSecret"] = "offline" }).Build();
            Client = new WarcraftLogsV2Client(config, http.Object, _factory.CreateLogger<WarcraftLogsV2Client>());
            // No DB, Discord client, global logger, or real network is used by these caller paths.
            var scopes = Mock.Of<IServiceScopeFactory>(MockBehavior.Strict);
            var cache = new WowCacheService(_memory, scopes, NullLogger<WowCacheService>.Instance);
            _module = new CharCommands(scopes, _factory.CreateLogger<CharCommands>(), null, null, null, Client, null, cache, null, null);
        }

        public async Task<object> CallProductionCaller(string kind)
        {
            var name = kind == "zone" ? "FetchWclV2DataWithFiltersAsync" : "FetchCharacterEncounterRankingsAsync";
            var method = typeof(CharCommands).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var call = method.Invoke(_module, new object[]
            {
                new CharacterInfo { Name = "Synthetic", Realm = "realm", RealmSlug = "realm", Region = "us" }, 1, null, null
            });
            return kind == "zone" ? await (Task<WclV2ZoneRankingsData>)call : await (Task<WclV2EncounterRankingsData>)call;
        }

        public void Dispose()
        {
            _factory.Dispose();
            _logger.Dispose();
            _memory.Dispose();
            Handler.Dispose();
            Sink.Text.Dispose();
        }
    }

    private sealed class OfflineHandler(string body) : HttpMessageHandler
    {
        public int RankingsRequests;
        public string LastRankingsQuery;
        public string Failure;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("www.warcraftlogs.com", request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath == "/oauth/token")
                return Ok("""{"access_token":"offline-token","expires_in":3600}""");
            Assert.Equal("/api/v2/client", request.RequestUri.AbsolutePath);
            var query = (string)JObject.Parse(await request.Content.ReadAsStringAsync(token))["query"];
            if (query.Contains("rateLimitData", StringComparison.Ordinal))
                return Ok("""{"data":{"rateLimitData":{"limitPerHour":1000,"pointsSpentThisHour":1,"pointsResetIn":100}}}""");
            RankingsRequests++;
            LastRankingsQuery = query;
            if (Failure == "http") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            if (Failure == "cancel") throw new OperationCanceledException("Synthetic transport cancellation.", token);
            return Ok(body);
        }

        private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }

    private sealed class RenderedSink : ILogEventSink
    {
        public readonly StringWriter Text = new();
        public readonly List<LogEvent> Events = new();
        // NinjaBot.ConfigureServices uses the default Serilog console/file templates, including {Exception}.
        private readonly MessageTemplateTextFormatter _console = new("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        private readonly MessageTemplateTextFormatter _file = new("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        public void Emit(LogEvent e)
        {
            Events.Add(e);
            _console.Format(e, Text);
            _file.Format(e, Text);
        }
    }
}
