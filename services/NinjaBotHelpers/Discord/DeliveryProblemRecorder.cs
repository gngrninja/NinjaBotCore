using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NinjaBotHelpers.Database;

namespace NinjaBotHelpers.Discord;

/// <summary>
/// Remembers channels Discord stopped letting the helpers post in, for the bot owner's
/// Actionable Alerts page. The same rules as NinjaBotCore's recorder: at most one write an
/// hour per broken channel, the next post that works resolves it, and it never throws.
/// </summary>
public sealed class DeliveryProblemRecorder
{
    public static readonly TimeSpan WriteInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DeliveryProblemRecorder> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<Key, Pending> _pending = new();
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

    public static string Problem(int? discordCode) => discordCode switch
    {
        10003 => "channel_missing",
        50001 => "no_access",
        50013 => "missing_permissions",
        _ => "other"
    };

    public async Task FailedAsync(long serverId, string feature, long channelId, int? discordCode)
    {
        var key = new Key(serverId, feature, channelId);
        _clear.TryRemove(key, out _);
        var problem = Problem(discordCode);
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
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            await RecordAsync(db, key, problem, discordCode, failures, now.UtcDateTime);
        }
        catch (Exception ex)
        {
            lock (pending)
            {
                pending.Unsaved += failures;
                pending.WrittenAt = null;
            }

            _logger.LogDebug("Recording a delivery problem failed ({Type})", ex.GetType().Name);
        }
    }

    public async Task DeliveredAsync(long serverId, string feature, long channelId)
    {
        var key = new Key(serverId, feature, channelId);
        if (_clear.ContainsKey(key))
        {
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            var open = await db.DeliveryProblems
                .Where(p => p.ServerId == serverId && p.Feature == feature && p.ChannelId == channelId && p.ResolvedAt == null)
                .ToListAsync();
            var now = _clock().UtcDateTime;
            foreach (var row in open)
            {
                row.ResolvedAt = now;
                row.Resolution = "delivered";
            }

            if (open.Count > 0)
            {
                await db.SaveChangesAsync();
            }

            _pending.TryRemove(key, out _);
            _clear.TryAdd(key, 0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Resolving a delivery problem failed ({Type})", ex.GetType().Name);
        }
    }

    private static async Task RecordAsync(HelpersDbContext db, Key key, string problem, int? discordCode, int failures, DateTime now)
    {
        var row = await db.DeliveryProblems
            .FirstOrDefaultAsync(p => p.ServerId == key.ServerId && p.Feature == key.Feature && p.ChannelId == key.ChannelId);
        if (row == null)
        {
            row = new DeliveryProblem
            {
                ServerId = key.ServerId,
                Feature = key.Feature,
                ChannelId = key.ChannelId,
                FirstSeenAt = now
            };
            db.DeliveryProblems.Add(row);
        }
        else if (row.ResolvedAt != null)
        {
            // Broken again after it worked: a new problem for the owner to review.
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
        await db.SaveChangesAsync();
    }

    private readonly record struct Key(long ServerId, string Feature, long ChannelId);

    private sealed class Pending
    {
        public int Unsaved;
        public DateTimeOffset? WrittenAt;
        public string? Problem;
    }
}
