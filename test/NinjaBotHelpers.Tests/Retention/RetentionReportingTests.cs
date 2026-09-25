using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.Retention;
using NinjaBotHelpers.Workers;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Xunit;
using Xunit.Abstractions;

namespace NinjaBotHelpers.Tests.Retention;

[Collection("ServerRetention")]
public class RetentionReportingTests(ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(Now); }
    private sealed class Sink : ILogEventSink
    {
        public readonly List<string> Lines = [];
        private readonly MessageTemplateTextFormatter formatter = new("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        public void Emit(LogEvent e) { using var w = new StringWriter(); formatter.Format(e, w); Lines.Add(w.ToString().TrimEnd()); }
    }
    private sealed class Harness : IDisposable
    {
        public readonly Sink Sink = new();
        private readonly Serilog.Core.Logger logger;
        private readonly ILoggerFactory factory;
        public readonly HelpersConfiguration Config;
        public readonly ServerRetentionWorker Worker;
        public Harness(string connection, Func<long, CancellationToken, Task<GuildMembership>> check, bool dry = false, int batch = 25)
        {
            Config = new HelpersConfiguration { ConnectionString = connection };
            Config.ServerRetention.Enabled = true; Config.ServerRetention.DryRun = dry; Config.ServerRetention.BatchSize = batch;
            logger = new LoggerConfiguration().WriteTo.Sink(Sink).CreateLogger();
            factory = LoggerFactory.Create(b => b.AddSerilog(logger));
            Worker = new(Config, new DepartedServerPurger(connection, new DepartedServerPurgerTests.Membership(check)), factory.CreateLogger<ServerRetentionWorker>(), new Clock());
        }
        public List<JsonElement> Reports() => Sink.Lines.Where(x => x.Contains("ServerRetentionReport ")).Select(x =>
        {
            // Same extraction contract as documented LogQL; parse the actual Serilog-rendered message.
            var match = Regex.Match(x, @"ServerRetentionReport (?<report>\{.*\})$");
            Assert.True(match.Success, x);
            using var doc = JsonDocument.Parse(match.Groups["report"].Value);
            return doc.RootElement.Clone();
        }).ToList();
        public JsonElement Event(string name) => Assert.Single(Reports(), x => x.GetProperty("event").GetString() == name);
        public void Dispose() { Worker.Dispose(); factory.Dispose(); logger.Dispose(); }
    }
    private void Examples(Harness h) { foreach (var line in h.Sink.Lines) { Assert.DoesNotContain("PRIVATE_SENTINEL", line); output.WriteLine(line); } }
    private static Task<GuildMembership> Absent(long _, CancellationToken ct) => Task.FromResult(GuildMembership.Absent);
    private static async Task Seed(RetentionDatabase f, params long[] ids)
    {
        await using var db = f.Context();
        foreach (var id in ids)
        {
            db.DiscordServers.Add(new DiscordServer { ServerId = id, BotPresent = false, LeftAt = Now.AddDays(-31), ServerName = "PRIVATE_SENTINEL" });
            db.Notes.Add(new Note { ServerId = id });
        }
        await db.SaveChangesAsync();
    }
    private static void ZeroCommitted(JsonElement e)
    {
        Assert.Equal(0, e.GetProperty("committedDeletedRows").GetInt64());
        Assert.All(e.GetProperty("committedRowsByTable").EnumerateObject(), p => Assert.Equal(0, p.Value.GetInt64()));
    }

    [RetentionPostgresFact]
    public async Task Counts_cover_inventory_children_tracking_and_only_target_server_for_plan_and_commit()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync(); await Seed(f, 101, 202);
        await using (var db = f.Context())
        {
            var owned = db.Model.GetEntityTypes().Select(e => new { Entity = e, Owner = e.GetProperties().SingleOrDefault(p => new[] { "ServerId", "ServerID", "GuildId", "DiscordGuildId" }.Contains(p.Name)) })
                .Where(x => x.Owner != null && x.Entity.ClrType != typeof(DiscordServer) && x.Entity.ClrType != typeof(Note));
            foreach (var x in owned)
                foreach (long id in new long[] { 101, 202 })
                {
                    var entity = Activator.CreateInstance(x.Entity.ClrType)!;
                    foreach (var p in x.Entity.GetProperties().Where(p => !p.IsNullable && !p.IsPrimaryKey()))
                    {
                        if (p.ClrType == typeof(string)) p.PropertyInfo!.SetValue(entity, "PRIVATE_SENTINEL");
                        if (p.ClrType == typeof(DateTime)) p.PropertyInfo!.SetValue(entity, Now);
                    }
                    x.Owner!.PropertyInfo!.SetValue(entity, id); db.Add(entity);
                }
            await db.SaveChangesAsync();
            foreach (var poll in await db.Polls.ToListAsync())
            {
                var option = new PollOption { PollId = poll.Id, OptionText = "PRIVATE_SENTINEL" }; db.PollOptions.Add(option); await db.SaveChangesAsync();
                db.PollVotes.Add(new PollVote { PollId = poll.Id, OptionId = option.Id, PollOption = option, UserId = 999, VotedAt = Now });
            }
            foreach (var group in await db.PushGroups.ToListAsync()) db.PushGroupSignups.Add(new PushGroupSignup { PushGroupId = group.Id, UserId = 999, SignedUpAt = Now });
            await db.SaveChangesAsync();
        }
        // A statement-level trigger rejects even a zero-row DELETE in dry-run; UPDATE repair is forbidden too.
        await f.Sql("""
            CREATE FUNCTION forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'PRIVATE_SENTINEL'; END $$;
            CREATE TRIGGER forbid_delete BEFORE DELETE ON "Notes" FOR EACH STATEMENT EXECUTE FUNCTION forbid_mutation();
            CREATE TRIGGER forbid_update BEFORE UPDATE ON "DiscordServers" FOR EACH STATEMENT EXECUTE FUNCTION forbid_mutation();
            """);
        using var dry = new Harness(f.ConnectionString, Absent, dry: true, batch: 1);
        Assert.Equal(1, await dry.Worker.RunOnceAsync(default));
        var plan = dry.Event("retention.server.finished");
        Assert.Equal("dry_run", plan.GetProperty("outcome").GetString()); ZeroCommitted(plan);
        var tables = DepartedServerPurger.DirectTables.Select(x => x.Table).Concat(new[] { "PollVotes", "PollOptions", "PushGroupSignups", "DiscordServers" }).Order().ToArray();
        Assert.Equal(tables, plan.GetProperty("plannedRowsByTable").EnumerateObject().Select(p => p.Name).Order());
        Assert.All(plan.GetProperty("plannedRowsByTable").EnumerateObject(), p => Assert.Equal(1, p.Value.GetInt64()));
        Assert.Equal(tables.Length, plan.GetProperty("plannedRows").GetInt64());
        Assert.Equal(Now.AddDays(-31), plan.GetProperty("departedUtc").GetDateTime());
        Assert.Equal(Now.AddDays(-1), plan.GetProperty("dueUtc").GetDateTime());
        Assert.True(plan.GetProperty("durationMs").GetDouble() >= 0);
        foreach (var table in tables) Assert.Equal(2, await f.Count(table));
        Assert.Equal(2, dry.Event("retention.sweep.finished").GetProperty("eligibleBacklogEstimate").GetInt64());
        await f.Sql("DROP TRIGGER forbid_delete ON \"Notes\"; DROP TRIGGER forbid_update ON \"DiscordServers\"");
        using var real = new Harness(f.ConnectionString, Absent, batch: 1);
        Assert.Equal(1, await real.Worker.RunOnceAsync(default));
        var committed = real.Event("retention.server.finished");
        Assert.Equal("deleted", committed.GetProperty("outcome").GetString());
        Assert.Equal(tables.Length, committed.GetProperty("committedDeletedRows").GetInt64());
        Assert.Equal(tables, committed.GetProperty("committedRowsByTable").EnumerateObject().Select(p => p.Name).Order());
        Assert.All(committed.GetProperty("committedRowsByTable").EnumerateObject(), p => Assert.Equal(1, p.Value.GetInt64()));
        Assert.Equal(0, committed.GetProperty("plannedRows").GetInt64());
        var finish = real.Event("retention.sweep.finished");
        Assert.Equal(tables.Length, finish.GetProperty("committedDeletedRows").GetInt64());
        Assert.Equal(2, finish.GetProperty("eligibleBacklogEstimate").GetInt64()); // as-of BEFORE page, not atomic remainder
        Assert.Equal(101, finish.GetProperty("cursorAfter").GetInt64());
        Assert.Equal(real.Event("retention.sweep.started").GetProperty("sweepId").GetString(), committed.GetProperty("sweepId").GetString());
        Assert.Equal(committed.GetProperty("sweepId").GetString(), finish.GetProperty("sweepId").GetString());
        Assert.True(Guid.TryParse(committed.GetProperty("operationId").GetString(), out _));
        foreach (var table in tables) Assert.Equal(1, await f.Count(table));
        Examples(dry); Examples(real);
    }

    [RetentionPostgresFact]
    public async Task Membership_reasons_and_stages_do_not_count_rolled_back_deletes()
    {
        foreach (bool late in new[] { false, true })
        foreach (var membership in new[] { GuildMembership.Present, GuildMembership.Unknown })
        {
            await using var f = new RetentionDatabase(); await f.InitializeAsync(); await Seed(f, 101);
            int calls = 0;
            using var h = new Harness(f.ConnectionString, (_, _) => Task.FromResult(late && ++calls == 1 ? GuildMembership.Absent : membership));
            await h.Worker.RunOnceAsync(default);
            var report = h.Event("retention.server.finished");
            Assert.Equal(membership == GuildMembership.Present ? "present" : "membership_unknown", report.GetProperty("outcome").GetString());
            Assert.Equal(late ? "precommit_membership" : "initial_membership", report.GetProperty("stage").GetString());
            Assert.Equal(late ? "rolled_back" : "not_started", report.GetProperty("deletionState").GetString()); ZeroCommitted(report);
            Assert.Equal(1, h.Event("retention.sweep.finished").GetProperty("deferredCount").GetInt32());
            Assert.Equal(1, await f.Count("Notes")); Assert.Equal(1, await f.Count("DiscordServers"));
            Examples(h);
        }
    }

    [RetentionPostgresFact]
    public async Task Stale_candidate_is_separate_from_membership_skip()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync(); await Seed(f, 101, 102);
        using var h = new Harness(f.ConnectionString, async (id, _) =>
        {
            if (id == 101) await f.Sql("UPDATE \"DiscordServers\" SET \"LeftAt\" = NULL WHERE \"ServerId\" = 102");
            return GuildMembership.Unknown;
        });
        await h.Worker.RunOnceAsync(default);
        var stale = Assert.Single(h.Reports(), x => x.GetProperty("event").GetString() == "retention.server.finished" && x.GetProperty("serverId").GetString() == "102");
        Assert.Equal("stale_ineligible", stale.GetProperty("outcome").GetString()); ZeroCommitted(stale);
        Assert.Equal(2, h.Event("retention.sweep.finished").GetProperty("processedCount").GetInt32()); Examples(h);
    }

    [RetentionPostgresFact]
    public async Task Empty_sweep_emits_correlated_heartbeat_with_zero_totals()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        using var h = new Harness(f.ConnectionString, Absent);
        Assert.Equal(0, await h.Worker.RunOnceAsync(default));
        var finish = h.Event("retention.sweep.finished"); Assert.Equal(2, h.Reports().Count);
        Assert.Equal("completed", finish.GetProperty("status").GetString());
        foreach (var key in new[] { "candidateCount", "processedCount", "deferredCount", "unprocessedCount", "committedDeletedRows", "plannedRows", "eligibleBacklogEstimate", "cursorAfter" }) Assert.Equal(0, finish.GetProperty(key).GetInt64());
        Examples(h);
    }

    [RetentionPostgresFact]
    public async Task Cancellation_after_deletes_reports_partial_sweep_without_claiming_completion()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync(); await Seed(f, 101, 102, 103);
        using var cts = new CancellationTokenSource(); int calls = 0;
        using var h = new Harness(f.ConnectionString, (id, ct) =>
        {
            if (id == 102 && ++calls == 2) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(GuildMembership.Absent);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.RunOnceAsync(cts.Token));
        var finish = h.Event("retention.sweep.finished");
        Assert.Equal("cancelled", finish.GetProperty("status").GetString());
        Assert.Equal(3, finish.GetProperty("candidateCount").GetInt32()); Assert.Equal(2, finish.GetProperty("processedCount").GetInt32());
        Assert.Equal(1, finish.GetProperty("unprocessedCount").GetInt32()); Assert.Equal(2, finish.GetProperty("committedDeletedRows").GetInt64());
        Assert.Equal(101, finish.GetProperty("cursorAfter").GetInt64());
        var cancelled = Assert.Single(h.Reports(), x => x.TryGetProperty("outcome", out var o) && o.GetString() == "cancelled");
        ZeroCommitted(cancelled); Assert.Equal("not_committed", cancelled.GetProperty("deletionState").GetString());
        Assert.Equal(2, await f.Count("Notes")); Examples(h);
    }

    [RetentionPostgresFact]
    public async Task DB_failure_is_private_reports_zero_commits_and_continues()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync(); await Seed(f, 101, 102);
        await f.Sql("""
            CREATE FUNCTION fail_retention() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF OLD."ServerId" = 101 THEN RAISE EXCEPTION 'PRIVATE_SENTINEL'; END IF; RETURN OLD; END $$;
            CREATE TRIGGER fail_retention BEFORE DELETE ON "DiscordServers" FOR EACH ROW EXECUTE FUNCTION fail_retention();
            """);
        using var h = new Harness(f.ConnectionString, Absent); await h.Worker.RunOnceAsync(default);
        var failure = Assert.Single(h.Reports(), x => x.TryGetProperty("outcome", out var o) && o.GetString() == "failed"); ZeroCommitted(failure);
        Assert.Equal("database", failure.GetProperty("errorCategory").GetString());
        var finish = h.Event("retention.sweep.finished"); Assert.Equal(1, finish.GetProperty("deferredCount").GetInt32()); Assert.Equal(2, finish.GetProperty("committedDeletedRows").GetInt64());
        Assert.Equal(1, await f.Count("Notes")); Examples(h);
    }

    [RetentionPostgresFact]
    public async Task Commit_exception_is_not_reported_as_confirmed_zero_or_rollback()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync(); await Seed(f, 101);
        await f.Sql("""
            CREATE FUNCTION fail_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'PRIVATE_SENTINEL'; END $$;
            CREATE CONSTRAINT TRIGGER fail_commit AFTER DELETE ON "DiscordServers" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_commit();
            """);
        using var h = new Harness(f.ConnectionString, Absent); await h.Worker.RunOnceAsync(default);
        var report = h.Event("retention.server.finished");
        Assert.Equal("commit_unknown", report.GetProperty("outcome").GetString());
        Assert.Equal("unknown", report.GetProperty("deletionState").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("committedDeletedRows").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("committedRowsByTable").ValueKind);
        Assert.Equal(1, h.Event("retention.sweep.finished").GetProperty("unknownCommitCount").GetInt32());
        Assert.False(h.Event("retention.sweep.finished").GetProperty("committedTotalsComplete").GetBoolean());
        Assert.Equal(1, await f.Count("Notes")); Examples(h);
    }

    [Fact]
    public async Task Discovery_failure_still_renders_parseable_partial_summary_and_no_exception_payload()
    {
        using var h = new Harness("PRIVATE_SENTINEL", Absent);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Worker.RunOnceAsync(default));
        var finish = h.Event("retention.sweep.finished"); Assert.Equal("failed", finish.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, finish.GetProperty("candidateCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, finish.GetProperty("eligibleBacklogEstimate").ValueKind);
        Examples(h);
    }
}
