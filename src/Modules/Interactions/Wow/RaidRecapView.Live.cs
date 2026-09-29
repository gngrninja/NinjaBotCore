using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>What the live card needs besides the report itself.</summary>
public sealed record RaidRecapLiveInfo(string GuildName, string Region, string ZoneName, string IconUrl)
{
    /// <summary>
    /// When this card's raid began and ended, as epoch milliseconds, for the header's date and
    /// length. Unset means the whole log. A log can hold several raids, each with its own card.
    /// </summary>
    public double? HeaderStartMs { get; init; }
    public double? HeaderEndMs { get; init; }
}

/// <summary>A death on the public card: when, which spec, what killed them. Never a name.</summary>
public sealed record RaidRecapLiveDeath(double ElapsedMs, string Identity, string Ability);

/// <summary>
/// A top performer on the public card, shown by name with spec and class. Being named here
/// is praise. <see cref="ActorId"/> is set only when the player was matched to the raid
/// roster, and only then is the name linked.
/// </summary>
public sealed record RaidRecapLivePerformer(string Identity, double? PerSecond, double? Percentile)
{
    public string Name { get; init; }
    public int? ActorId { get; init; }

    /// <summary>The player's whole damage or healing on the kill, to check target shares against.</summary>
    public double? Total { get; init; }

    /// <summary>Where this player's damage or healing went, top first. Null when not read.</summary>
    public IReadOnlyList<RaidRecapTarget> Targets { get; init; }
}

/// <summary>The killing blow that most often started a wipe on the current boss.</summary>
public sealed record RaidRecapLiveWipePattern(string Ability, int Count, int Wipes);

/// <summary>How a kill went for the whole raid. Any part may be missing.</summary>
public sealed record RaidRecapLiveKillQuality(
    double? DamageAverage,
    int DamageParses,
    double? HealingAverage,
    int HealingParses,
    double? Speed,
    double? Execution)
{
    public bool HasAny => DamageAverage.HasValue || HealingAverage.HasValue || Speed.HasValue || Execution.HasValue;
}

/// <summary>A name and how many times, for kicks and dispels.</summary>
public sealed record RaidRecapLiveCount(string Name, double Count);

/// <summary>
/// Interrupts and dispels on one pull: how many, and who did the most. Only players matched
/// to the raid roster are named, so pets and unknown sources never show. What was dispelled
/// is named by its spell.
/// </summary>
public sealed record RaidRecapLiveUtility(RaidRecapFight Pull)
{
    public const int Shown = 3;

    public double Kicks { get; init; }
    public double WentOff { get; init; }
    public IReadOnlyList<RaidRecapLiveCount> Kickers { get; init; } = Array.Empty<RaidRecapLiveCount>();
    public bool KicksComplete { get; init; }
    public double Dispels { get; init; }
    public IReadOnlyList<RaidRecapLiveCount> Debuffs { get; init; } = Array.Empty<RaidRecapLiveCount>();
    public IReadOnlyList<RaidRecapLiveCount> Dispellers { get; init; } = Array.Empty<RaidRecapLiveCount>();
    public bool DispelsComplete { get; init; }

    /// <summary>
    /// Nothing more to learn by reading again: both results complete, names included. A result
    /// whose counts are whole but whose names could not be matched, for example because the
    /// roster could not be read, is worth another try.
    /// </summary>
    public bool Settled { get; init; }

    public bool HasKicks => Kicks > 0 || WentOff > 0;
    public bool HasDispels => Dispels > 0;

    public static RaidRecapLiveUtility From(RaidRecapFight pull, RaidRecapAnalysis interrupts, RaidRecapAnalysis dispels) => new(pull)
    {
        Kicks = Sum(interrupts?.Utility.Select(u => u.Actions)),
        WentOff = Sum(interrupts?.Utility.Select(u => u.CompletedCasts)),
        Kickers = Players(interrupts),
        // The count is complete when every spell's count is known. Credit going to a pet or
        // someone off the roster makes attribution partial, not the count.
        KicksComplete = Counted(interrupts),
        Dispels = Sum(dispels?.Utility.Select(u => u.Actions)),
        Debuffs = (dispels?.Utility ?? Array.Empty<RaidRecapUtility>())
            .Where(u => u.Actions is > 0)
            .GroupBy(u => u.Name, StringComparer.Ordinal)
            .Select(g => new RaidRecapLiveCount(g.Key, g.Sum(u => u.Actions!.Value)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(2)
            .ToArray(),
        Dispellers = Players(dispels).Take(2).ToArray(),
        DispelsComplete = Counted(dispels),
        Settled = interrupts?.Complete == true && dispels?.Complete == true
    };

    private static bool Counted(RaidRecapAnalysis analysis) =>
        analysis != null && analysis.Utility.All(u => u.Actions.HasValue);

    private static double Sum(IEnumerable<double?> values) =>
        values?.Where(v => v is >= 0 && double.IsFinite(v.Value)).Sum(v => v!.Value) ?? 0;

    private static IReadOnlyList<RaidRecapLiveCount> Players(RaidRecapAnalysis analysis) =>
        (analysis?.Utility ?? Array.Empty<RaidRecapUtility>())
            .SelectMany(u => u.Participants)
            .Where(p => p.VerifiedPlayer && p.ActorId is > 0 && p.Count is > 0)
            .GroupBy(p => p.ActorId!.Value)
            .Select(g => new RaidRecapLiveCount(g.First().Name, g.Sum(p => p.Count!.Value)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(Shown)
            .ToArray();
}

/// <summary>A player who finished in the top three of damage or healing on several kills.</summary>
public sealed record RaidRecapLiveMvp(string Name, int Finishes);

/// <summary>
/// The detail on the live card: the first deaths of the latest wipe, by spec and class only,
/// and the top damage and healing of the latest kill, by name. Any part may be missing.
/// </summary>
public sealed record RaidRecapLiveHighlights
{
    public const int Shown = 3;

    /// <summary>A pattern needs at least this many wipes with full death data.</summary>
    public const int PatternWipes = 3;

    /// <summary>Most-top-three lists need at least this many kills.</summary>
    public const int MvpKills = 2;

    public RaidRecapLiveWipePattern Pattern { get; init; }

    /// <summary>Kicks and dispels on the latest finished pull, when read.</summary>
    public RaidRecapLiveUtility Utility { get; init; }
    public RaidRecapLiveKillQuality Quality { get; init; }
    public IReadOnlyList<RaidRecapLiveMvp> Mvps { get; init; } = Array.Empty<RaidRecapLiveMvp>();

    /// <summary>
    /// The most common first killing blow across a boss's wipes. Reported only when it
    /// started at least half of them and more than any other blow, so neither one unlucky
    /// pull nor a tie is called a pattern.
    /// Pass one entry per wipe with full death data; null when nobody died or the blow is unknown.
    /// </summary>
    public static RaidRecapLiveWipePattern FindPattern(IReadOnlyList<string> firstBlows)
    {
        if (firstBlows == null || firstBlows.Count < PatternWipes)
        {
            return null;
        }

        var ranked = firstBlows
            .Where(b => !string.IsNullOrWhiteSpace(b) && !b.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            .GroupBy(b => b, StringComparer.Ordinal)
            .Select(g => (Ability: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Count < 2 || ranked[0].Count * 2 < firstBlows.Count)
        {
            return null;
        }

        if (ranked.Length > 1 && ranked[1].Count == ranked[0].Count)
        {
            return null;
        }

        return new(ranked[0].Ability, ranked[0].Count, firstBlows.Count);
    }

    /// <summary>
    /// Average parse of the damage dealers and of the healers, plus the raid's speed and
    /// execution standing when WarcraftLogs sent them. Tanks are left out of both averages.
    /// </summary>
    public static RaidRecapLiveKillQuality FindQuality(RaidRecapOutput damage, RaidRecapOutput healing)
    {
        static (double? Average, int Count) Average(RaidRecapOutput output, string role)
        {
            var parses = (output?.Rows ?? Array.Empty<RaidRecapStanding>())
                .Where(r => r.Player?.Role == role && r.Parse != null)
                .Select(r => r.Parse.Percentile)
                .ToArray();
            return parses.Length == 0 ? (null, 0) : (parses.Average(), parses.Length);
        }

        var dps = Average(damage, "dps");
        var heal = Average(healing, "healers");
        var quality = new RaidRecapLiveKillQuality(
            dps.Average,
            dps.Count,
            heal.Average,
            heal.Count,
            damage?.Parses?.SpeedPercent ?? healing?.Parses?.SpeedPercent,
            damage?.Parses?.ExecutionPercent ?? healing?.Parses?.ExecutionPercent);
        return quality.HasAny ? quality : null;
    }

    /// <summary>
    /// The record-setting wipe and the best it beat, when the latest finished pull is a wipe
    /// that beat every earlier one on that boss. Null otherwise. Lower boss health left is better.
    /// </summary>
    public static (RaidRecapFight Fight, double Previous)? BeatenBest(RaidRecapReport report)
    {
        var last = report.CompletedPulls.LastOrDefault();
        if (last?.IsWipe != true || last.Remaining is not double now)
        {
            return null;
        }

        var earlier = report.CompletedPulls
            .Where(f => f != last && f.IsWipe && f.EncounterId == last.EncounterId && f.Difficulty == last.Difficulty && f.Remaining.HasValue)
            .Select(f => f.Remaining.Value)
            .ToArray();
        return earlier.Length > 0 && now < earlier.Min() ? (last, earlier.Min()) : null;
    }

    /// <summary>The middle gap between one pull ending and the next starting.</summary>
    public static double? MedianGapMs(RaidRecapReport report)
    {
        var pulls = report.CompletedPulls;
        var gaps = new List<double>();
        for (var i = 1; i < pulls.Count; i++)
        {
            if (pulls[i].StartMs - pulls[i - 1].EndMs is double gap && gap > 0 && double.IsFinite(gap))
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count < 2)
        {
            return null;
        }

        gaps.Sort();
        return gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : gaps[gaps.Count / 2 - 1] / 2 + gaps[gaps.Count / 2] / 2;
    }

    /// <summary>
    /// Who finished in the top three on the most kills. Pass one list per kill holding that
    /// kill's top damage and top healing together. A player counts once per kill, however
    /// many lists they topped, and needs at least two kills to be listed. Players are told
    /// apart by their id in the log, so two players who share a name stay separate.
    /// </summary>
    public static IReadOnlyList<RaidRecapLiveMvp> FindMvps(IReadOnlyList<IReadOnlyList<RaidRecapLivePerformer>> perKill)
    {
        if (perKill == null || perKill.Count < MvpKills)
        {
            return Array.Empty<RaidRecapLiveMvp>();
        }

        static string Key(RaidRecapLivePerformer p) => p.ActorId is int id ? "#" + id : "n:" + p.Name;

        return perKill
            .SelectMany(kill => kill
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(Key, StringComparer.Ordinal)
                .Select(g => g.First()))
            .GroupBy(Key, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.First().Name, StringComparer.Ordinal)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(Shown)
            .Select(g => new RaidRecapLiveMvp(g.First().Name, g.Count()))
            .ToArray();
    }

    /// <summary>Top three of a raw table, by name only. Used for kills that are not the latest.</summary>
    public static IReadOnlyList<RaidRecapLivePerformer> Performers(IReadOnlyList<RaidRecapStanding> rows) =>
        rows
            .Where(r => r.PerSecond.HasValue)
            .Take(Shown)
            .Select(r => new RaidRecapLivePerformer("", r.PerSecond, null) { Name = r.Name, ActorId = r.ActorId })
            .ToArray();

    public RaidRecapFight Wipe { get; init; }
    public IReadOnlyList<RaidRecapLiveDeath> FirstDeaths { get; init; } = Array.Empty<RaidRecapLiveDeath>();
    public int TotalDeaths { get; init; }
    public bool DeathsComplete { get; init; }
    public RaidRecapFight Kill { get; init; }
    public IReadOnlyList<RaidRecapLivePerformer> TopDamage { get; init; } = Array.Empty<RaidRecapLivePerformer>();
    public IReadOnlyList<RaidRecapLivePerformer> TopHealing { get; init; } = Array.Empty<RaidRecapLivePerformer>();

    /// <summary>The latest finished wipe, only when it is the most recent finished pull.</summary>
    public static RaidRecapFight LatestWipe(RaidRecapReport report)
    {
        var last = report.CompletedPulls.LastOrDefault();
        return last?.IsWipe == true && last.DurationMs is > 0 ? last : null;
    }

    public static RaidRecapFight LatestKill(RaidRecapReport report) =>
        report.CompletedPulls.LastOrDefault(f => f.IsKill && f.DurationMs is > 0);

    public static IReadOnlyList<RaidRecapLiveDeath> Deaths(RaidRecapAnalysis analysis, RaidRecapRoster roster) =>
        analysis.Deaths
            .OrderBy(d => d.ElapsedMs)
            .Take(Shown)
            .Select(d => new RaidRecapLiveDeath(
                d.ElapsedMs,
                RaidRecapPlayerPresentation.Identity(roster?.Players.FirstOrDefault(p => p.ActorId == d.ActorId)),
                d.Ability))
            .ToArray();

    public static IReadOnlyList<RaidRecapLivePerformer> Performers(RaidRecapOutput output) =>
        output.Rows
            .Where(r => r.PerSecond.HasValue)
            .Take(Shown)
            .Select(r => new RaidRecapLivePerformer(
                RaidRecapPlayerPresentation.Identity(r.Player),
                r.PerSecond,
                r.Parse?.Percentile)
            {
                Name = r.Player?.Name ?? r.Name,
                ActorId = r.Player?.ActorId,
                Total = r.Total
            })
            .ToArray();
}

public static partial class RaidRecapView
{
    public const string LiveOpenId = "rrlive_open";
    private const uint LiveColor = 0xED4245;
    private const uint EndedColor = 0x57F287;
    private const int StripPulls = 12;

    /// <summary>
    /// The public live card. It is posted once and edited as pulls arrive. It names the top
    /// damage and healing players of the latest kill. Deaths are shown by spec and class only.
    /// "Open my recap" gives each viewer their private recap.
    /// </summary>
    /// <summary>Total text a card may hold. Discord allows 4000; the rest is headroom.</summary>
    public const int LiveTextBudget = 3800;

    public static MessageComponent Live(
        RaidRecapReport report,
        RaidRecapLiveInfo info,
        bool ended,
        DateTimeOffset updated,
        RaidRecapLiveHighlights highlights = null) =>
        Live(report, info, ended, updated, highlights, LiveTextBudget);

    /// <summary>
    /// Targets under each top player are the first thing to go when a busy night with long
    /// names would push the card past the text budget.
    /// </summary>
    internal static MessageComponent Live(
        RaidRecapReport report,
        RaidRecapLiveInfo info,
        bool ended,
        DateTimeOffset updated,
        RaidRecapLiveHighlights highlights,
        int textBudget)
    {
        // Trimmed in order of how much a reader loses: target lines first, then kicks and dispels.
        foreach (var (targets, utility) in new[] { (true, true), (false, true) })
        {
            var card = BuildLive(report, info, ended, updated, highlights, targets, utility);
            if (TextLength(card.Components) <= textBudget)
            {
                return card;
            }
        }

        return BuildLive(report, info, ended, updated, highlights, targets: false, utility: false);
    }

    private static int TextLength(IEnumerable<IMessageComponent> components) => components.Sum(c => c switch
    {
        TextDisplayComponent t => t.Content?.Length ?? 0,
        ContainerComponent k => TextLength(k.Components),
        SectionComponent s => TextLength(s.Components.Cast<IMessageComponent>()),
        _ => 0
    });

    private static MessageComponent BuildLive(
        RaidRecapReport report,
        RaidRecapLiveInfo info,
        bool ended,
        DateTimeOffset updated,
        RaidRecapLiveHighlights highlights,
        bool targets,
        bool utility)
    {
        var s = Session(report, info);
        var card = new ContainerBuilder().WithAccentColor(new Color(ended ? EndedColor : LiveColor));
        var details = ReportSubtitle(s, includeTitle: false);
        AddHeader(card, s, (ended ? "🏁 **Raid ended**" : "🔴 **LIVE**") + (details.Length == 0 ? "" : "\n" + details));
        card.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));

        var text = new StringBuilder();
        text.Append(Summary(report));
        if (ended)
        {
            text.Append(Night(report, highlights));
        }

        text.Append(Current(report, ended));
        card.AddComponent(new TextDisplayBuilder(text.ToString()));

        var detail = Highlights(report, highlights, targets, utility);
        if (detail.Length > 0)
        {
            card.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
            card.AddComponent(new TextDisplayBuilder(detail));
        }

        card.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
        card.AddComponent(new TextDisplayBuilder(BossLines(report)));
        card.AddComponent(new TextDisplayBuilder(LiveFooter(info, ended, updated)));
        card.AddComponent(LiveButtons(report));
        return new ComponentBuilderV2().AddComponent(card).Build();
    }

    /// <summary>Shown when watching stops early, so a card never claims to be live forever.</summary>
    public static MessageComponent LiveStopped(string reportCode, RaidRecapLiveInfo info, DateTimeOffset updated)
    {
        var report = new RaidRecapReport(reportCode, "Raid report", null, null, null, updated, Array.Empty<RaidRecapFight>());
        var s = Session(report, info);
        var card = new ContainerBuilder().WithAccentColor(new Color(0x7F8C8D));
        AddHeader(card, s, "⏹️ **Live updates stopped**");
        card.AddComponent(new TextDisplayBuilder($"This card is no longer updating. Open the log on {RaidRecapFormat.Source} for the latest."));
        card.AddComponent(new TextDisplayBuilder(LiveFooter(info, ended: true, updated)));
        card.AddComponent(new ActionRowBuilder().WithButton(
            new ButtonBuilder(RaidRecapFormat.Source, style: ButtonStyle.Link, url: report.Url)));
        return new ComponentBuilderV2().AddComponent(card).Build();
    }

    private static RaidRecapSession Session(RaidRecapReport report, RaidRecapLiveInfo info) => new()
    {
        // Only the header reads these times; everything else uses the report as given.
        Report = report with
        {
            StartTime = info?.HeaderStartMs ?? report.StartTime,
            EndTime = info?.HeaderEndMs ?? report.EndTime
        },
        GuildName = info?.GuildName,
        GuildRegion = info?.Region,
        GuildIconUrl = info?.IconUrl,
        Reports = string.IsNullOrWhiteSpace(info?.ZoneName)
            ? Array.Empty<WclV2Report>()
            : new[] { new WclV2Report { Code = report.Code, Zone = new WclV2Zone { Name = info.ZoneName } } }
    };

    /// <summary>Closing facts for the final card: pace, and who stood out across the kills.</summary>
    private static string Night(RaidRecapReport report, RaidRecapLiveHighlights highlights)
    {
        var text = new StringBuilder();
        if (RaidRecapLiveHighlights.MedianGapMs(report) is double gap)
        {
            text.AppendLine($"⏳ {RaidRecapFormat.Clock(gap)} between pulls, typically");
        }

        if (highlights?.Mvps.Count > 0)
        {
            text.AppendLine("🏆 Most top-3 finishes: " + string.Join(" · ",
                highlights.Mvps.Select(m => $"**{RaidRecapRules.Text(m.Name, 30)}** {m.Finishes}")));
        }

        return text.ToString();
    }

    private static string Current(RaidRecapReport report, bool ended)
    {
        var last = report.Fights
            .OrderBy(f => f.StartMs ?? double.MaxValue)
            .ThenBy(f => f.Id)
            .LastOrDefault();
        if (last == null)
        {
            return "";
        }

        var boss = report.Bosses.First(b => b.EncounterId == last.EncounterId && b.Difficulty == last.Difficulty);
        var text = new StringBuilder();
        text.AppendLine();
        // "Now" means the raid is working on this boss. After a kill they have moved on.
        var label = ended ? "Last boss" : last.IsKill ? "Latest" : "Now";
        text.AppendLine($"**{label} · {RaidRecapRules.Text(boss.Name, 75)} · {RaidRecapFormat.DifficultyShort(boss.Difficulty)}**"
            + $" · {RaidRecapFormat.Plural(boss.Attempts.Count, "pull")}");

        var line = $"Last pull {RaidRecapFormat.OutcomeEmoji(last)} {RaidRecapFormat.Outcome(last)}";
        if (last.DurationMs.HasValue)
        {
            line += " · " + RaidRecapFormat.Clock(last.DurationMs);
        }

        if (last.IsWipe && last.Remaining.HasValue)
        {
            line += $" · **{RaidRecapFormat.Percent(last.Remaining)}**";
        }

        if (boss.Kills == 0 && boss.Wipes > 0)
        {
            line += $" · Best **{RaidRecapFormat.Percent(boss.BestRemaining)}**";
        }

        text.AppendLine(line);
        // Only while that record pull is the one shown above. A pull that has just begun has no result yet.
        if (RaidRecapLiveHighlights.BeatenBest(report) is { } best && best.Fight == last)
        {
            text.AppendLine($"🔥 **New best pull** · {RaidRecapFormat.Percent(best.Fight.Remaining)}, was {RaidRecapFormat.Percent(best.Previous)}");
        }

        if (boss.Attempts.Count > 1)
        {
            text.AppendLine(PullStrip(boss));
        }

        return text.ToString();
    }

    /// <summary>
    /// Deaths are shown by spec and class only. Top damage and healing are shown by name.
    /// </summary>
    private static string Highlights(RaidRecapReport report, RaidRecapLiveHighlights highlights, bool targets, bool utility)
    {
        var text = new StringBuilder();
        if (highlights?.Wipe != null && highlights.FirstDeaths.Count > 0)
        {
            text.AppendLine($"**💀 Last wipe · first deaths** · #{highlights.Wipe.Id}");
            foreach (var death in highlights.FirstDeaths)
            {
                text.AppendLine($"`{RaidRecapFormat.Elapsed(death.ElapsedMs)}` {death.Identity} · {RaidRecapRules.Text(death.Ability, 55)}");
            }

            if (highlights.TotalDeaths > highlights.FirstDeaths.Count || !highlights.DeathsComplete)
            {
                text.AppendLine($"-# {(highlights.DeathsComplete ? "" : "at least ")}{RaidRecapFormat.Plural(highlights.TotalDeaths, "death")} on this pull");
            }
        }

        var now = report.Fights.OrderBy(f => f.StartMs ?? double.MaxValue).ThenBy(f => f.Id).LastOrDefault();
        var sameBoss = highlights?.Wipe != null && now != null
            && now.EncounterId == highlights.Wipe.EncounterId && now.Difficulty == highlights.Wipe.Difficulty;
        if (sameBoss && highlights.Pattern is { } pattern)
        {
            // The one thing to fix. A killing blow, never a person.
            text.AppendLine($"📌 First death was **{RaidRecapRules.Text(pattern.Ability, 55)}** on {pattern.Count} of {RaidRecapFormat.Plural(pattern.Wipes, "wipe")}");
        }

        if (utility && highlights?.Utility is { } done && (done.HasKicks || done.HasDispels))
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            Utility(text, done);
        }

        if (highlights?.Kill != null && (highlights.TopDamage.Count > 0 || highlights.TopHealing.Count > 0))
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.AppendLine($"**✅ {RaidRecapRules.Text(highlights.Kill.Name, 60)} kill** · {RaidRecapFormat.Clock(highlights.Kill.DurationMs)}");
            Performers(text, report, highlights.Kill, "⚔️ Top damage", "DPS", highlights.TopDamage, targets);
            Performers(text, report, highlights.Kill, "💚 Top healing", "HPS", highlights.TopHealing, targets);
            Quality(text, highlights.Quality);
        }

        return text.ToString();
    }

    /// <summary>Kicks and dispels on the latest finished pull, with who did the most.</summary>
    private static void Utility(StringBuilder text, RaidRecapLiveUtility u)
    {
        static string Names(IEnumerable<RaidRecapLiveCount> counts) =>
            string.Join(" · ", counts.Select(c => $"{RaidRecapRules.Text(c.Name, 24)} {RaidRecapFormat.Count(c.Count)}"));

        if (u.HasKicks)
        {
            text.AppendLine($"✋ **Kicks** · #{u.Pull.Id} · {(u.KicksComplete ? "" : "at least ")}{RaidRecapFormat.Count(u.Kicks)} stopped"
                + (u.WentOff > 0 ? $" · {RaidRecapFormat.Count(u.WentOff)} went off" : ""));
            if (u.Kickers.Count > 0)
            {
                text.AppendLine("-# " + Names(u.Kickers));
            }
        }

        if (u.HasDispels)
        {
            text.AppendLine($"✨ **Dispels** · #{u.Pull.Id} · {(u.DispelsComplete ? "" : "at least ")}{RaidRecapFormat.Count(u.Dispels)}");
            var parts = new List<string>();
            if (u.Debuffs.Count > 0)
            {
                parts.Add(Names(u.Debuffs));
            }

            if (u.Dispellers.Count > 0)
            {
                parts.Add("by " + Names(u.Dispellers));
            }

            if (parts.Count > 0)
            {
                text.AppendLine("-# " + string.Join(" · ", parts));
            }
        }
    }

    /// <summary>One small line for the whole raid: average parses, then speed and execution.</summary>
    private static void Quality(StringBuilder text, RaidRecapLiveKillQuality quality)
    {
        if (quality == null)
        {
            return;
        }

        var parts = new List<string>();
        static string Dot(double? value)
        {
            var badge = RaidRecapParsePalette.Badge(value);
            return $"{badge.Emoji} **{badge.Display}**";
        }

        if (quality.DamageAverage.HasValue)
        {
            parts.Add($"damage parse avg {Dot(quality.DamageAverage)}");
        }

        if (quality.HealingAverage.HasValue)
        {
            parts.Add($"healing avg {Dot(quality.HealingAverage)}");
        }

        if (quality.Speed.HasValue)
        {
            parts.Add($"speed {Dot(quality.Speed)}");
        }

        if (quality.Execution.HasValue)
        {
            parts.Add($"execution {Dot(quality.Execution)}");
        }

        if (parts.Count > 0)
        {
            text.AppendLine("-# Raid: " + string.Join(" · ", parts));
        }
    }

    private static void Performers(
        StringBuilder text,
        RaidRecapReport report,
        RaidRecapFight kill,
        string title,
        string metric,
        IReadOnlyList<RaidRecapLivePerformer> rows,
        bool targets)
    {
        if (rows.Count == 0)
        {
            return;
        }

        text.AppendLine($"**{title}**");
        for (var i = 0; i < rows.Count; i++)
        {
            var medal = i switch { 0 => "🥇", 1 => "🥈", _ => "🥉" };
            var badge = RaidRecapParsePalette.Badge(rows[i].Percentile);
            text.AppendLine($"{medal} {PerformerName(report, kill, rows[i])}{rows[i].Identity} · **{RaidRecapFormat.Compact(rows[i].PerSecond)}** {metric}"
                + (rows[i].Percentile.HasValue && badge.Known ? $" · {badge.Emoji} **{badge.Display}**" : ""));
            if (targets && rows[i].Targets is { Count: > 0 } list)
            {
                text.AppendLine("-# ↳ " + string.Join(" · ", list.Select(t =>
                    $"{(t.Self ? "self" : t.Pet ? "pet" : RaidRecapRules.Text(t.Name, 24))} "
                    + (t.Amount is > 0 ? $"{RaidRecapFormat.Compact(t.Amount)} ({Share(t.Share)})" : Share(t.Share)))));
            }
        }
    }

    private static string Share(double share)
    {
        var percent = share * 100;
        return percent is > 0 and < 1 ? "<1%" : Math.Round(percent).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string PerformerName(RaidRecapReport report, RaidRecapFight kill, RaidRecapLivePerformer row)
    {
        if (string.IsNullOrWhiteSpace(row.Name))
        {
            return "";
        }

        // Link only a player matched to the roster, and only when the kill is in this report.
        if (row.ActorId is int actor && actor > 0 && report.Fights.Contains(kill))
        {
            return $"**{RaidRecapLinks.Name(report, kill, actor, row.Name, 30)}** · ";
        }

        return $"**{RaidRecapRules.Text(row.Name, 30)}** · ";
    }

    /// <summary>
    /// Boss health left on each pull in order, e.g. `62 · 55 · 41 · 12 · ✅`. Shows the latest
    /// twelve pulls. ✅ is a kill, ⏳ has no result yet and ? has no recorded health.
    /// </summary>
    internal static string PullStrip(RaidRecapBoss boss)
    {
        var pulls = boss.Chronological;
        var shown = pulls.Skip(Math.Max(0, pulls.Count - StripPulls)).Select(StripValue);
        return "`" + (pulls.Count > StripPulls ? "… " : "") + string.Join(" · ", shown) + "`";
    }

    private static string StripValue(RaidRecapFight fight)
    {
        if (fight.IsKill)
        {
            return "✅";
        }

        if (!fight.IsWipe)
        {
            return "⏳";
        }

        if (fight.Remaining is not double left)
        {
            return "?";
        }

        return left > 0 && left < 1 ? "<1" : Math.Round(left).ToString("0", CultureInfo.InvariantCulture);
    }

    private static string LiveFooter(RaidRecapLiveInfo info, bool ended, DateTimeOffset updated)
    {
        var owner = "";
        if (!string.IsNullOrWhiteSpace(info?.GuildName))
        {
            owner = RaidRecapRules.Text(info.GuildName, 60);
            if (!string.IsNullOrWhiteSpace(info.Region))
            {
                owner += $" ({RaidRecapRules.Text(info.Region.ToUpperInvariant(), 4)})";
            }

            owner += " | ";
        }

        return $"-# {owner}Data from {RaidRecapFormat.Source} · updated <t:{updated.ToUnixTimeSeconds()}:R>"
            + (ended ? " · final" : " · refreshes while the raid is live");
    }

    private static ActionRowBuilder LiveButtons(RaidRecapReport report) => new ActionRowBuilder()
        .WithButton(new ButtonBuilder(RaidRecapFormat.Source, style: ButtonStyle.Link, url: report.Url))
        .WithButton(new ButtonBuilder("Open my recap", $"{LiveOpenId}~{report.Code}", ButtonStyle.Primary, emote: new Emoji("🔍")));
}
