using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Modules.Wow;

public sealed record RaidRecapStanding(string Name,double? Total,double? PerSecond);

public sealed record RaidRecapFight(int Id, int EncounterId, int? Difficulty, string Name,
    bool? Kill, bool? InProgress, double? StartMs, double? EndMs, double? Remaining)
{
    public bool IsKill => Kill == true && InProgress == false;
    public bool IsWipe => Kill == false && InProgress == false;
    public double? DurationMs => InProgress == false && StartMs >= 0 && EndMs > StartMs ? EndMs - StartMs : null;
}

public sealed record RaidRecapBoss(int EncounterId, int? Difficulty, string Name, IReadOnlyList<RaidRecapFight> Attempts)
{
    public int Kills => Attempts.Count(f => f.IsKill);
    public int Wipes => Attempts.Count(f => f.IsWipe);
    public double? BestRemaining => Attempts.Where(f => f.IsWipe).Select(f => f.Remaining).Min();
    public double? FastestKillMs => Attempts.Where(f => f.IsKill).Select(f => f.DurationMs).Min();
    public IReadOnlyList<RaidRecapFight> Chronological => Attempts.OrderBy(f => f.StartMs ?? double.MaxValue).ThenBy(f => f.Id).ToArray();
    public double? LatestRemaining => Chronological.LastOrDefault(f => f.IsWipe)?.Remaining;
    public int Completed => Kills + Wipes;
    public int KnownDurations => Attempts.Count(f => (f.IsKill || f.IsWipe) && f.DurationMs.HasValue);
    public double? CombatMs => KnownDurations == 0 ? null : Attempts.Where(f => f.IsKill || f.IsWipe).Sum(f => f.DurationMs ?? 0);
    public int WipeDurationSamples => Attempts.Count(f => f.IsWipe && f.DurationMs.HasValue);
    public double? MedianWipeMs
    {
        get
        {
            var durations = Attempts.Where(f => f.IsWipe && f.DurationMs.HasValue).Select(f => f.DurationMs.Value).OrderBy(d => d).ToArray();
            return durations.Length == 0 ? null : durations.Length % 2 == 1 ? durations[durations.Length / 2]
                : durations[durations.Length / 2 - 1] / 2 + durations[durations.Length / 2] / 2;
        }
    }
}

public sealed record RaidRecapReport(string Code, string Title, int? Revision, double? StartTime,
    double? EndTime, DateTimeOffset AsOf, IReadOnlyList<RaidRecapFight> Fights)
{
    public string Url => "https://www.warcraftlogs.com/reports/" + Code;
    public int Kills => Fights.Count(f => f.IsKill);
    public int Wipes => Fights.Count(f => f.IsWipe);
    public int Unfinished => Fights.Count - Kills - Wipes;
    public IReadOnlyList<RaidRecapFight> CompletedPulls => Fights.Where(f => f.IsKill || f.IsWipe)
        .OrderBy(f => f.StartMs ?? double.MaxValue).ThenBy(f => f.Id).ToArray();
    public IReadOnlyList<RaidRecapBoss> Bosses => Fights.GroupBy(f => (f.EncounterId, f.Difficulty))
        .Select(g => new RaidRecapBoss(g.Key.EncounterId, g.Key.Difficulty, g.First().Name, g.ToArray())).ToArray();
    // Timestamp protects live appends even when WCL's re-export revision is unchanged.
    public string SnapshotKey => $"{Code}:{Revision}:{EndTime}:{AsOf.UtcTicks}";
}
