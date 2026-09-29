#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>One raid inside a log: consecutive raid pulls in one zone on one night.</summary>
public sealed record RaidRecapLiveSession(long StartMs, int? ZoneId, string? ZoneName, IReadOnlyList<RaidRecapFight> Fights)
{
    /// <summary>When the latest pull in this raid started or ended, in milliseconds from the start of the log.</summary>
    public double LastActivityMs => Fights.Max(f => f.EndMs ?? f.StartMs ?? 0);

    public bool HasPullInProgress => Fights.Any(f => f.InProgress == true);
}

/// <summary>
/// Splits a log into raids. A new raid starts at a raid pull in a different zone from the
/// previous raid pull, or at a raid pull that begins <see cref="NewNightGap"/> or more after
/// the previous one ended. Pulls that are not raid bosses, such as Mythic+ dungeons, are
/// left out entirely. A pull whose zone WarcraftLogs did not report stays with the raid
/// before it.
/// </summary>
public static class RaidRecapLiveSessions
{
    /// <summary>LFR, Normal, Heroic and Mythic.</summary>
    public static readonly IReadOnlyList<int> RaidDifficulties = new[] { 1, 3, 4, 5 };

    /// <summary>Anything shorter is a break in the same raid; anything longer is a new night.</summary>
    public static readonly TimeSpan NewNightGap = TimeSpan.FromHours(4);

    public static bool IsRaid(RaidRecapFight fight) =>
        fight.Difficulty is int difficulty && RaidDifficulties.Contains(difficulty);

    public static IReadOnlyList<RaidRecapLiveSession> Split(RaidRecapReport report)
    {
        var sessions = new List<RaidRecapLiveSession>();
        var current = new List<RaidRecapFight>();
        int? zone = null;
        string? zoneName = null;
        double? lastEnd = null;

        void Close()
        {
            if (current.Count > 0)
            {
                sessions.Add(new RaidRecapLiveSession(
                    (long)Math.Round(current[0].StartMs!.Value), zone, zoneName, current.ToArray()));
            }

            current.Clear();
        }

        var pulls = report.Fights
            .Where(IsRaid)
            .Where(f => f.StartMs is double start && double.IsFinite(start) && start >= 0)
            .OrderBy(f => f.StartMs)
            .ThenBy(f => f.Id);
        foreach (var pull in pulls)
        {
            var otherZone = pull.ZoneId is int id && zone is int known && id != known;
            var nextNight = lastEnd is double end && pull.StartMs!.Value - end >= NewNightGap.TotalMilliseconds;
            if (current.Count > 0 && (otherZone || nextNight))
            {
                Close();
                zone = null;
                zoneName = null;
            }

            current.Add(pull);
            if (pull.ZoneId is int pullZone && zone == null)
            {
                zone = pullZone;
                zoneName = pull.ZoneName;
            }

            var pullEnd = pull.EndMs ?? pull.StartMs!.Value;
            lastEnd = lastEnd is double previous ? Math.Max(previous, pullEnd) : pullEnd;
        }

        Close();
        return sessions;
    }

    /// <summary>
    /// The raid a card covers. Cards made before raids were told apart hold 0 and cover the
    /// first raid in the log.
    /// </summary>
    public static RaidRecapLiveSession? Find(IReadOnlyList<RaidRecapLiveSession> sessions, long startMs) =>
        sessions.FirstOrDefault(s => s.StartMs == startMs) ?? (startMs == 0 ? sessions.FirstOrDefault() : null);

    /// <summary>The log narrowed to one raid's pulls. Codes, revision and times are unchanged.</summary>
    public static RaidRecapReport Scope(RaidRecapReport report, RaidRecapLiveSession? session) =>
        report with { Fights = session?.Fights ?? Array.Empty<RaidRecapFight>() };

    /// <summary>The wall-clock time of the raid's latest activity.</summary>
    public static DateTimeOffset? LastActivityAt(RaidRecapReport report, RaidRecapLiveSession session)
    {
        if (report.StartTime is not (>= 0 and <= 253402300799999) || session.Fights.Count == 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(report.StartTime.Value + session.LastActivityMs));
    }
}
