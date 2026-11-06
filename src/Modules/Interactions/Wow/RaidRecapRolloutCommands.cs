#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Attributes;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>The bot owner's switch for which servers may use the live raid recap.</summary>
[OwnerGuildOnly]
public class RaidRecapRolloutCommands : InteractionModuleBase<IInteractionContext>
{
    // Room for the list inside one notice, below Discord's 4000 character text limit.
    private const int MaxListText = 3500;

    private readonly IServiceScopeFactory _scopes;
    private readonly RaidRecapLiveGate _gate;
    private readonly RaidRecapLiveCoordinator _coordinator;
    private readonly RaidRecapLiveSelection _selection;
    private readonly NinjaBotCore.Modules.Wow.WarcraftLogsV2Client _warcraftLogs;
    private readonly ILogger<RaidRecapRolloutCommands> _logger;

    public RaidRecapRolloutCommands(
        IServiceScopeFactory scopes,
        RaidRecapLiveGate gate,
        RaidRecapLiveCoordinator coordinator,
        RaidRecapLiveSelection selection,
        NinjaBotCore.Modules.Wow.WarcraftLogsV2Client warcraftLogs,
        ILogger<RaidRecapRolloutCommands> logger)
    {
        _scopes = scopes;
        _gate = gate;
        _coordinator = coordinator;
        _selection = selection;
        _warcraftLogs = warcraftLogs;
        _logger = logger;
    }

    [Discord.Interactions.RequireOwner]
    [SlashCommand("raid-recap-rollout", "Bot owner: choose which servers may use the live raid recap")]
    public async Task RolloutAsync(
        [Summary("mode", "Where the live raid recap is available")]
        [Choice("Status", "status")]
        [Choice("Off (kill switch)", "off")]
        [Choice("My servers", "mine")]
        [Choice("Selected servers and mine", "selected")]
        [Choice("Everyone", "everyone")]
        string mode = "status")
    {
        await DeferAsync(ephemeral: true);
        try
        {
            RaidRecapRolloutMode? requested = mode switch
            {
                "off" => RaidRecapRolloutMode.Off,
                "mine" => RaidRecapRolloutMode.OwnerServers,
                "selected" => RaidRecapRolloutMode.Selected,
                "everyone" => RaidRecapRolloutMode.Everyone,
                _ => null
            };
            var previous = await _gate.ModeAsync();
            if (requested.HasValue && requested.Value != previous)
            {
                await _gate.SetModeAsync(requested.Value);
            }

            var current = await _gate.ModeAsync();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var status = await RaidRecapLiveStatus.BuildAsync(
                db, current, _coordinator.Health.Snapshot(), _warcraftLogs.LastRateLimit, DateTimeOffset.UtcNow, _warcraftLogs.AuthHealth);
            await NoticeAsync(
                RolloutLine(current, previous, requested.HasValue) + "\n" + status.Text(),
                current == RaidRecapRolloutMode.Off ? Color.LightGrey : Color.Green);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap rollout change failed ({Type})", ex.GetType().Name);
            await NoticeAsync("Something went wrong changing the rollout. Try again shortly.", Color.Orange);
        }
    }

    [Discord.Interactions.RequireOwner]
    [SlashCommand("raid-recap-select", "Bot owner: pick servers for the live raid recap rollout")]
    public async Task SelectAsync(
        [Summary("action", "List, preview or select the most active servers, or add or remove one")]
        [Choice("List selected servers", "list")]
        [Choice("Preview most active", "preview")]
        [Choice("Select most active", "top")]
        [Choice("Add a server", "add")]
        [Choice("Remove a server", "remove")]
        string action = "list",
        [Summary("count", "How many of the most active servers (default 20)")]
        [MinValue(1), MaxValue(RaidRecapLiveSelection.MaxSelect)]
        int count = 20,
        [Summary("server", "Server ID, to add or remove one server")]
        string? server = null)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            ulong serverId = 0;
            if (action is "add" or "remove" && (!ulong.TryParse(server?.Trim(), out serverId) || serverId == 0))
            {
                await NoticeAsync("Give the server's ID in `server` to add or remove it.", Color.Orange);
                return;
            }

            switch (action)
            {
                case "preview":
                {
                    var (candidates, active) = await _selection.MostActiveAsync(count);
                    await NoticeAsync(CandidateText(candidates, active, preview: true), Color.Blue);
                    return;
                }
                case "top":
                {
                    var switched = await AllowSelectingAsync();
                    if (switched == null)
                    {
                        return;
                    }

                    var (results, active) = await _selection.SelectMostActiveAsync(count, Context.User.Id, Context.User.Username ?? "");
                    await NoticeAsync(switched + CandidateText(results, active, preview: false), Color.Green);
                    return;
                }
                case "add":
                {
                    var switched = await AllowSelectingAsync();
                    if (switched == null)
                    {
                        return;
                    }

                    var status = await _selection.SelectAsync(serverId, Context.User.Id, Context.User.Username ?? "");
                    await NoticeAsync(switched + $"Server `{serverId}`: {Describe(status)}.", Color.Green);
                    return;
                }
                case "remove":
                    await NoticeAsync(await _selection.UnselectAsync(serverId)
                            ? $"Server `{serverId}` is no longer selected. Unless it's one of your servers, a live card there has stopped and no new one posts while the rollout is limited to selected servers. Its own on or off setting is kept."
                            : $"Server `{serverId}` wasn't selected.",
                        Color.LightGrey);
                    return;
                default:
                    await NoticeAsync(SelectedText(await _selection.SelectedAsync(), await _gate.ModeAsync()), Color.Blue);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap select failed ({Type})", ex.GetType().Name);
            await NoticeAsync("Something went wrong selecting servers. Try again shortly.", Color.Orange);
        }
    }

    /// <summary>
    /// Opens the rollout to selected servers. Returns what changed, or null when the kill switch
    /// is on, after saying so.
    /// </summary>
    private async Task<string?> AllowSelectingAsync()
    {
        var mode = await _selection.OpenForSelectedAsync();
        if (mode == null)
        {
            await NoticeAsync("The rollout is **off** (kill switch). Set `/raid-recap-rollout mode:` to selected servers first.", Color.Orange);
            return null;
        }

        return mode == RaidRecapRolloutMode.OwnerServers
            ? $"Rollout changed from {Describe(mode.Value)} to {Describe(RaidRecapRolloutMode.Selected)}.\n"
            : "";
    }

    public static string CandidateText(IReadOnlyList<RaidRecapLiveCandidate> candidates, int active, bool preview)
    {
        var text = new StringBuilder();
        text.AppendLine($"**{active}** {(active == 1 ? "server" : "servers")} got a New log in the last {RaidRecapLiveSelection.ActiveWindow.TotalDays:0} days, ranked by logs posted. Each pick is checked with Warcraft Logs for a report this week.");
        if (preview)
        {
            text.AppendLine("Nothing has changed. Run **Select most active** to turn the card on for these.");
        }

        var lines = candidates
            .Select((c, i) => $"{i + 1}. {NinjaBotCore.Modules.Wow.RaidRecapRules.Text(c.Name, 40)} · {c.LogsPosted} {(c.LogsPosted == 1 ? "log" : "logs")} posted"
                + (c.ReportsThisWeek is int week && c.LatestReport is DateTime latest
                    ? $" · {week}{(week >= RaidRecapLiveCoordinator.DiscoveryReports ? "+" : "")} this week, last <t:{new DateTimeOffset(DateTime.SpecifyKind(latest, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>"
                    : "")
                + $" · {Describe(c.Status)}")
            .ToList();
        AppendLines(text, lines);
        return text.ToString().TrimEnd();
    }

    public static string SelectedText(IReadOnlyList<RaidRecapLiveSelected> selected, RaidRecapRolloutMode mode)
    {
        var text = new StringBuilder();
        text.AppendLine($"Rollout is **{Describe(mode)}**. **{selected.Count}** {(selected.Count == 1 ? "server is" : "servers are")} selected.");
        if (selected.Count == 0)
        {
            text.AppendLine("Run **Preview most active** to see which servers would be picked.");
        }

        AppendLines(text, selected
            .Select(s => $"• {NinjaBotCore.Modules.Wow.RaidRecapRules.Text(s.Name, 40)} `{s.ServerId}` · "
                + (!s.BotPresent ? "bot left the server" : s.Enabled ? $"on in <#{s.ChannelId}>" : "turned off by the server")
                + (s.NoticeSentAt == null ? "" : " · told"))
            .ToList());
        return text.ToString().TrimEnd();
    }

    private static void AppendLines(StringBuilder text, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (text.Length + lines[i].Length > MaxListText)
            {
                text.AppendLine($"…and {lines.Count - i} more.");
                return;
            }

            text.AppendLine(lines[i]);
        }
    }

    public static string Describe(RaidRecapLiveSelectStatus status) => status switch
    {
        RaidRecapLiveSelectStatus.Ready => "will turn on",
        RaidRecapLiveSelectStatus.TurnedOn => "turned on, message posted",
        RaidRecapLiveSelectStatus.AlreadyOn => "already on",
        RaidRecapLiveSelectStatus.TurnedOff => "turned off by the server, left off",
        RaidRecapLiveSelectStatus.NoGuild => "skipped, no WoW guild set",
        RaidRecapLiveSelectStatus.NoLogChannel => "skipped, no /watchlogs channel",
        RaidRecapLiveSelectStatus.CannotPost => "skipped, can't post in its log channel",
        RaidRecapLiveSelectStatus.NoticeFailed => "skipped, the message didn't post",
        RaidRecapLiveSelectStatus.NoRecentLog => "skipped, no Warcraft Logs report this week",
        RaidRecapLiveSelectStatus.Unchecked => "skipped, couldn't check Warcraft Logs",
        RaidRecapLiveSelectStatus.LookupFailed => "skipped, Warcraft Logs couldn't look up its guild",
        RaidRecapLiveSelectStatus.Full => $"skipped, {RaidRecapLiveSelection.MaxSelect} servers are already selected",
        RaidRecapLiveSelectStatus.RolloutOff => "skipped, the rollout was switched off",
        RaidRecapLiveSelectStatus.Failed => "skipped, something went wrong",
        _ => "unknown"
    };

    /// <summary>Says "changed" only when the mode really is different from before.</summary>
    public static string RolloutLine(RaidRecapRolloutMode current, RaidRecapRolloutMode previous, bool requested)
    {
        var line = $"Rollout is **{Describe(current)}**";
        if (!requested)
        {
            return line + ".";
        }

        return current == previous
            ? line + ". It was already set to that, so nothing changed."
            : line + $". Changed from {Describe(previous)}.";
    }

    private static string Describe(RaidRecapRolloutMode mode) => mode switch
    {
        RaidRecapRolloutMode.Off => "off everywhere",
        RaidRecapRolloutMode.OwnerServers => "limited to servers you are a member of",
        RaidRecapRolloutMode.Selected => "limited to servers you selected or are a member of",
        RaidRecapRolloutMode.Everyone => "open to every server",
        _ => "unknown"
    };

    private Task NoticeAsync(string message, Color accent) =>
        Context.Interaction.ModifyToV2Async(WowCardV2.Notice(RaidRecapLiveCommands.Title, message, accent).Build());
}
