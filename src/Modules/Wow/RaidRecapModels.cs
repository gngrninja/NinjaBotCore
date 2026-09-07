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
}

public sealed record RaidRecapReport(string Code, string Title, int? Revision, double? StartTime,
    double? EndTime, DateTimeOffset AsOf, IReadOnlyList<RaidRecapFight> Fights)
{
    public string Url => "https://www.warcraftlogs.com/reports/" + Code;
    public int Kills => Fights.Count(f => f.IsKill);
    public int Wipes => Fights.Count(f => f.IsWipe);
    public int Unfinished => Fights.Count - Kills - Wipes;
    public IReadOnlyList<RaidRecapBoss> Bosses => Fights.GroupBy(f => (f.EncounterId, f.Difficulty))
        .Select(g => new RaidRecapBoss(g.Key.EncounterId, g.Key.Difficulty, g.First().Name, g.ToArray())).ToArray();
    // Timestamp protects live appends even when WCL's re-export revision is unchanged.
    public string SnapshotKey => $"{Code}:{Revision}:{EndTime}:{AsOf.UtcTicks}";
}
