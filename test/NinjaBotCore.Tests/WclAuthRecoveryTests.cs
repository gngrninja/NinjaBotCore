#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;
using NinjaBot.WarcraftLogs;

namespace NinjaBotCore.Tests;

public class WclAuthRecoveryTests
{
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { Timeout = TimeSpan.FromMilliseconds(handler.TimeoutMs) };
    }
    private static object Create(Handler h, Clock clock, Logs logs) => new WarcraftLogsV2Client(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["WclClientId"]="synthetic-id", ["WclClientSecret"]="SECRET-SENTINEL" }).Build(),
        new Factory(h), logs, clock);
    private static async Task Call(object client, bool quota = false, CancellationToken ct = default)
    {
        var method = typeof(WarcraftLogsV2Client).GetMethod(quota ? "GetRateLimitDataAsync" : "ExecuteGraphQLInternalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = quota ? new object[] { ct } : new object[] { "query { reportData { reports { data { code } } } }", null!, WowGameVersion.Retail, ct };
        if (!quota) method = method.MakeGenericMethod(typeof(JObject));
        await (Task)method.Invoke(client, args)!;
    }
    private sealed class Logs : ILogger<WarcraftLogsV2Client>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = new();
        public Sink Rendered { get; } = new();
        public sealed class Sink : ILogEventSink
        {
            public List<string> Lines { get; } = new();
            public void Emit(LogEvent e)
            {
                using var writer = new StringWriter();
                new MessageTemplateTextFormatter("[{Level:u3}] {Message:lj}{NewLine}{Exception}").Format(e, writer);
                lock(Lines) Lines.Add(writer.ToString());
            }
        }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            lock(Entries) Entries.Add((level, formatter(state, ex)));
            using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(Rendered).CreateLogger();
            using var provider = new SerilogLoggerProvider(serilog);
            provider.CreateLogger("WCL-test").Log(level, id, state, ex, formatter);
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Tokens, Api;
        public int RetrySeconds;
        public int TimeoutMs = 45000;
        public string? ApiBody;
        public string OAuthBody = "";
        public int OAuthStatus = 200, ApiStatus = 200;
        public bool RejectA, RejectAll;
        public Func<string, CancellationToken, Task>? Before;
        public List<string> Bearers { get; } = new();
        public HashSet<HttpRequestMessage> Requests { get; } = new();
        public HashSet<HttpContent> Contents { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var oauth = req.RequestUri!.AbsolutePath.Contains("oauth");
            var number = oauth ? Interlocked.Increment(ref Tokens) : Interlocked.Increment(ref Api);
            var bearer = req.Headers.Authorization!.Parameter!;
            lock (Requests) { Assert.True(Requests.Add(req)); Assert.True(Contents.Add(req.Content!)); if (!oauth) Bearers.Add(bearer); }
            if (Before != null) await Before(oauth ? "oauth" : bearer, ct);
            var status = oauth ? OAuthStatus : RejectAll || RejectA && bearer == "TOKEN-A" ? 401 : ApiStatus;
            var body = oauth ? OAuthBody != "" ? OAuthBody : $"{{\"access_token\":\"TOKEN-{(number == 1 ? "A" : "B")}\",\"expires_in\":31104000}}" :
                status != 200 ? "SECRET-SENTINEL" : ApiBody ?? "{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":1,\"pointsResetIn\":3600}}}";
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body), ReasonPhrase = "SECRET-SENTINEL" };
            if (RetrySeconds > 0) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(RetrySeconds));
            return response;
        }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void AuthUnavailable(Exception? error) { Assert.NotNull(error); Assert.Equal("WclAuthenticationUnavailableException", error!.GetType().Name); Assert.DoesNotContain("SECRET-SENTINEL", error.ToString()); }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Rejected360DayCachedTokenRefreshesAndReplaysOnce(bool quota)
    {
        using var h = new Handler(); var logs = new Logs(); var client = Create(h, new Clock(), logs);
        await Call(client, quota); h.RejectA = true;
        await Call(client, quota);
        Assert.Equal(2, h.Tokens); Assert.Equal(3, h.Api);
        Assert.Equal(new[] { "TOKEN-A", "TOKEN-A", "TOKEN-B" }, h.Bearers);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReplacementRejectionSharesCooldownThenRecovers(bool quota)
    {
        using var h = new Handler { RejectAll = true }; var clock = new Clock(); var logs = new Logs(); var client = Create(h, clock, logs);
        AuthUnavailable(await Record.ExceptionAsync(() => Call(client, quota)));
        for(var i=0;i<12;i++) AuthUnavailable(await Record.ExceptionAsync(() => Call(client, quota)));
        Assert.Equal(2,h.Tokens); Assert.Equal(2,h.Api);
        Assert.Single(logs.Entries, e=>e.Text.Contains("state=unavailable"));
        Assert.DoesNotContain(logs.Entries,e=>e.Text.Contains("state=recovered"));
        clock.Advance(30); h.RejectAll=false; await Call(client,quota);
        Assert.Equal(3,h.Tokens); Assert.Equal(3,h.Api);
        Assert.Single(logs.Entries, e=>e.Text.Contains("state=recovered"));
        Assert.DoesNotContain(logs.Entries,e=>e.Text.Contains("SECRET-SENTINEL") || e.Text.Contains("TOKEN-"));
    }
    [Theory] [InlineData(401, "")] [InlineData(500, "")] [InlineData(200, "{}")] [InlineData(200, "invalid SECRET-SENTINEL")] [InlineData(200, "{\"access_token\":\"TOKEN-A\",\"expires_in\":0}")]
    public async Task OAuthFailureIsBoundedSanitizedAndLaterRecovers(int status,string body)
    {
        using var h = new Handler { OAuthStatus=status, OAuthBody=body }; var clock = new Clock(); var logs=new Logs(); var client=Create(h,clock,logs);
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));
        Assert.Equal(1,h.Tokens); Assert.Equal(0,h.Api);
        clock.Advance(30); h.OAuthStatus=200; h.OAuthBody=""; await Call(client);
        Assert.Equal(2,h.Tokens); Assert.Equal(1,h.Api);
        Assert.Single(logs.Entries, e=>e.Text.Contains("state=recovered"));
        Assert.DoesNotContain(logs.Entries,e=>e.Text.Contains("SECRET-SENTINEL"));
    }
    [Fact]
    public async Task ColdAndExpiredFanInUseSingleFullyInitializedToken()
    {
        using var h = new Handler(); var clock=new Clock(); var client=Create(h,clock,new Logs());
        foreach (var expired in new[] { false,true })
        {
            if(expired) clock.Advance(31104000);
            var entered=Signal(); var release=Signal();
            h.Before=async (route,ct)=> { if(route=="oauth") {entered.TrySetResult(); await release.Task.WaitAsync(ct);} };
            var tasks=Enumerable.Range(0,8).Select(_=>Call(client)).ToArray();
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(expired?2:1,h.Tokens); }
            finally {release.TrySetResult();}
            await Task.WhenAll(tasks);
            Assert.Equal(expired?2:1,h.Tokens);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExpiredGenerationSuccessCannotRecoverFailedRefresh(bool quota)
    {
        using var h = new Handler();
        var clock = new Clock(); var logs = new Logs(); var client = Create(h, clock, logs);
        WclAuthHealthSnapshot Health() => (WclAuthHealthSnapshot)client.GetType().GetProperty("AuthHealth")!.GetValue(client)!;
        await Call(client, quota);
        var oldArrived = Signal(); var releaseOld = Signal();
        var refreshArrived = Signal(); var releaseRefresh = Signal();
        var freshArrived = Signal(); var releaseFresh = Signal();
        var owned = new List<Task<Exception?>>();
        h.Before = async (route, ct) =>
        {
            if (route == "TOKEN-A") { oldArrived.TrySetResult(); await releaseOld.Task.WaitAsync(ct); }
            if (route == "oauth") { refreshArrived.TrySetResult(); await releaseRefresh.Task.WaitAsync(ct); }
            if (route == "TOKEN-B") { freshArrived.TrySetResult(); await releaseFresh.Task.WaitAsync(ct); }
        };
        try
        {
            var old = Record.ExceptionAsync(() => Call(client, quota)); owned.Add(old);
            await oldArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(31104000); h.OAuthStatus = 500;
            var refresh = Record.ExceptionAsync(() => Call(client, quota)); owned.Add(refresh);
            await refreshArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseRefresh.TrySetResult();
            AuthUnavailable(await refresh);
            var failed = Health();
            Assert.True(failed.Unavailable);
            Assert.Equal(clock.Now.AddSeconds(30), failed.NextRetryAt);
            Assert.Equal(clock.Now, failed.LastRefreshFailure);
            Assert.Equal(1, failed.ConsecutiveAuthFailures);
            Assert.Equal(2, h.Tokens); Assert.Equal(2, h.Api);

            releaseOld.TrySetResult();
            Assert.Null(await old);
            Assert.True(Health().Unavailable);
            Assert.Equal(failed, Health()); // Includes retry deadline, failures and both success clocks.
            Assert.DoesNotContain(logs.Rendered.Lines, l => l.Contains("state=recovered"));
            clock.Advance(29);
            AuthUnavailable(await Record.ExceptionAsync(() => Call(client, !quota)));
            Assert.Equal(failed, Health());
            Assert.Equal(2, h.Tokens); Assert.Equal(2, h.Api);

            clock.Advance(1); h.OAuthStatus = 200;
            var fresh = Record.ExceptionAsync(() => Call(client, quota)); owned.Add(fresh);
            await freshArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Health().Unavailable); // OAuth issuance alone is not recovery.
            Assert.Equal(failed.LastApiSuccess, Health().LastApiSuccess);
            Assert.DoesNotContain(logs.Rendered.Lines, l => l.Contains("state=recovered"));
            releaseFresh.TrySetResult();
            Assert.Null(await fresh);
            Assert.False(Health().Unavailable);
            Assert.Null(Health().NextRetryAt);
            Assert.Equal(0, Health().ConsecutiveAuthFailures);
            Assert.Equal(clock.Now, Health().LastApiSuccess);
            Assert.Equal(clock.Now, Health().LastRefreshSuccess);
            Assert.Equal(failed.LastRefreshFailure, Health().LastRefreshFailure);
            Assert.Equal(3, h.Tokens); Assert.Equal(3, h.Api);
            await Call(client, !quota);
            Assert.Equal(3, h.Tokens); Assert.Equal(4, h.Api);
            Assert.Single(logs.Rendered.Lines, l => l.Contains("[INF] [WCL Auth] state=recovered reason=api_success"));
        }
        finally
        {
            releaseOld.TrySetResult(); releaseRefresh.TrySetResult(); releaseFresh.TrySetResult();
            await Task.WhenAll(owned); // Record.ExceptionAsync observes failures without masking the assertion.
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExpiredGenerationSuccessCannotUpdateHealthDuringRefresh(bool quota)
    {
        using var h = new Handler();
        var clock = new Clock(); var logs = new Logs(); var client = Create(h, clock, logs);
        WclAuthHealthSnapshot Health() => (WclAuthHealthSnapshot)client.GetType().GetProperty("AuthHealth")!.GetValue(client)!;
        await Call(client, quota);
        var original = Health();
        var oldArrived = Signal(); var releaseOld = Signal();
        var refreshArrived = Signal(); var releaseRefresh = Signal();
        var freshArrived = Signal(); var releaseFresh = Signal();
        var owned = new List<Task<Exception?>>();
        h.Before = async (route, ct) =>
        {
            if (route == "TOKEN-A") { oldArrived.TrySetResult(); await releaseOld.Task.WaitAsync(ct); }
            if (route == "oauth") { refreshArrived.TrySetResult(); await releaseRefresh.Task.WaitAsync(ct); }
            if (route == "TOKEN-B") { freshArrived.TrySetResult(); await releaseFresh.Task.WaitAsync(ct); }
        };
        try
        {
            var old = Record.ExceptionAsync(() => Call(client, quota)); owned.Add(old);
            await oldArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(31104000);
            var refresh = Record.ExceptionAsync(() => Call(client, quota)); owned.Add(refresh);
            await refreshArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseOld.TrySetResult();
            Assert.Null(await old);
            Assert.Equal(original, Health());
            Assert.Equal(2, h.Tokens); Assert.Equal(2, h.Api);
            releaseRefresh.TrySetResult();
            await freshArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(original.LastApiSuccess, Health().LastApiSuccess);
            Assert.Equal(clock.Now, Health().LastRefreshSuccess);
            releaseFresh.TrySetResult();
            Assert.Null(await refresh);
            Assert.Equal(clock.Now, Health().LastApiSuccess);
            Assert.False(Health().Unavailable);
            Assert.Equal(2, h.Tokens); Assert.Equal(3, h.Api);
            Assert.DoesNotContain(logs.Rendered.Lines, l => l.Contains("state=recovered"));
        }
        finally
        {
            releaseOld.TrySetResult(); releaseRefresh.TrySetResult(); releaseFresh.TrySetResult();
            await Task.WhenAll(owned);
        }
    }

    [Fact]
    public async Task LateOldRejectionCannotEvictReplacement()
    {
        using var h=new Handler(); var client=Create(h,new Clock(),new Logs()); await Call(client);
        h.RejectA=true; var entered=Signal();var release=Signal(); var oldCalls=0;
        h.Before=async (route,ct)=>{if(route=="TOKEN-A" && Interlocked.Increment(ref oldCalls)==1){entered.TrySetResult();await release.Task.WaitAsync(ct);}};
        var late=Call(client);
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Call(client))); Assert.Equal(2,h.Tokens);}
        finally {release.TrySetResult();}
        await late; Assert.Equal(2,h.Tokens); await Call(client);Assert.Equal(2,h.Tokens);
    }
    [Fact]
    public async Task CancellationAtRefreshAndWaitingGateDoesNotCreateOutage()
    {
        using var h=new Handler();var logs=new Logs();var client=Create(h,new Clock(),logs);
        var entered=Signal();var release=Signal(); h.Before=async(route,ct)=>{if(route=="oauth"){entered.TrySetResult();await release.Task.WaitAsync(ct);}};
        using var cancel=new CancellationTokenSource(); var first=Call(client,ct:cancel.Token);
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));using var waiting=new CancellationTokenSource();var second=Call(client,ct:waiting.Token);waiting.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>second);cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);}
        finally {release.TrySetResult();}
        h.Before=null;await Call(client);
        Assert.DoesNotContain(logs.Entries,e=>e.Text.Contains("state=unavailable"));
    }
    [Fact]
    public async Task RenderedOutageRemindersAreBoundedAndIssuingATokenIsNotRecovery()
    {
        using var h=new Handler {OAuthStatus=401}; var clock=new Clock();var logs=new Logs();var client=Create(h,clock,logs);
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));
        clock.Advance(30);h.OAuthStatus=200;h.ApiStatus=403;
        await Assert.ThrowsAsync<HttpRequestException>(()=>Call(client));
        Assert.Single(logs.Rendered.Lines, l=>l.Contains("state=unavailable"));
        Assert.DoesNotContain(logs.Rendered.Lines,l=>l.Contains("state=recovered"));
        h.RejectAll=true;
        clock.Advance(270);AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));
        Assert.Equal(2,logs.Rendered.Lines.Count(l=>l.Contains("[ERR] [WCL Auth] state=unavailable reason=")));
        for(var i=0;i<4;i++) AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));
        Assert.Equal(2,logs.Rendered.Lines.Count(l=>l.Contains("state=unavailable")));
        clock.Advance(30); h.RejectAll=false;h.ApiStatus=200;await Call(client);await Call(client);
        Assert.Single(logs.Rendered.Lines,l=>l.Contains("[INF] [WCL Auth] state=recovered reason=api_success"));
        Assert.DoesNotContain(logs.Rendered.Lines,l=>l.Contains("SECRET-SENTINEL") || l.Contains("TOKEN-"));
        var health=(WclAuthHealthSnapshot)client.GetType().GetProperty("AuthHealth")!.GetValue(client)!;
        Assert.False(health.Unavailable);Assert.Equal(0,health.ConsecutiveAuthFailures);Assert.Equal(clock.Now,health.LastApiSuccess);
        Assert.NotNull(health.LastRefreshFailure);Assert.NotNull(health.LastRefreshSuccess);
    }
    [Fact]
    public async Task OAuthRetryAfterExtendsSharedCooldownWithoutHotLoop()
    {
        using var h=new Handler {OAuthStatus=429, RetrySeconds=120};var clock=new Clock();var client=Create(h,clock,new Logs());
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));
        clock.Advance(119);AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));Assert.Equal(1,h.Tokens);
        clock.Advance(1);h.OAuthStatus=200;h.RetrySeconds=0;await Call(client);Assert.Equal(2,h.Tokens);
    }
    [Fact]
    public async Task OAuthDeadlineIsAnOutageButCallerCancellationIsNot()
    {
        using var h=new Handler {TimeoutMs=80};var logs=new Logs();var clock=new Clock();var client=Create(h,clock,logs);
        h.Before=(route,ct)=>Task.Delay(Timeout.Infinite,ct);
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client).WaitAsync(TimeSpan.FromSeconds(2))));
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client)));Assert.Equal(1,h.Tokens);
        clock.Advance(30);h.Before=null;await Call(client);
    }
    [Fact]
    public async Task OversizedApiBodyRemainsBoundedAndDoesNotRefresh()
    {
        using var h=new Handler {ApiBody="{\"data\":{\"padding\":\""+new string('x',4*1024*1024)+"\"}}"};var client=Create(h,new Clock(),new Logs());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Call(client));Assert.Equal(1,h.Tokens);
    }
    [Fact]
    public async Task AllInFlightOld401sShareOneRefresh()
    {
        using var h=new Handler();var client=Create(h,new Clock(),new Logs());await Call(client);h.RejectA=true;
        var arrived=Signal();var release=Signal();var old=0;
        h.Before=async(route,ct)=>{if(route=="TOKEN-A"){if(Interlocked.Increment(ref old)==8)arrived.TrySetResult();await release.Task.WaitAsync(ct);}};
        var tasks=Enumerable.Range(0,8).Select(_=>Call(client)).ToArray();
        try{await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(1,h.Tokens);}
        finally{release.TrySetResult();}
        await Task.WhenAll(tasks);Assert.Equal(2,h.Tokens);Assert.Equal(17,h.Api);
    }
    [Fact]
    public async Task GraphQLErrorsNeverRefreshOrExposeProviderText()
    {
        using var h=new Handler {ApiBody="{\"errors\":[{\"message\":\"SECRET-SENTINEL\"}],\"data\":null}"};
        var logs=new Logs();var client=Create(h,new Clock(),logs);await Call(client);await Call(client);
        Assert.Equal(1,h.Tokens);Assert.Equal(2,h.Api);
        Assert.DoesNotContain(logs.Rendered.Lines,l=>l.Contains("SECRET-SENTINEL") || l.Contains("state=unavailable"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ApiCancellationPropagatesWithoutCooldownAndNextCallWorks(bool quota)
    {
        using var h=new Handler();var logs=new Logs();var client=Create(h,new Clock(),logs);var entered=Signal();
        h.Before=async(route,ct)=>{if(route!="oauth"){entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}};
        using var cancel=new CancellationTokenSource();var task=Call(client,quota,cancel.Token);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));}finally{cancel.Cancel();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        h.Before=null;await Call(client,quota);Assert.Equal(1,h.Tokens);
        Assert.DoesNotContain(logs.Rendered.Lines,l=>l.Contains("state=unavailable"));
    }
    [Fact]
    public async Task AuthReplaySharesOneOverallDeadline()
    {
        using var h=new Handler {TimeoutMs=400, RejectA=true};var logs=new Logs();var client=Create(h,new Clock(),logs);
        h.Before=(route,ct)=>Task.Delay(150,ct);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        AuthUnavailable(await Record.ExceptionAsync(()=>Call(client).WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.InRange(elapsed.Elapsed.TotalMilliseconds,300,850);
        Assert.Equal(2,h.Tokens);Assert.Equal(1,h.Api);
    }
    [Theory] [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task OtherStatusesNeverRefreshAuthentication(int status)
    {
        using var h=new Handler {ApiStatus=status};var client=Create(h,new Clock(),new Logs());
        var error=await Record.ExceptionAsync(()=>Call(client)); Assert.IsAssignableFrom<HttpRequestException>(error);Assert.Equal((HttpStatusCode)status,((HttpRequestException)error!).StatusCode);Assert.Equal(1,h.Tokens);
    }
}
