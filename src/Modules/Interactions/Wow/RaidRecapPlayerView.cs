using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>The Players view: pick a pull, pick a player, pick what to look at.</summary>
public static class RaidRecapPlayerView
{
    public static MessageComponent Build(RaidRecapSession s)
    {
        var panel = s.PlayerPanel;
        var fight = panel.Fight;

        var scope = new ContainerBuilder().WithAccentColor(new Color(0x5865F2));
        RaidRecapView.AddHeader(scope, s, RaidRecapView.ReportSubtitle(s));

        var controls = new ComponentBuilder();
        RaidRecapView.AddNavigation(controls, s, activeView: null);

        var parse = RaidRecapPlayerPresentation.SelectedParse(panel);
        var card = new ContainerBuilder().WithAccentColor(new Color(RaidRecapParsePalette.Badge(parse?.Percentile).Color));
        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(s.Notice))
        {
            text.AppendLine("> " + RaidRecapRules.Text(s.Notice, 260));
        }

        if (fight == null)
        {
            ChoosePull(text, controls, s);
            card.AddComponent(new TextDisplayBuilder(text.ToString()));
        }
        else
        {
            card.AddComponent(new SectionBuilder(
                new ButtonBuilder("Change pull", RaidRecapView.Nav(s, "player_change"), ButtonStyle.Secondary, emote: new Emoji("🔁")),
                new TextDisplayBuilder($"## 👥 {RaidRecapRules.Text(fight.Name, 60)} · {RaidRecapFormat.Difficulty(fight.Difficulty)}\n"
                    + $"{RaidRecapFormat.OutcomeEmoji(fight)} {RaidRecapFormat.Outcome(fight)} #{fight.Id} · {RaidRecapFormat.Clock(fight.DurationMs)}")));
            Roster(card, text, controls, s);
        }

        RaidRecapView.AddFooter(card, s, "players");

        var rows = controls.Build().Components.OfType<ActionRowComponent>().ToArray();
        foreach (var row in rows.Take(2))
        {
            scope.AddComponent(new ActionRowBuilder(row));
        }

        foreach (var row in rows.Skip(2))
        {
            card.AddComponent(new ActionRowBuilder(row));
        }

        return new ComponentBuilderV2().AddComponent(scope).AddComponent(card).Build();
    }

    private static void ChoosePull(StringBuilder text, ComponentBuilder controls, RaidRecapSession s)
    {
        var panel = s.PlayerPanel;
        var pulls = s.Report.CompletedPulls;
        text.AppendLine("## 👥 Players");
        if (pulls.Count == 0)
        {
            text.AppendLine("No finished boss pulls in this log yet.");
            return;
        }

        text.AppendLine("Pick a pull to see who was there.");
        var menu = new SelectMenuBuilder()
            .WithCustomId(RaidRecapView.Pick(s, "player_pull"))
            .WithPlaceholder($"Select a pull... · page {panel.PullPage + 1}/{Math.Max(1, (pulls.Count + 24) / 25)}")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var pull in pulls.Skip(panel.PullPage * 25).Take(25))
        {
            menu.AddOption(
                label: $"{RaidRecapRules.Plain(pull.Name, 45)} · {RaidRecapFormat.Outcome(pull)} #{pull.Id}",
                value: pull.Id.ToString(CultureInfo.InvariantCulture),
                description: $"{RaidRecapFormat.Difficulty(pull.Difficulty)} · {RaidRecapFormat.Clock(pull.DurationMs)}",
                emote: new Emoji(RaidRecapFormat.OutcomeEmoji(pull)));
        }

        controls.WithSelectMenu(menu, row: 2);
        controls.WithButton(
            label: "Previous pulls",
            customId: RaidRecapView.Nav(s, "player_pull_prev"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("◀️"),
            disabled: panel.PullPage == 0,
            row: 3);
        controls.WithButton(
            label: "Next pulls",
            customId: RaidRecapView.Nav(s, "player_pull_next"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("▶️"),
            disabled: (panel.PullPage + 1) * 25 >= pulls.Count,
            row: 3);
    }

    private static void Roster(ContainerBuilder card, StringBuilder text, ComponentBuilder controls, RaidRecapSession s)
    {
        var panel = s.PlayerPanel;
        var fight = panel.Fight;
        if (panel.Roster == null)
        {
            text.AppendLine($"Couldn't load the roster for this pull. Change pull to retry, or open {RaidRecapFormat.Source}.");
            card.AddComponent(new TextDisplayBuilder(text.ToString()));
            return;
        }

        if (!panel.Roster.Complete)
        {
            text.AppendLine("⚠️ **Partial roster** · some players could not be matched and are not linked.");
        }

        var players = panel.Roster.Players;
        if (players.Count > 0)
        {
            var duplicates = players.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var menu = new SelectMenuBuilder()
                .WithCustomId(RaidRecapView.Pick(s, "player"))
                .WithPlaceholder($"Select a player... · page {panel.OptionPage + 1}/{(players.Count + 24) / 25}")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var player in players.Skip(panel.OptionPage * 25).Take(25))
            {
                menu.AddOption(
                    label: RaidRecapRules.Plain(player.Name, 55) + (duplicates.Contains(player.Name) ? $" · #{player.ActorId}" : ""),
                    value: player.ActorId.ToString(CultureInfo.InvariantCulture),
                    description: RaidRecapPlayerPresentation.Identity(player),
                    isDefault: player.ActorId == panel.ActorId);
            }

            controls.WithSelectMenu(menu, row: 2);
        }

        if (panel.Selected is not { } chosen)
        {
            text.AppendLine(players.Count == 0 ? "No players found on this pull." : "Pick a player below.");
            card.AddComponent(new TextDisplayBuilder(text.ToString()));
        }
        else
        {
            Lens(card, text, controls, s, chosen);
        }

        // Without a selected player there is no lens row. Do not insert a gap:
        // the SDK would split the pair across separately appended rows.
        var optionRow = panel.Selected != null ? 4 : players.Count > 0 ? 3 : 2;
        controls.WithButton(
            label: "Previous players",
            customId: RaidRecapView.Nav(s, "player_options_prev"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("◀️"),
            disabled: panel.OptionPage == 0,
            row: optionRow);
        controls.WithButton(
            label: "Next players",
            customId: RaidRecapView.Nav(s, "player_options_next"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("▶️"),
            disabled: (panel.OptionPage + 1) * 25 >= players.Count,
            row: optionRow);
    }

    /// <summary>Link to the whole raid's view of this lens for the pull.</summary>
    private static string FightView(RaidRecapSession s, RaidRecapFight fight, string lens)
    {
        if (lens == "summary")
        {
            return "";
        }

        var rule = RaidRecapMechanics.Rule(lens);
        var view = rule?.View ?? (lens == "damage" ? "damage-done" : lens);
        var url = s.Report.Url + "#fight=" + fight.Id + "&type=" + view + (rule == null ? "" : "&ability=" + rule.SpellId);
        return $" · [whole raid]({url})";
    }

    private static void Lens(ContainerBuilder card, StringBuilder text, ComponentBuilder controls, RaidRecapSession s, RaidRecapPlayer chosen)
    {
        var panel = s.PlayerPanel;
        var fight = panel.Fight;

        var lens = new SelectMenuBuilder()
            .WithCustomId(RaidRecapView.Pick(s, "player_lens"))
            .WithPlaceholder("What do you want to see?")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var name in new[] { "summary", "damage", "healing", "deaths", "interrupts", "dispels", RaidRecapMechanics.Junk, RaidRecapMechanics.Spin })
        {
            lens.AddOption(RaidRecapPlayerPresentation.Label(name), name, isDefault: name == panel.Lens);
        }

        controls.WithSelectMenu(lens, row: 3);

        var pages = RaidRecapPlayerPresentation.Pages(s);
        var index = Math.Clamp(panel.EvidencePage, 0, pages.Count - 1);
        text.AppendLine($"**{RaidRecapLinks.Name(s.Report, fight, chosen.ActorId, chosen.Name)}**");
        text.AppendLine(RaidRecapPlayerPresentation.Identity(chosen));
        text.AppendLine();
        text.AppendLine($"**{RaidRecapPlayerPresentation.Label(panel.Lens)}**"
            + (pages.Count > 1 ? $" · page {index + 1}/{pages.Count}" : "")
            + FightView(s, fight, panel.Lens));
        if (panel.Observations.TryGetValue(panel.Lens, out var analysis) && !analysis.Complete)
        {
            text.AppendLine("⚠️ **Partial data** · " + RaidRecapRules.Text(analysis.Notice, 150));
        }

        foreach (var row in pages[index])
        {
            text.AppendLine(row);
        }

        card.AddComponent(new TextDisplayBuilder(text.ToString()));
        card.AddComponent(new SectionBuilder(
            new ButtonBuilder("Player on WarcraftLogs", style: ButtonStyle.Link, url: RaidRecapLinks.Player(s.Report, fight, chosen.ActorId)),
            new TextDisplayBuilder("-# " + RaidRecapPlayerPresentation.Question(panel.Lens))));

        controls.WithButton(
            label: "Previous rows",
            customId: RaidRecapView.Nav(s, "player_rows_prev"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("◀️"),
            disabled: index == 0,
            row: 4);
        controls.WithButton(
            label: "Next rows",
            customId: RaidRecapView.Nav(s, "player_rows_next"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("▶️"),
            disabled: index == pages.Count - 1,
            row: 4);
    }
}
