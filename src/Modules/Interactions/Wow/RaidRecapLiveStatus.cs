#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public sealed record RaidRecapLiveCardCounts(int Live, int Ended, int Stopped, int Deleted);

public sealed record RaidRecapLiveCardView(
    long Id, string ServerId, string? Guild, string? Zone, string Report,
    DateTimeOffset StartedAt, DateTimeOffset LastChangedAt, int Failures, string? Link);

public sealed record RaidRecapLiveEventView(
    DateTimeOffset At, string Kind, string ServerId, long? CardId, string? Report, string? Guild, string? Reason);

public sealed record RaidRecapLiveWatcher(
    DateTimeOffset Since,
    DateTimeOffset? LastSweepAt,
    double? LastSweepMs,
    bool KillSwitch,
    int CardsWatched,
    int ServersChecked,
    DateTimeOffset? LastErrorAt,
    string? LastErrorWhere,
    string? LastErrorType,
    int ErrorsLastHour,
    int QuotaWaitsLastHour,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<RaidRecapLiveEventView> Recent);

public sealed record RaidRecapLiveWarcraftLogs(
    double PointsSpent, double LimitPerHour, double UsagePercent, DateTimeOffset CheckedAt, DateTimeOffset ResetsAt);

/// <summary>
/// The live card watcher at a glance, for the bot owner: the rollout, the cards, what the
/// watcher did lately and the WarcraftLogs points used. Shown by the rollout command's Status
/// and served to the web admin page. Server IDs are strings, so no client rounds them.
/// </summary>
public sealed record RaidRecapLiveStatus(
    string Rollout,
    int ServersOn,
    RaidRecapLiveCardCounts Cards,
    int PostedLastDay,
    IReadOnlyList<RaidRecapLiveCardView> Live,
    RaidRecapLiveWatcher Watcher,
    RaidRecapLiveWarcraftLogs? WarcraftLogs)
{
    public static async Task<RaidRecapLiveStatus> BuildAsync(
        NinjaBotEntities db,
        RaidRecapRolloutMode mode,
        RaidRecapLiveHealthSnapshot health,
        WclV2RateLimitSnapshot? points,
        DateTimeOffset now)
    {
        var servers = await db.RaidRecapLiveSettings.CountAsync(s => s.Enabled);
        var states = await db.RaidRecapLiveCards.AsNoTracking()
            .GroupBy(c => c.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();
        int Count(RaidRecapLiveState state) => states.FirstOrDefault(s => s.State == state)?.Count ?? 0;
        var dayAgo = (now - TimeSpan.FromDays(1)).UtcDateTime;
        var postedLastDay = await db.RaidRecapLiveCards.CountAsync(c => c.StartedAt > dayAgo);
        var live = (await db.RaidRecapLiveCards.AsNoTracking()
                .Where(c => c.State == RaidRecapLiveState.Live)
                .OrderBy(c => c.StartedAt)
                .Take(RaidRecapLiveCoordinator.MaxLiveCards + 5)
                .ToListAsync())
            .Select(c => new RaidRecapLiveCardView(
                c.Id, c.DiscordGuildId.ToString(), c.GuildName, c.ZoneName, c.ReportCode,
                Utc(c.StartedAt), Utc(c.LastChangedAt), c.Failures,
                c.MessageId == 0 ? null : $"https://discord.com/channels/{c.DiscordGuildId}/{c.ChannelId}/{c.MessageId}"))
            .ToArray();

        var watcher = new RaidRecapLiveWatcher(
            health.Since,
            health.LastSweep?.At,
            health.LastSweep == null ? null : Math.Round(health.LastSweep.Took.TotalMilliseconds),
            health.LastSweep?.KillSwitch ?? false,
            health.LastSweep?.CardsWatched ?? 0,
            health.LastSweep?.ServersChecked ?? 0,
            health.LastError?.At,
            health.LastError?.Where,
            health.LastError?.Type,
            health.ErrorsLastHour,
            health.QuotaWaitsLastHour,
            health.Counts,
            health.Recent.Select(e => new RaidRecapLiveEventView(
                e.At, e.Kind, e.ServerId.ToString(), e.CardId, e.Report, e.Guild, e.Reason)).ToArray());

        return new RaidRecapLiveStatus(
            Describe(mode),
            servers,
            new RaidRecapLiveCardCounts(
                Count(RaidRecapLiveState.Live), Count(RaidRecapLiveState.Ended),
                Count(RaidRecapLiveState.Stopped), Count(RaidRecapLiveState.Deleted)),
            postedLastDay,
            live,
            watcher,
            points == null ? null : new RaidRecapLiveWarcraftLogs(
                Math.Round(points.Spent, 1), points.LimitPerHour, Math.Round(points.UsagePercent, 1), points.CheckedAt, points.ResetsAt));
    }

    public static string Describe(RaidRecapRolloutMode mode) => mode switch
    {
        RaidRecapRolloutMode.Off => "off",
        RaidRecapRolloutMode.OwnerServers => "my servers",
        RaidRecapRolloutMode.Everyone => "everyone",
        _ => "unknown"
    };

    /// <summary>The short version for Discord: one line each, then the latest few events.</summary>
    public string Text(int events = 5)
    {
        var text = new StringBuilder();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        text.AppendLine($"Servers opted in: **{ServersOn}**");
        text.AppendLine($"Cards: **{Cards.Live}** live of {RaidRecapLiveCoordinator.MaxLiveCards} allowed · {Cards.Ended} ended · {Cards.Stopped} stopped · {Cards.Deleted} deleted · {PostedLastDay} posted in the last day");
        text.AppendLine(Watcher.LastSweepAt is { } swept
            ? $"Last sweep: {Stamp(swept)}{(Watcher.KillSwitch ? " (kill switch on, nothing checked)" : string.Create(invariant, $", {Watcher.LastSweepMs:0} ms, {Watcher.CardsWatched} watched, {Watcher.ServersChecked} servers checked"))}"
            : $"Last sweep: none since the bot started {Stamp(Watcher.Since)}");
        text.AppendLine(Watcher.LastErrorAt is { } failed
            ? $"Last error: {Stamp(failed)} in {Watcher.LastErrorWhere} ({Watcher.LastErrorType}) · {Watcher.ErrorsLastHour} in the last hour"
            : "Last error: none since the bot started");
        if (Watcher.QuotaWaitsLastHour > 0)
        {
            text.AppendLine($"Waited for WarcraftLogs points {Watcher.QuotaWaitsLastHour} times in the last hour");
        }

        text.AppendLine(WarcraftLogs is { } points
            ? string.Create(invariant, $"WarcraftLogs: **{points.PointsSpent:N0}** of {points.LimitPerHour:N0} points this hour ({points.UsagePercent:0.#}%), read {Stamp(points.CheckedAt)}")
            : "WarcraftLogs: no points reading yet");
        foreach (var e in Watcher.Recent.Take(events))
        {
            text.AppendLine($"-# {Stamp(e.At)} · card {e.CardId} {e.Kind}{(string.IsNullOrEmpty(e.Reason) ? "" : $": {Clip(e.Reason)}")}");
        }

        return text.ToString().TrimEnd();
    }

    private static string Stamp(DateTimeOffset at) => $"<t:{at.ToUnixTimeSeconds()}:R>";

    // Zone names come from WarcraftLogs; keep them plain and short in Discord.
    private static string Clip(string value) => NinjaBotCore.Modules.Wow.RaidRecapRules.Text(value, 60);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
