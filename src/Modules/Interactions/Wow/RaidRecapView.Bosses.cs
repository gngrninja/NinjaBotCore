using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public static partial class RaidRecapView
{
    private const int PullsPerPage = 10;

    private static void Bosses(ContainerBuilder body, StringBuilder text, ComponentBuilder controls, RaidRecapSession s)
    {
        var report = s.Report;
        AppendNotice(text, s.Notice);
        if (report.Bosses.Count == 0)
        {
            text.AppendLine("## 🐉 Bosses");
            text.AppendLine("No boss pulls in this log yet. Trash is left out.");
            return;
        }

        var boss = report.Bosses[Math.Clamp(s.BossIndex, 0, report.Bosses.Count - 1)];
        var unfinished = boss.Attempts.Count - boss.Completed;
        text.AppendLine($"## 🐉 {RaidRecapRules.Text(boss.Name, 100)} · {RaidRecapFormat.Difficulty(boss.Difficulty)}");
        text.AppendLine($"**{RaidRecapFormat.Plural(boss.Attempts.Count, "pull")}** · ✅ {boss.Kills} · 💀 {boss.Wipes}"
            + (unfinished > 0 ? $" · ⏳ {unfinished}" : ""));
        if (boss.Wipes > 0)
        {
            text.AppendLine($"Best pull **{RaidRecapFormat.Percent(boss.BestRemaining)}** · Last wipe **{RaidRecapFormat.Percent(boss.LatestRemaining)}**");
        }

        if (boss.Attempts.Count > 1)
        {
            // The whole boss at a glance: how each pull ended, in order.
            text.AppendLine(PullStrip(boss));
        }

        if (boss.Kills > 0)
        {
            text.AppendLine($"Fastest kill **{RaidRecapFormat.Clock(boss.FastestKillMs)}**");
        }

        if (boss.Completed > 0)
        {
            text.AppendLine($"Time in combat **{RaidRecapFormat.Clock(boss.CombatMs)}**"
                + Timed(boss.KnownDurations, boss.Completed, "pull"));
        }

        if (boss.Wipes > 0)
        {
            text.AppendLine($"Median wipe **{RaidRecapFormat.Clock(boss.MedianWipeMs)}**"
                + Timed(boss.WipeDurationSamples, boss.Wipes, "wipe"));
        }

        var pages = PageCount(boss.Attempts.Count, PullsPerPage);
        text.AppendLine();
        text.AppendLine($"**Pulls** · page {s.AttemptPage + 1}/{pages}");
        foreach (var fight in boss.Chronological.Skip(s.AttemptPage * PullsPerPage).Take(PullsPerPage))
        {
            text.AppendLine(PullLine(report, fight));
        }

        // The SDK inserts absent rows; create earlier rows before the paired controls.
        Menu(controls, s, "boss", report.Bosses.Select(BossOption).ToArray(), s.BossPage, s.BossIndex);
        Pages(controls, s, "bosses", s.BossPage, report.Bosses.Count, 3);
        controls.WithButton(
            label: "Previous attempts",
            customId: Nav(s, "attempts_prev"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("◀️"),
            disabled: s.AttemptPage == 0,
            row: 4);
        controls.WithButton(
            label: "Next attempts",
            customId: Nav(s, "attempts_next"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("▶️"),
            disabled: (s.AttemptPage + 1) * PullsPerPage >= boss.Attempts.Count,
            row: 4);
    }

    private static (string Label, string Emoji) BossOption(RaidRecapBoss boss)
    {
        var label = $"{RaidRecapRules.Plain(boss.Name, 65)} · {RaidRecapFormat.Difficulty(boss.Difficulty)}";
        return (label, boss.Kills > 0 ? "✅" : boss.Wipes > 0 ? "🟠" : "⏳");
    }

    private static string Timed(int known, int total, string noun) =>
        known < total ? $" · {known} of {RaidRecapFormat.Plural(total, noun)} timed" : "";

    private static string PullLine(RaidRecapReport report, RaidRecapFight fight)
    {
        var line = $"[#{fight.Id}]({RaidRecapLinks.Fight(report, fight)}) {RaidRecapFormat.OutcomeEmoji(fight)} {RaidRecapFormat.Outcome(fight)}";
        if (fight.DurationMs.HasValue)
        {
            line += " · " + RaidRecapFormat.Clock(fight.DurationMs);
        }

        if (!fight.IsKill && fight.Remaining.HasValue)
        {
            line += $" · **{RaidRecapFormat.Percent(fight.Remaining)}**";
        }

        return line;
    }

    // ===== Compare two pulls =====

    private static void Comparison(ContainerBuilder body, StringBuilder text, ComponentBuilder controls, RaidRecapSession s)
    {
        var report = s.Report;
        var (a, b) = RaidRecapReview.Selection(s);
        AppendNotice(text, s.Notice);

        if (s.CompareLosses && RaidRecapReview.Current(s) is { } ties && ties.DeathsA.Complete && ties.DeathsB.Complete)
        {
            FirstDeaths(text, controls, s, a, b);
            return;
        }

        if (a == null)
        {
            text.AppendLine("## 🔁 Compare pulls");
            text.AppendLine(RaidRecapReview.NoPair);
            return;
        }

        text.AppendLine($"## 🔁 Compare pulls · {RaidRecapRules.Text(a.Name, 80)} · {RaidRecapFormat.Difficulty(a.Difficulty)}");
        foreach (var (label, fight) in new[] { ("A", a), ("B", b) })
        {
            text.AppendLine($"**{label}** · {FightLink(report, fight, "deaths")} · {RaidRecapFormat.Clock(fight.DurationMs)}"
                + (fight.IsKill ? "" : $" · {RaidRecapFormat.Percent(fight.Remaining)}"));
        }

        var candidates = RaidRecapReview.Candidates(report.Bosses[s.BossIndex]);
        CompareMenu(controls, s, candidates, "a", s.CompareAPage, s.CompareAIndex, 2);
        CompareMenu(controls, s, candidates, "b", s.CompareBPage, s.CompareBIndex, 3);
        controls.WithButton("A previous", Nav(s, "compare_a_prev"), ButtonStyle.Secondary, disabled: s.CompareAPage == 0, row: 4);
        controls.WithButton("A next", Nav(s, "compare_a_next"), ButtonStyle.Secondary, disabled: (s.CompareAPage + 1) * 25 >= candidates.Count, row: 4);
        controls.WithButton("B previous", Nav(s, "compare_b_prev"), ButtonStyle.Secondary, disabled: s.CompareBPage == 0, row: 4);
        controls.WithButton("B next", Nav(s, "compare_b_next"), ButtonStyle.Secondary, disabled: (s.CompareBPage + 1) * 25 >= candidates.Count, row: 4);
        controls.WithButton("Compare deaths", Nav(s, "compare_deaths"), ButtonStyle.Success, new Emoji("☠️"), row: 4);

        var result = RaidRecapReview.Current(s);
        if (result == null)
        {
            text.AppendLine();
            text.AppendLine("Pick A and B below, then press **Compare deaths**.");
            return;
        }

        var complete = result.DeathsA.Complete && result.DeathsB.Complete;
        text.AppendLine();
        if (!complete)
        {
            text.AppendLine("⚠️ **Partial data** · counts are minimums, so changes and first deaths are hidden.");
        }

        text.AppendLine("**☠️ Whole pull**");
        text.AppendLine(DeathCount("A", result.DeathsA.Deaths, result.DeathsA.Complete, complete));
        text.AppendLine(DeathCount("B", result.DeathsB.Deaths, result.DeathsB.Complete, complete));

        var window = Math.Min(a.DurationMs.Value, b.DurationMs.Value);
        var windowA = result.DeathsA.Deaths.Where(d => d.ElapsedMs >= 0 && d.ElapsedMs <= window).ToArray();
        var windowB = result.DeathsB.Deaths.Where(d => d.ElapsedMs >= 0 && d.ElapsedMs <= window).ToArray();
        text.AppendLine();
        text.AppendLine($"**⏱️ First {RaidRecapFormat.Elapsed(window)} of each pull**");
        text.AppendLine(DeathCount("A", windowA, result.DeathsA.Complete, complete));
        text.AppendLine(DeathCount("B", windowB, result.DeathsB.Complete, complete));

        if (!complete)
        {
            return;
        }

        text.AppendLine($"Change B − A: **{Signed(windowB.Length - windowA.Length)}** deaths"
            + $" · **{Signed(Players(windowB) - Players(windowA))}** players");

        var first = new StringBuilder();
        foreach (var (label, fight, deaths) in new[] { ("A", a, result.DeathsA.Deaths), ("B", b, result.DeathsB.Deaths) })
        {
            var losses = RaidRecapPlayerPresentation.FirstLosses(deaths);
            if (losses.Count == 0)
            {
                continue;
            }

            first.AppendLine($"**{label}** · " + string.Join(", ", losses.Take(3).Select(d => RaidRecapLinks.Name(report, fight, d.ActorId, d.Name, 30)))
                + (losses.Count > 3 ? $" +{losses.Count - 3} more" : ""));
        }

        body.AddComponent(new TextDisplayBuilder(text.ToString()));
        text.Clear();
        body.AddComponent(new SectionBuilder(
            new ButtonBuilder("All first deaths", Nav(s, "compare_losses"), ButtonStyle.Secondary),
            new TextDisplayBuilder("**First to die**\n" + (first.Length == 0 ? "Nobody died on either pull." : first.ToString().TrimEnd()))));
    }

    private static void FirstDeaths(StringBuilder text, ComponentBuilder controls, RaidRecapSession s, RaidRecapFight a, RaidRecapFight b)
    {
        var pages = RaidRecapPlayerPresentation.Pack(RaidRecapPlayerPresentation.FirstLossRows(s));
        var page = Math.Clamp(s.CompareLossPage, 0, pages.Count - 1);
        text.AppendLine($"## 🔁 First to die · page {page + 1}/{pages.Count}");
        text.AppendLine($"A is #{a.Id} · B is #{b.Id} · players who died at the same moment are all listed");
        foreach (var line in pages[page])
        {
            text.AppendLine(line);
        }

        controls.WithButton("Comparison", Nav(s, "compare_return"), ButtonStyle.Secondary, new Emoji("↩️"), row: 2);
        controls.WithButton("Previous ties", Nav(s, "compare_loss_prev"), ButtonStyle.Secondary, new Emoji("◀️"), disabled: page == 0, row: 2);
        controls.WithButton("Next ties", Nav(s, "compare_loss_next"), ButtonStyle.Secondary, new Emoji("▶️"), disabled: page == pages.Count - 1, row: 2);
    }

    private static string DeathCount(string label, IReadOnlyList<RaidRecapDeath> deaths, bool complete, bool showFirst)
    {
        var line = $"**{label}** · {RaidRecapFormat.Plural(deaths.Count, "death")}"
            + $" ({RaidRecapFormat.Plural(Players(deaths), "player")})";
        if (!complete)
        {
            return line + " · at least";
        }

        if (!showFirst)
        {
            return line;
        }

        if (deaths.Count == 0)
        {
            return $"**{label}** · no deaths";
        }

        var first = deaths.Min(d => d.ElapsedMs);
        var tied = deaths.Where(d => d.ElapsedMs == first).Select(d => d.ActorId).Distinct().Count();
        return line + $" · first at {RaidRecapFormat.Elapsed(first)}" + (tied > 1 ? $" ({tied} together)" : "");
    }

    private static int Players(IReadOnlyList<RaidRecapDeath> deaths) => deaths.Select(d => d.ActorId).Distinct().Count();

    private static string Signed(int value) =>
        value > 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture).Replace("-", "−");

    private static void CompareMenu(
        ComponentBuilder controls,
        RaidRecapSession s,
        IReadOnlyList<RaidRecapFight> candidates,
        string side,
        int page,
        int selected,
        int row)
    {
        var name = side.ToUpperInvariant();
        var menu = new SelectMenuBuilder()
            .WithCustomId(Pick(s, "compare_" + side))
            .WithPlaceholder($"Pull {name} · page {page + 1}/{PageCount(candidates.Count, 25)}")
            .WithMinValues(1)
            .WithMaxValues(1);
        for (var i = page * 25; i < Math.Min(candidates.Count, (page + 1) * 25); i++)
        {
            var fight = candidates[i];
            menu.AddOption(
                label: $"{name} · {RaidRecapFormat.Outcome(fight)} #{fight.Id} · {RaidRecapFormat.Clock(fight.DurationMs)}"
                    + (fight.IsKill || !fight.Remaining.HasValue ? "" : " · " + RaidRecapFormat.Percent(fight.Remaining)),
                value: i.ToString(CultureInfo.InvariantCulture),
                emote: new Emoji(RaidRecapFormat.OutcomeEmoji(fight)),
                isDefault: i == selected);
        }

        controls.WithSelectMenu(menu, row: row);
    }
}
