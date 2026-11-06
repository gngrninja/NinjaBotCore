#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Remembers features Discord stopped letting the bot post for, so the bot owner can review
    /// them on the admin page and tell the server. A broken channel costs at most one write an
    /// hour, however often the feature tries; the next post that works resolves the problem.
    /// Never throws: recording a problem must not break the feature that hit it.
    /// </summary>
    public sealed class DeliveryProblemRecorder
    {
        public static readonly TimeSpan WriteInterval = TimeSpan.FromHours(1);

        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<DeliveryProblemRecorder> _logger;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ConcurrentDictionary<Key, Pending> _pending = new();

        // Keys known to have no open problem, so a working feature never touches the database.
        private readonly ConcurrentDictionary<Key, byte> _clear = new();

        public DeliveryProblemRecorder(
            IServiceScopeFactory scopes,
            ILogger<DeliveryProblemRecorder> logger,
            Func<DateTimeOffset>? clock = null)
        {
            _scopes = scopes;
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public Task FailedAsync(ulong serverId, string feature, ulong channelId, int? discordCode) =>
            FailedAsync(serverId, feature, channelId, DeliveryProblemKind.FromDiscordCode(discordCode), discordCode);

        public async Task FailedAsync(ulong serverId, string feature, ulong channelId, string problem, int? discordCode)
        {
            if (serverId == 0)
            {
                // Not a server channel; nobody to tell.
                return;
            }

            var key = new Key((long)serverId, feature, (long)channelId);
            _clear.TryRemove(key, out _);
            var now = _clock();
            var pending = _pending.GetOrAdd(key, _ => new Pending());
            int failures;
            lock (pending)
            {
                pending.Unsaved++;
                if (pending.WrittenAt is { } written && now - written < WriteInterval && pending.Problem == problem)
                {
                    return;
                }

                failures = pending.Unsaved;
                pending.Unsaved = 0;
                pending.WrittenAt = now;
                pending.Problem = problem;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                await DeliveryProblemStore.RecordAsync(db, key.ServerId, feature, key.ChannelId, problem, discordCode, failures, now.UtcDateTime);
            }
            catch (Exception ex)
            {
                // Count these failures again on the next attempt.
                lock (pending)
                {
                    pending.Unsaved += failures;
                    pending.WrittenAt = null;
                }

                _logger.LogDebug("Recording a delivery problem failed ({Type})", ex.GetType().Name);
            }
        }

        public async Task DeliveredAsync(ulong serverId, string feature, ulong channelId)
        {
            var key = new Key((long)serverId, feature, (long)channelId);
            if (_clear.ContainsKey(key))
            {
                return;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                await DeliveryProblemStore.ResolveAsync(db, key.ServerId, feature, key.ChannelId, DeliveryResolution.Delivered, _clock().UtcDateTime);
                _pending.TryRemove(key, out _);
                _clear.TryAdd(key, 0);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Resolving a delivery problem failed ({Type})", ex.GetType().Name);
            }
        }

        private readonly record struct Key(long ServerId, string Feature, long ChannelId);

        private sealed class Pending
        {
            public int Unsaved;
            public DateTimeOffset? WrittenAt;
            public string? Problem;
        }
    }

    /// <summary>The writes behind <see cref="DeliveryProblemRecorder"/>, shared with the admin API.</summary>
    public static class DeliveryProblemStore
    {
        public static async Task RecordAsync(
            NinjaBotEntities db,
            long serverId,
            string feature,
            long channelId,
            string problem,
            int? discordCode,
            int failures,
            DateTime now)
        {
            var row = await db.DeliveryProblems
                .FirstOrDefaultAsync(p => p.ServerId == serverId && p.Feature == feature && p.ChannelId == channelId);
            if (row == null)
            {
                row = new DeliveryProblem
                {
                    ServerId = serverId,
                    Feature = feature,
                    ChannelId = channelId,
                    FirstSeenAt = now
                };
                db.DeliveryProblems.Add(row);
            }
            else if (row.ResolvedAt != null)
            {
                // Broken again after it worked: a new problem, so the owner reviews it afresh.
                row.FirstSeenAt = now;
                row.Failures = 0;
                row.ResolvedAt = null;
                row.Resolution = null;
                row.DismissedAt = null;
                row.NotifiedAt = null;
                row.NotifiedUserId = null;
                row.NotifiedRole = null;
                row.LastAttemptAt = null;
                row.LastAttemptRole = null;
                row.LastAttemptResult = null;
            }

            row.Problem = problem;
            row.DiscordCode = discordCode;
            row.LastSeenAt = now;
            row.Failures += Math.Max(1, failures);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) when (db.Entry(row).State == EntityState.Added)
            {
                // Another writer added it first. Count these failures on that row instead.
                db.Entry(row).State = EntityState.Detached;
                var existing = await db.DeliveryProblems
                    .FirstAsync(p => p.ServerId == serverId && p.Feature == feature && p.ChannelId == channelId);
                existing.Problem = problem;
                existing.DiscordCode = discordCode;
                existing.LastSeenAt = now;
                existing.Failures += Math.Max(1, failures);
                await db.SaveChangesAsync();
            }
        }

        /// <summary>Resolves the open problem for this feature and channel, if there is one.</summary>
        public static async Task<bool> ResolveAsync(
            NinjaBotEntities db,
            long serverId,
            string feature,
            long channelId,
            string resolution,
            DateTime now)
        {
            var open = await db.DeliveryProblems
                .Where(p => p.ServerId == serverId && p.Feature == feature && p.ChannelId == channelId && p.ResolvedAt == null)
                .ToListAsync();
            foreach (var row in open)
            {
                row.ResolvedAt = now;
                row.Resolution = resolution;
            }

            if (open.Count > 0)
            {
                await db.SaveChangesAsync();
            }

            return open.Count > 0;
        }
    }
}
