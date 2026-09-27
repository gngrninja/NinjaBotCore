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
public sealed record RaidRecapLiveInfo(string GuildName, string Region, string ZoneName, string IconUrl);

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
}

/// <summary>
/// The detail on the live card: the first deaths of the latest wipe, by spec and class only,
/// and the top damage and healing of the latest kill, by name. Any part may be missing.
/// </summary>
public sealed record RaidRecapLiveHighlights
{
    public const int Shown = 3;

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
                ActorId = r.Player?.ActorId
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
    public static MessageComponent Live(
        RaidRecapReport report,
        RaidRecapLiveInfo info,
        bool ended,
        DateTimeOffset updated,
        RaidRecapLiveHighlights highlights = null)
    {
        var s = Session(report, info);
        var card = new ContainerBuilder().WithAccentColor(new Color(ended ? EndedColor : LiveColor));
        var details = ReportSubtitle(s, includeTitle: false);
        AddHeader(card, s, (ended ? "🏁 **Raid ended**" : "🔴 **LIVE**") + (details.Length == 0 ? "" : "\n" + details));
        card.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));

        var text = new StringBuilder();
        text.Append(Summary(report));
        text.Append(Current(report, ended));
        card.AddComponent(new TextDisplayBuilder(text.ToString()));

        var detail = Highlights(report, highlights);
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
        Report = report,
        GuildName = info?.GuildName,
        GuildRegion = info?.Region,
        GuildIconUrl = info?.IconUrl,
        Reports = string.IsNullOrWhiteSpace(info?.ZoneName)
            ? Array.Empty<WclV2Report>()
            : new[] { new WclV2Report { Code = report.Code, Zone = new WclV2Zone { Name = info.ZoneName } } }
    };

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
        if (boss.Attempts.Count > 1)
        {
            text.AppendLine(PullStrip(boss));
        }

        return text.ToString();
    }

    /// <summary>
    /// Deaths are shown by spec and class only. Top damage and healing are shown by name.
    /// </summary>
    private static string Highlights(RaidRecapReport report, RaidRecapLiveHighlights highlights)
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

        if (highlights?.Kill != null && (highlights.TopDamage.Count > 0 || highlights.TopHealing.Count > 0))
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.AppendLine($"**✅ {RaidRecapRules.Text(highlights.Kill.Name, 60)} kill** · {RaidRecapFormat.Clock(highlights.Kill.DurationMs)}");
            Performers(text, report, highlights.Kill, "⚔️ Top damage", "DPS", highlights.TopDamage);
            Performers(text, report, highlights.Kill, "💚 Top healing", "HPS", highlights.TopHealing);
        }

        return text.ToString();
    }

    private static void Performers(
        StringBuilder text,
        RaidRecapReport report,
        RaidRecapFight kill,
        string title,
        string metric,
        IReadOnlyList<RaidRecapLivePerformer> rows)
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
        }
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
