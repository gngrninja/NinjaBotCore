using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Retention;

namespace NinjaBotHelpers.Workers;

public sealed class ServerRetentionWorker(HelpersConfiguration config, DepartedServerPurger purger, ILogger<ServerRetentionWorker> logger, TimeProvider clock) : BackgroundService
{
    private long afterServerId;

    // One sequential bounded page per tick. In-memory keyset progress avoids a bad first page
    // starving later servers; restart resets the cursor, never the persisted departure clock.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!config.ServerRetention.Enabled) return 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var now = clock.GetUtcNow().UtcDateTime;
        var sweepId = Guid.NewGuid().ToString("N");
        var dryRun = config.ServerRetention.DryRun;
        var mode = dryRun ? "dry_run" : "delete";
        var cursorBefore = afterServerId;
        int? candidateCount = null;
        long? eligibleBacklogEstimate = null;
        DateTime? backlogObservedUtc = null;
        var reports = new List<RetentionServerReport>();
        var status = "failed";
        string? errorCategory = null;
        RetentionReports.Write(logger, new
        {
            version = 1, @event = "retention.sweep.started", sweepId, mode, startedUtc = now,
            eligibilityAsOfUtc = now, cursorBefore, batchSize = config.ServerRetention.BatchSize
        });
        try
        {
            ct.ThrowIfCancellationRequested();
            // Separate pre-page observation, not an atomic snapshot or a remaining-work count.
            eligibleBacklogEstimate = await purger.CountEligibleAsync(now, ct);
            backlogObservedUtc = clock.GetUtcNow().UtcDateTime;
            var candidates = await purger.FindCandidatesAsync(now, afterServerId, config.ServerRetention.BatchSize, ct);
            candidateCount = candidates.Count;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    await purger.PurgeAsync(candidate, now, dryRun, timeout.Token, report =>
                    {
                        reports.Add(report);
                        RetentionReports.Write(logger, report);
                    }, sweepId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* The purger emitted its safe partial result; continue the bounded page. */ }
                afterServerId = candidate.ServerId;
            }
            if (candidates.Count < config.ServerRetention.BatchSize) afterServerId = 0;
            status = "completed";
            return candidates.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "cancelled";
            errorCategory = "cancelled_or_timeout";
            throw;
        }
        catch (Exception ex)
        {
            errorCategory = RetentionReports.ErrorCategory(ex);
            throw;
        }
        finally
        {
            var unknownCommitCount = reports.Count(x => x.TransactionState == "commit_unknown");
            RetentionReports.Write(logger, new
            {
                version = 1, @event = "retention.sweep.finished", sweepId, mode, status,
                startedUtc = now, finishedUtc = clock.GetUtcNow().UtcDateTime, durationMs = timer.Elapsed.TotalMilliseconds,
                eligibilityAsOfUtc = now, backlogObservedUtc, eligibleBacklogEstimate,
                candidateCount, processedCount = reports.Count,
                unprocessedCount = candidateCount - reports.Count,
                deferredCount = reports.Count(x => x.Outcome is not ("deleted" or "dry_run")),
                outcomeCounts = reports.GroupBy(x => x.Outcome).ToDictionary(g => g.Key, g => g.Count()),
                committedDeletedRows = reports.Sum(x => x.CommittedDeletedRows ?? 0),
                plannedRows = reports.Sum(x => x.PlannedRows), unknownCommitCount,
                committedTotalsComplete = unknownCommitCount == 0,
                cursorBefore, cursorAfter = afterServerId, errorCategory
            });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.ServerRetention.Enabled)
        {
            logger.LogInformation("Server retention is disabled");
            return;
        }
        logger.LogInformation("Server retention enabled; DryRun={DryRun}, batch={Batch}, interval={Minutes}m, retention=30d",
            config.ServerRetention.DryRun, config.ServerRetention.BatchSize, config.ServerRetention.CheckIntervalMinutes);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.ServerRetention.InitialDelaySeconds), clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* RunOnceAsync already emitted the partial sweep report. */ }
                await Task.Delay(TimeSpan.FromMinutes(config.ServerRetention.CheckIntervalMinutes), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
