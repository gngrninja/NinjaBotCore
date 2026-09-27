using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Modules.Wow;

// Metadata-only recommendations; indices/actions are resolved again from the owning snapshot.
public sealed record RaidRecapReviewCard(int BossIndex, RaidRecapFight A, RaidRecapFight B = null);
public sealed record RaidRecapComparison(string SnapshotKey, RaidRecapFight A, RaidRecapFight B,
    RaidRecapAnalysis DeathsA, RaidRecapAnalysis DeathsB);

public static class RaidRecapReview
{
    public const string NoPair = "Nothing to compare yet. You need two finished pulls of the same boss on the same difficulty.";

    private static bool Eligible(RaidRecapFight f) => f != null && f.Id > 0 && f.EncounterId > 0 && f.Difficulty > 0
        && (f.IsKill || f.IsWipe) && f.DurationMs is > 0 && double.IsFinite(f.DurationMs.Value)
        && f.StartMs.HasValue && double.IsFinite(f.StartMs.Value) && f.EndMs.HasValue && double.IsFinite(f.EndMs.Value);

    public static IReadOnlyList<RaidRecapFight> Candidates(RaidRecapBoss boss) => boss == null
        ? Array.Empty<RaidRecapFight>() : boss.Chronological.Where(f => Eligible(f)
            && f.EncounterId == boss.EncounterId && f.Difficulty == boss.Difficulty)
            .GroupBy(f => f.Id).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();

    private static bool Matched(RaidRecapFight a, RaidRecapFight b) => Eligible(a) && Eligible(b) && a.Id != b.Id
        && a.EncounterId == b.EncounterId && a.Difficulty == b.Difficulty;

    public static (RaidRecapFight A, RaidRecapFight B) Initial(RaidRecapBoss boss)
    {
        var candidates = Candidates(boss);
        if (candidates.Count < 2) return (null, null);
        var all = boss.Chronological;
        // Do not skip an intervening live/unknown-duration attempt to invent an immediate wipe -> kill.
        for (var i = all.Count - 1; i > 0; i--)
            if (all[i].IsKill && all[i - 1].IsWipe && candidates.Contains(all[i]) && candidates.Contains(all[i - 1]))
                return (all[i - 1], all[i]);
        return (candidates[^2], candidates[^1]);
    }

    public static IReadOnlyList<RaidRecapReviewCard> Cards(RaidRecapReport report)
    {
        var cards = new List<RaidRecapReviewCard>(2);
        var bosses = report.Bosses;
        var unresolved = bosses.Select((b, i) => (b, i)).Where(p => p.b.EncounterId > 0 && p.b.Kills == 0)
            .OrderByDescending(p => p.b.Attempts.Count).ThenBy(p => p.b.EncounterId).ThenBy(p => p.b.Difficulty).FirstOrDefault();
        if (unresolved.b != null)
            cards.Add(new(unresolved.i, unresolved.b.Chronological.LastOrDefault(f => f.IsWipe) ?? unresolved.b.Chronological.Last()));
        var pair = bosses.Select((b, i) => (i, pair: Initial(b))).Where(p => p.pair.A != null)
            .OrderByDescending(p => p.pair.A.IsWipe && p.pair.B.IsKill)
            .ThenByDescending(p => p.pair.B.StartMs).ThenByDescending(p => p.pair.B.Id).FirstOrDefault();
        if (pair.pair.A != null) cards.Add(new(pair.i, pair.pair.A, pair.pair.B));
        return cards;
    }

    public static (RaidRecapFight A, RaidRecapFight B) Selection(RaidRecapSession s)
    {
        if (!s.Comparing || s.View != "bosses" || s.Report == null || s.CompareSnapshotKey != s.Report.SnapshotKey)
            return (null, null);
        var candidates = Candidates(s.Report.Bosses.ElementAtOrDefault(s.BossIndex));
        var a = candidates.ElementAtOrDefault(s.CompareAIndex);
        var b = candidates.ElementAtOrDefault(s.CompareBIndex);
        return Matched(a, b) ? (a, b) : (null, null);
    }

    public static RaidRecapComparison Current(RaidRecapSession s)
    {
        var (a, b) = Selection(s);
        var result = s.Comparison;
        return a != null && result?.SnapshotKey == s.Report.SnapshotKey && result.A == a && result.B == b ? result : null;
    }
}
