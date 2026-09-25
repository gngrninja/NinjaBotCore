using NinjaBotHelpers.Discord;
using Npgsql;

namespace NinjaBotHelpers.Retention;

public sealed record DepartedServerCandidate(long ServerId, DateTime LeftAt);
public enum PurgeResult { Ineligible, MembershipNotAbsent, DryRun, Deleted }

/// <summary>Explicit PostgreSQL deletion inventory; never infer ownership from user IDs or names.</summary>
public sealed class DepartedServerPurger(string connectionString, IDiscordMembershipClient membership)
{
    // Identifiers are code-owned constants, never configuration or caller input.
    public static IReadOnlyList<(string Table, string Column)> DirectTables { get; } = Array.AsReadOnly(new (string, string)[]
    {
        ("Notes", "ServerId"), ("ChannelOutputs", "ServerId"), ("Giphy", "ServerId"),
        ("ServerSettings", "ServerId"), ("WowGuildAssociations", "ServerId"),
        ("WowClassicGuild", "ServerId"), ("WowVanillaGuild", "ServerId"),
        ("WowResources", "ServerId"), ("LogMonitoring", "ServerId"), ("Warnings", "ServerId"),
        ("WowMChar", "ServerId"), ("WordList", "ServerId"), ("WclPosted", "ServerId"),
        ("WowCharAssociation", "ServerId"), ("Requests", "ServerID"),
        ("ServerGreetings", "DiscordGuildId"), ("ServerPollSettings", "DiscordGuildId"),
        ("ServerCraftSettings", "DiscordGuildId"), ("ServerPushGroupSettings", "DiscordGuildId"),
        ("VoiceWatcher", "DiscordGuildId"), ("ModerationWatcher", "DiscordGuildId"),
        ("CraftProfessionRoleMappings", "GuildId"), ("CraftTickets", "GuildId"),
        ("Polls", "GuildId"), ("PushGroups", "GuildId"),
        ("RealmWatchSubscriptions", "GuildId"), ("ApiUsageLogs", "GuildId")
    });

    public async Task<IReadOnlyList<DepartedServerCandidate>> FindCandidatesAsync(DateTime now, long afterServerId, int batchSize, CancellationToken ct)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required", nameof(now));
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT "ServerId", "LeftAt" FROM "DiscordServers"
            WHERE "ServerId" > @after AND NOT "BotPresent" AND "LeftAt" <= @cutoff
            ORDER BY "ServerId" LIMIT @batch
            """, connection);
        cmd.Parameters.AddWithValue("after", Math.Max(0, afterServerId));
        cmd.Parameters.AddWithValue("cutoff", now.AddDays(-30));
        cmd.Parameters.AddWithValue("batch", batchSize);
        var result = new List<DepartedServerCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetDateTime(1)));
        return result;
    }

    public async Task<long> CountEligibleAsync(DateTime now, CancellationToken ct)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required", nameof(now));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM "DiscordServers"
            WHERE "ServerId" > 0 AND NOT "BotPresent" AND "LeftAt" <= @cutoff
            """, connection);
        cmd.Parameters.AddWithValue("cutoff", now.AddDays(-30));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<PurgeResult> PurgeAsync(DepartedServerCandidate candidate, DateTime now, bool dryRun, CancellationToken ct,
        Action<RetentionServerReport>? reportCompleted = null, string? sweepId = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var report = new RetentionServerReport
        {
            SweepId = sweepId ?? Guid.NewGuid().ToString("N"), Mode = dryRun ? "dry_run" : "delete",
            ServerId = candidate.ServerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DepartedUtc = candidate.LeftAt,
            DueUtc = candidate.LeftAt <= DateTime.MaxValue.AddDays(-30) ? candidate.LeftAt.AddDays(30) : null,
            EligibilityAsOfUtc = now
        };
        try { return await PurgeCoreAsync(candidate, now, dryRun, ct, report); }
        catch (Exception ex)
        {
            report.ErrorCategory = RetentionReports.ErrorCategory(ex);
            if (report.TransactionState == "commit_unknown") report.Outcome = "commit_unknown";
            else if (report.DeletionState != "committed") report.Outcome = ex is OperationCanceledException ? "cancelled" : "failed";
            // Before COMMIT is attempted, no deletion can commit. Disposal aborts the transaction;
            // do not assert an acknowledged rollback on a broken connection.
            if (report.DeletionState == "in_progress") report.DeletionState = "not_committed";
            throw;
        }
        finally
        {
            report.DurationMs = timer.Elapsed.TotalMilliseconds;
            reportCompleted?.Invoke(report);
        }
    }

    private async Task<PurgeResult> PurgeCoreAsync(DepartedServerCandidate candidate, DateTime now, bool dryRun,
        CancellationToken ct, RetentionServerReport report)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required", nameof(now));
        if (candidate.ServerId <= 0 || candidate.LeftAt.Kind != DateTimeKind.Utc || candidate.LeftAt > now.AddDays(-30))
            return PurgeResult.Ineligible;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        report.TransactionState = "no_commit_attempted";
        async Task<int> Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("id", candidate.ServerId);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        await Execute("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s'");
        // Same transaction-scoped key as DiscordServerLifecycle; acquire BEFORE reading state.
        await Execute("SELECT pg_advisory_xact_lock(@id)");
        await using (var check = new NpgsqlCommand("""
            SELECT "LeftAt" FROM "DiscordServers"
            WHERE "ServerId" = @id AND NOT "BotPresent" AND "LeftAt" = @left AND "LeftAt" <= @cutoff
            FOR UPDATE
            """, connection, transaction))
        {
            check.Parameters.AddWithValue("id", candidate.ServerId);
            check.Parameters.AddWithValue("left", candidate.LeftAt);
            check.Parameters.AddWithValue("cutoff", now.AddDays(-30));
            if (await check.ExecuteScalarAsync(ct) is not DateTime) return PurgeResult.Ineligible;
        }
        async Task RecordPresence()
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE "DiscordServers" SET "BotPresent" = TRUE, "LeftAt" = NULL, "JoinedAt" = @now
                WHERE "ServerId" = @id
                """, connection, transaction);
            cmd.Parameters.AddWithValue("id", candidate.ServerId);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
            report.TransactionState = "commit_unknown";
            await transaction.CommitAsync(ct);
            report.TransactionState = "committed";
        }
        report.Stage = "initial_membership";
        var currentMembership = await membership.CheckAsync(candidate.ServerId, ct);
        if (currentMembership != GuildMembership.Absent)
        {
            report.Outcome = currentMembership == GuildMembership.Present ? "present" : "membership_unknown";
            if (!dryRun && currentMembership == GuildMembership.Present) await RecordPresence();
            return PurgeResult.MembershipNotAbsent;
        }

        // The same code-owned predicates drive counts and deletes, including child ownership.
        // Each SELECT returns only a count, never row payloads or unrelated child IDs.
        var inventory = new[]
        {
            (Table: "PollVotes", Predicate: "\"PollId\" IN (SELECT \"Id\" FROM \"Polls\" WHERE \"GuildId\" = @id)"),
            (Table: "PollOptions", Predicate: "\"PollId\" IN (SELECT \"Id\" FROM \"Polls\" WHERE \"GuildId\" = @id)"),
            (Table: "PushGroupSignups", Predicate: "\"PushGroupId\" IN (SELECT \"Id\" FROM \"PushGroups\" WHERE \"GuildId\" = @id)")
        }.Concat(DirectTables.Select(x => (Table: x.Table, Predicate: $"\"{x.Column}\" = @id")))
         .Append((Table: "DiscordServers", Predicate: "\"ServerId\" = @id"));
        var counts = new Dictionary<string, long>();
        if (dryRun)
        {
            report.Stage = "count";
            foreach (var (table, predicate) in inventory)
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\" WHERE {predicate}", connection, transaction);
                count.Parameters.AddWithValue("id", candidate.ServerId);
                counts[table] = (long)(await count.ExecuteScalarAsync(ct))!;
            }
            report.PlannedRowsByTable = counts;
            report.Outcome = "dry_run";
            return PurgeResult.DryRun;
        }
        await transaction.SaveAsync("before_delete", ct);
        report.Stage = "delete";
        report.DeletionState = "in_progress";
        foreach (var (table, predicate) in inventory)
            counts[table] = await Execute($"DELETE FROM \"{table}\" WHERE {predicate}");

        // Late rejoin/uncertainty cancels the entire transaction, including the tracking row.
        report.Stage = "precommit_membership";
        currentMembership = await membership.CheckAsync(candidate.ServerId, ct);
        if (currentMembership != GuildMembership.Absent)
        {
            report.Outcome = currentMembership == GuildMembership.Present ? "present" : "membership_unknown";
            if (currentMembership == GuildMembership.Present)
            {
                // Keep the initial locks, undo every delete, and cancel the obsolete clock.
                await transaction.RollbackAsync("before_delete", ct);
                report.DeletionState = "rolled_back";
                await RecordPresence();
            }
            else
            {
                await transaction.RollbackAsync(ct);
                report.TransactionState = "rolled_back";
                report.DeletionState = "rolled_back";
            }
            return PurgeResult.MembershipNotAbsent;
        }
        report.Stage = "commit";
        report.TransactionState = "commit_unknown";
        report.DeletionState = "unknown";
        report.CommittedRowsByTable = null;
        await transaction.CommitAsync(ct);
        report.TransactionState = "committed";
        report.DeletionState = "committed";
        report.CommittedRowsByTable = counts;
        report.Outcome = "deleted";
        return PurgeResult.Deleted;
    }
}
