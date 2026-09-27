using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>
/// Builds the /raid-recap cards. The shell follows the /char WarcraftLogs cards: a titled
/// header with the server icon, emoji view buttons, one accent per view, parse dots,
/// compact numbers and a single footer line. Explanations live behind "How to read".
/// </summary>
public static partial class RaidRecapView
{
    private const uint HeaderColor = 0x5865F2;
    private const uint OverviewColor = 0x8B80D9;
    private const uint DamageColor = 0xE06C75;
    private const uint HealingColor = 0x2AA198;

    private static readonly (string View, string Label, string Emoji)[] Tabs =
    {
        ("overview", "Overview", "🏠"),
        ("bosses", "Bosses", "🐉"),
        ("damage", "Damage", "⚔️"),
        ("healing", "Healing", "💚"),
        ("analysis", "Analysis", "🔬")
    };

    public static MessageComponent Build(RaidRecapSession s, bool shared = false)
    {
        if (!shared && s.PlayerPanel != null)
        {
            return RaidRecapPlayerView.Build(s);
        }

        var view = shared ? "overview" : s.View;
        if (s.Report == null || view == "reports")
        {
            return Picker(s, shared);
        }

        if (shared)
        {
            return Shared(s);
        }

        var scope = new ContainerBuilder().WithAccentColor(new Color(HeaderColor));
        AddHeader(scope, s, ReportSubtitle(s));

        var controls = new ComponentBuilder();
        AddNavigation(controls, s, view);

        if (view is "damage" or "healing" && s.Report.Kills > 0)
        {
            return Output(scope, controls, s);
        }

        var body = new ContainerBuilder().WithAccentColor(new Color(ViewColor(view)));
        var text = new StringBuilder();
        var help = view;

        switch (view)
        {
            case "overview":
                Overview(body, text, s);
                break;
            case "bosses" when s.Comparing:
                help = "compare";
                Comparison(body, text, controls, s);
                break;
            case "bosses":
                Bosses(body, text, controls, s);
                break;
            case "analysis":
                help = Analysis(body, text, controls, s);
                break;
            default:
                text.AppendLine($"## {(view == "healing" ? "💚 Healing" : "⚔️ Damage")}");
                AppendNotice(text, s.Notice);
                text.AppendLine("No boss kills in this log yet. Damage and healing are shown for kills.");
                text.AppendLine();
                text.Append(BossLines(s.Report));
                break;
        }

        if (text.Length > 0)
        {
            body.AddComponent(new TextDisplayBuilder(text.ToString()));
        }

        if (view == "bosses" && !s.Comparing && s.Report.Bosses.Count > 0)
        {
            body.AddComponent(new SectionBuilder(
                new ButtonBuilder("Compare", Nav(s, "compare"), ButtonStyle.Secondary, emote: new Emoji("🔁")),
                new TextDisplayBuilder("**🔁 Compare two pulls**\nPick two pulls of this boss and see how the deaths differ.")));
        }

        AddFooter(body, s, help);

        var rows = controls.Build().Components.OfType<ActionRowComponent>().ToArray();
        foreach (var row in rows.Take(2))
        {
            scope.AddComponent(new ActionRowBuilder(row));
        }

        foreach (var row in rows.Skip(2))
        {
            body.AddComponent(new ActionRowBuilder(row));
        }

        return new ComponentBuilderV2().AddComponent(scope).AddComponent(body).Build();
    }

    // ===== Shell =====

    private static MessageComponent Picker(RaidRecapSession s, bool shared)
    {
        var scope = new ContainerBuilder().WithAccentColor(new Color(HeaderColor));
        AddHeader(scope, s, "Choose a report to review");
        if (shared)
        {
            return new ComponentBuilderV2().AddComponent(scope).Build();
        }

        var body = new ContainerBuilder().WithAccentColor(new Color(OverviewColor));
        if (!string.IsNullOrEmpty(s.Notice))
        {
            body.AddComponent(new TextDisplayBuilder(RaidRecapRules.Text(s.Notice, 300)));
        }

        var page = RaidRecapService.Page(s.ReportPage, s.Reports.Count);
        var pages = Math.Max(1, (s.Reports.Count + 24) / 25);
        body.AddComponent(new TextDisplayBuilder(s.Reports.Count == 0
            ? "No reports found for this guild yet.\nUpload a log to the guild on WarcraftLogs, or run `/raid-recap report:` with a report link."
            : $"**{RaidRecapFormat.Plural(s.Reports.Count, "recent report")}** · newest first · page {page + 1}/{pages}\n"
                + "-# Only you can see this. For an older log, run `/raid-recap report:` with its link."));

        if (s.Reports.Count > 0)
        {
            var controls = new ComponentBuilder();
            var menu = new SelectMenuBuilder()
                .WithCustomId(Pick(s, "report"))
                .WithPlaceholder("Select a report...")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var pair in s.Reports.Select((report, index) => (report, index)).Skip(page * 25).Take(25))
            {
                menu.AddOption(
                    label: RaidRecapRules.Plain(pair.report.Title, 70) + " · " + Date(pair.report.StartTime),
                    value: pair.index.ToString(CultureInfo.InvariantCulture),
                    description: RaidRecapRules.Plain(pair.report.ZoneName, 60) + " · "
                        + RaidRecapFormat.Span((double)pair.report.EndTime - pair.report.StartTime),
                    emote: new Emoji("📊"));
            }

            controls.WithSelectMenu(menu, row: 0);
            Pages(controls, s, "reports", page, s.Reports.Count, 1);
            foreach (var row in controls.Build().Components.OfType<ActionRowComponent>())
            {
                body.AddComponent(new ActionRowBuilder(row));
            }
        }

        return new ComponentBuilderV2().AddComponent(scope).AddComponent(body).Build();
    }

    /// <summary>The public card: bosses and results only. No player names, no controls.</summary>
    private static MessageComponent Shared(RaidRecapSession s)
    {
        var card = new ContainerBuilder().WithAccentColor(new Color(HeaderColor));
        AddHeader(card, s, ReportSubtitle(s));
        card.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
        card.AddComponent(new TextDisplayBuilder(Summary(s.Report) + "\n" + BossLines(s.Report)));
        card.AddComponent(new TextDisplayBuilder(Footer(s)));
        card.AddComponent(new ActionRowBuilder().WithButton(
            new ButtonBuilder(RaidRecapFormat.Source, style: ButtonStyle.Link, url: s.Report.Url)));
        return new ComponentBuilderV2().AddComponent(card).Build();
    }

    internal static void AddHeader(ContainerBuilder container, RaidRecapSession s, string subtitle, string title = "Raid Recap")
    {
        var heading = $"# 📊 {title}";
        if (!string.IsNullOrWhiteSpace(s.GuildName))
        {
            heading += " · " + RaidRecapRules.Text(s.GuildName, 60);
        }

        var text = new TextDisplayBuilder(heading + "\n" + subtitle);
        if (IsHttps(s.GuildIconUrl))
        {
            container.AddComponent(new SectionBuilder(
                new ThumbnailBuilder(new UnfurledMediaItemProperties(s.GuildIconUrl), "Server icon"),
                text));
            return;
        }

        container.AddComponent(text);
    }

    /// <summary>
    /// Title line plus zone, difficulty, date and length. The title is free text typed by
    /// whoever uploaded the log, so the automatic public card leaves it out.
    /// </summary>
    internal static string ReportSubtitle(RaidRecapSession s, bool includeTitle = true)
    {
        var report = s.Report;
        var parts = new List<string>();
        var zone = s.Reports?.FirstOrDefault(r => r.Code == report.Code)?.ZoneName;
        if (!string.IsNullOrWhiteSpace(zone))
        {
            parts.Add(RaidRecapRules.Text(zone, 60));
        }

        var difficulties = report.Bosses
            .Select(b => b.Difficulty)
            .Where(d => d.HasValue)
            .Distinct()
            .OrderByDescending(d => d)
            .Select(RaidRecapFormat.Difficulty)
            .ToArray();
        if (difficulties.Length > 0)
        {
            parts.Add(string.Join(" + ", difficulties));
        }

        if (report.StartTime is >= 0 and <= 253402300799999)
        {
            parts.Add($"<t:{(long)(report.StartTime.Value / 1000)}:D>");
        }

        if (report.EndTime > report.StartTime)
        {
            parts.Add(RaidRecapFormat.Span(report.EndTime - report.StartTime) + " raid");
        }

        var details = string.Join(" · ", parts);
        if (!includeTitle)
        {
            return details;
        }

        var line = $"**{RaidRecapRules.Text(report.Title, 160)}**";
        return parts.Count == 0 ? line : line + "\n" + details;
    }

    internal static void AddNavigation(ComponentBuilder controls, RaidRecapSession s, string activeView)
    {
        foreach (var tab in Tabs)
        {
            controls.WithButton(
                label: tab.Label,
                customId: Nav(s, tab.View),
                style: tab.View == activeView ? ButtonStyle.Success : ButtonStyle.Primary,
                emote: new Emoji(tab.Emoji),
                row: 0);
        }

        controls.WithButton(
            label: "Reports",
            customId: Nav(s, "reports"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("📋"),
            disabled: s.Reports.Count == 0,
            row: 1);
        controls.WithButton(
            label: "Refresh",
            customId: Nav(s, "refresh"),
            style: ButtonStyle.Secondary,
            emote: new Emoji("🔄"),
            row: 1);
        controls.WithButton(
            label: s.ShareAttempted ? "Share attempted" : "Share overview",
            customId: Nav(s, "share"),
            style: ButtonStyle.Secondary,
            emote: new Emoji(s.ShareAttempted ? "✅" : "📢"),
            disabled: !s.CanShare || s.ShareAttempted,
            row: 1);
        controls.WithButton(
            label: RaidRecapFormat.Source,
            style: ButtonStyle.Link,
            url: s.Report.Url,
            row: 1);

        if (s.PlayerPanel != null)
        {
            controls.WithButton("Back", Nav(s, "player_back"), ButtonStyle.Secondary, new Emoji("↩️"), row: 1);
        }
        else if (activeView == "bosses" && s.Comparing)
        {
            controls.WithButton("All pulls", Nav(s, "attempts"), ButtonStyle.Secondary, new Emoji("↩️"), row: 1);
        }
        else
        {
            controls.WithButton("Players", Nav(s, "players"), ButtonStyle.Primary, new Emoji("👥"), row: 1);
        }
    }

    /// <summary>Footer line with the "How to read" control. Open help sits just above it.</summary>
    internal static void AddFooter(ContainerBuilder container, RaidRecapSession s, string helpTopic, string lead = null)
    {
        if (s.OutputHelp)
        {
            container.AddComponent(new TextDisplayBuilder(RaidRecapHelp.For(helpTopic, s)));
        }

        var text = (string.IsNullOrEmpty(lead) ? "" : lead + "\n") + Footer(s);
        container.AddComponent(new SectionBuilder(
            new ButtonBuilder(
                s.OutputHelp ? "Hide help" : "How to read",
                Nav(s, "output_help"),
                ButtonStyle.Secondary,
                emote: new Emoji("❔")),
            new TextDisplayBuilder(text)));
    }

    internal static string Footer(RaidRecapSession s)
    {
        var owner = "";
        if (!string.IsNullOrWhiteSpace(s.GuildName))
        {
            owner = RaidRecapRules.Text(s.GuildName, 60);
            if (!string.IsNullOrWhiteSpace(s.GuildRegion))
            {
                owner += $" ({RaidRecapRules.Text(s.GuildRegion.ToUpperInvariant(), 4)})";
            }

            owner += " | ";
        }

        return $"-# {owner}Data from {RaidRecapFormat.Source} · fetched <t:{s.Report.AsOf.ToUnixTimeSeconds()}:R> · may still update";
    }

    // ===== Overview =====

    private static void Overview(ContainerBuilder body, StringBuilder text, RaidRecapSession s)
    {
        var report = s.Report;
        AppendNotice(text, s.Notice);
        text.AppendLine(Summary(report));
        text.Append(BossLines(report));
        body.AddComponent(new TextDisplayBuilder(text.ToString()));
        text.Clear();

        var cards = RaidRecapReview.Cards(report);
        if (cards.Count == 0)
        {
            return;
        }

        body.AddComponent(new TextDisplayBuilder("## 👀 Worth a look"));
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var boss = report.Bosses[card.BossIndex];
            var name = $"{RaidRecapRules.Text(boss.Name, 75)} · {RaidRecapFormat.Difficulty(boss.Difficulty)}";
            string content;
            string button;
            if (card.B == null)
            {
                button = "Details";
                content = $"**🟠 Still progressing · {name}**\n"
                    + $"{RaidRecapFormat.Plural(boss.Attempts.Count, "pull")} · best {RaidRecapFormat.Percent(boss.BestRemaining)}\n"
                    + (boss.Attempts.Count > 1 ? PullStrip(boss) + "\n" : "")
                    + (card.A.IsWipe
                        ? $"[Who died on the last wipe?]({RaidRecapLinks.Fight(report, card.A, "deaths")})"
                        : $"[Pull #{card.A.Id}]({RaidRecapLinks.Fight(report, card.A)}) has no result yet.");
            }
            else
            {
                button = "Compare";
                content = $"**🔁 What changed? · {name}**\n"
                    + $"{FightLink(report, card.A)} → {FightLink(report, card.B)}\n"
                    + "Compare the deaths on these two pulls.";
            }

            body.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
            body.AddComponent(new SectionBuilder(
                new ButtonBuilder(button, Nav(s, "review_" + i), ButtonStyle.Secondary),
                new TextDisplayBuilder(content)));
        }
    }

    private static string Summary(RaidRecapReport report)
    {
        var line = $"**✅ {RaidRecapFormat.Plural(report.Kills, "kill")} · 💀 {RaidRecapFormat.Plural(report.Wipes, "wipe")}**"
            + $" · {RaidRecapFormat.Plural(report.Fights.Count, "pull")}";
        if (report.Unfinished > 0)
        {
            line += $" · ⏳ {report.Unfinished} in progress";
        }

        var combat = report.CompletedPulls.Sum(f => f.DurationMs ?? 0);
        if (combat > 0)
        {
            line += $"\n⏱️ {RaidRecapFormat.Span(combat)} on bosses";
        }

        return line + "\n";
    }

    private static string BossLines(RaidRecapReport report)
    {
        if (report.Bosses.Count == 0)
        {
            return "No boss pulls in this log yet.\n";
        }

        var text = new StringBuilder();
        foreach (var boss in report.Bosses.Take(8))
        {
            var name = $"**{RaidRecapRules.Text(boss.Name, 75)}** · {RaidRecapFormat.DifficultyShort(boss.Difficulty)}";
            var pulls = RaidRecapFormat.Plural(boss.Attempts.Count, "pull");
            if (boss.Kills > 0)
            {
                text.AppendLine($"✅ {name} · {pulls} · {RaidRecapFormat.Clock(boss.FastestKillMs)}");
            }
            else if (boss.Wipes > 0)
            {
                text.AppendLine($"🟠 {name} · {pulls} · best {RaidRecapFormat.Percent(boss.BestRemaining)}");
            }
            else
            {
                text.AppendLine($"⏳ {name} · {pulls} · in progress");
            }
        }

        if (report.Bosses.Count > 8)
        {
            text.AppendLine($"-# +{report.Bosses.Count - 8} more in Bosses");
        }

        return text.ToString();
    }

    // ===== Shared helpers =====

    private static uint ViewColor(string view) => view switch
    {
        "damage" => DamageColor,
        "healing" => HealingColor,
        _ => OverviewColor
    };

    private static void AppendNotice(StringBuilder text, string notice, int limit = 300)
    {
        if (!string.IsNullOrEmpty(notice))
        {
            text.AppendLine("> " + RaidRecapRules.Text(notice, limit));
        }
    }

    private static string FightLink(RaidRecapReport report, RaidRecapFight fight, string view = null) =>
        $"[{RaidRecapFormat.OutcomeEmoji(fight)} {RaidRecapFormat.Outcome(fight)} #{fight.Id}]({RaidRecapLinks.Fight(report, fight, view)})";

    /// <summary>Plain text for select menus, which do not render markdown.</summary>
    private static string FightLabel(RaidRecapFight fight, int nameLimit) =>
        $"{RaidRecapRules.Plain(fight.Name, nameLimit)} · {RaidRecapFormat.Difficulty(fight.Difficulty)} · {RaidRecapFormat.Outcome(fight)} #{fight.Id}";

    public static string Nav(RaidRecapSession s, string action) => $"rr_nav~{s.Token}~{s.Generation}~{action}";

    public static string Pick(RaidRecapSession s, string action) => $"rr_pick~{s.Token}~{s.Generation}~{action}";

    private static void Menu(
        ComponentBuilder controls,
        RaidRecapSession s,
        string kind,
        IReadOnlyList<(string Label, string Emoji)> options,
        int page,
        int selected)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(Pick(s, kind))
            .WithPlaceholder(kind switch
            {
                "boss" => "Select a boss...",
                "pull" => "Select a pull...",
                _ => "Select a kill..."
            })
            .WithMinValues(1)
            .WithMaxValues(1);
        for (var i = page * 25; i < Math.Min(options.Count, (page + 1) * 25); i++)
        {
            menu.AddOption(
                label: options[i].Label,
                value: i.ToString(CultureInfo.InvariantCulture),
                emote: new Emoji(options[i].Emoji),
                isDefault: i == selected);
        }

        controls.WithSelectMenu(menu, row: 2);
    }

    private static void Pages(ComponentBuilder controls, RaidRecapSession s, string kind, int page, int count, int row)
    {
        controls.WithButton("Previous", Nav(s, kind + "_prev"), ButtonStyle.Secondary, new Emoji("◀️"), disabled: page <= 0, row: row);
        controls.WithButton("Next", Nav(s, kind + "_next"), ButtonStyle.Secondary, new Emoji("▶️"), disabled: (page + 1) * 25 >= count, row: row);
    }

    private static int PageCount(int rows, int size) => Math.Max(1, (rows + size - 1) / size);

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps;

    private static string Date(long ms) => ms >= 0 && ms <= 253402300799999
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
        : "Unknown date";

    public static string Difficulty(int? id) => RaidRecapFormat.Difficulty(id);

    public static string Duration(double? ms) => RaidRecapFormat.Clock(ms);
}
