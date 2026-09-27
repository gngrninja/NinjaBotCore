using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public static partial class RaidRecapView
{
    private static readonly (string Metric, string Label, string Emoji)[] Subviews =
    {
        ("deaths", "Deaths", "☠️"),
        ("incoming", "Damage taken", "🔥"),
        ("interrupts", "Interrupts", "✋"),
        ("dispels", "Dispels", "✨"),
        ("mechanics", "Mechanics", "🔎")
    };

    /// <summary>Renders one pull's analysis and returns the help topic for the footer.</summary>
    private static string Analysis(ContainerBuilder body, StringBuilder text, ComponentBuilder controls, RaidRecapSession s)
    {
        var report = s.Report;
        var pulls = report.CompletedPulls;
        AppendNotice(text, s.Notice);
        if (pulls.Count == 0)
        {
            text.AppendLine("## 🔬 Analysis");
            text.AppendLine("No finished boss pulls in this log yet.");
            return "analysis";
        }

        var fight = pulls[Math.Clamp(s.PullIndex, 0, pulls.Count - 1)];
        var metric = s.AnalysisMetric;
        var mechanic = RaidRecapMechanics.Rule(metric);

        text.AppendLine($"## 🔬 {RaidRecapRules.Text(fight.Name, 80)} · {RaidRecapFormat.Difficulty(fight.Difficulty)}");
        text.AppendLine($"{FightLink(report, fight, mechanic == null ? WclView(metric) : null)} · {RaidRecapFormat.Clock(fight.DurationMs)}"
            + (fight.IsKill || !fight.Remaining.HasValue ? "" : $" · {RaidRecapFormat.Percent(fight.Remaining)}"));

        Menu(
            controls,
            s,
            "pull",
            pulls.Select(f => (FightLabel(f, 40), RaidRecapFormat.OutcomeEmoji(f))).ToArray(),
            s.PullPage,
            s.PullIndex);
        foreach (var subview in Subviews)
        {
            var active = metric == subview.Metric || (subview.Metric == "mechanics" && mechanic != null);
            controls.WithButton(
                label: subview.Label,
                customId: Nav(s, subview.Metric),
                style: active ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji(subview.Emoji),
                row: 3);
        }

        controls.WithButton(
            label: "Previous pulls",
            customId: Nav(s, "pulls_prev"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("◀️"),
            disabled: s.PullPage == 0,
            row: 4);
        controls.WithButton(
            label: "Next pulls",
            customId: Nav(s, "pulls_next"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("▶️"),
            disabled: (s.PullPage + 1) * 25 >= pulls.Count,
            row: 4);

        if (mechanic != null)
        {
            Mechanic(body, text, controls, s, fight, mechanic);
            return "mechanics";
        }

        var analysis = s.Analysis;
        if (analysis == null || analysis.Metric != metric)
        {
            text.AppendLine();
            text.AppendLine($"This analysis is unavailable. Pick a tab below to retry, press Refresh, or open the fight on {RaidRecapFormat.Source}.");
            return metric;
        }

        text.AppendLine();
        if (!analysis.Complete)
        {
            text.AppendLine("⚠️ **Partial data** · " + RaidRecapRules.Text(analysis.Notice ?? "Data incomplete", 180));
        }

        var lines = metric switch
        {
            "deaths" => Deaths(text, report, fight, analysis),
            "incoming" => Incoming(text, analysis),
            _ => Utility(text, report, fight, analysis, metric)
        };

        RowPages(text, controls, s, lines);
        return metric;
    }

    private static string WclView(string metric) => metric == "incoming" ? "damage-taken" : metric;

    private static List<string> Deaths(StringBuilder text, RaidRecapReport report, RaidRecapFight fight, RaidRecapAnalysis analysis)
    {
        var lines = new List<string>();
        var summary = $"**☠️ {RaidRecapFormat.Plural(analysis.Deaths.Count, "death")}**"
            + $" · {RaidRecapFormat.Plural(analysis.DistinctPlayers, "player")}";
        if (!analysis.Complete && analysis.Deaths.Count == 0)
        {
            text.AppendLine("**☠️ No deaths recorded yet.** The data is partial.");
        }
        else if (!analysis.Complete)
        {
            text.AppendLine(summary + " · at least");
        }
        else if (analysis.Deaths.Count == 0)
        {
            text.AppendLine("**☠️ No player deaths** on this pull.");
        }
        else
        {
            var first = analysis.Deaths.Min(d => d.ElapsedMs);
            var tied = analysis.Deaths.Where(d => d.ElapsedMs == first).Select(d => d.ActorId).Distinct().Count();
            text.AppendLine(summary + $" · first at **{RaidRecapFormat.Elapsed(first)}**" + (tied > 1 ? $" ({tied} together)" : ""));
        }

        var seen = new Dictionary<int, int>();
        foreach (var death in analysis.Deaths)
        {
            seen.TryGetValue(death.ActorId, out var count);
            seen[death.ActorId] = ++count;
            lines.Add($"`{RaidRecapFormat.Elapsed(death.ElapsedMs)}` {RaidRecapLinks.Name(report, fight, death.ActorId, death.Name)}"
                + $" · {RaidRecapRules.Text(death.Ability, 55)}"
                + (count > 1 ? $" · {RaidRecapFormat.Ordinal(count)} death" : ""));
        }

        return lines;
    }

    private static List<string> Incoming(StringBuilder text, RaidRecapAnalysis analysis)
    {
        text.AppendLine("**🔥 Damage taken** by ability, whole raid");
        if (analysis.Incoming.Count == 0 && analysis.Complete)
        {
            text.AppendLine("No damage taken rows for this pull.");
        }

        return analysis.Incoming
            .Select(row => $"**{RaidRecapRules.Text(row.Name, 55)}** · {RaidRecapFormat.Compact(row.Total)} · {RaidRecapRules.Text(row.Source, 50)}")
            .ToList();
    }

    private static List<string> Utility(StringBuilder text, RaidRecapReport report, RaidRecapFight fight, RaidRecapAnalysis analysis, string metric)
    {
        var interrupts = metric == "interrupts";
        text.AppendLine(interrupts ? "**✋ Interrupts** by enemy spell" : "**✨ Dispels** by debuff");
        if (analysis.Utility.Count == 0 && analysis.Complete)
        {
            text.AppendLine(interrupts ? "No interrupts on this pull." : "No dispels on this pull.");
        }

        var lines = new List<string>();
        foreach (var spell in analysis.Utility)
        {
            var name = RaidRecapRules.Text(spell.Name, 50);
            var line = $"**{name}** · {RaidRecapFormat.Count(spell.Actions)} {(interrupts ? "kicked" : "dispelled")}";
            if (interrupts)
            {
                line += $" · {RaidRecapFormat.Count(spell.CompletedCasts)} went off";
                if (spell.Channels is > 0)
                {
                    line += $" · {RaidRecapFormat.Count(spell.Channels)} channels stopped";
                }
            }

            if (!spell.AttributionKnown)
            {
                line += " · players unknown";
            }

            lines.Add(line);
            foreach (var participant in spell.Participants)
            {
                var who = participant.VerifiedPlayer && participant.ActorId is int actorId
                    ? RaidRecapLinks.Name(report, fight, actorId, participant.Name, 40)
                    : RaidRecapRules.Text(participant.Name, 40) + " (not on roster)";
                lines.Add($"↳ {who} · {RaidRecapFormat.Count(participant.Count)} · {name}");
            }
        }

        return lines;
    }

    private static void RowPages(StringBuilder text, ComponentBuilder controls, RaidRecapSession s, IReadOnlyList<string> lines)
    {
        var size = RaidRecapAnalysisRules.PageSize;
        var page = Math.Clamp(s.AnalysisPage, 0, Math.Max(0, (lines.Count - 1) / size));
        if (lines.Count > 0)
        {
            text.AppendLine($"-# page {page + 1}/{PageCount(lines.Count, size)}");
        }

        foreach (var line in lines.Skip(page * size).Take(size))
        {
            text.AppendLine(line);
        }

        controls.WithButton(
            label: "Previous rows",
            customId: Nav(s, "analysis_prev"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("◀️"),
            disabled: page == 0,
            row: 4);
        controls.WithButton(
            label: "Next rows",
            customId: Nav(s, "analysis_next"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("▶️"),
            disabled: (page + 1) * size >= lines.Count,
            row: 4);
    }

    private static void Mechanic(
        ContainerBuilder body,
        StringBuilder text,
        ComponentBuilder controls,
        RaidRecapSession s,
        RaidRecapFight fight,
        RaidRecapMechanicRule rule)
    {
        body.AddComponent(new TextDisplayBuilder(text.ToString()));
        text.Clear();

        // One switch keeps the card inside Discord's component budget.
        var other = RaidRecapMechanics.Rule(rule.Metric == RaidRecapMechanics.Junk ? RaidRecapMechanics.Spin : RaidRecapMechanics.Junk);
        body.AddComponent(new SectionBuilder(
            new ButtonBuilder(ShortName(other), Nav(s, other.Metric), ButtonStyle.Primary, emote: new Emoji("🔁")),
            new TextDisplayBuilder($"**🔎 {ShortName(rule)}** · [spell {rule.SpellId}]({s.Report.Url}#fight={fight.Id}&type={rule.View}&ability={rule.SpellId})")));

        var analysis = s.Analysis;
        var result = analysis?.Mechanic;
        if (analysis?.Metric != rule.Metric || result == null || result.SnapshotKey != s.Report.SnapshotKey
            || result.FightId != fight.Id || result.StartMs != fight.StartMs || result.EndMs != fight.EndMs)
        {
            text.AppendLine($"This mechanic is unavailable. Switch mechanic to retry, press Refresh, or open the fight on {RaidRecapFormat.Source}.");
            return;
        }

        if (result.Coverage == "unsupported")
        {
            text.AppendLine("**Not supported for this fight** · " + RaidRecapRules.Text(analysis.Notice, 250));
            return;
        }

        if (!analysis.Complete)
        {
            text.AppendLine("⚠️ **Partial data** · counts are minimums.");
        }

        var noun = rule.Metric == RaidRecapMechanics.Junk ? "hit" : "application";
        if (result.Events.Count == 0 && analysis.Complete)
        {
            text.AppendLine($"**No {noun}s** on players this pull.");
        }
        else
        {
            text.AppendLine($"**{RaidRecapFormat.Plural(result.Events.Count, noun)}**"
                + $" · {RaidRecapFormat.Plural(result.DistinctPlayers, "player")}"
                + (analysis.Complete ? "" : " · at least"));
        }

        var lines = result.Events
            .Select(row => $"`{RaidRecapFormat.Elapsed(row.ElapsedMs)}` {RaidRecapLinks.Name(s.Report, fight, row.ActorId, row.Name, 45)}")
            .ToArray();
        RowPages(text, controls, s, lines);
    }

    private static string ShortName(RaidRecapMechanicRule rule) =>
        rule.Metric == RaidRecapMechanics.Junk ? "Throw Junk" : "Shell Spin";
}
