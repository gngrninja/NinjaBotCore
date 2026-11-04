#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public enum RaidRecapLiveSelectStatus
{
    /// <summary>Preview only: selecting would turn the card on.</summary>
    Ready,

    /// <summary>Turned on in the server's log channel, and the one-time message was posted.</summary>
    TurnedOn,

    /// <summary>The card was already on, so nothing was posted.</summary>
    AlreadyOn,

    /// <summary>An officer turned the card off. It stays off; they can turn it back on.</summary>
    TurnedOff,

    /// <summary>The server has no single WoW guild set.</summary>
    NoGuild,

    /// <summary>The server has no /watchlogs channel to post in.</summary>
    NoLogChannel,

    /// <summary>The bot is not in the server, or can't see, post or read history in the log channel.</summary>
    CannotPost,

    /// <summary>The one-time message could not be posted, so the card was not turned on.</summary>
    NoticeFailed,

    /// <summary>Warcraft Logs has no report for the guild in the last week.</summary>
    NoRecentLog,

    /// <summary>Warcraft Logs could not be asked, for example when the points ran out.</summary>
    Unchecked,

    /// <summary>The selection is full.</summary>
    Full,

    /// <summary>Something went wrong selecting this server.</summary>
    Failed
}

/// <summary>
/// A server with a recent log, ranked for the rollout. Its reports this week and latest report
/// come from Warcraft Logs, and are null when it wasn't asked.
/// </summary>
public sealed record RaidRecapLiveCandidate(
    long ServerId, string Name, int LogsPosted, int? ReportsThisWeek, DateTime? LatestReport, RaidRecapLiveSelectStatus Status);

/// <summary>A server the bot owner selected, and whether its card is on.</summary>
public sealed record RaidRecapLiveSelected(long ServerId, string Name, bool Enabled, long? ChannelId, DateTime? NoticeSentAt);

/// <summary>
/// The bot owner's pick of servers for the live raid recap rollout. Selecting a server turns
/// the card on in its /watchlogs channel and posts a one-time message there saying what the
/// card is and how to turn it off. A server an officer turned off stays off.
/// </summary>
public sealed class RaidRecapLiveSelection
{
    /// <summary>A server counts as active when it got a New log post this recently.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// At most this many servers are selected. Close to the 20 cards that can be live at once,
    /// since busy servers raid on the same nights, and well inside the 50 servers that discovery
    /// (5 per 30 second sweep) keeps on their 5 minute check.
    /// </summary>
    public const int MaxSelect = 25;

    public const string NoticeTitle = "Live raid recap is on";

    private readonly IServiceScopeFactory _scopes;
    private readonly IRaidRecapLiveDiscord _discord;
    private readonly RaidRecapLiveGate _gate;
    private readonly RaidRecapLiveCoordinator _coordinator;
    private readonly ILogger<RaidRecapLiveSelection> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public RaidRecapLiveSelection(
        IServiceScopeFactory scopes,
        IRaidRecapLiveDiscord discord,
        RaidRecapLiveGate gate,
        RaidRecapLiveCoordinator coordinator,
        ILogger<RaidRecapLiveSelection> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _discord = discord;
        _gate = gate;
        _coordinator = coordinator;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Servers whose /watchlogs channel had a New log in the last week, the most logs posted
    /// first. Walks down that list until <paramref name="count"/> servers could take the card,
    /// confirming with Warcraft Logs that each one really has a report this week, because
    /// /setguild and /watchlogs also set the latest log time. Servers that can't take the card
    /// stay in the list so the owner sees why they were skipped. Also returns how many servers
    /// had a New log.
    /// </summary>
    public async Task<(IReadOnlyList<RaidRecapLiveCandidate> Candidates, int Active)> MostActiveAsync(int count)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var now = _clock();
        var since = (now - ActiveWindow).UtcDateTime;
        var active = (await db.LogMonitoring.AsNoTracking()
                .Where(l => l.MonitorLogs && l.LatestLogRetail != null && l.LatestLogRetail > since)
                .Select(l => new { l.ServerId, l.ServerName, LatestLog = l.LatestLogRetail!.Value })
                .ToListAsync())
            .GroupBy(l => l.ServerId)
            .Select(g => g.OrderByDescending(l => l.LatestLog).First())
            .ToList();
        var ids = active.Select(a => a.ServerId).ToList();
        var posted = await db.WclPosted.AsNoTracking()
            .Where(p => ids.Contains(p.ServerId))
            .GroupBy(p => p.ServerId)
            .Select(g => new { ServerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(p => p.ServerId, p => p.Count);
        var slots = MaxSelect - await db.RaidRecapLiveSettings.CountAsync(s => s.Selected);

        var candidates = new List<RaidRecapLiveCandidate>();
        var ready = 0;
        foreach (var server in active
                     .OrderByDescending(a => posted.GetValueOrDefault(a.ServerId))
                     .ThenByDescending(a => a.LatestLog)
                     .ThenBy(a => a.ServerId))
        {
            if (ready >= count)
            {
                break;
            }

            var check = await CheckAsync(db, (ulong)server.ServerId);
            var candidate = new RaidRecapLiveCandidate(
                server.ServerId, server.ServerName ?? server.ServerId.ToString(),
                posted.GetValueOrDefault(server.ServerId), null, null, check.Status);
            if (check.Status is RaidRecapLiveSelectStatus.Ready or RaidRecapLiveSelectStatus.AlreadyOn)
            {
                if (!check.Selected && slots <= 0)
                {
                    candidates.Add(candidate with { Status = RaidRecapLiveSelectStatus.Full });
                    break;
                }

                candidate = await ConfirmAsync(candidate, check.Guild!, now);
                if (candidate.Status == RaidRecapLiveSelectStatus.Unchecked)
                {
                    // Out of points or unreachable: asking about the rest would fail the same way.
                    candidates.Add(candidate);
                    break;
                }

                if (candidate.Status is RaidRecapLiveSelectStatus.Ready or RaidRecapLiveSelectStatus.AlreadyOn)
                {
                    ready++;
                    if (!check.Selected)
                    {
                        slots--;
                    }
                }
            }

            candidates.Add(candidate);
        }

        return (candidates, active.Count);
    }

    /// <summary>Asks Warcraft Logs for the guild's newest reports, as live discovery does.</summary>
    private async Task<RaidRecapLiveCandidate> ConfirmAsync(RaidRecapLiveCandidate candidate, RaidRecapGuild guild, DateTimeOffset now)
    {
        IReadOnlyList<NinjaBotCore.Models.Wow.WclV2Report> reports;
        try
        {
            reports = await _coordinator.NewestReportsAsync(guild);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap live selection couldn't check {Server} ({Type})", candidate.ServerId, ex.GetType().Name);
            return candidate with { Status = RaidRecapLiveSelectStatus.Unchecked };
        }

        var since = (now - ActiveWindow).ToUnixTimeMilliseconds();
        var thisWeek = reports.Count(r => r.EndTime >= since);
        if (thisWeek == 0)
        {
            return candidate with { ReportsThisWeek = 0, Status = RaidRecapLiveSelectStatus.NoRecentLog };
        }

        var latest = DateTimeOffset.FromUnixTimeMilliseconds(reports.Max(r => r.EndTime)).UtcDateTime;
        return candidate with { ReportsThisWeek = thisWeek, LatestReport = latest };
    }

    /// <summary>
    /// Selects the server. Turns the card on and posts the one-time message, unless the card is
    /// already on or an officer turned it off.
    /// </summary>
    public async Task<RaidRecapLiveSelectStatus> SelectAsync(ulong guildId, ulong ownerId, string ownerName)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var id = checked((long)guildId);
        var (status, selected, guild, channel) = await CheckAsync(db, guildId);
        if (!selected && status is RaidRecapLiveSelectStatus.Ready or RaidRecapLiveSelectStatus.AlreadyOn or RaidRecapLiveSelectStatus.TurnedOff
            && await db.RaidRecapLiveSettings.CountAsync(s => s.Selected) >= MaxSelect)
        {
            return RaidRecapLiveSelectStatus.Full;
        }

        var settings = await db.RaidRecapLiveSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == id);
        if (status is RaidRecapLiveSelectStatus.AlreadyOn or RaidRecapLiveSelectStatus.TurnedOff)
        {
            // Selected, so the officers' own choice keeps working under the Selected rollout.
            settings!.Selected = true;
            await db.SaveChangesAsync();
            return status;
        }

        if (status != RaidRecapLiveSelectStatus.Ready)
        {
            return status;
        }

        try
        {
            var notice = WowCardV2.Notice(NoticeTitle, NoticeText(guild!.Name), Color.Green).Build();
            if (await _discord.SendAsync((ulong)channel!.Value, notice) == null)
            {
                return RaidRecapLiveSelectStatus.NoticeFailed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap live notice failed for {Server} ({Type})", id, ex.GetType().Name);
            return RaidRecapLiveSelectStatus.NoticeFailed;
        }

        if (settings == null)
        {
            settings = new RaidRecapLiveSettings { DiscordGuildId = id };
            db.RaidRecapLiveSettings.Add(settings);
        }

        var now = _clock().UtcDateTime;
        settings.Enabled = true;
        settings.ChannelId = channel.Value;
        settings.Selected = true;
        settings.SetById = checked((long)ownerId);
        settings.SetByName = ownerName.Length <= 100 ? ownerName : ownerName[..100];
        settings.TimeSet = now;
        settings.NoticeSentAt = now;
        await db.SaveChangesAsync();

        // A server turned on here never had the card, so there is nothing for the watcher to
        // revive, and it is checked for a live log first on the next sweep.
        return RaidRecapLiveSelectStatus.TurnedOn;
    }

    /// <summary>
    /// Takes the server out of the selection. Its card setting is left as it is, so it stops only
    /// while the rollout is limited to selected servers, and then a live card stops right away.
    /// </summary>
    public async Task<bool> UnselectAsync(ulong guildId)
    {
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var id = checked((long)guildId);
            var settings = await db.RaidRecapLiveSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == id && s.Selected);
            if (settings == null)
            {
                return false;
            }

            settings.Selected = false;
            await db.SaveChangesAsync();
        }

        if (!await _gate.AllowsAsync(guildId))
        {
            await _coordinator.StopServerAsync(guildId);
        }

        return true;
    }

    public async Task<IReadOnlyList<RaidRecapLiveSelected>> SelectedAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var selected = await db.RaidRecapLiveSettings.AsNoTracking()
            .Where(s => s.Selected)
            .OrderBy(s => s.DiscordGuildId)
            .ToListAsync();
        var ids = selected.Select(s => s.DiscordGuildId).ToList();
        var names = (await db.DiscordServers.AsNoTracking()
                .Where(d => ids.Contains(d.ServerId))
                .Select(d => new { d.ServerId, d.ServerName })
                .ToListAsync())
            .ToDictionary(d => d.ServerId, d => d.ServerName);
        return selected
            .Select(s => new RaidRecapLiveSelected(
                s.DiscordGuildId, names.GetValueOrDefault(s.DiscordGuildId) ?? s.DiscordGuildId.ToString(),
                s.Enabled && s.ChannelId != null, s.ChannelId, s.NoticeSentAt))
            .ToList();
    }

    /// <summary>The one-time message: what the card is, and how to turn it off or move it.</summary>
    public static string NoticeText(string guildName) =>
        $"This channel now gets a live raid recap for **{RaidRecapRules.Text(guildName, 80)}**. "
        + "When your raid's Warcraft Logs report goes live, I post one card here and keep it updated until the raid ends: "
        + "pulls, kills and wipes, the first deaths of the latest wipe, and the top damage and healing of the latest kill. "
        + "**Open my recap** on the card shows each raider their own details, privately.\n\n"
        + "**To turn it off**, anyone with the Kick Members permission can run `/raid-recap-live state:Off`. "
        + "To move it, run `/raid-recap-live state:On` and pick a `channel`.\n"
        + "-# NinjaBot sends this message once.";

    /// <summary>What selecting the server would do, without asking Warcraft Logs.</summary>
    private async Task<(RaidRecapLiveSelectStatus Status, bool Selected, RaidRecapGuild? Guild, long? Channel)> CheckAsync(
        NinjaBotEntities db, ulong guildId)
    {
        var id = checked((long)guildId);
        var settings = await db.RaidRecapLiveSettings.AsNoTracking().FirstOrDefaultAsync(s => s.DiscordGuildId == id);
        var selected = settings?.Selected == true;
        if (settings != null && !settings.Enabled)
        {
            return (RaidRecapLiveSelectStatus.TurnedOff, selected, null, null);
        }

        var guild = await RaidRecapDiscord.FindGuildAsync(db, guildId);
        if (guild == null)
        {
            return (RaidRecapLiveSelectStatus.NoGuild, selected, null, null);
        }

        if (settings?.ChannelId != null)
        {
            return (RaidRecapLiveSelectStatus.AlreadyOn, selected, guild, settings.ChannelId);
        }

        var channel = await LogChannelAsync(db, id);
        if (channel == null)
        {
            return (RaidRecapLiveSelectStatus.NoLogChannel, selected, guild, null);
        }

        return (_discord.CanPost(guildId, (ulong)channel.Value)
            ? RaidRecapLiveSelectStatus.Ready
            : RaidRecapLiveSelectStatus.CannotPost, selected, guild, channel);
    }

    private static async Task<long?> LogChannelAsync(NinjaBotEntities db, long id)
    {
        var channel = await db.LogMonitoring.AsNoTracking()
            .Where(l => l.ServerId == id && l.MonitorLogs && l.ChannelId > 0)
            .OrderByDescending(l => l.LatestLogRetail)
            .Select(l => (long?)l.ChannelId)
            .FirstOrDefaultAsync();
        return channel;
    }
}
