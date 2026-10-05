#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>What happened to a live card, as shown to the bot owner.</summary>
public static class RaidRecapLiveEventKind
{
    public const string Posted = "posted";
    public const string Resumed = "resumed";
    public const string Revived = "revived";
    public const string Ended = "ended";
    public const string Stopped = "stopped";
    public const string Deleted = "deleted";
}

public sealed record RaidRecapLiveEvent(
    DateTimeOffset At, string Kind, long ServerId, long? CardId, string? Report, string? Guild, string? Reason);

public sealed record RaidRecapLiveSweep(DateTimeOffset At, TimeSpan Took, bool KillSwitch, int CardsWatched, int ServersChecked);

public sealed record RaidRecapLiveError(DateTimeOffset At, string Where, string Type);

public sealed record RaidRecapLiveHealthSnapshot(
    DateTimeOffset Since,
    RaidRecapLiveSweep? LastSweep,
    RaidRecapLiveError? LastError,
    int ErrorsLastHour,
    int QuotaWaitsLastHour,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<RaidRecapLiveEvent> Recent);

/// <summary>
/// What the live card watcher did lately, for the owner's rollout Status and the web admin
/// page. Memory only, so it starts empty after a restart. Errors are kept as type names,
/// never messages, because a message can carry provider detail.
/// </summary>
public sealed class RaidRecapLiveHealth
{
    /// <summary>How many recent card events are kept.</summary>
    public const int MaxEvents = 30;

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private const int MaxTimestamps = 2000;

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly LinkedList<RaidRecapLiveEvent> _events = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _errors = new();
    private readonly Queue<DateTimeOffset> _quotaWaits = new();
    private RaidRecapLiveSweep? _lastSweep;
    private RaidRecapLiveError? _lastError;

    public RaidRecapLiveHealth(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Since = _clock();
    }

    public DateTimeOffset Since { get; }

    public void Sweep(DateTimeOffset at, TimeSpan took, bool killSwitch, int cardsWatched, int serversChecked)
    {
        lock (_gate)
        {
            _lastSweep = new(at, took, killSwitch, cardsWatched, serversChecked);
        }
    }

    public void Error(string where, Exception error) => Error(where, error.GetType().Name);

    public void Error(string where, string type)
    {
        lock (_gate)
        {
            var now = _clock();
            _lastError = new(now, where, type);
            Stamp(_errors, now);
        }
    }

    public void QuotaWait()
    {
        lock (_gate)
        {
            Stamp(_quotaWaits, _clock());
        }
    }

    public void Event(string kind, long serverId, long? cardId = null, string? report = null, string? guild = null, string? reason = null)
    {
        lock (_gate)
        {
            _events.AddFirst(new RaidRecapLiveEvent(_clock(), kind, serverId, cardId, report, guild, reason));
            while (_events.Count > MaxEvents)
            {
                _events.RemoveLast();
            }

            _counts[kind] = _counts.TryGetValue(kind, out var count) ? count + 1 : 1;
        }
    }

    public RaidRecapLiveHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            return new(Since, _lastSweep, _lastError, Recent(_errors, now), Recent(_quotaWaits, now),
                new Dictionary<string, int>(_counts, StringComparer.Ordinal), _events.ToArray());
        }
    }

    private static void Stamp(Queue<DateTimeOffset> stamps, DateTimeOffset now)
    {
        stamps.Enqueue(now);
        while (stamps.Count > MaxTimestamps || (stamps.Count > 0 && now - stamps.Peek() >= Window))
        {
            stamps.Dequeue();
        }
    }

    private static int Recent(Queue<DateTimeOffset> stamps, DateTimeOffset now) => stamps.Count(t => now - t < Window);
}
