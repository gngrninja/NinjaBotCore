using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests;

public sealed class DiscordServerTrackingOrderingTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now.AddTicks(7));
    }
    // Pause the actual batch at its first database await, after the guild snapshot.
    // No client login, gateway connection, REST call or real bot host is used.
    private sealed class PauseFirstOpen : DbConnectionInterceptor
    {
        private int opens;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref opens) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return result;
        }
    }

    private static SocketGuild Guild(DiscordSocketClient shard, ulong id)
    {
        var guild = (SocketGuild)Activator.CreateInstance(typeof(SocketGuild), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { shard, id }, null);
        typeof(SocketGuild).GetProperty(nameof(SocketGuild.Name)).SetValue(guild, "fixture " + id);
        typeof(SocketGuild).GetField("_members", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(guild, new ConcurrentDictionary<ulong, SocketGuildUser>());
        return guild;
    }
    private static Task Invoke(DiscordServerTrackingService service, string method, object argument) =>
        (Task)typeof(DiscordServerTrackingService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(service, new[] { argument });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task Snapshot_paused_before_writes_cannot_resurrect_a_later_leave(int path, int previousState)
    {
        var connectionString = $"Data Source=ordering-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using var ef = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<NinjaBotEntities>().UseSqlite(connectionString).UseInternalServiceProvider(ef);
        await using var verify = new NinjaBotEntities(options.Options);
        await verify.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "DiscordServers" (
                "ServerId" INTEGER PRIMARY KEY, "ServerName" TEXT, "OwnerId" INTEGER,
                "OwnerName" TEXT, "BotPresent" INTEGER NOT NULL, "JoinedAt" TEXT, "LeftAt" TEXT)
            """);
        if (previousState != 0)
            await DiscordServerLifecycle.MarkPresentAsync(verify, new DiscordServer { ServerId = 102 }, Now.AddDays(-50));
        if (previousState == 2)
            await DiscordServerLifecycle.MarkAbsentAsync(verify, 102, Now.AddDays(-40));
        await DiscordServerLifecycle.MarkPresentAsync(verify, new DiscordServer { ServerId = 101 }, Now.AddDays(-50));
        var pause = new PauseFirstOpen();
        using var shard = new Mock<DiscordSocketClient>().Object;
        var guilds = new[] { Guild(shard, 101), Guild(shard, 102) };
        var shardMock = Mock.Get(shard);
        shardMock.SetupGet(s => s.Guilds).Returns(() => guilds);
        var client = new Mock<DiscordShardedClient>(new DiscordSocketConfig { TotalShards = 1 });
        client.SetupGet(c => c.Guilds).Returns(() => guilds);
        client.Setup(c => c.GetGuild(It.IsAny<ulong>())).Returns((ulong id) => guilds.SingleOrDefault(g => g.Id == id));
        using var discord = client.Object;
        await using var services = new ServiceCollection()
            .AddSingleton(discord)
            .AddSingleton<TimeProvider>(new FrozenClock())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<DiscordServerTrackingService>>(NullLogger<DiscordServerTrackingService>.Instance)
            .AddDbContext<NinjaBotEntities>(o => o.UseSqlite(connectionString).UseInternalServiceProvider(ef).AddInterceptors(pause))
            .BuildServiceProvider();
        using var service = new DiscordServerTrackingService(services);
        var batch = path switch
        {
            1 => Invoke(service, "OnShardReady", shard),
            2 => Invoke(service, "OnJoinedDiscordServer", guilds[1]),
            _ => service.InitializeAsync()
        };
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var departed = guilds[1];
        guilds = new[] { guilds[0] };
        try { await Invoke(service, "OnLeftDiscordServer", departed).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { pause.Release.TrySetResult(); }
        await batch.WaitAsync(TimeSpan.FromSeconds(5));
        var row = await verify.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 102);
        Assert.False(row.BotPresent);
        Assert.Equal(Now.AddTicks(10), row.LeftAt);
        Assert.Equal(Now, row.JoinedAt);
        Assert.True((await verify.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 101)).BotPresent);
        await Invoke(service, "OnLeftDiscordServer", departed);
        row = await verify.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 102);
        Assert.Equal(Now.AddTicks(10), row.LeftAt);
        Assert.Equal(Now, row.JoinedAt);
        // A genuinely later presence still cancels the departure clock.
        guilds = new[] { guilds[0], departed };
        await Invoke(service, "OnJoinedDiscordServer", departed);
        row = await verify.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 102);
        Assert.True(row.BotPresent);
        Assert.Null(row.LeftAt);
        Assert.Equal(Now.AddTicks(30), row.JoinedAt);
        guilds = new[] { guilds[0] };
        await Invoke(service, "OnLeftDiscordServer", departed);
        row = await verify.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 102);
        Assert.False(row.BotPresent);
        Assert.Equal(Now.AddTicks(40), row.LeftAt);
        Assert.Equal(Now.AddTicks(30), row.JoinedAt);
    }
}
