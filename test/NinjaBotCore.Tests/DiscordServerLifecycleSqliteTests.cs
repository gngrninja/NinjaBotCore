using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using Xunit;

namespace NinjaBotCore.Tests;

// Real SQLite, independent of Helpers and its PostgreSQL-only retention worker.
public sealed class DiscordServerLifecycleSqliteTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly ServiceProvider ef = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static DiscordServer Server() => new() { ServerId = 101, ServerName = "sqlite fixture" };
    private async Task<NinjaBotEntities> OpenAsync()
    {
        await connection.OpenAsync();
        var db = new NinjaBotEntities(new DbContextOptionsBuilder<NinjaBotEntities>()
            .UseSqlite(connection).UseInternalServiceProvider(ef).Options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "DiscordServers" (
                "ServerId" INTEGER PRIMARY KEY, "ServerName" TEXT, "OwnerId" INTEGER,
                "OwnerName" TEXT, "BotPresent" INTEGER NOT NULL, "JoinedAt" TEXT, "LeftAt" TEXT)
            """);
        return db;
    }

    [Fact]
    public async Task Join_duplicate_leave_rejoin_and_next_leave_work_without_helpers()
    {
        await using var db = await OpenAsync();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-40));
        var joined = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(joined.BotPresent);
        Assert.Equal(Now.AddDays(-40), joined.JoinedAt);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-31));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-1));
        Assert.Equal(Now.AddDays(-31), (await db.DiscordServers.AsNoTracking().SingleAsync()).LeftAt);
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        joined = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(joined.BotPresent);
        Assert.Null(joined.LeftAt);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(1));
        Assert.Equal(Now.AddDays(1), (await db.DiscordServers.AsNoTracking().SingleAsync()).LeftAt);
    }

    [Fact]
    public async Task Stale_reconciliation_cannot_overwrite_a_new_presence()
    {
        await using var db = await OpenAsync();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-1));
        var snapshot = await db.DiscordServers.AsNoTracking().SingleAsync();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now, snapshot.JoinedAt);
        Assert.True((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
        var current = await db.DiscordServers.AsNoTracking().SingleAsync();
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now, current.JoinedAt);
        Assert.False((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        await ef.DisposeAsync();
    }
}
