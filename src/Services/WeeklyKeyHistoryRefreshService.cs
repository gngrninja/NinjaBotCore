#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Background sync of every linked main character's weekly M+ runs from Raider.IO into
    /// WeeklyKeyHistory (best key + run count per dungeon per reset window). Powers
    /// /keys leaderboard, mybest, the vault guild overview and the hub footer.
    /// Also prunes keystone-board entries from previous reset windows.
    /// </summary>
    public class WeeklyKeyHistoryRefreshService : IHostedService, IDisposable
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(4);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan PerCharacterFreshness = TimeSpan.FromHours(2);
        private static readonly TimeSpan PerRequestSpacing = TimeSpan.FromMilliseconds(150);

        private readonly ILogger<WeeklyKeyHistoryRefreshService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RaiderIOApi _rio;

        private Timer? _timer;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _tickGate = new(1, 1);
        private bool _disposed;

        public WeeklyKeyHistoryRefreshService(
            ILogger<WeeklyKeyHistoryRefreshService> logger,
            IServiceScopeFactory scopeFactory,
            RaiderIOApi rio)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _rio = rio;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("WeeklyKeyHistoryRefreshService starting (every {Interval})", RefreshInterval);
            _timer = new Timer(_ => _ = TickAsync(_cts.Token), null, InitialDelay, RefreshInterval);
            return Task.CompletedTask;
        }

        private async Task TickAsync(CancellationToken ct)
        {
            if (_disposed || ct.IsCancellationRequested) return;
            if (!await _tickGate.WaitAsync(0, ct)) return; // previous tick still running
            try
            {
                await RefreshAllAsync(ct);
                await PruneStaleKeystonesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Weekly key history refresh pass failed");
            }
            finally
            {
                _tickGate.Release();
            }
        }

        private async Task RefreshAllAsync(CancellationToken ct)
        {
            List<WowCharAssociation> mains;
            Dictionary<long, DateTime> lastRefreshed;

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var weekFloor = MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow);

                mains = (await db.WowCharAssociation
                        .Where(a => a.IsMain && a.UserId != null && a.CharName != null)
                        .ToListAsync(ct))
                    .GroupBy(a => a.UserId!.Value)
                    .Select(g => g.OrderByDescending(a => a.TimeSet ?? DateTime.MinValue).First())
                    .ToList();

                lastRefreshed = (await db.WeeklyKeyHistory
                        .Where(h => h.WeekStartUtc > weekFloor)
                        .GroupBy(h => h.UserId)
                        .Select(g => new { g.Key, Latest = g.Max(h => h.LastRefreshedAt) })
                        .ToListAsync(ct))
                    .ToDictionary(x => x.Key, x => x.Latest);
            }

            var refreshed = 0;
            var failed = 0;
            var now = DateTime.UtcNow;

            foreach (var main in mains)
            {
                if (ct.IsCancellationRequested) return;
                var userId = main.UserId!.Value;
                if (lastRefreshed.TryGetValue(userId, out var last) && now - last < PerCharacterFreshness)
                {
                    continue;
                }

                try
                {
                    var realmSlug = RealmSlugFor(main);
                    var region = string.IsNullOrWhiteSpace(main.WowRegion) ? "us" : main.WowRegion!.ToLowerInvariant();
                    var info = await _rio.GetCharMythicPlusInfoAsync(main.CharName, realmSlug, region, ct);
                    await UpsertRunsAsync(userId, main, region, info?.MythicPlusWeeklyHighestLevelRuns, ct);
                    refreshed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug(ex, "Weekly run fetch failed for {Char} ({UserId})", main.CharName, main.UserId);
                }

                await Task.Delay(PerRequestSpacing, ct);
            }

            if (refreshed > 0 || failed > 0)
            {
                _logger.LogInformation("Weekly key history refresh: {Refreshed} characters updated, {Failed} failed, {Total} linked mains",
                    refreshed, failed, mains.Count);
            }
        }

        private async Task UpsertRunsAsync(long userId, WowCharAssociation main, string region, RaiderIOModels.MythicPlusRun[]? runs, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var weekStart = MythicPlusWeekly.WeekStartUtc(region, now);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var existing = await db.WeeklyKeyHistory
                .Where(h => h.UserId == userId && h.WeekStartUtc == weekStart)
                .ToListAsync(ct);

            var byDungeon = (runs ?? Array.Empty<RaiderIOModels.MythicPlusRun>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Dungeon))
                .GroupBy(r => SlugForRun(r), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in byDungeon)
            {
                var best = (int)group.Max(r => r.MythicLevel);
                var count = group.Count();
                var row = existing.FirstOrDefault(h => string.Equals(h.DungeonSlug, group.Key, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                {
                    db.WeeklyKeyHistory.Add(new WeeklyKeyHistory
                    {
                        UserId = userId,
                        WowCharacterName = main.CharName ?? string.Empty,
                        WowCharacterRealm = main.WowRealm ?? string.Empty,
                        DungeonSlug = group.Key,
                        WeekStartUtc = weekStart,
                        BestKeyLevel = best,
                        RunCount = count,
                        LastRefreshedAt = now,
                    });
                }
                else
                {
                    row.BestKeyLevel = Math.Max(row.BestKeyLevel, best);
                    row.RunCount = count;
                    row.LastRefreshedAt = now;
                }
            }

            // Character had zero runs: still stamp freshness so we don't refetch them every tick.
            if (byDungeon.Count == 0)
            {
                foreach (var row in existing) row.LastRefreshedAt = now;
                if (existing.Count == 0)
                {
                    db.WeeklyKeyHistory.Add(new WeeklyKeyHistory
                    {
                        UserId = userId,
                        WowCharacterName = main.CharName ?? string.Empty,
                        WowCharacterRealm = main.WowRealm ?? string.Empty,
                        DungeonSlug = PushGroupConstants.NoRunsSentinelSlug,
                        WeekStartUtc = weekStart,
                        BestKeyLevel = 0,
                        RunCount = 0,
                        LastRefreshedAt = now,
                    });
                }
            }

            await db.SaveChangesAsync(ct);
        }

        private async Task PruneStaleKeystonesAsync(CancellationToken ct)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var floor = MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow);
            var stale = await db.UserKeystones.Where(k => k.WeekStartUtc <= floor).ToListAsync(ct);
            if (stale.Count == 0) return;

            db.UserKeystones.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Pruned {Count} keystone(s) from previous reset windows", stale.Count);
        }

        private static string RealmSlugFor(WowCharAssociation main) =>
            WowRealmSlug.From(!string.IsNullOrWhiteSpace(main.LocalRealmSlug) ? main.LocalRealmSlug : main.WowRealm);

        private static string SlugForRun(RaiderIOModels.MythicPlusRun run)
        {
            var match = MythicPlusRotation.Current.FirstOrDefault(d =>
                string.Equals(d.Name, run.Dungeon, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(run.ShortName) && string.Equals(d.ShortName, run.ShortName, StringComparison.OrdinalIgnoreCase)));
            return match?.Slug ?? WowRealmSlug.From(run.Dungeon);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("WeeklyKeyHistoryRefreshService stopping");
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Cancel before disposing so an in-flight tick observes cancellation instead of
            // hitting disposed scopes/semaphores during provider teardown.
            try { _cts.Cancel(); } catch { /* already disposed */ }
            _timer?.Dispose();
            _cts?.Dispose();
            _tickGate?.Dispose();
        }
    }
}
