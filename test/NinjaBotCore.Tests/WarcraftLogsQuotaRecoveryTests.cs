using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

public class WarcraftLogsQuotaRecoveryTests
{
    private const string Code = "AbCdEfGh12345678";
    private const string Healthy = """{"data":{"rateLimitData":{"limitPerHour":1000,"pointsSpentThisHour":1,"pointsResetIn":3600}}}""";
    private const string Report = """{"data":{"reportData":{"report":{"code":"AbCdEfGh12345678","revision":1,"fights":[]}}}}""";
    private const string Hostile = "PRIVATE-provider-report-or-token-do-not-log";
    private const int MaxBytes = 4 * 1024 * 1024;

    [Fact]
    public async Task ExhaustedQuotaRechecksAfterAdvertisedResetAndRecovers()
    {
        using var h = new Handler();
        var client = Client(h);
        await client.GetRaidRecapReportAsync(Code);
        Assert.Equal(1, h.Rates);
        h.Quota = Healthy;
        Assert.Equal(Code, (await client.GetRaidRecapReportAsync(Code)).Code);
        Assert.Equal(2, h.Rates);
        Assert.Equal(2, h.Reports);
    }

    [Fact]
    public async Task ObservedResetBlocksBeforeDeadlineAndRechecksAtDeadline()
    {
        using var h = new Handler { Quota = Quota(950, 120) };
        var clock = new Clock();
        var client = Client(h, clock);
        await client.GetRaidRecapReportAsync(Code);
        h.Quota = Healthy;
        clock.Advance(119);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Contains("1s", blocked.Message);
        Assert.Equal(1, h.Rates);
        Assert.Equal(1, h.Reports);
        clock.Advance(1);
        Assert.Equal(Code, (await client.GetRaidRecapReportAsync(Code)).Code);
        Assert.Equal(2, h.Rates);
        Assert.Equal(2, h.Reports);
    }

    [Fact]
    public async Task ConcurrentRecoveryCallersShareOneProbeBeforeAnyReportTraffic()
    {
        using var h = new Handler();
        var client = Client(h);
        await client.GetRaidRecapReportAsync(Code);
        var entered = Signal(); var release = Signal();
        h.BeforeResponse = async (route, token) =>
        {
            if (route != "rate") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        h.Quota = Healthy;
        var calls = Enumerable.Range(0, 6).Select(_ => client.GetRaidRecapReportAsync(Code)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, h.Rates);
            Assert.Equal(1, h.Reports);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(calls);
        Assert.Equal(2, h.Rates);
        Assert.Equal(7, h.Reports);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"rateLimitData\":null}}")]
    [InlineData("invalid-json")]
    [InlineData("{\"data\":{\"rateLimitData\":{}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsResetIn\":10}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":0}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":0,\"pointsSpentThisHour\":0,\"pointsResetIn\":10}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":-1,\"pointsResetIn\":10}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":\"NaN\",\"pointsResetIn\":10}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1e400,\"pointsSpentThisHour\":0,\"pointsResetIn\":10}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":0,\"pointsResetIn\":-1}}}")]
    [InlineData("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":0,\"pointsResetIn\":1.5}}}")]
    [InlineData("{\"errors\":[{\"message\":\"denied\"}],\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":0,\"pointsResetIn\":10}}}")]
    public async Task UnusableRecoveryResponseKeepsTrafficBlockedThenAllowsLaterHealthyProbe(string body)
    {
        using var h = new Handler();
        var clock = new Clock(); var client = Client(h, clock);
        await client.GetRaidRecapReportAsync(Code);
        h.Quota = body;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates);
        Assert.Equal(1, h.Reports);
        h.Quota = Healthy;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates); // No hot-loop after malformed/null/failed refresh.
        clock.Advance(30);
        Assert.Equal(Code, (await client.GetRaidRecapReportAsync(Code)).Code);
        Assert.Equal(3, h.Rates);
        Assert.Equal(2, h.Reports);
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(429, false)]
    [InlineData(503, true)]
    public async Task FailedRecoveryRespectsRetryAfterAndCanRecover(int status, bool httpDate)
    {
        using var h = new Handler();
        var clock = new Clock(); var client = Client(h, clock);
        await client.GetRaidRecapReportAsync(Code);
        h.QuotaStatus = (HttpStatusCode)status;
        h.QuotaRetry = httpDate ? new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(120)) : new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates);
        h.QuotaStatus = HttpStatusCode.OK; h.Quota = Healthy; h.QuotaRetry = null;
        clock.Advance(119);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates); Assert.Equal(1, h.Reports);
        clock.Advance(1);
        await client.GetRaidRecapReportAsync(Code);
        Assert.Equal(3, h.Rates); Assert.Equal(2, h.Reports);
    }

    [Fact]
    public async Task StillExhaustedProbeUsesItsNewObservedReset()
    {
        using var h = new Handler();
        var clock = new Clock(); var client = Client(h, clock);
        await client.GetRaidRecapReportAsync(Code);
        h.Quota = Quota(980, 60);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates);
        h.Quota = Healthy;
        clock.Advance(59);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates);
        clock.Advance(1);
        await client.GetRaidRecapReportAsync(Code);
        Assert.Equal(3, h.Rates);
    }

    [Fact]
    public async Task RecoveryProbeHasBoundedWaitWithoutCallerCancellation()
    {
        using var h = new Handler(); var client = Client(h);
        await client.GetRaidRecapReportAsync(Code);
        h.BeforeResponse = (route, token) => route == "rate" ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 0, 20);
        Assert.Equal(2, h.Rates); Assert.Equal(1, h.Reports);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("report")]
    [InlineData("rate")]
    public async Task CallerCancellationReachesTokenGraphQlAndQuota(string target)
    {
        using var h = new Handler { Quota = Healthy }; var client = Client(h);
        var entered = Signal(); var release = Signal();
        h.BeforeResponse = async (route, token) =>
        {
            if (route != target) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var cancel = new CancellationTokenSource();
        var call = Execute(client, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.Same(call, await Task.WhenAny(call, Task.Delay(1000)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationWhileWaitingForRecoveryGateDoesNotStartAnotherProbe()
    {
        using var h = new Handler(); var client = Client(h);
        await client.GetRaidRecapReportAsync(Code);
        var entered = Signal(); var release = Signal();
        h.Quota = Healthy;
        h.BeforeResponse = async (route, token) =>
        {
            if (route != "rate") return;
            entered.TrySetResult(); await release.Task.WaitAsync(token);
        };
        var first = Execute(client);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var waiting = Execute(client, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, h.Rates); Assert.Equal(1, h.Reports);
        }
        finally { release.TrySetResult(); }
        await first;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedChunkedOrCompressedGraphQlResponseIsBounded(bool compressed)
    {
        using var h = new Handler { Quota = Healthy };
        var oversized = Encoding.UTF8.GetBytes("{\"data\":{\"padding\":\"" + new string('x', MaxBytes + 1024) + "\"}}");
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(oversized);
            oversized = output.ToArray();
        }
        var stream = new CountedStream(oversized);
        h.ReportContent = () =>
        {
            var content = new StreamContent(stream);
            if (compressed) content.Headers.ContentEncoding.Add("gzip");
            return content;
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(Client(h)));
        Assert.Contains("response exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
        if (!compressed) Assert.InRange(stream.BytesRead, 1, MaxBytes + 1);
        Assert.True(stream.WasDisposed);
        Assert.Equal(1, h.Reports); Assert.Equal(0, h.Rates);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("GZIP")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("identity")]
    public async Task BoundedReaderAcceptsValidCompressedAndPlainPayloads(string encoding)
    {
        using var h = new Handler { Quota = Healthy };
        var bytes = Encoding.UTF8.GetBytes(Report);
        if (encoding != "identity")
        {
            using var output = new MemoryStream();
            using (Stream compressor = encoding.ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(output, CompressionMode.Compress, true),
                "deflate" => new ZLibStream(output, CompressionMode.Compress, true),
                _ => new BrotliStream(output, CompressionMode.Compress, true)
            }) compressor.Write(bytes);
            bytes = output.ToArray();
        }
        var stream = new CountedStream(bytes);
        h.ReportContent = () =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentEncoding.Add(encoding);
            return content;
        };
        Assert.Equal(Code, (await Client(h).GetRaidRecapReportAsync(Code)).Code);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task ExactlyMaximumUtf8BodyRemainsAccepted()
    {
        using var h = new Handler { Quota = Healthy };
        const string prefix = "{\"data\":{\"padding\":\"", suffix = "\"}}";
        var bytes = Encoding.UTF8.GetBytes(prefix + new string('x', MaxBytes - prefix.Length - suffix.Length) + suffix);
        Assert.Equal(MaxBytes, bytes.Length);
        var stream = new CountedStream(bytes);
        h.ReportContent = () => new StreamContent(stream);
        Assert.NotNull((await Execute(Client(h))).Data);
        Assert.Equal(MaxBytes, stream.BytesRead);
        Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("graphql")]
    [InlineData("malformed")]
    [InlineData("token-http")]
    [InlineData("token-malformed")]
    public async Task HostileUpstreamErrorsNeverEnterLogs(string mode)
    {
        using var h = new Handler { Quota = Healthy };
        if (mode == "http") { h.ReportStatus = HttpStatusCode.BadRequest; h.ReportBody = Hostile; }
        if (mode == "graphql") h.ReportBody = "{\"errors\":[{\"message\":\"" + Hostile + "\"}],\"data\":null}";
        if (mode == "malformed") h.ReportBody = Hostile;
        if (mode == "token-http") { h.TokenStatus = HttpStatusCode.BadRequest; h.TokenBody = Hostile; }
        if (mode == "token-malformed") h.TokenBody = "{\"" + Hostile + "\":";
        var log = new CaptureLogger(); var client = Client(h, logger: log);
        var error = await Record.ExceptionAsync(() => client.GetRaidRecapReportAsync(Code));
        Assert.NotNull(error);
        Assert.DoesNotContain(log.Messages, m => m.Contains(Hostile, StringComparison.Ordinal));
        Assert.DoesNotContain(Hostile, error.ToString());
        Assert.NotEmpty(log.Messages);
    }

    [Fact]
    public async Task CallerCancellationInterruptsStreamReadAndDisposesResponse()
    {
        using var h = new Handler { Quota = Healthy };
        var entered = Signal(); var release = Signal();
        var stream = new CountedStream(Encoding.UTF8.GetBytes(Report))
        {
            BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        h.ReportContent = () => new StreamContent(stream);
        using var cancel = new CancellationTokenSource();
        var call = Execute(Client(h), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.Same(call, await Task.WhenAny(call, Task.Delay(1000)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.True(stream.WasDisposed);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task UnchangedExhaustionWithZeroResetDoesNotHotLoopProbes()
    {
        using var h = new Handler(); var clock = new Clock(); var client = Client(h, clock);
        await client.GetRaidRecapReportAsync(Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(2, h.Rates);
        clock.Advance(30); h.Quota = Healthy;
        await client.GetRaidRecapReportAsync(Code);
        Assert.Equal(3, h.Rates);
    }

    [Fact]
    public async Task HttpThrottleStopsSubsequentReportTrafficUntilRetryAfter()
    {
        using var h = new Handler { ReportStatus = HttpStatusCode.TooManyRequests, Quota = Healthy };
        h.ReportRetry = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var clock = new Clock(); var client = Client(h, clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRaidRecapReportAsync(Code));
        h.ReportStatus = HttpStatusCode.OK; h.ReportRetry = null;
        clock.Advance(59);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Equal(1, h.Reports);
        clock.Advance(1);
        await client.GetRaidRecapReportAsync(Code);
        Assert.Equal(2, h.Reports);
    }

    [Fact]
    public async Task HttpBackoffMessageDoesNotUseHealthyBudgetsFutureReset()
    {
        using var h = new Handler { Quota = Healthy };
        var clock = new Clock(); var client = Client(h, clock);
        await client.GetRaidRecapReportAsync(Code);
        h.ReportStatus = HttpStatusCode.TooManyRequests;
        h.ReportRetry = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRaidRecapReportAsync(Code));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRaidRecapReportAsync(Code));
        Assert.Contains("Retry in 60s", error.Message);
        Assert.Equal(2, h.Reports);
    }

    [Fact]
    public async Task LegacyHttpClientTimeoutStillBoundsStreamBodyWithoutCallerToken()
    {
        using var h = new Handler { Quota = Healthy };
        var entered = Signal(); var release = Signal();
        var stream = new CountedStream(Encoding.UTF8.GetBytes(Report))
        {
            BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        h.ReportContent = () => new StreamContent(stream);
        var client = Client(h, httpTimeout: TimeSpan.FromMilliseconds(100));
        var call = client.GetRaidRecapReportAsync(Code);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(call, await Task.WhenAny(call, Task.Delay(2000)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.True(stream.WasDisposed);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("zone")]
    [InlineData("encounter")]
    [InlineData("batch")]
    public async Task LegacyNestedParsingDoesNotLogProviderPayloadOrExceptionData(string query)
    {
        using var h = new Handler { Quota = Healthy };
        var log = new CaptureLogger(); var client = Client(h, logger: log);
        if (query == "batch")
        {
            h.ReportBody = "{\"data\":{\"guild_0\":{\"reports\":{\"data\":[{\"code\":\"synthetic\",\"startTime\":\"" + Hostile + "\"}]}}}}";
            await client.GetBatchGuildReportsAsync(new() { ("Synthetic", "realm", "us", "key") });
        }
        else
        {
            h.ReportBody = "{\"data\":{\"characterData\":{\"character\":{\"" + query + "Rankings\":\"" + Hostile + "\"}}}}";
            var error = await Record.ExceptionAsync(() => query == "zone"
                ? (Task)client.GetCharacterZoneRankingsAsync("Synthetic", "realm", "us", 1)
                : client.GetCharacterEncounterRankingsAsync("Synthetic", "realm", "us", 1));
            Assert.NotNull(error);
        }
        Assert.DoesNotContain(log.Messages, m => m.Contains(Hostile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyGraphQlErrorPathNeverEntersLogs()
    {
        using var h = new Handler { Quota = Healthy };
        h.ReportBody = JObject.FromObject(new
        {
            data = new { },
            errors = new[] { new { message = "No guild exists for this name/server/region", path = new[] { Hostile } } }
        }).ToString();
        var log = new CaptureLogger(); var client = Client(h, logger: log);
        await client.GetBatchGuildReportsAsync(new() { ("Synthetic", "realm", "us", "key") });
        Assert.DoesNotContain(log.Messages, m => m.Contains(Hostile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyGuildReportsStillUseSharedTransportAndRecover()
    {
        using var h = new Handler { ReportBody = "{\"data\":{\"reportData\":{\"reports\":{\"data\":[]}}}}" };
        var client = Client(h);
        Assert.Empty(await client.GetGuildReportsAsync("Synthetic", "realm", "us"));
        h.Quota = Healthy;
        Assert.Empty(await client.GetGuildReportsAsync("Synthetic", "realm", "us"));
        Assert.Equal(2, h.Rates); Assert.Equal(2, h.Reports);
    }

    private static string Quota(int spent, int reset) => JObject.FromObject(new
    { data = new { rateLimitData = new { limitPerHour = 1000, pointsSpentThisHour = spent, pointsResetIn = reset } } }).ToString();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private static WarcraftLogsV2Client Client(Handler handler, TimeProvider clock = null, ILogger<WarcraftLogsV2Client> logger = null, TimeSpan? httpTimeout = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false) { Timeout = httpTimeout ?? TimeSpan.FromSeconds(100) });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        { ["WclClientId"] = "offline", ["WclClientSecret"] = "offline" }).Build();
        return clock == null
            ? new WarcraftLogsV2Client(config, factory.Object, logger ?? NullLogger<WarcraftLogsV2Client>.Instance)
            : new WarcraftLogsV2Client(config, factory.Object, logger ?? NullLogger<WarcraftLogsV2Client>.Instance, clock);
    }

    // Exercise the shared private cancellation seam without changing any public/legacy signatures.
    private static Task<GraphQLResponse<JObject>> Execute(WarcraftLogsV2Client client, CancellationToken token = default) =>
        (Task<GraphQLResponse<JObject>>)typeof(WarcraftLogsV2Client)
            .GetMethod("ExecuteGraphQLAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            .MakeGenericMethod(typeof(JObject)).Invoke(client, new object[] { "query { reportData { report { code } } }", null, WowGameVersion.Retail, token });

    // All traffic is synthetic HTTP; no provider credentials or live services.
    private sealed class Handler : HttpMessageHandler
    {
        public int Rates, Reports;
        public string Quota = WarcraftLogsQuotaRecoveryTests.Quota(960, 0);
        public string ReportBody = Report;
        public string TokenBody = """{"access_token":"offline-token","expires_in":3600}""";
        public HttpStatusCode QuotaStatus = HttpStatusCode.OK, ReportStatus = HttpStatusCode.OK, TokenStatus = HttpStatusCode.OK;
        public RetryConditionHeaderValue QuotaRetry, ReportRetry;
        public Func<string, CancellationToken, Task> BeforeResponse;
        public Func<HttpContent> ReportContent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string route;
            if (request.RequestUri.AbsolutePath.Contains("oauth")) route = "token";
            else
            {
                Assert.Equal("https://www.warcraftlogs.com/api/v2/client", request.RequestUri.AbsoluteUri);
                route = (await request.Content.ReadAsStringAsync(token)).Contains("rateLimitData") ? "rate" : "report";
                if (route == "rate") Interlocked.Increment(ref Rates); else Interlocked.Increment(ref Reports);
            }
            if (BeforeResponse != null) await BeforeResponse(route, token);
            var response = new HttpResponseMessage(route == "token" ? TokenStatus : route == "rate" ? QuotaStatus : ReportStatus)
            {
                ReasonPhrase = Hostile,
                Content = route == "report" && ReportContent != null ? ReportContent() : new StringContent(route == "token" ? TokenBody : route == "rate" ? Quota : ReportBody)
            };
            if (route == "rate") response.Headers.RetryAfter = QuotaRetry;
            if (route == "report") response.Headers.RetryAfter = ReportRetry;
            return response;
        }
    }

    private sealed class CaptureLogger : ILogger<WarcraftLogsV2Client>
    {
        public readonly ConcurrentQueue<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Messages.Enqueue(formatter(state, exception) + exception);
    }

    // Unknown Content-Length models chunked/decompressed input rather than a prebuffered string.
    private sealed class CountedStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int BytesRead;
        public bool WasDisposed;
        public Func<CancellationToken, Task> BeforeRead;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count); BytesRead += read; return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BeforeRead != null) await BeforeRead(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer.Span); BytesRead += read; return read;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
