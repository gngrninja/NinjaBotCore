using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Wow;

/// <summary>Bounded cache including in-flight entries. Never evicts a live fetch to create a second flight.</summary>
public sealed class RaidRecapCache
{
    private sealed class Entry
    {
        public Lazy<Task<object>> Work;
        public DateTimeOffset Expires = DateTimeOffset.MaxValue;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _capacity;
    public RaidRecapCache(Func<DateTimeOffset> clock = null, int capacity = 128)
    { _clock = clock ?? (() => DateTimeOffset.UtcNow); _capacity = Math.Max(1, capacity); }
    public int Count { get { lock (_sync) return _entries.Count; } }

    public async Task<object> GetAsync(string key, Func<Task<object>> load, TimeSpan ttl)
    {
        Entry entry;
        lock (_sync)
        {
            foreach (var expired in _entries.Where(p => p.Value.Expires <= _clock()).Select(p => p.Key).ToArray()) _entries.Remove(expired);
            if (!_entries.TryGetValue(key, out entry))
            {
                if (_entries.Count >= _capacity)
                {
                    var victim = _entries.Where(p => p.Value.Work.IsValueCreated && p.Value.Work.Value.IsCompleted)
                        .OrderBy(p => p.Value.Expires).FirstOrDefault();
                    if (victim.Key == null) throw new InvalidOperationException("Raid recap is busy. Try again shortly.");
                    _entries.Remove(victim.Key);
                }
                entry = new Entry { Work = new Lazy<Task<object>>(load, LazyThreadSafetyMode.ExecutionAndPublication) };
                _entries.Add(key, entry);
            }
        }
        try
        {
            var value = await entry.Work.Value;
            lock (_sync)
                if (entry.Expires == DateTimeOffset.MaxValue) entry.Expires = _clock() + ttl;
            return value;
        }
        catch
        {
            lock (_sync)
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(entry, current)) _entries.Remove(key);
            throw;
        }
    }
}

public sealed class RaidRecapSession
{
    public IReadOnlyList<NinjaBotCore.Models.Wow.WclV2Report> Reports { get; set; } = Array.Empty<NinjaBotCore.Models.Wow.WclV2Report>();
    public int ReportPage { get; set; }
    public int BossPage { get; set; }
    public int KillPage { get; set; }
    public int RankPage { get; set; }
    public int BossIndex { get; set; }
    public int KillIndex { get; set; }
    public IReadOnlyList<RaidRecapStanding> Performance { get; set; }
    public string Notice { get; set; }
    public RaidRecapReport Report { get; set; }
    public string View { get; set; } = "overview";
    public bool CanShare { get; set; }
    public string Token { get; init; }
    public ulong Actor { get; init; }
    public ulong Guild { get; init; }
    public ulong Channel { get; init; }
    public DateTimeOffset Expires { get; init; }
    public int Generation { get; internal set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private int _shareAttempted;
    public bool ShareAttempted => _shareAttempted != 0;
    public bool TryBeginShare() => Interlocked.CompareExchange(ref _shareAttempted, 1, 0) == 0;
}

/// <summary>Process-local private sessions; restarts expire controls rather than reconstructing authority.</summary>
public sealed class RaidRecapSessions
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RaidRecapSession> _sessions = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _capacity;
    public RaidRecapSessions(Func<DateTimeOffset> clock = null, int capacity = 256)
    { _clock = clock ?? (() => DateTimeOffset.UtcNow); _capacity = Math.Max(1, capacity); }
    public int Count { get { lock (_sync) return _sessions.Count; } }
    public RaidRecapSession Create(ulong actor, ulong guild, ulong channel)
    {
        lock (_sync)
        {
            foreach (var key in _sessions.Where(p => p.Value.Expires <= _clock()).Select(p => p.Key).ToArray()) _sessions.Remove(key);
            if (_sessions.Count >= _capacity) _sessions.Remove(_sessions.OrderBy(p => p.Value.Expires).First().Key);
            var s = new RaidRecapSession { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), Actor = actor, Guild = guild, Channel = channel, Expires = _clock().AddMinutes(10) };
            _sessions.Add(s.Token, s);
            return s;
        }
    }
    public bool IsCurrent(RaidRecapSession s)
    {
        lock(_sync) return s!=null && s.Expires>_clock() && _sessions.TryGetValue(s.Token,out var current) && ReferenceEquals(s,current);
    }
    public async Task<bool> RunAsync(string token, ulong actor, ulong guild, ulong channel, int generation, Func<RaidRecapSession, Task> action)
    {
        RaidRecapSession s;
        lock (_sync)
            if (token == null || !_sessions.TryGetValue(token, out s) || s.Actor != actor || s.Guild != guild || s.Channel != channel) return false;
        await s.Gate.WaitAsync();
        try
        {
            lock (_sync)
                if (!_sessions.TryGetValue(token, out var current) || !ReferenceEquals(s, current) || s.Expires <= _clock() || s.Generation != generation) return false;
            // Invalidate captured controls before awaiting; hold through the final Discord edit/send.
            s.Generation++;
            await action(s);
            return true;
        }
        finally { s.Gate.Release(); }
    }
}
