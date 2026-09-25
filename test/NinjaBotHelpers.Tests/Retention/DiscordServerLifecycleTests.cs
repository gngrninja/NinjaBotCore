using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Database;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.Retention;
using Npgsql;
using Xunit;

namespace NinjaBotHelpers.Tests.Retention;

[Collection("ServerRetention")]
public class DiscordServerLifecycleTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static DiscordServer Server() => new() { ServerId = 101, ServerName = "fixture" };

    [RetentionPostgresFact]
    public async Task Duplicate_leave_preserves_first_timestamp_and_rejoin_starts_new_period()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-40));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-31));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-1));
        Assert.Equal(Now.AddDays(-31), (await db.DiscordServers.AsNoTracking().SingleAsync()).LeftAt);
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        var joined = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(joined.BotPresent); Assert.Null(joined.LeftAt);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(1));
        Assert.Equal(Now.AddDays(1), (await db.DiscordServers.AsNoTracking().SingleAsync()).LeftAt);
    }

    [RetentionPostgresFact]
    public async Task Stale_snapshot_cannot_overwrite_new_presence_generation()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-40));
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now, Now.AddDays(-40));
        Assert.True((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
    }

    [RetentionPostgresFact]
    public async Task Observation_order_not_write_order_controls_presence_even_at_provider_precision()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddTicks(10));
        // An older snapshot arriving after the leave must not resurrect the row.
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddTicks(1));
        Assert.False((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
        // Equal provider-precision times conservatively favor absence.
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddTicks(19));
        Assert.False((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddTicks(20));
        Assert.True((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
        // The inverse ordering must not allow a delayed old leave to erase a rejoin.
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddTicks(10));
        Assert.True((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
        await DiscordServerLifecycle.MarkPresentAsync(db, new DiscordServer { ServerId = 101, ServerName = "obsolete" }, Now);
        var row = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.Equal("fixture", row.ServerName);
        Assert.Equal(Now.AddTicks(20), row.JoinedAt);
    }

    [RetentionPostgresFact]
    public async Task Leave_before_first_snapshot_write_fences_that_snapshot()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddTicks(10));
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        var row = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.False(row.BotPresent);
        Assert.Equal(Now.AddTicks(10), row.LeftAt);
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddTicks(20));
        Assert.True((await db.DiscordServers.AsNoTracking().SingleAsync()).BotPresent);
    }

    [RetentionPostgresFact]
    public async Task Observed_rejoin_then_leave_repairs_already_absent_row_before_delayed_presence_write()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-50));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-40));
        await DiscordServerLifecycle.MarkPresentAsync(db, new DiscordServer { ServerId = 202 }, Now.AddDays(-50));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 202, Now.AddDays(-40));
        db.Notes.AddRange(new Note { ServerId = 101 }, new Note { ServerId = 202 });
        await db.SaveChangesAsync();
        // Captured startup presence has not reached the database when the newer leave commits.
        var leftAt = Now.AddTicks(10);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, leftAt, precedingPresenceAt: Now);
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        var row = await db.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 101);
        Assert.False(row.BotPresent);
        Assert.Equal(Now, row.JoinedAt); // Actual observed presence, not a fabricated duplicate join.
        Assert.Equal(leftAt, row.LeftAt);
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(1));
        // Reusing the same presence evidence must not extend the departure either.
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(2), precedingPresenceAt: Now);
        row = await db.DiscordServers.AsNoTracking().SingleAsync(s => s.ServerId == 101);
        Assert.Equal(leftAt, row.LeftAt);
        Assert.Equal(Now, row.JoinedAt);
        var membership = new DepartedServerPurgerTests.Membership((_, _) => Task.FromResult(GuildMembership.Absent));
        var purger = new DepartedServerPurger(f.ConnectionString, membership);
        Assert.DoesNotContain(await purger.FindCandidatesAsync(leftAt.AddDays(30).AddTicks(-10), 0, 100, default), c => c.ServerId == 101);
        Assert.Equal(PurgeResult.Ineligible, await purger.PurgeAsync(new(101, Now.AddDays(-40)), Now.AddDays(1), false, default));
        Assert.Equal(0, membership.Calls);
        Assert.Equal(2, await f.Count("Notes"));
        Assert.Contains(await purger.FindCandidatesAsync(leftAt.AddDays(30), 0, 100, default), c => c.ServerId == 101 && c.LeftAt == leftAt);
        Assert.Equal(PurgeResult.Deleted, await purger.PurgeAsync(new(101, leftAt), leftAt.AddDays(30), false, default));
        Assert.Equal(202, (await db.Notes.AsNoTracking().SingleAsync()).ServerId);
        var other = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.Equal(202, other.ServerId);
        Assert.Equal(Now.AddDays(-40), other.LeftAt);
    }

    [RetentionPostgresFact]
    public async Task Duplicate_without_presence_evidence_does_not_invent_join_or_erase_later_real_rejoin()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-50));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-40));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now);
        var row = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.Equal(Now.AddDays(-50), row.JoinedAt);
        Assert.Equal(Now.AddDays(-40), row.LeftAt);
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddTicks(20));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddTicks(10), precedingPresenceAt: Now);
        row = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(row.BotPresent);
        Assert.Equal(Now.AddTicks(20), row.JoinedAt);
        Assert.Null(row.LeftAt);
    }

    [RetentionPostgresFact]
    public async Task Rejoin_committed_before_purge_lock_preserves_data()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-40));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-31));
        db.Notes.Add(new Note { ServerId = 101 }); await db.SaveChangesAsync();
        await using var c = new NpgsqlConnection(f.ConnectionString); await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(101)", c, tx)) await cmd.ExecuteNonQueryAsync();
        // A lifecycle writer holding the lock commits presence while the purge is queued.
        var m = new DepartedServerPurgerTests.Membership((_, _) => throw new Exception("HTTP must not run"));
        var purge = new DepartedServerPurger(f.ConnectionString, m).PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default);
        await f.WaitForAdvisoryWaiter();
        await using (var cmd = new NpgsqlCommand("UPDATE \"DiscordServers\" SET \"BotPresent\"=true, \"LeftAt\"=NULL WHERE \"ServerId\"=101", c, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        Assert.Equal(PurgeResult.Ineligible, await purge);
        Assert.Equal(1, await f.Count("Notes"));
    }

    [RetentionPostgresFact]
    public async Task Rejoin_queued_during_purge_recreates_tracking_row_after_purge_without_stale_update()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-40));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-31));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = new DepartedServerPurgerTests.Membership(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return GuildMembership.Absent; });
        var purge = new DepartedServerPurger(f.ConnectionString, m).PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var join = DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now);
        await f.WaitForAdvisoryWaiter();
        Assert.False(join.IsCompleted);
        release.SetResult();
        Assert.Equal(PurgeResult.Deleted, await purge);
        await join;
        var row = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(row.BotPresent); Assert.Null(row.LeftAt);
    }

    [RetentionPostgresFact]
    public async Task Two_workers_cannot_double_delete_same_departure()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using var db = f.Context();
        await DiscordServerLifecycle.MarkPresentAsync(db, Server(), Now.AddDays(-40));
        await DiscordServerLifecycle.MarkAbsentAsync(db, 101, Now.AddDays(-31));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = new DepartedServerPurgerTests.Membership(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return GuildMembership.Absent; });
        var p = new DepartedServerPurger(f.ConnectionString, m);
        var first = p.PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = p.PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default);
        await f.WaitForAdvisoryWaiter();
        release.SetResult();
        Assert.Equal(PurgeResult.Deleted, await first);
        Assert.Equal(PurgeResult.Ineligible, await second);
        Assert.Equal(2, m.Calls);
    }
}
