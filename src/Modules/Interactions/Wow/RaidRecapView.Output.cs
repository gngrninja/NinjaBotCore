using System;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public static partial class RaidRecapView
{
    /// <summary>
    /// Damage / Healing for one boss kill. Each player is a card whose accent is their
    /// WarcraftLogs parse colour, in the same order WarcraftLogs lists them.
    /// </summary>
    private static MessageComponent Output(ContainerBuilder scope, ComponentBuilder controls, RaidRecapSession s)
    {
        var report = s.Report;
        var healing = s.View == "healing";
        var kills = report.Fights.Where(f => f.IsKill).ToArray();
        var fight = kills[Math.Clamp(s.KillIndex, 0, kills.Length - 1)];

        var heading = new StringBuilder();
        heading.AppendLine($"## {(healing ? "💚 Healing" : "⚔️ Damage")} · {RaidRecapRules.Text(fight.Name, 100)} · {RaidRecapFormat.Difficulty(fight.Difficulty)}");
        heading.AppendLine($"{FightLink(report, fight)} · {RaidRecapFormat.Clock(fight.DurationMs)}");
        AppendNotice(heading, s.Notice);
        AppendNotice(heading, s.PerformanceNotice, 200);
        if (s.Performance == null)
        {
            heading.AppendLine($"Couldn't load this kill. Press Refresh or open it on {RaidRecapFormat.Source}.");
        }
        else if (s.Performance.Count == 0)
        {
            heading.AppendLine($"{RaidRecapFormat.Source} returned no players for this kill.");
        }

        var pages = RaidRecapPlayerPresentation.OutputPages(s);
        var page = Math.Clamp(s.RankPage, 0, pages.Count - 1);
        var offset = pages.Take(page).Sum(p => p.Count);

        // Build earlier rows first: Discord.Net inserts absent row indices instead of reserving slots.
        Menu(
            controls,
            s,
            "kill",
            kills.Select(f => ($"{RaidRecapRules.Plain(f.Name, 60)} · {RaidRecapFormat.Difficulty(f.Difficulty)} · #{f.Id}", "✅")).ToArray(),
            s.KillPage,
            s.KillIndex);
        var nextRow = 3;
        if (kills.Length > 25)
        {
            controls.WithButton(
                label: "Previous kill options",
                customId: Nav(s, "kills_prev"),
                style: ButtonStyle.Secondary,
                emote: new Emoji("◀️"),
                disabled: s.KillPage == 0,
                row: nextRow);
            controls.WithButton(
                label: "Next kill options",
                customId: Nav(s, "kills_next"),
                style: ButtonStyle.Secondary,
                emote: new Emoji("▶️"),
                disabled: (s.KillPage + 1) * 25 >= kills.Length,
                row: nextRow);
            nextRow++;
        }

        if (pages.Count > 1)
        {
            controls.WithButton(
                label: "Previous players",
                customId: Nav(s, "ranks_prev"),
                style: ButtonStyle.Secondary,
                emote: new Emoji("◀️"),
                disabled: page == 0,
                row: nextRow);
            controls.WithButton(
                label: "Next players",
                customId: Nav(s, "ranks_next"),
                style: ButtonStyle.Secondary,
                emote: new Emoji("▶️"),
                disabled: page == pages.Count - 1,
                row: nextRow);
        }

        var rows = controls.Build().Components.OfType<ActionRowComponent>().ToArray();
        foreach (var row in rows.Take(2))
        {
            scope.AddComponent(new ActionRowBuilder(row));
        }

        scope.AddComponent(new TextDisplayBuilder(heading.ToString()));

        var result = new ComponentBuilderV2().AddComponent(scope);
        for (var i = 0; i < pages[page].Count; i++)
        {
            var source = s.Performance[offset + i];
            var accent = RaidRecapParsePalette.Badge(source.Parse?.Percentile).Color;
            result.AddComponent(new ContainerBuilder()
                .WithAccentColor(new Color(accent))
                .AddComponent(new TextDisplayBuilder(pages[page][i])));
        }

        var lead = new StringBuilder();
        if (s.Performance?.Count > 0)
        {
            lead.Append($"Players {offset + 1}–{offset + pages[page].Count} of {s.Performance.Count} · page {page + 1}/{pages.Count}");
        }

        if (kills.Length > 25)
        {
            if (lead.Length > 0)
            {
                lead.Append('\n');
            }

            lead.Append($"Kill list · page {s.KillPage + 1}/{PageCount(kills.Length, 25)}");
        }

        var detail = new ContainerBuilder().WithAccentColor(new Color(healing ? HealingColor : DamageColor));
        AddFooter(detail, s, healing ? "healing" : "damage", lead.ToString());
        foreach (var row in rows.Skip(2))
        {
            detail.AddComponent(new ActionRowBuilder(row));
        }

        return result.AddComponent(detail).Build();
    }
}
