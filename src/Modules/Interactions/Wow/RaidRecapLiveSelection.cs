#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
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

    /// <summary>Warcraft Logs answered but couldn't look up the guild, for example a guild that never logged.</summary>
    LookupFailed,

    /// <summary>The selection is full.</summary>
    Full,

    /// <summary>The kill switch went on before this server was selected, so nothing changed.</summary>
    RolloutOff,

    /// <summary>Something went wrong selecting this server.</summary>
    Failed
}

/// <summary>
/// A server with a recent log, ranked for the rollout. Its reports this week and latest report
/// come from Warcraft Logs, and are null when it wasn't asked.
/// </summary>
public sealed record RaidRecapLiveCandidate(
    long ServerId, string Name, int LogsPosted, int? ReportsThisWeek, DateTime? LatestReport, RaidRecapLiveSelectStatus Status);

/// <summary>
/// A server the bot owner selected, and whether its card is on. A server the bot has left is
/// listed so it can be removed, but doesn't use up a place.
/// </summary>
public sealed record RaidRecapLiveSelected(long ServerId, string Name, bool Enabled, long? ChannelId, DateTime? NoticeSentAt, bool BotPresent = true);

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

    // Selecting checks the cap, posts the message and then saves. Two at once, say the Discord
    // command and the web admin page, could post the message twice or pass the cap.
    private readonly SemaphoreSlim _sync = new(1, 1);

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
        var slots = MaxSelect - await SelectedCountAsync(db);

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
                    // Out of points, signed out or unreachable: asking about the rest would fail the same way.
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
            // Only a failure every guild would hit ends the list. An error about this guild,
            // such as one that never logged, skips just this server.
            return candidate with
            {
                Status = IsOutage(ex) ? RaidRecapLiveSelectStatus.Unchecked : RaidRecapLiveSelectStatus.LookupFailed
            };
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

    /// <summary>Out of points, WarcraftLogs sign-in down, the network or a timeout.</summary>
    private static bool IsOutage(Exception ex) =>
        RaidRecapLiveCoordinator.IsQuota(ex) || ex is HttpRequestException or TimeoutException or TaskCanceledException;

    /// <summary>
    /// Selects the server. Turns the card on and posts the one-time message, unless the card is
    /// already on or an officer turned it off.
    /// </summary>
    public async Task<RaidRecapLiveSelectStatus> SelectAsync(ulong guildId, ulong ownerId, string ownerName)
    {
        await _sync.WaitAsync();
        try
        {
            return await SelectLockedAsync(guildId, ownerId, ownerName);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<RaidRecapLiveSelectStatus> SelectLockedAsync(ulong guildId, ulong ownerId, string ownerName)
    {
        // Checked for every server: the kill switch can go on during a long "select most
        // active" run, and nobody is told about a card that won't post.
        if (await _gate.ModeAsync() == RaidRecapRolloutMode.Off)
        {
            return RaidRecapLiveSelectStatus.RolloutOff;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var id = checked((long)guildId);
        var (status, selected, guild, channel) = await CheckAsync(db, guildId);
        if (!selected && status is RaidRecapLiveSelectStatus.Ready or RaidRecapLiveSelectStatus.AlreadyOn or RaidRecapLiveSelectStatus.TurnedOff
            && await SelectedCountAsync(db) >= MaxSelect)
        {
            return RaidRecapLiveSelectStatus.Full;
        }

        if (status is RaidRecapLiveSelectStatus.AlreadyOn or RaidRecapLiveSelectStatus.TurnedOff)
        {
            // Selected, so the officers' own choice keeps working under the Selected rollout.
            var existing = await db.RaidRecapLiveSettings.FirstAsync(s => s.DiscordGuildId == id);
            existing.Selected = true;
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

        var now = _clock().UtcDateTime;
        try
        {
            return await SaveSelectedAsync(db, id, channel.Value, ownerId, ownerName, now);
        }
        catch (DbUpdateException)
        {
            // An officer's /raid-recap-live added the server's row at the same moment. Read it again.
            db.ChangeTracker.Clear();
            return await SaveSelectedAsync(db, id, channel.Value, ownerId, ownerName, now);
        }
    }

    /// <summary>
    /// Turns the card on after the message posted. Read only now: an officer may have turned
    /// the card off meanwhile, which stays off, or on in a channel of their choosing, which is
    /// kept. The message did post, so it is never sent again.
    /// </summary>
    private static async Task<RaidRecapLiveSelectStatus> SaveSelectedAsync(
        NinjaBotEntities db, long id, long channel, ulong ownerId, string ownerName, DateTime now)
    {
        var settings = await db.RaidRecapLiveSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == id);
        if (settings is { Enabled: false } or { ChannelId: not null })
        {
            settings.Selected = true;
            settings.NoticeSentAt ??= now;
            await db.SaveChangesAsync();
            return settings.Enabled ? RaidRecapLiveSelectStatus.AlreadyOn : RaidRecapLiveSelectStatus.TurnedOff;
        }

        if (settings == null)
        {
            settings = new RaidRecapLiveSettings { DiscordGuildId = id };
            db.RaidRecapLiveSettings.Add(settings);
        }

        settings.Enabled = true;
        settings.ChannelId = channel;
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
    /// Selected servers only get the card while the rollout covers them. Moves "my servers" up to
    /// "selected servers and mine", which still includes every server it did. Returns the mode
    /// before, or null when the kill switch is on, so nobody is told about a card that won't post.
    /// </summary>
    public async Task<RaidRecapRolloutMode?> OpenForSelectedAsync()
    {
        var mode = await _gate.ModeAsync();
        if (mode == RaidRecapRolloutMode.Off)
        {
            return null;
        }

        if (mode == RaidRecapRolloutMode.OwnerServers)
        {
            await _gate.SetModeAsync(RaidRecapRolloutMode.Selected);
        }

        return mode;
    }

    /// <summary>
    /// Selects the most active servers that can take the card, as <see cref="MostActiveAsync"/>
    /// ranks them. Returns every candidate with what happened to it.
    /// </summary>
    public async Task<(IReadOnlyList<RaidRecapLiveCandidate> Results, int Active)> SelectMostActiveAsync(int count, ulong ownerId, string ownerName)
    {
        var (candidates, active) = await MostActiveAsync(count);
        var results = new List<RaidRecapLiveCandidate>();
        foreach (var candidate in candidates)
        {
            var status = candidate.Status;
            if (status is RaidRecapLiveSelectStatus.Ready or RaidRecapLiveSelectStatus.AlreadyOn)
            {
                try
                {
                    status = await SelectAsync((ulong)candidate.ServerId, ownerId, ownerName);
                }
                catch (Exception ex)
                {
                    // One server failing must not hide what happened to the others.
                    _logger.LogWarning("Raid recap select failed for {Server} ({Type})", candidate.ServerId, ex.GetType().Name);
                    status = RaidRecapLiveSelectStatus.Failed;
                }
            }

            results.Add(candidate with { Status = status });
        }

        return (results, active);
    }

    /// <summary>
    /// Takes the server out of the selection. Its card setting is left as it is, so it stops only
    /// while the rollout is limited to selected servers, and then a live card stops right away.
    /// </summary>
    public async Task<bool> UnselectAsync(ulong guildId)
    {
        await _sync.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
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
        finally
        {
            _sync.Release();
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
        var servers = (await db.DiscordServers.AsNoTracking()
                .Where(d => ids.Contains(d.ServerId))
                .Select(d => new { d.ServerId, d.ServerName, d.BotPresent })
                .ToListAsync())
            .ToDictionary(d => d.ServerId);
        return selected
            .Select(s => new RaidRecapLiveSelected(
                s.DiscordGuildId, servers.GetValueOrDefault(s.DiscordGuildId)?.ServerName ?? s.DiscordGuildId.ToString(),
                s.Enabled && s.ChannelId != null, s.ChannelId, s.NoticeSentAt,
                servers.GetValueOrDefault(s.DiscordGuildId)?.BotPresent ?? true))
            .ToList();
    }

    /// <summary>
    /// How many places are used. A server the bot has left doesn't count, so it can't block the
    /// rollout for the 30 days until its data is purged.
    /// </summary>
    private static Task<int> SelectedCountAsync(NinjaBotEntities db) =>
        db.RaidRecapLiveSettings.CountAsync(s => s.Selected
            && !db.DiscordServers.Any(d => d.ServerId == s.DiscordGuildId && !d.BotPresent));

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
            // Already on, but discovery would never post where the bot can't, so it doesn't take a place.
            return (_discord.CanPost(guildId, (ulong)settings.ChannelId.Value)
                ? RaidRecapLiveSelectStatus.AlreadyOn
                : RaidRecapLiveSelectStatus.CannotPost, selected, guild, settings.ChannelId);
        }

        var channel = await RaidRecapLiveSwitch.LogChannelAsync(db, id);
        if (channel == null)
        {
            return (RaidRecapLiveSelectStatus.NoLogChannel, selected, guild, null);
        }

        return (_discord.CanPost(guildId, (ulong)channel.Value)
            ? RaidRecapLiveSelectStatus.Ready
            : RaidRecapLiveSelectStatus.CannotPost, selected, guild, channel);
    }
}
