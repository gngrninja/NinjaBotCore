using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Modules.Wow;

/// <summary>
/// Row text for players: damage / healing cards and the per-player lenses. Rows are packed
/// into whole pages so nothing is cut off mid-row.
/// </summary>
public static class RaidRecapPlayerPresentation
{
    public static string Identity(RaidRecapPlayer p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Class))
        {
            return "👤 Class unknown";
        }

        var spec = string.IsNullOrWhiteSpace(p.Spec) ? "" : RaidRecapFormat.ClassName(p.Spec) + " ";
        return $"{RaidRecapFormat.RoleEmoji(p.Role)} {spec}{RaidRecapFormat.ClassName(p.Class)}";
    }

    /// <summary>Parse dot and number, e.g. "🟣 **82**". Numbers round down.</summary>
    public static string Badge(RaidRecapParse p)
    {
        var badge = RaidRecapParsePalette.Badge(p?.Percentile);
        return p == null || !badge.Known ? "no parse" : $"{badge.Emoji} **{badge.Display}**";
    }

    public static RaidRecapParse SelectedParse(RaidRecapPlayerPanel panel)
    {
        if (panel?.Selected is not { } player)
        {
            return null;
        }

        var output = panel.Lens == "damage" ? panel.Damage : panel.Lens == "healing" ? panel.Healing : null;
        return output?.Parses?.Entries.SingleOrDefault(p => p.ActorId == player.ActorId);
    }

    public static string Standing(RaidRecapReport report, RaidRecapFight fight, RaidRecapStanding row, int position, string metric)
    {
        var medal = position switch { 1 => "🥇 ", 2 => "🥈 ", 3 => "🥉 ", _ => "" };
        var name = row.Player == null
            ? RaidRecapRules.Text(row.Name, 45)
            : RaidRecapLinks.Name(report, fight, row.Player.ActorId, row.Player.Name);
        var identity = row.Player == null ? "Not matched to the raid roster" : Identity(row.Player);
        return $"{position}. {medal}**{name}** · **{RaidRecapFormat.Compact(row.PerSecond)}** {metric} · {Badge(row.Parse)}\n-# {identity}";
    }

    public static string Amount(double? value) => RaidRecapFormat.Count(value);

    public static string Elapsed(double value) => RaidRecapFormat.Elapsed(value);

    public static IReadOnlyList<IReadOnlyList<string>> Pack(IReadOnlyList<string> rows, int rowLimit = 8, int textLimit = 1800)
    {
        var pages = new List<IReadOnlyList<string>>();
        var page = new List<string>();
        var length = 0;
        foreach (var row in rows)
        {
            if (row.Length + 1 > textLimit)
            {
                throw new InvalidOperationException("An evidence row exceeds the presentation budget.");
            }

            if (page.Count == rowLimit || length + row.Length + 1 > textLimit)
            {
                pages.Add(page.ToArray());
                page.Clear();
                length = 0;
            }

            page.Add(row);
            length += row.Length + 1;
        }

        if (page.Count > 0 || pages.Count == 0)
        {
            pages.Add(page.ToArray());
        }

        return pages;
    }

    public static string Question(string lens) => lens switch
    {
        "deaths" => "What happened just before each death?",
        "interrupts" => "Do these match the kick rotation?",
        "dispels" => "Was the timing right for the strategy?",
        RaidRecapMechanics.Junk or RaidRecapMechanics.Spin => "What was going on around each hit?",
        "damage" or "healing" => "Does this fit their spec and job on this fight?",
        _ => "Pick what to look at in the menu below."
    };

    public static IReadOnlyList<IReadOnlyList<string>> OutputPages(RaidRecapSession s)
    {
        var fight = s.Report?.Fights.Where(f => f.IsKill).ElementAtOrDefault(s.KillIndex);
        var metric = s.View == "healing" ? "HPS" : "DPS";
        // Leave room for the heading, notices, controls and open help. The same whole-row pages
        // are used with help open or closed, so toggling it never moves a player.
        var rows = (s.Performance ?? Array.Empty<RaidRecapStanding>())
            .Select((row, index) => Standing(s.Report, fight, row, index + 1, metric))
            .ToArray();
        return Pack(rows, 5, 1400);
    }

    public static IReadOnlyList<IReadOnlyList<string>> Pages(RaidRecapSession s) => Pack(Rows(s));

    public static IReadOnlyList<string> Rows(RaidRecapSession s)
    {
        var p = s.PlayerPanel;
        var rows = new List<string>();
        if (p?.Selected == null)
        {
            return rows;
        }

        if (p.Lens == "summary")
        {
            rows.Add(OutputSummary(p, p.Damage, "damage", "DPS"));
            rows.Add(OutputSummary(p, p.Healing, "healing", "HPS"));
            foreach (var lens in new[] { "deaths", "interrupts", "dispels", RaidRecapMechanics.Junk, RaidRecapMechanics.Spin })
            {
                rows.Add(ObservationSummary(p, lens));
            }

            return rows;
        }

        if (p.Lens is "damage" or "healing")
        {
            return OutputRows(s, p, rows);
        }

        if (!p.Observations.TryGetValue(p.Lens, out var analysis))
        {
            rows.Add("Couldn't load this. Choose it again to retry.");
            return rows;
        }

        if (p.Lens == "deaths")
        {
            rows.AddRange(analysis.Deaths
                .Where(d => d.ActorId == p.ActorId)
                .Select(d => $"`{Elapsed(d.ElapsedMs)}` {RaidRecapRules.Text(d.Ability, 55)}"));
        }
        else if (RaidRecapMechanics.IsMetric(p.Lens))
        {
            rows.AddRange((analysis.Mechanic?.Events ?? Array.Empty<RaidRecapMechanicEvent>())
                .Where(d => d.ActorId == p.ActorId)
                .Select(d => $"`{Elapsed(d.ElapsedMs)}` {MechanicName(p.Lens)}"));
        }
        else
        {
            foreach (var spell in analysis.Utility)
            {
                rows.AddRange(spell.Participants
                    .Where(d => d.VerifiedPlayer && d.ActorId == p.ActorId)
                    .Select(d => $"**{RaidRecapRules.Text(spell.Name, 55)}** · {Amount(d.Count)}"));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(analysis.Complete && p.Lens == "deaths"
                ? "No deaths on this pull."
                : "Nothing recorded for this player.");
        }

        return rows;
    }

    private static IReadOnlyList<string> OutputRows(RaidRecapSession s, RaidRecapPlayerPanel p, List<string> rows)
    {
        var output = p.Lens == "damage" ? p.Damage : p.Healing;
        if (output == null)
        {
            rows.Add(p.Fight.IsKill
                ? "Couldn't load this. Choose it again to retry."
                : "Damage and healing need a kill. Wipes have no parses.");
            return rows;
        }

        var standing = output.Rows.SingleOrDefault(r => r.Player?.ActorId == p.ActorId);
        var parse = SelectedParse(p);
        if (standing == null)
        {
            rows.Add("No row for this player on this kill.");
            rows.Add(Badge(parse));
        }
        else
        {
            var position = output.Rows.ToList().IndexOf(standing) + 1;
            rows.Add($"**{RaidRecapFormat.Compact(standing.PerSecond)}** {(p.Lens == "damage" ? "DPS" : "HPS")} · {Badge(parse)}"
                + $" · {RaidRecapFormat.Ordinal(position)} of {output.Rows.Count}");
        }

        if (parse != null)
        {
            var itemLevel = RaidRecapParsePalette.Badge(parse.ItemLevelPercentile);
            rows.Add($"Against **{parse.TotalParses:N0}** parses"
                + (itemLevel.Known ? $" · ilvl parse {itemLevel.Emoji} **{itemLevel.Display}**" : "")
                + (parse.ItemLevel.HasValue ? $" · ilvl {parse.ItemLevel}" : ""));
        }

        rows.Add(output.Parses is { } context
            ? $"-# Parses checked <t:{context.AsOf.ToUnixTimeSeconds()}:R>"
            : "-# Parses are unavailable right now.");
        return rows;
    }

    private static string OutputSummary(RaidRecapPlayerPanel p, RaidRecapOutput output, string lens, string metric)
    {
        if (output == null)
        {
            return $"{Label(lens)} · not loaded";
        }

        var standing = output.Rows.SingleOrDefault(r => r.Player?.ActorId == p.ActorId);
        if (standing == null)
        {
            return $"{Label(lens)} · no row";
        }

        var parse = output.Parses?.Entries.SingleOrDefault(e => e.ActorId == p.ActorId);
        return $"{Label(lens)} · **{RaidRecapFormat.Compact(standing.PerSecond)}** {metric} · {Badge(parse)}";
    }

    private static string ObservationSummary(RaidRecapPlayerPanel p, string lens)
    {
        if (!p.Observations.TryGetValue(lens, out var analysis))
        {
            return $"{Label(lens)} · not loaded";
        }

        string count;
        if (lens == "deaths")
        {
            count = RaidRecapFormat.Plural(analysis.Deaths.Count(d => d.ActorId == p.ActorId), "death");
        }
        else if (RaidRecapMechanics.IsMetric(lens))
        {
            count = RaidRecapFormat.Plural(analysis.Mechanic?.Events.Count(d => d.ActorId == p.ActorId) ?? 0, "hit");
        }
        else
        {
            count = UtilitySummary(analysis, p.Selected.ActorId);
        }

        return $"{Label(lens)} · **{count}**" + (analysis.Complete ? "" : " · ⚠️ partial");
    }

    private static string UtilitySummary(RaidRecapAnalysis analysis, int actorId)
    {
        var participants = analysis.Utility
            .SelectMany(u => u.Participants)
            .Where(p => p.VerifiedPlayer && p.ActorId == actorId)
            .ToArray();
        var known = participants.Where(p => p.Count.HasValue).ToArray();
        if (known.Length == 0)
        {
            // No row for this player is an observation, not a proven zero.
            return participants.Length == 0 ? "none recorded" : "unknown";
        }

        var total = Amount(known.Sum(p => p.Count.Value));
        return analysis.Complete && known.Length == participants.Length ? total : "at least " + total;
    }

    public static IReadOnlyList<RaidRecapDeath> FirstLosses(IReadOnlyList<RaidRecapDeath> deaths)
    {
        if (deaths.Count == 0)
        {
            return Array.Empty<RaidRecapDeath>();
        }

        var first = deaths.Min(d => d.ElapsedMs);
        return deaths.Where(d => d.ElapsedMs == first).GroupBy(d => d.ActorId).Select(g => g.First()).ToArray();
    }

    public static IReadOnlyList<string> FirstLossRows(RaidRecapSession s)
    {
        var result = RaidRecapReview.Current(s);
        if (result == null || !result.DeathsA.Complete || !result.DeathsB.Complete)
        {
            return Array.Empty<string>();
        }

        return new[] { ("A", result.A, result.DeathsA), ("B", result.B, result.DeathsB) }
            .SelectMany(pair => FirstLosses(pair.Item3.Deaths)
                .Select(d => $"**{pair.Item1}** · `{Elapsed(d.ElapsedMs)}` {RaidRecapLinks.Name(s.Report, pair.Item2, d.ActorId, d.Name)} · {RaidRecapRules.Text(d.Ability, 55)}"))
            .ToArray();
    }

    private static string MechanicName(string lens) => lens == RaidRecapMechanics.Junk ? "Throw Junk" : "Shell Spin";

    public static string Label(string lens) => lens switch
    {
        "summary" => "📋 Summary",
        "damage" => "⚔️ Damage",
        "healing" => "💚 Healing",
        "deaths" => "☠️ Deaths",
        "interrupts" => "✋ Interrupts",
        "dispels" => "✨ Dispels",
        RaidRecapMechanics.Junk => "🔎 Throw Junk",
        RaidRecapMechanics.Spin => "🔎 Shell Spin",
        _ => "Players"
    };
}
