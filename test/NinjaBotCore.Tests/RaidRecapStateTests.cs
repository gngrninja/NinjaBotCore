using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

public class RaidRecapStateTests
{
    [Fact]
    public async Task CacheSingleFlightsAndSeparatesScopesAndExpires()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new RaidRecapCache(() => now, 2);
        var gate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<object> Load() { Interlocked.Increment(ref calls); return gate.Task; }
        var first = cache.GetAsync("report:r1:f2:damage", Load, TimeSpan.FromSeconds(30));
        var second = cache.GetAsync("report:r1:f2:damage", Load, TimeSpan.FromSeconds(30));
        gate.SetResult("ok");
        Assert.Equal(new object[] { "ok", "ok" }, await Task.WhenAll(first, second));
        Assert.Equal(1, calls);
        await cache.GetAsync("report:r2:f2:damage", Load, TimeSpan.FromSeconds(30));
        Assert.Equal(2, calls);
        now += TimeSpan.FromSeconds(31);
        await cache.GetAsync("report:r1:f2:damage", Load, TimeSpan.FromSeconds(30));
        Assert.Equal(3, calls);
        for (var i=0;i<10;i++) await cache.GetAsync("key"+i, Load, TimeSpan.FromSeconds(30));
        Assert.InRange(cache.Count, 0, 2);
    }

    [Fact]
    public async Task CacheFailureDoesNotPoisonRetry()
    {
        var cache = new RaidRecapCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("k", () => Task.FromException<object>(new InvalidOperationException()), TimeSpan.FromMinutes(1)));
        Assert.Equal("ok", await cache.GetAsync("k", () => Task.FromResult<object>("ok"), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task SessionsRejectWrongActorOriginStaleGenerationAndExpiry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var sessions = new RaidRecapSessions(() => now, 2);
        var s = sessions.Create(1, 2, 3);
        var called = false;
        Task Act(RaidRecapSession _) { called=true; return Task.CompletedTask; }
        Assert.False(await sessions.RunAsync(s.Token, 9, 2, 3, 0, Act));
        Assert.False(await sessions.RunAsync(s.Token, 1, 9, 3, 0, Act));
        Assert.False(await sessions.RunAsync(s.Token, 1, 2, 9, 0, Act));
        Assert.False(called);
        Assert.True(await sessions.RunAsync(s.Token, 1, 2, 3, 0, Act));
        Assert.False(await sessions.RunAsync(s.Token, 1, 2, 3, 0, Act));
        now += TimeSpan.FromMinutes(11);
        Assert.False(await sessions.RunAsync(s.Token, 1, 2, 3, 1, Act));
        for (var i=0;i<5;i++) sessions.Create(1,2,3);
        Assert.InRange(sessions.Count, 0, 2);
    }

    [Fact]
    public async Task OverlappingTransitionsCannotPublishOutOfOrderOrDoubleShare()
    {
        var sessions = new RaidRecapSessions();
        var s = sessions.Create(1,2,3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = 0;
        var first = sessions.RunAsync(s.Token,1,2,3,0,async state =>
        {
            entered.SetResult(); await release.Task;
            Assert.True(state.TryBeginShare()); publications++;
        });
        await entered.Task;
        var second = sessions.RunAsync(s.Token,1,2,3,0,state => { publications++; return Task.CompletedTask; });
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(1, publications);
        Assert.False(s.TryBeginShare());
    }
}
