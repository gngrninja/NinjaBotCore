using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Keeps <see cref="MythicPlusRotation"/> current by periodically pulling the active
    /// season's dungeon pool from Raider.IO's static-data endpoint and caching it in the DB.
    /// On startup it loads the DB cache immediately (instant + survives Raider.IO outages),
    /// then refreshes from the API and on a fixed cadence thereafter. The expansion id is
    /// read from config key "RaiderIO:ExpansionId" (default 11 = Midnight); it only needs
    /// bumping once per expansion — new seasons within an expansion are picked up automatically.
    /// </summary>
    public class MythicPlusDungeonService : IHostedService, IDisposable
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

        private readonly ILogger<MythicPlusDungeonService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IRaiderIOApi _rio;
        private readonly int _expansionId;

        private Timer _timer;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _tickGate = new(1, 1);
        private bool _loadedFromCache;
        private bool _disposed;

        public MythicPlusDungeonService(
            ILogger<MythicPlusDungeonService> logger,
            IServiceScopeFactory scopeFactory,
            IRaiderIOApi rio,
            IConfigurationRoot config)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _rio = rio;
            _expansionId = int.TryParse(config["RaiderIO:ExpansionId"], out var e) ? e : 11;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MythicPlusDungeonService starting (expansion_id={ExpansionId})", _expansionId);
            _timer = new Timer(_ => _ = TickAsync(_cts.Token), null, InitialDelay, RefreshInterval);
            return Task.CompletedTask;
        }

        private async Task TickAsync(CancellationToken ct)
        {
            if (_disposed || ct.IsCancellationRequested) return;
            if (!await _tickGate.WaitAsync(0, ct)) return; // previous tick still running
            try
            {
                // Populate from the DB cache once (fast + a fallback if the API is unreachable),
                // then overwrite with live data.
                if (!_loadedFromCache)
                {
                    await LoadFromCacheAsync(ct);
                    _loadedFromCache = true;
                }
                await RefreshFromApiAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "M+ dungeon pool refresh failed — keeping existing pool");
            }
            finally
            {
                _tickGate.Release();
            }
        }

        private async Task LoadFromCacheAsync(CancellationToken ct)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var cached = await db.MythicPlusDungeonCache.OrderBy(d => d.Name).ToListAsync(ct);
            if (cached.Count == 0) return;

            MythicPlusRotation.SetCurrent(cached
                .Select(c => new MythicPlusRotation.Dungeon(c.Slug, c.Name, c.ShortName))
                .ToList());
            _logger.LogInformation("Loaded {Count} M+ dungeons from cache (season {Season})",
                cached.Count, cached[0].SeasonSlug);
        }

        private async Task RefreshFromApiAsync(CancellationToken ct)
        {
            var data = await _rio.GetMythicPlusStaticDataAsync(_expansionId, ct);
            var seasons = data?.Seasons;
            if (seasons == null || seasons.Count == 0)
            {
                _logger.LogWarning("Raider.IO static-data returned no seasons for expansion {ExpansionId}", _expansionId);
                return;
            }

            var active = SelectActiveSeason(seasons, DateTimeOffset.UtcNow) ?? seasons[0];

            var dungeons = (active.Dungeons ?? new List<RaiderIOModels.MythicPlusStaticDungeon>())
                .Where(d => !string.IsNullOrWhiteSpace(d.Slug) && !string.IsNullOrWhiteSpace(d.Name))
                .Select(d => new MythicPlusRotation.Dungeon(d.Slug, d.Name, d.ShortName ?? string.Empty))
                .ToList();

            if (dungeons.Count == 0)
            {
                _logger.LogWarning("Active season {Season} has no dungeons — keeping existing pool", active.Slug);
                return;
            }

            MythicPlusRotation.SetCurrent(dungeons);

            await PersistAsync(dungeons, active.Slug ?? string.Empty, ct);
        }

        private async Task PersistAsync(IReadOnlyList<MythicPlusRotation.Dungeon> dungeons, string seasonSlug, CancellationToken ct)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var existing = await db.MythicPlusDungeonCache.ToListAsync(ct);

            // Skip the write when nothing changed — avoids churning the table every cycle.
            var unchanged = existing.Count == dungeons.Count
                && existing.All(e => e.SeasonSlug == seasonSlug)
                && existing.Select(e => e.Slug).OrderBy(x => x)
                    .SequenceEqual(dungeons.Select(d => d.Slug).OrderBy(x => x));
            if (unchanged)
            {
                _logger.LogDebug("M+ dungeon pool unchanged (season {Season})", seasonSlug);
                return;
            }

            // Two-phase replace so re-inserting an identical slug (same season) doesn't trip
            // EF's change tracker on a duplicate key — wrapped in one transaction so a crash
            // between the phases can't leave the cache empty.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            db.MythicPlusDungeonCache.RemoveRange(existing);
            await db.SaveChangesAsync(ct);

            var stamp = DateTime.UtcNow;
            foreach (var d in dungeons)
            {
                db.MythicPlusDungeonCache.Add(new MythicPlusDungeonCache
                {
                    Slug = d.Slug,
                    Name = d.Name,
                    ShortName = d.ShortName,
                    SeasonSlug = seasonSlug,
                    CachedAt = stamp,
                });
            }
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation("Refreshed M+ dungeon pool from Raider.IO: {Count} dungeons (season {Season})",
                dungeons.Count, seasonSlug);
        }

        /// <summary>
        /// Selects the canonical current M+ season from a Raider.IO static-data season list.
        /// Raider.IO returns every season under an expansion — numbered main seasons
        /// (is_main_season=true) plus event variants ("break-the-meta", "cutoffs", "legion-remix"…)
        /// that can start LATER than the real season, so "latest start" alone would grab the wrong
        /// pool. Filter to main seasons and pick the one whose [start, end) (US) window contains
        /// <paramref name="now"/>; fall back defensively if the flag is absent or no window matches.
        /// Returns null only for a null/empty list.
        /// </summary>
        public static RaiderIOModels.MythicPlusSeason SelectActiveSeason(
            IReadOnlyList<RaiderIOModels.MythicPlusSeason> seasons, DateTimeOffset now)
        {
            if (seasons == null || seasons.Count == 0) return null;

            var mains = seasons.Where(s => s.IsMainSeason).ToList();
            var pool = mains.Count > 0 ? mains : seasons;

            return pool.Where(s => SeasonStart(s) <= now && now < SeasonEnd(s)).OrderByDescending(SeasonStart).FirstOrDefault()
                ?? pool.Where(s => SeasonStart(s) <= now).OrderByDescending(SeasonStart).FirstOrDefault()
                ?? pool.OrderByDescending(SeasonStart).FirstOrDefault()
                ?? seasons[0];
        }

        private static DateTimeOffset SeasonStart(RaiderIOModels.MythicPlusSeason s)
        {
            if (s.Starts != null && s.Starts.TryGetValue("us", out var v) && v.HasValue)
                return v.Value;
            return DateTimeOffset.MinValue;
        }

        private static DateTimeOffset SeasonEnd(RaiderIOModels.MythicPlusSeason s)
        {
            if (s.Ends != null && s.Ends.TryGetValue("us", out var v) && v.HasValue)
                return v.Value;
            return DateTimeOffset.MaxValue;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MythicPlusDungeonService stopping");
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
