#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>
/// Finds live guild logs, posts one card per report, keeps it fresh while the raid runs and
/// finishes it when the raid ends. All state is in the database, so a restart resumes.
/// One card's trouble never holds up another: each card and each server is handled in its
/// own database scope and its failures are contained.
/// </summary>
public sealed class RaidRecapLiveCoordinator
{
    /// <summary>How often a live card re-reads its log while pulls are landing.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(90);

    /// <summary>How often it re-reads once no pull has landed for <see cref="QuietAfter"/>.</summary>
    public static readonly TimeSpan QuietRefreshInterval = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(10);

    /// <summary>How often a server without a live card is checked for a new log.</summary>
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);

    /// <summary>A log counts as live when its last event is at most this old.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(15);

    /// <summary>The raid has ended when the log has been quiet for this long.</summary>
    public static readonly TimeSpan EndAfter = TimeSpan.FromMinutes(30);

    /// <summary>No card is watched for longer than this.</summary>
    public static readonly TimeSpan MaxWatch = TimeSpan.FromHours(8);

    /// <summary>At most this many cards are refreshed at once, across all servers.</summary>
    public const int MaxLiveCards = 20;

    /// <summary>At most this many servers are checked for a new log in one sweep.</summary>
    public const int MaxDiscoveriesPerSweep = 5;

    /// <summary>How many of a guild's newest live logs are looked at for a raid.</summary>
    public const int MaxCandidateReports = 3;

    /// <summary>
    /// How many of a guild's newest reports discovery asks for. Room for members' dungeon logs
    /// started after the raid log; a log appended to an older report is found only while it is
    /// among these.
    /// </summary>
    public const int DiscoveryReports = 10;

    public const int MaxFailures = 10;

    /// <summary>How many pulls one redraw may read from WarcraftLogs.</summary>
    public const int MaxPullReadsPerRedraw = 3;

    /// <summary>How many times one pull is read before the card settles for what it has.</summary>
    public const int MaxPullAttempts = 3;

    /// <summary>How many of the current boss's latest wipes the wipe pattern looks at.</summary>
    public const int MaxPatternWipes = 10;

    /// <summary>How long one target read may take before the rest wait for the next redraw.</summary>
    public static readonly TimeSpan TargetReadDeadline = TimeSpan.FromSeconds(15);

    /// <summary>How many of the night's latest kills the final summary looks at.</summary>
    public const int MaxSummaryKills = 12;

    /// <summary>
    /// Two logs whose pulls of the same boss began this close together are the same raid,
    /// recorded by two people. Only one card is kept for it.
    /// </summary>
    public static readonly TimeSpan SameRaidPullWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The first raid in a log is timed from the start of the log only when its first pull
    /// began this soon after, so trash before the first boss counts but a log left open for
    /// hours beforehand does not.
    /// </summary>
    public static readonly TimeSpan LogStartCountsWithin = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A raid ends after this long without a raid pull, even while the log keeps going,
    /// for example Mythic+ after the raid. A later raid pull brings the card back.
    /// </summary>
    public static readonly TimeSpan NoRaidPullEndAfter = TimeSpan.FromMinutes(60);

    /// <summary>At most this many cards are live in one server at once.</summary>
    public const int MaxCardsPerServer = 2;

    /// <summary>
    /// A card is revived at most once in this long, so a card whose edits keep failing while
    /// its log reads fine does not stop and come back all night.
    /// </summary>
    public static readonly TimeSpan RevivalBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IRaidRecapSource _source;
    private readonly RaidRecapService _service;
    private readonly RaidRecapCache _cache;
    private readonly RaidRecapLiveGate _gate;
    private readonly IRaidRecapLiveDiscord _discord;
    private readonly ILogger<RaidRecapLiveCoordinator> _logger;
    private readonly Func<DateTimeOffset> _clock;

    // A sweep and an officer turning the feature off never interleave, so a stopped card
    // cannot be overwritten by a refresh that was already in flight. The dictionaries below
    // are only touched while this is held.
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<long, DateTimeOffset> _lastDiscovery = new();
    private readonly Dictionary<string, ReportMemory> _highlights = new();
    private readonly Dictionary<long, DateTimeOffset> _lastPull = new();
    private readonly Dictionary<long, DateTimeOffset> _revivedAt = new();

    public RaidRecapLiveCoordinator(
        IServiceScopeFactory scopes,
        IRaidRecapSource source,
        RaidRecapService service,
        RaidRecapCache cache,
        RaidRecapLiveGate gate,
        IRaidRecapLiveDiscord discord,
        ILogger<RaidRecapLiveCoordinator> logger,
        Func<DateTimeOffset>? clock = null,
        RaidRecapLiveHealth? health = null)
    {
        _scopes = scopes;
        _source = source;
        _service = service;
        _cache = cache;
        _gate = gate;
        _discord = discord;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Health = health ?? new RaidRecapLiveHealth(_clock);
    }

    /// <summary>What the watcher did lately, for the owner.</summary>
    public RaidRecapLiveHealth Health { get; }

    public async Task RunSweepAsync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var started = _clock();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var (killSwitch, watching, checkedServers) = await SweepAsync(ct);
            Health.Sweep(started, timer.Elapsed, killSwitch, watching, checkedServers);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Marks a server's live cards as stopped, for when an officer turns the feature off.</summary>
    public async Task StopServerAsync(ulong guildId)
    {
        await _sync.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var id = checked((long)guildId);
            var cards = await db.RaidRecapLiveCards
                .Where(c => c.DiscordGuildId == id && c.State == RaidRecapLiveState.Live)
                .ToListAsync();
            foreach (var card in cards)
            {
                await StopAsync(card, "turned off");
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// For when an officer turns the feature on. A card someone deleted is allowed back as a
    /// new post, and the server is checked on the next sweep, so a raid that is going gets its
    /// card within a minute.
    /// </summary>
    public async Task StartServerAsync(ulong guildId)
    {
        await _sync.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var id = checked((long)guildId);
            var since = (_clock() - MaxWatch).UtcDateTime;
            var cards = await db.RaidRecapLiveCards
                .Where(c => c.DiscordGuildId == id && c.State == RaidRecapLiveState.Deleted && c.StartedAt > since)
                .ToListAsync();
            foreach (var card in cards)
            {
                card.State = RaidRecapLiveState.Stopped;
                card.MessageId = 0;
            }

            await db.SaveChangesAsync();
            _lastDiscovery.Remove(id);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Returns whether the kill switch is on, how many cards are watched and how many servers were checked.</summary>
    private async Task<(bool KillSwitch, int Watching, int Checked)> SweepAsync(CancellationToken ct)
    {
        var mode = await _gate.ModeAsync();
        if (mode == RaidRecapRolloutMode.Off)
        {
            // Kill switch: the sweep makes no WarcraftLogs calls and no Discord calls.
            return (true, 0, 0);
        }

        Dictionary<long, long?> enabled;
        List<(long Id, long Guild)> live;
        HashSet<string> watched;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            enabled = await db.RaidRecapLiveSettings.AsNoTracking()
                .Where(s => s.Enabled && s.ChannelId != null)
                .ToDictionaryAsync(s => s.DiscordGuildId, s => s.ChannelId, ct);
            var cards = await db.RaidRecapLiveCards.AsNoTracking()
                .Where(c => c.State == RaidRecapLiveState.Live)
                .OrderBy(c => c.Id)
                .Select(c => new { c.Id, c.DiscordGuildId, c.ReportCode })
                .ToListAsync(ct);
            live = cards.Select(c => (c.Id, c.DiscordGuildId)).ToList();
            watched = cards.Select(c => c.ReportCode).ToHashSet(StringComparer.Ordinal);
        }

        // Let go of what was read about logs nobody is watching any more. Logs with a live
        // card are never dropped, so an active card never has to read its pulls again.
        foreach (var code in _highlights.Keys.Where(code => !watched.Contains(code)).ToArray())
        {
            _highlights.Remove(code);
        }

        foreach (var card in _revivedAt.Where(r => _clock() - r.Value >= RevivalBackoff).Select(r => r.Key).ToArray())
        {
            _revivedAt.Remove(card);
        }

        var allowed = new Dictionary<long, bool>();
        async Task<bool> AllowedAsync(long guild)
        {
            if (!allowed.TryGetValue(guild, out var yes))
            {
                yes = await _gate.AllowsAsync((ulong)guild, mode);
                allowed[guild] = yes;
            }

            return yes;
        }

        var watching = 0;
        foreach (var (cardId, guild) in live)
        {
            ct.ThrowIfCancellationRequested();
            var eligible = enabled.ContainsKey(guild) && await AllowedAsync(guild);
            var stillLive = await ContainAsync(
                () => RefreshCardAsync(cardId, enabled.ContainsKey(guild), eligible, watching < MaxLiveCards, ct),
                "refresh", cardId, ct);
            if (stillLive && eligible)
            {
                // Cards outside the rollout cost nothing, so they do not use up a slot.
                watching++;
            }
        }

        // Longest-waiting servers first, a few per sweep, so a restart or a large rollout
        // never turns into a burst of WarcraftLogs calls. Servers with a live card are checked
        // too, so a second raid that night is found and takes over.
        var now = _clock();
        var due = enabled.Keys
            .Where(g => !_lastDiscovery.TryGetValue(g, out var last) || now - last >= DiscoveryInterval)
            .OrderBy(g => _lastDiscovery.TryGetValue(g, out var last) ? last : DateTimeOffset.MinValue)
            .ThenBy(g => g)
            .ToList();
        var checkedServers = 0;
        foreach (var guild in due)
        {
            ct.ThrowIfCancellationRequested();
            if (watching >= MaxLiveCards || checkedServers >= MaxDiscoveriesPerSweep)
            {
                break;
            }

            if (!await AllowedAsync(guild))
            {
                continue;
            }

            checkedServers++;
            _lastDiscovery[guild] = now;
            var channel = enabled[guild]!.Value;
            if (await ContainAsync(() => DiscoverAsync(guild, channel, ct), "discovery", guild, ct))
            {
                watching++;
            }
        }

        return (false, watching, checkedServers);
    }

    /// <summary>Runs one unit of work so that its failure cannot stop the rest of the sweep.</summary>
    private async Task<bool> ContainAsync(Func<Task<bool>> work, string what, long id, CancellationToken ct)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap live {What} failed for {Id} ({Type})", what, id, ex.GetType().Name);
            Health.Error($"{what} {id}", ex);
            return false;
        }
    }

    /// <summary>Returns true while the card is still live.</summary>
    private async Task<bool> RefreshCardAsync(long cardId, bool serverOn, bool eligible, bool hasSlot, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var card = await db.RaidRecapLiveCards.FirstOrDefaultAsync(c => c.Id == cardId, ct);
        if (card == null || card.State != RaidRecapLiveState.Live)
        {
            return false;
        }

        try
        {
            await RefreshAsync(card, serverOn, eligible, hasSlot, ct);
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return card.State == RaidRecapLiveState.Live;
    }

    private async Task RefreshAsync(RaidRecapLiveCard card, bool serverOn, bool eligible, bool hasSlot, CancellationToken ct)
    {
        var now = _clock();
        if (!serverOn)
        {
            await StopAsync(card, "turned off");
            return;
        }

        if (!eligible)
        {
            // The rollout no longer covers this server. Spend nothing on it, but do not let
            // the card claim to be live for ever.
            if (now - Utc(card.LastChangedAt) >= EndAfter || now - Utc(card.StartedAt) >= MaxWatch)
            {
                await StopAsync(card, "outside the rollout");
            }

            return;
        }

        // Back off between bosses and during breaks; speed up again with the next pull.
        var quiet = _lastPull.TryGetValue(card.Id, out var lastPull) && now - lastPull >= QuietAfter;
        if (!hasSlot || now - Utc(card.LastCheckedAt) < (quiet ? QuietRefreshInterval : RefreshInterval))
        {
            return;
        }

        card.LastCheckedAt = now.UtcDateTime;
        try
        {
            var full = await LoadReportAsync(card.ReportCode);
            var sessions = RaidRecapLiveSessions.Split(full);
            var session = RaidRecapLiveSessions.Find(sessions, card.SessionStartMs);
            var report = Scope(full, session);
            if (LastPullAt(report) is { } pulled)
            {
                _lastPull[card.Id] = pulled;
            }

            // A later raid in the same log, such as the next zone, closes this card. Discovery
            // is asked to look at the server this sweep, so the next card is not kept waiting.
            var replaced = session != null && sessions[^1].StartMs != session.StartMs;
            if (replaced)
            {
                _lastDiscovery.Remove(card.DiscordGuildId);
            }

            var ended = replaced || HasEnded(report, card, now) || RaidQuiet(full, session, now);
            var fingerprint = Fingerprint(report, ended);
            if (fingerprint != card.Fingerprint || card.MessageId == 0)
            {
                var highlights = await HighlightsAsync(report, ct);
                var component = RaidRecapView.Live(report, Info(card, full, sessions, session, ended), ended, now, highlights);
                if (card.MessageId == 0)
                {
                    // Posting was interrupted last time. Post now.
                    var posted = await _discord.SendAsync((ulong)card.ChannelId, component);
                    if (posted == null)
                    {
                        await FailAsync(card, "channel unavailable");
                        return;
                    }

                    card.MessageId = checked((long)posted.Value);
                }
                else
                {
                    var result = await _discord.EditAsync((ulong)card.ChannelId, (ulong)card.MessageId, component);
                    if (result == RaidRecapLiveEdit.Missing)
                    {
                        // Someone deleted the card. Respect that and stop.
                        Forget(card);
                        card.State = RaidRecapLiveState.Deleted;
                        card.LastChangedAt = now.UtcDateTime;
                        Note(card, RaidRecapLiveEventKind.Deleted);
                        return;
                    }

                    if (result == RaidRecapLiveEdit.Unavailable)
                    {
                        await FailAsync(card, "channel unavailable");
                        return;
                    }
                }

                card.Fingerprint = fingerprint;
                card.LastChangedAt = now.UtcDateTime;
            }

            card.Failures = 0;
            if (ended)
            {
                Forget(card);
                card.State = RaidRecapLiveState.Ended;
                Note(card, RaidRecapLiveEventKind.Ended, replaced ? "next raid in the log"
                    : now - Utc(card.StartedAt) >= MaxWatch ? "watched 8 hours"
                    : RaidQuiet(full, session, now) ? "no raid pull for an hour"
                    : "log quiet 30 minutes");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (global::NinjaBot.WarcraftLogs.WclAuthenticationUnavailableException)
        {
            // A shared dependency outage is not a card failure. Keep LastCheckedAt/cadence;
            // the client owns deduplicated outage/recovery logs and the refresh cooldown.
        }
        catch (Exception ex) when (IsQuota(ex))
        {
            // The WarcraftLogs budget is spent for now. That is not this card's fault, so it
            // does not count against it. Try again at the next refresh.
            _logger.LogInformation("Raid recap live refresh for card {CardId} waits for WarcraftLogs quota", card.Id);
            Health.QuotaWait();
        }
        catch (Exception ex)
        {
            // Includes provider timeouts, which surface as cancellations we did not ask for.
            await FailAsync(card, ex.GetType().Name);
        }
    }

    private async Task FailAsync(RaidRecapLiveCard card, string reason)
    {
        card.Failures++;
        _logger.LogWarning("Raid recap live refresh failed for card {CardId} ({Reason}), attempt {Failures}",
            card.Id, reason, card.Failures);
        Health.Error($"refresh {card.Id}", reason);
        if (card.Failures >= MaxFailures)
        {
            await StopAsync(card, $"kept failing ({reason})");
        }
    }

    private async Task StopAsync(RaidRecapLiveCard card, string reason)
    {
        Forget(card);
        card.State = RaidRecapLiveState.Stopped;
        // When it stopped: only raid activity after this brings it back.
        card.LastChangedAt = _clock().UtcDateTime;
        Note(card, RaidRecapLiveEventKind.Stopped, reason);
        if (card.MessageId == 0)
        {
            return;
        }

        try
        {
            // Best effort, so the card does not claim to be live for ever.
            await _discord.EditAsync((ulong)card.ChannelId, (ulong)card.MessageId,
                RaidRecapView.LiveStopped(card.ReportCode, Info(card), _clock()));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Raid recap live stop notice failed for card {CardId} ({Type})", card.Id, ex.GetType().Name);
        }
    }

    /// <summary>Logs what happened to a card and keeps it for the owner's health view.</summary>
    private void Note(RaidRecapLiveCard card, string kind, string? reason = null)
    {
        _logger.LogInformation("Raid recap live card {CardId} {Kind} for guild {GuildId}, report {Report} ({Reason})",
            card.Id, kind, card.DiscordGuildId, card.ReportCode, reason ?? "-");
        Health.Event(kind, card.DiscordGuildId, card.Id, card.ReportCode, card.GuildName, reason);
    }

    private void Forget(RaidRecapLiveCard card)
    {
        // What was read about the log stays until no live card uses it. Another server may be
        // watching the same log.
        _lastPull.Remove(card.Id);
    }

    /// <summary>Returns true when a card went live for this server.</summary>
    private async Task<bool> DiscoverAsync(long guild, long channel, CancellationToken ct)
    {
        var now = _clock();
        var guildId = (ulong)guild;
        var channelId = (ulong)channel;
        if (!_discord.CanPost(guildId, channelId))
        {
            return false;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var wow = await RaidRecapDiscord.FindGuildAsync(db, guildId);
        if (wow == null)
        {
            return false;
        }

        IReadOnlyList<WclV2Report> reports;
        try
        {
            // Its own cache entry: the picker's list is longer, and must not get this short one.
            reports = (IReadOnlyList<WclV2Report>)await _cache.GetAsync(
                "live:" + RaidRecapService.GuildKey(wow.Name, wow.Realm, wow.Region),
                async () => await _source.GetRaidRecapReportsAsync(wow.Name, wow.Realm, wow.Region, DiscoveryReports),
                TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (IsQuota(ex))
        {
            Health.QuotaWait();
            return false;
        }

        // Usually one log is live. A dungeon log started after the raid log must not hide it.
        var candidates = reports
            .Where(r => IsLive(r, now))
            .OrderByDescending(r => r.StartTime)
            .Take(MaxCandidateReports);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string code;
            try
            {
                code = RaidRecapRules.ReportCode(candidate.Code);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var full = await LoadReportAsync(code);
            var sessions = RaidRecapLiveSessions.Split(full);
            if (sessions.Count == 0)
            {
                // No raid boss pull yet, or not a raid log.
                continue;
            }

            // Only the log's latest raid can be live.
            var latest = sessions[^1];
            var existing = await db.RaidRecapLiveCards.FirstOrDefaultAsync(
                c => c.DiscordGuildId == guild && c.ReportCode == code && c.SessionStartMs == latest.StartMs, ct);
            if (existing == null && sessions.Count == 1)
            {
                // A card made before raids were told apart covers the first raid.
                existing = await db.RaidRecapLiveCards.FirstOrDefaultAsync(
                    c => c.DiscordGuildId == guild && c.ReportCode == code && c.SessionStartMs == 0, ct);
            }

            var liveHere = await db.RaidRecapLiveCards.CountAsync(
                c => c.DiscordGuildId == guild && c.State == RaidRecapLiveState.Live, ct);
            if (existing != null)
            {
                if (existing.State == RaidRecapLiveState.Ended
                    && now - Utc(existing.StartedAt) < MaxWatch
                    && RaidRecapLiveSessions.LastActivityAt(full, latest) is { } activity
                    && activity > Utc(existing.LastChangedAt)
                    && liveHere < MaxCardsPerServer)
                {
                    // Back from a break: a raid pull after the card closed.
                    return await ResumeAsync(db, existing, full, sessions, latest, now, ct);
                }

                if (existing.State == RaidRecapLiveState.Ended
                    && existing.Fingerprint != Fingerprint(Scope(full, latest), ended: true))
                {
                    // Pulls from before the card closed arrived late. Redraw the final card.
                    await FinishAsync(existing, full, sessions, latest, now, ct);
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                // A stopped card picks up again below, like a new card would start, but only for
                // a raid pull after it stopped: a log the team has moved on from stays stopped.
                // Live already, deleted, or nothing new since it closed: leave it.
                if (existing.State != RaidRecapLiveState.Stopped
                    || now - Utc(existing.StartedAt) >= MaxWatch
                    || RaidRecapLiveSessions.LastActivityAt(full, latest) is not { } sinceStop
                    || sinceStop <= Utc(existing.LastChangedAt)
                    || (_revivedAt.TryGetValue(existing.Id, out var revived) && now - revived < RevivalBackoff))
                {
                    continue;
                }
            }

            // A card needs a raid that is actually going on: a pull in progress or a recent
            // one. A raid that finished hours ago in a log kept live by keys is not.
            if (!latest.HasPullInProgress
                && (RaidRecapLiveSessions.LastActivityAt(full, latest) is not { } recent || now - recent >= EndAfter))
            {
                continue;
            }

            // First close the cards this raid takes over from, unless it is a raid that is
            // already on a card because someone else is logging it too.
            if (!await TakeOverAsync(db, guild, full, latest, now, ct))
            {
                continue;
            }

            liveHere = await db.RaidRecapLiveCards.CountAsync(
                c => c.DiscordGuildId == guild && c.State == RaidRecapLiveState.Live, ct);
            if (liveHere >= MaxCardsPerServer)
            {
                continue;
            }

            return existing != null
                ? await ReviveAsync(db, existing, channel, now)
                : await PostAsync(db, guild, channel, wow, candidate, full, sessions, latest, now, ct);
        }

        return false;
    }

    /// <summary>
    /// Prepares the server for a new raid's card. Returns false when the raid is already on a
    /// card because a second person is logging it; nothing is closed then. Otherwise closes
    /// the cards the new raid takes over from:
    /// <list type="bullet">
    /// <item>an earlier raid in the same log, always;</item>
    /// <item>a raid in another log that had no activity after the new raid's first pull began,
    /// because the same team has moved on, for example after restarting the uploader.</item>
    /// </list>
    /// A raid in another log that is still going after the new one began is a second team,
    /// and its card stays.
    /// </summary>
    private async Task<bool> TakeOverAsync(
        NinjaBotEntities db,
        long guild,
        RaidRecapReport newFull,
        RaidRecapLiveSession newRaid,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var cards = await db.RaidRecapLiveCards
            .Where(c => c.DiscordGuildId == guild && c.State == RaidRecapLiveState.Live)
            .ToListAsync(ct);
        var newStart = newFull.StartTime + newRaid.StartMs;
        var closing = new List<(RaidRecapLiveCard Card, RaidRecapReport Full, IReadOnlyList<RaidRecapLiveSession> Sessions, RaidRecapLiveSession? Session)>();
        foreach (var card in cards)
        {
            RaidRecapReport full;
            try
            {
                full = await LoadReportAsync(card.ReportCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unknown, so leave it alone; its own refresh closes it once it goes quiet.
                _logger.LogDebug("Raid recap live card {CardId} could not be checked ({Type})", card.Id, ex.GetType().Name);
                continue;
            }

            var sessions = RaidRecapLiveSessions.Split(full);
            var session = RaidRecapLiveSessions.Find(sessions, card.SessionStartMs);
            if (card.ReportCode == newFull.Code)
            {
                closing.Add((card, full, sessions, session));
                continue;
            }

            if (session != null && SameRaid(full, session, newFull, newRaid))
            {
                return false;
            }

            var lastActivity = session == null ? null : full.StartTime + session.LastActivityMs;
            var stillGoing = session?.HasPullInProgress == true || (lastActivity is double last && newStart is double begun && last > begun);
            if (!stillGoing)
            {
                closing.Add((card, full, sessions, session));
            }
        }

        foreach (var (card, full, sessions, session) in closing)
        {
            try
            {
                await FinishAsync(card, full, sessions, session, now, ct);
                if (card.State == RaidRecapLiveState.Ended)
                {
                    Note(card, RaidRecapLiveEventKind.Ended,
                        card.ReportCode == newFull.Code ? "next raid in the log" : "another log took over");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Raid recap live card {CardId} could not be closed ({Type})", card.Id, ex.GetType().Name);
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// True when two logs recorded the same raid: a pull of the same boss on the same
    /// difficulty, in the same zone if both say, began at nearly the same moment in both.
    /// </summary>
    public static bool SameRaid(RaidRecapReport aFull, RaidRecapLiveSession a, RaidRecapReport bFull, RaidRecapLiveSession b)
    {
        if (aFull.StartTime is not double aStart || bFull.StartTime is not double bStart)
        {
            return false;
        }

        if (a.ZoneId is int aZone && b.ZoneId is int bZone && aZone != bZone)
        {
            return false;
        }

        var window = SameRaidPullWindow.TotalMilliseconds;
        return a.Fights.Any(x => b.Fights.Any(y =>
            x.EncounterId == y.EncounterId
            && x.Difficulty == y.Difficulty
            && x.StartMs is double xs && y.StartMs is double ys
            && Math.Abs(aStart + xs - (bStart + ys)) <= window));
    }

    /// <summary>Draws a card's final version and marks it ended.</summary>
    private async Task FinishAsync(
        RaidRecapLiveCard card,
        RaidRecapReport full,
        IReadOnlyList<RaidRecapLiveSession> sessions,
        RaidRecapLiveSession? session,
        DateTimeOffset now,
        CancellationToken ct)
    {
        Forget(card);
        if (card.MessageId == 0)
        {
            card.State = RaidRecapLiveState.Stopped;
            Note(card, RaidRecapLiveEventKind.Stopped, "never posted");
            return;
        }

        var report = Scope(full, session);
        var highlights = await HighlightsAsync(report, ct);
        var result = await _discord.EditAsync((ulong)card.ChannelId, (ulong)card.MessageId,
            RaidRecapView.Live(report, Info(card, full, sessions, session, closed: true), ended: true, now, highlights));
        if (result == RaidRecapLiveEdit.Missing)
        {
            card.State = RaidRecapLiveState.Deleted;
            card.LastChangedAt = now.UtcDateTime;
            Note(card, RaidRecapLiveEventKind.Deleted);
            return;
        }

        if (result != RaidRecapLiveEdit.Updated)
        {
            return;
        }

        card.State = RaidRecapLiveState.Ended;
        card.Fingerprint = Fingerprint(report, ended: true);
        card.LastChangedAt = now.UtcDateTime;
        _logger.LogInformation("Raid recap live card {CardId} drawn as ended", card.Id);
    }

    private async Task<bool> PostAsync(
        NinjaBotEntities db,
        long guild,
        long channel,
        RaidRecapGuild wow,
        WclV2Report listed,
        RaidRecapReport full,
        IReadOnlyList<RaidRecapLiveSession> sessions,
        RaidRecapLiveSession session,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var card = new RaidRecapLiveCard
        {
            DiscordGuildId = guild,
            ChannelId = channel,
            MessageId = 0,
            ReportCode = full.Code,
            SessionStartMs = session.StartMs,
            ZoneId = session.ZoneId,
            State = RaidRecapLiveState.Live,
            GuildName = Limit(wow.Name, 100),
            Region = Limit(wow.Region, 8),
            ZoneName = Limit(session.ZoneName ?? listed.ZoneName, 200),
            StartedAt = now.UtcDateTime,
            LastChangedAt = now.UtcDateTime,
            LastCheckedAt = now.UtcDateTime
        };

        // Claim the raid before posting. The unique index makes sure that two bot processes
        // running side by side during a deploy cannot both post.
        db.RaidRecapLiveCards.Add(card);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return false;
        }

        var report = Scope(full, session);
        ulong? message = null;
        try
        {
            var highlights = await HighlightsAsync(report, ct);
            message = await _discord.SendAsync((ulong)channel,
                RaidRecapView.Live(report, Info(card, full, sessions, session), ended: false, now, highlights));
        }
        finally
        {
            if (message == null)
            {
                // Nothing was posted. Release the claim so the next check can try again.
                // What was read is let go by the next sweep, unless another card uses it.
                db.RaidRecapLiveCards.Remove(card);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        if (message == null)
        {
            return false;
        }

        card.MessageId = checked((long)message.Value);
        card.Fingerprint = Fingerprint(report, ended: false);
        await db.SaveChangesAsync(CancellationToken.None);
        Note(card, RaidRecapLiveEventKind.Posted, card.ZoneName);
        return true;
    }

    /// <summary>
    /// A stopped card whose raid is still going is watched again. Its refresh on the next
    /// sweep redraws it: in place, or as a new post when the server's card channel has
    /// changed since. The old message then keeps saying it stopped.
    /// </summary>
    private async Task<bool> ReviveAsync(NinjaBotEntities db, RaidRecapLiveCard card, long channel, DateTimeOffset now)
    {
        if (card.ChannelId != channel)
        {
            card.ChannelId = channel;
            card.MessageId = 0;
        }

        _revivedAt[card.Id] = now;
        card.State = RaidRecapLiveState.Live;
        card.Failures = 0;
        card.Fingerprint = null;
        card.LastChangedAt = now.UtcDateTime;
        card.LastCheckedAt = (now - QuietRefreshInterval).UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
        Note(card, RaidRecapLiveEventKind.Revived, card.MessageId == 0 ? "new post" : "in place");
        return true;
    }

    /// <summary>The raid came back after a break. The same card carries on.</summary>
    private async Task<bool> ResumeAsync(
        NinjaBotEntities db,
        RaidRecapLiveCard card,
        RaidRecapReport full,
        IReadOnlyList<RaidRecapLiveSession> sessions,
        RaidRecapLiveSession session,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var report = Scope(full, session);
        var fingerprint = Fingerprint(report, ended: false);
        var highlights = await HighlightsAsync(report, ct);
        var result = await _discord.EditAsync((ulong)card.ChannelId, (ulong)card.MessageId,
            RaidRecapView.Live(report, Info(card, full, sessions, session), ended: false, now, highlights));
        if (result == RaidRecapLiveEdit.Missing)
        {
            card.State = RaidRecapLiveState.Deleted;
            card.LastChangedAt = now.UtcDateTime;
            Note(card, RaidRecapLiveEventKind.Deleted);
            await db.SaveChangesAsync(CancellationToken.None);
            return false;
        }

        if (result != RaidRecapLiveEdit.Updated)
        {
            return false;
        }

        card.State = RaidRecapLiveState.Live;
        card.Fingerprint = fingerprint;
        card.Failures = 0;
        card.LastChangedAt = now.UtcDateTime;
        card.LastCheckedAt = now.UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
        Note(card, RaidRecapLiveEventKind.Resumed, "raid pull after the card closed");
        return true;
    }

    /// <summary>
    /// What the card shows beyond pull results: deaths of the latest wipe, top output of the
    /// latest kill, the wipe pattern on the current boss and the night's standouts.
    /// <para>
    /// Each pull is read from WarcraftLogs and remembered. A redraw reads at most
    /// <see cref="MaxPullReadsPerRedraw"/> pulls, newest first, so catching up after a restart
    /// is spread out. A pull that fails or comes back incomplete is tried again on a later
    /// redraw, at most <see cref="MaxPullAttempts"/> times in all. Earlier pulls get a lighter
    /// read than the latest ones, because less of them is shown.
    /// </para>
    /// The card is still posted if any of it fails.
    /// </summary>
    private async Task<RaidRecapLiveHighlights?> HighlightsAsync(RaidRecapReport report, CancellationToken ct)
    {
        var wipe = RaidRecapLiveHighlights.LatestWipe(report);
        var kill = RaidRecapLiveHighlights.LatestKill(report);
        if (wipe == null && kill == null)
        {
            return null;
        }

        if (!_highlights.TryGetValue(report.Code, out var known))
        {
            known = new ReportMemory();
            _highlights[report.Code] = known;
        }

        var finished = report.CompletedPulls.Where(f => f.DurationMs is > 0).ToArray();
        var bossWipes = wipe == null
            ? Array.Empty<RaidRecapFight>()
            : finished.Where(f => f.IsWipe && f.EncounterId == wipe.EncounterId && f.Difficulty == wipe.Difficulty)
                .Reverse().Take(MaxPatternWipes).ToArray();
        var kills = finished.Where(f => f.IsKill).Reverse().Take(MaxSummaryKills).ToArray();

        // What the card needs most comes first: the latest wipe, the latest kill, then the rest.
        var wanted = new List<RaidRecapFight>();
        if (wipe != null) wanted.Add(wipe);
        if (kill != null) wanted.Add(kill);
        wanted.AddRange(bossWipes.Where(f => f != wipe));
        wanted.AddRange(kills.Where(f => f != kill));

        var reads = 0;
        var budgetSpent = false;
        var latestPull = report.CompletedPulls.LastOrDefault(f => f.DurationMs is > 0);
        var utilityKey = latestPull == null ? null : "u:" + PullKey(latestPull);
        var latestCount = wanted.Count(f => f == wipe || f == kill);
        for (var index = 0; index <= wanted.Count; index++)
        {
            // Kicks and dispels for the latest pull are read right after the latest wipe and kill.
            // They are one small read of their own each redraw, apart from the pull budget, so
            // catching up on older pulls is not slowed down by them.
            if (index == latestCount && utilityKey != null && !budgetSpent)
            {
                if (!known.UtilitySettled(utilityKey))
                {
                    ct.ThrowIfCancellationRequested();
                    known.Attempts[utilityKey] = known.Attempts.GetValueOrDefault(utilityKey) + 1;
                    try
                    {
                        var interrupts = await _service.GetUtilityAsync(report, latestPull!, dispels: false);
                        var dispels = await _service.GetUtilityAsync(report, latestPull!, dispels: true);
                        known.Utility[utilityKey] = RaidRecapLiveUtility.From(latestPull!, interrupts, dispels);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (IsQuota(ex))
                    {
                        known.Attempts[utilityKey]--;
                        budgetSpent = true;
                        Health.QuotaWait();
                    }
                    catch (Exception ex) when (ReportMoved(ex))
                    {
                        // The log grew while reading; that is not this pull's fault. Try again
                        // at the next redraw without using up an attempt.
                        known.Attempts[utilityKey]--;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Raid recap live kicks and dispels unavailable for {Report} fight {Fight} ({Type})",
                            report.Code, latestPull!.Id, ex.GetType().Name);
                    }
                }
            }

            if (index == wanted.Count || budgetSpent)
            {
                break;
            }

            var fight = wanted[index];
            ct.ThrowIfCancellationRequested();
            var key = PullKey(fight);
            var latest = fight == wipe || fight == kill;
            if (known.Settled(key, latest))
            {
                continue;
            }

            if (reads >= MaxPullReadsPerRedraw)
            {
                // Budget used: older pulls wait, but the loop still reaches the kicks and dispels.
                continue;
            }

            reads++;
            known.Attempts[key] = known.Attempts.GetValueOrDefault(key) + 1;
            try
            {
                if (fight.IsWipe)
                {
                    known.Wipes[key] = await ReadWipeAsync(report, fight, latest);
                }
                else
                {
                    known.Kills.TryGetValue(key, out var previous);
                    known.Kills[key] = await ReadKillAsync(report, fight, latest, previous, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsQuota(ex))
            {
                // The budget is spent. That is not this pull's fault, and asking again now
                // cannot succeed. Stop reading until the next redraw.
                known.Attempts[key]--;
                budgetSpent = true;
                Health.QuotaWait();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Raid recap live pull {Fight} unavailable for {Report} ({Type})", fight.Id, report.Code, ex.GetType().Name);
            }
        }

        known.Wipes.TryGetValue(wipe == null ? "" : PullKey(wipe), out var latestWipe);
        known.Kills.TryGetValue(kill == null ? "" : PullKey(kill), out var latestKill);

        // A pattern is only stated once every wipe it could cover has been looked at, so the
        // card never says "3 of 3" while five more wipes are still unread.
        RaidRecapLiveWipePattern? pattern = null;
        if (bossWipes.All(f => known.Settled(PullKey(f), f == wipe)))
        {
            var blows = bossWipes
                .Select(f => known.Wipes.TryGetValue(PullKey(f), out var facts) && facts.Complete ? facts : null)
                .Where(facts => facts != null)
                .Select(facts => facts!.FirstBlow!)
                .ToArray();
            pattern = RaidRecapLiveHighlights.FindPattern(blows);
        }

        var perKill = kills
            .Where(f => known.Kills.ContainsKey(PullKey(f)))
            .Select(f => known.Kills[PullKey(f)])
            .Select(k => (IReadOnlyList<RaidRecapLivePerformer>)k.TopDamage.Concat(k.TopHealing).ToArray())
            .ToArray();

        return new RaidRecapLiveHighlights
        {
            Wipe = wipe!,
            FirstDeaths = latestWipe?.FirstDeaths ?? Array.Empty<RaidRecapLiveDeath>(),
            TotalDeaths = latestWipe?.Total ?? 0,
            DeathsComplete = latestWipe?.Complete ?? false,
            Pattern = pattern!,
            Kill = kill!,
            TopDamage = latestKill?.TopDamage ?? Array.Empty<RaidRecapLivePerformer>(),
            TopHealing = latestKill?.TopHealing ?? Array.Empty<RaidRecapLivePerformer>(),
            Quality = latestKill?.Quality!,
            Mvps = RaidRecapLiveHighlights.FindMvps(perKill),
            Utility = utilityKey != null && known.Utility.TryGetValue(utilityKey, out var utility) ? utility : null!
        };
    }

    /// <summary>
    /// The latest wipe is shown in full, so it is read with the roster for spec and class.
    /// An earlier wipe only feeds the pattern, which needs the first killing blow alone.
    /// </summary>
    private async Task<WipeFacts> ReadWipeAsync(RaidRecapReport report, RaidRecapFight fight, bool latest)
    {
        var deaths = await _service.GetDeathsAsync(report, fight);
        var roster = latest && deaths.Deaths.Count > 0 ? await _service.GetRosterAsync(report, fight) : null;
        return new WipeFacts(
            latest ? RaidRecapLiveHighlights.Deaths(deaths, roster!) : Array.Empty<RaidRecapLiveDeath>(),
            deaths.Deaths.Count,
            deaths.Complete,
            deaths.Deaths.OrderBy(d => d.ElapsedMs).FirstOrDefault()?.Ability,
            Full: latest);
    }

    /// <summary>
    /// The latest kill is shown in full: specs, parses and the raid's standing. An earlier
    /// kill only feeds the night's standouts, which need the top three names alone.
    /// </summary>
    private async Task<KillFacts> ReadKillAsync(RaidRecapReport report, RaidRecapFight fight, bool latest, KillFacts? previous, CancellationToken ct)
    {
        if (latest && previous is { Complete: true, Full: true, TargetsPending: true })
        {
            // Everything else is in hand; only some target lines are still to read.
            var (damageTop, damagePending) = await WithTargetsAsync(report, fight, false, previous.TopDamage, previous.TopDamage, ct);
            var (healingTop, healingPending) = await WithTargetsAsync(report, fight, true, previous.TopHealing, previous.TopHealing, ct);
            return previous with { TopDamage = damageTop, TopHealing = healingTop, TargetsPending = damagePending || healingPending };
        }

        if (!latest)
        {
            return new KillFacts(
                RaidRecapLiveHighlights.Performers(await _service.GetTableAsync(report, fight, healing: false)),
                RaidRecapLiveHighlights.Performers(await _service.GetTableAsync(report, fight, healing: true)),
                Quality: null,
                Complete: true,
                Full: false);
        }

        var damage = await _service.GetOutputAsync(report, fight, healing: false);
        var healing = await _service.GetOutputAsync(report, fight, healing: true);
        var (topDamage, damageTargetsPending) = await WithTargetsAsync(report, fight, false, RaidRecapLiveHighlights.Performers(damage), previous?.TopDamage, ct);
        var (topHealing, healingTargetsPending) = await WithTargetsAsync(report, fight, true, RaidRecapLiveHighlights.Performers(healing), previous?.TopHealing, ct);
        return new KillFacts(
            topDamage,
            topHealing,
            RaidRecapLiveHighlights.FindQuality(damage, healing),
            Complete: damage.Parses != null && healing.Parses != null,
            Full: true)
        {
            TargetsPending = damageTargetsPending || healingTargetsPending
        };
    }

    /// <summary>
    /// Adds where each top player's damage or healing went: one small read per player, kept
    /// from an earlier read of the same kill when there is one. Best effort: a player whose
    /// targets cannot be read has no line. The first failed read, including one slower than
    /// <see cref="TargetReadDeadline"/>, leaves the rest for a later redraw, and the result
    /// says so; <see cref="MaxPullAttempts"/> bounds how often that happens.
    /// </summary>
    private async Task<(IReadOnlyList<RaidRecapLivePerformer> Top, bool Pending)> WithTargetsAsync(
        RaidRecapReport report,
        RaidRecapFight fight,
        bool healing,
        IReadOnlyList<RaidRecapLivePerformer> top,
        IReadOnlyList<RaidRecapLivePerformer>? earlier,
        CancellationToken ct)
    {
        if (_source is not IRaidRecapTargetSource source)
        {
            return (top, false);
        }

        var result = new List<RaidRecapLivePerformer>(top.Count);
        var pending = false;
        foreach (var player in top)
        {
            var kept = earlier?.FirstOrDefault(p => p.ActorId == player.ActorId && p.Targets != null)?.Targets;
            if (kept != null || player.ActorId is not int actor || actor <= 0)
            {
                result.Add(kept == null ? player : player with { Targets = kept });
                continue;
            }

            if (pending)
            {
                result.Add(player);
                continue;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TargetReadDeadline);
            try
            {
                var table = await source.GetRaidRecapTargetsAsync(report, fight, healing, actor, deadline.Token);
                // A table that is not in the expected shape, or does not add up to the
                // player's own total, gives an empty list: no line, and no asking again.
                result.Add(player with
                {
                    Targets = RaidRecapTargets.Parse(table, player.Name, actor, player.Total) ?? Array.Empty<RaidRecapTarget>()
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                pending = true;
                _logger.LogDebug("Raid recap live targets unavailable for {Report} fight {Fight} ({Type})", report.Code, fight.Id, ex.GetType().Name);
                result.Add(player);
            }
        }

        return (result, pending);
    }

    // A finished pull never changes, so its id and end time identify what was read.
    private static string PullKey(RaidRecapFight fight) => FormattableString.Invariant($"{fight.Id}:{fight.EndMs}");

    private sealed record WipeFacts(
        IReadOnlyList<RaidRecapLiveDeath> FirstDeaths,
        int Total,
        bool Complete,
        string? FirstBlow,
        bool Full);

    private sealed record KillFacts(
        IReadOnlyList<RaidRecapLivePerformer> TopDamage,
        IReadOnlyList<RaidRecapLivePerformer> TopHealing,
        RaidRecapLiveKillQuality? Quality,
        bool Complete,
        bool Full)
    {
        /// <summary>Some target lines could not be read yet and are worth another try.</summary>
        public bool TargetsPending { get; init; }
    }

    private sealed class ReportMemory
    {
        public Dictionary<string, WipeFacts> Wipes { get; } = new();
        public Dictionary<string, KillFacts> Kills { get; } = new();
        public Dictionary<string, int> Attempts { get; } = new();
        public Dictionary<string, RaidRecapLiveUtility> Utility { get; } = new();

        /// <summary>Kicks and dispels need no further reading: complete with names, or every attempt used.</summary>
        public bool UtilitySettled(string key) =>
            Attempts.GetValueOrDefault(key) >= MaxPullAttempts
            || (Utility.TryGetValue(key, out var utility) && utility.Settled);

        /// <summary>
        /// True when the pull needs no further reading: what is held is complete and detailed
        /// enough for how the pull is shown now, or every attempt has been used.
        /// </summary>
        public bool Settled(string key, bool latest)
        {
            if (Attempts.GetValueOrDefault(key) >= MaxPullAttempts)
            {
                return true;
            }

            if (Wipes.TryGetValue(key, out var wipe))
            {
                return wipe.Complete && (wipe.Full || !latest);
            }

            return Kills.TryGetValue(key, out var kill) && kill.Complete && (kill.Full || !latest)
                && !(latest && kill.TargetsPending);
        }
    }

    private async Task<RaidRecapReport> LoadReportAsync(string code) => (RaidRecapReport)await _cache.GetAsync(
        RaidRecapService.ReportKey(code),
        async () => await _source.GetRaidRecapReportAsync(code),
        TimeSpan.FromSeconds(30));

    /// <summary>
    /// What the card's header needs. When the log holds several raids, the header shows this
    /// raid's own start and, once a later raid has taken over or the card is closed, its end.
    /// The first raid starts with the log, so time spent before its first pull still counts.
    /// </summary>
    private RaidRecapLiveInfo Info(
        RaidRecapLiveCard card,
        RaidRecapReport? full = null,
        IReadOnlyList<RaidRecapLiveSession>? sessions = null,
        RaidRecapLiveSession? session = null,
        bool closed = false)
    {
        var info = new RaidRecapLiveInfo(
            card.GuildName ?? "",
            card.Region ?? "",
            card.ZoneName ?? "",
            _discord.GuildIconUrl((ulong)card.DiscordGuildId) ?? "");
        if (full?.StartTime is not double start || session == null || sessions == null || sessions.Count == 0)
        {
            return info;
        }

        var first = sessions[0].StartMs == session.StartMs;
        var latest = sessions[^1].StartMs == session.StartMs;
        var fromLogStart = first && session.StartMs <= LogStartCountsWithin.TotalMilliseconds;
        return info with
        {
            HeaderStartMs = fromLogStart ? null : start + session.StartMs,
            HeaderEndMs = latest && !closed ? null : start + session.LastActivityMs
        };
    }

    /// <summary>The log narrowed to the card's raid. Without one, every raid pull in the log.</summary>
    private static RaidRecapReport Scope(RaidRecapReport full, RaidRecapLiveSession? session) => session != null
        ? RaidRecapLiveSessions.Scope(full, session)
        : full with { Fights = full.Fights.Where(RaidRecapLiveSessions.IsRaid).ToArray() };

    /// <summary>True when the raid has had no raid pull for <see cref="NoRaidPullEndAfter"/>.</summary>
    public static bool RaidQuiet(RaidRecapReport full, RaidRecapLiveSession? session, DateTimeOffset now) =>
        session != null
        && !session.HasPullInProgress
        && RaidRecapLiveSessions.LastActivityAt(full, session) is { } at
        && now - at >= NoRaidPullEndAfter;

    /// <summary>True when a read was refused because the log changed after it was fetched.</summary>
    public static bool ReportMoved(Exception ex) => ex is RaidRecapSnapshotException
        || (ex is InvalidOperationException && ex.Message.StartsWith("Report changed", StringComparison.Ordinal));

    /// <summary>
    /// True when WarcraftLogs refused because the hourly budget is spent or it asked us to
    /// slow down. The client reports both as plain exceptions.
    /// </summary>
    public static bool IsQuota(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
        InvalidOperationException invalid => invalid.Message.Contains("quota", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    public static bool IsLive(WclV2Report report, DateTimeOffset now)
    {
        if (report.EndTime <= 0 || report.EndTime > 253402300799999)
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeMilliseconds(report.EndTime);
        return age <= LiveWindow && age >= -LiveWindow;
    }

    /// <summary>When the latest pull finished, as wall-clock time. Fight times are relative to the log start.</summary>
    public static DateTimeOffset? LastPullAt(RaidRecapReport report)
    {
        var end = report.Fights.Max(f => f.EndMs);
        if (report.StartTime is not (>= 0 and <= 253402300799999) || end is not (>= 0 and <= 31536000000))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(report.StartTime.Value + end.Value));
    }

    public static bool HasEnded(RaidRecapReport report, RaidRecapLiveCard card, DateTimeOffset now)
    {
        if (now - Utc(card.StartedAt) >= MaxWatch)
        {
            return true;
        }

        var lastEvent = report.EndTime is >= 0 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)report.EndTime.Value)
            : Utc(card.LastChangedAt);
        return now - lastEvent >= EndAfter;
    }

    /// <summary>Everything the card shows. The raid length only moves it every five minutes.</summary>
    public static string Fingerprint(RaidRecapReport report, bool ended)
    {
        var last = report.Fights.OrderBy(f => f.StartMs ?? double.MaxValue).ThenBy(f => f.Id).LastOrDefault();
        // While live, the header's raid length grows with the log; redraw every five minutes
        // for it. A final card is timed to its last pull, so only new pulls change it.
        var span = !ended && report.EndTime > report.StartTime
            ? (long)((report.EndTime.Value - report.StartTime!.Value) / TimeSpan.FromMinutes(5).TotalMilliseconds)
            : 0;
        return FormattableString.Invariant(
            $"{report.Fights.Count}:{report.Kills}:{report.Wipes}:{last?.Id}:{last?.EndMs}:{last?.Remaining}:{span}:{(ended ? 1 : 0)}");
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? Limit(string? value, int length) =>
        string.IsNullOrEmpty(value) || value.Length <= length ? value : value[..length];
}
