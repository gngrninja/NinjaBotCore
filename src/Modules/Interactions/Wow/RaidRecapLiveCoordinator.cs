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

    public const int MaxFailures = 10;

    private static readonly int?[] RaidDifficulties = { 1, 3, 4, 5 };

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
    private readonly Dictionary<string, HighlightCache> _highlights = new();
    private readonly Dictionary<long, DateTimeOffset> _lastPull = new();

    public RaidRecapLiveCoordinator(
        IServiceScopeFactory scopes,
        IRaidRecapSource source,
        RaidRecapService service,
        RaidRecapCache cache,
        RaidRecapLiveGate gate,
        IRaidRecapLiveDiscord discord,
        ILogger<RaidRecapLiveCoordinator> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _source = source;
        _service = service;
        _cache = cache;
        _gate = gate;
        _discord = discord;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task RunSweepAsync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            await SweepAsync(ct);
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
                await StopAsync(card);
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var mode = await _gate.ModeAsync();
        if (mode == RaidRecapRolloutMode.Off)
        {
            // Kill switch: the sweep makes no WarcraftLogs calls and no Discord calls.
            return;
        }

        Dictionary<long, long?> enabled;
        List<(long Id, long Guild)> live;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            enabled = await db.RaidRecapLiveSettings.AsNoTracking()
                .Where(s => s.Enabled && s.ChannelId != null)
                .ToDictionaryAsync(s => s.DiscordGuildId, s => s.ChannelId, ct);
            live = (await db.RaidRecapLiveCards.AsNoTracking()
                    .Where(c => c.State == RaidRecapLiveState.Live)
                    .OrderBy(c => c.Id)
                    .Select(c => new { c.Id, c.DiscordGuildId })
                    .ToListAsync(ct))
                .Select(c => (c.Id, c.DiscordGuildId))
                .ToList();
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
        var busy = new HashSet<long>();
        foreach (var (cardId, guild) in live)
        {
            ct.ThrowIfCancellationRequested();
            var eligible = enabled.ContainsKey(guild) && await AllowedAsync(guild);
            var stillLive = await ContainAsync(
                () => RefreshCardAsync(cardId, enabled.ContainsKey(guild), eligible, watching < MaxLiveCards, ct),
                "refresh", cardId, ct);
            if (stillLive)
            {
                busy.Add(guild);
                if (eligible)
                {
                    // Cards outside the rollout cost nothing, so they do not use up a slot.
                    watching++;
                }
            }
        }

        // Longest-waiting servers first, a few per sweep, so a restart or a large rollout
        // never turns into a burst of WarcraftLogs calls.
        var now = _clock();
        var due = enabled.Keys
            .Where(g => !busy.Contains(g))
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
            await StopAsync(card);
            return;
        }

        if (!eligible)
        {
            // The rollout no longer covers this server. Spend nothing on it, but do not let
            // the card claim to be live for ever.
            if (now - Utc(card.LastChangedAt) >= EndAfter || now - Utc(card.StartedAt) >= MaxWatch)
            {
                await StopAsync(card);
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
            var report = await LoadReportAsync(card.ReportCode);
            if (LastPullAt(report) is { } pulled)
            {
                _lastPull[card.Id] = pulled;
            }

            var ended = HasEnded(report, card, now);
            var fingerprint = Fingerprint(report, ended);
            if (fingerprint != card.Fingerprint || card.MessageId == 0)
            {
                var highlights = await HighlightsAsync(report, ct);
                var component = RaidRecapView.Live(report, Info(card), ended, now, highlights);
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
                        card.State = RaidRecapLiveState.Stopped;
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
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsQuota(ex))
        {
            // The WarcraftLogs budget is spent for now. That is not this card's fault, so it
            // does not count against it. Try again at the next refresh.
            _logger.LogInformation("Raid recap live refresh for card {CardId} waits for WarcraftLogs quota", card.Id);
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
        if (card.Failures >= MaxFailures)
        {
            await StopAsync(card);
        }
    }

    private async Task StopAsync(RaidRecapLiveCard card)
    {
        Forget(card);
        card.State = RaidRecapLiveState.Stopped;
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

    private void Forget(RaidRecapLiveCard card)
    {
        _lastPull.Remove(card.Id);
        _highlights.Remove(card.ReportCode);
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
            reports = (IReadOnlyList<WclV2Report>)await _cache.GetAsync(
                RaidRecapService.GuildKey(wow.Name, wow.Realm, wow.Region),
                async () => await _source.GetRaidRecapReportsAsync(wow.Name, wow.Realm, wow.Region),
                TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (IsQuota(ex))
        {
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

            var existing = await db.RaidRecapLiveCards.FirstOrDefaultAsync(c => c.DiscordGuildId == guild && c.ReportCode == code, ct);
            if (existing != null && (existing.State != RaidRecapLiveState.Ended || now - Utc(existing.StartedAt) >= MaxWatch))
            {
                // Already live, or stopped on purpose, or watched for long enough.
                continue;
            }

            var report = await LoadReportAsync(code);
            if (!report.Fights.Any(f => RaidDifficulties.Contains(f.Difficulty)))
            {
                // No raid boss pull yet, or not a raid log.
                continue;
            }

            if (existing != null)
            {
                return await ResumeAsync(db, existing, report, now, ct);
            }

            return await PostAsync(db, guild, channel, wow, candidate, report, now, ct);
        }

        return false;
    }

    private async Task<bool> PostAsync(
        NinjaBotEntities db,
        long guild,
        long channel,
        RaidRecapGuild wow,
        WclV2Report listed,
        RaidRecapReport report,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var card = new RaidRecapLiveCard
        {
            DiscordGuildId = guild,
            ChannelId = channel,
            MessageId = 0,
            ReportCode = report.Code,
            State = RaidRecapLiveState.Live,
            GuildName = Limit(wow.Name, 100),
            Region = Limit(wow.Region, 8),
            ZoneName = Limit(listed.ZoneName, 200),
            StartedAt = now.UtcDateTime,
            LastChangedAt = now.UtcDateTime,
            LastCheckedAt = now.UtcDateTime
        };

        // Claim the report before posting. The unique index makes sure that two bot processes
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

        ulong? message = null;
        try
        {
            var highlights = await HighlightsAsync(report, ct);
            message = await _discord.SendAsync((ulong)channel, RaidRecapView.Live(report, Info(card), ended: false, now, highlights));
        }
        finally
        {
            if (message == null)
            {
                // Nothing was posted. Release the claim so the next check can try again.
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
        _logger.LogInformation("Raid recap live card posted for guild {GuildId}, report {Report}", guild, report.Code);
        return true;
    }

    /// <summary>The raid came back after a long break. The same card carries on.</summary>
    private async Task<bool> ResumeAsync(
        NinjaBotEntities db,
        RaidRecapLiveCard card,
        RaidRecapReport report,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var fingerprint = Fingerprint(report, ended: false);
        var highlights = await HighlightsAsync(report, ct);
        var result = await _discord.EditAsync((ulong)card.ChannelId, (ulong)card.MessageId,
            RaidRecapView.Live(report, Info(card), ended: false, now, highlights));
        if (result == RaidRecapLiveEdit.Missing)
        {
            card.State = RaidRecapLiveState.Stopped;
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
        _logger.LogInformation("Raid recap live card resumed for guild {GuildId}, report {Report}", card.DiscordGuildId, report.Code);
        return true;
    }

    /// <summary>
    /// Deaths of the latest wipe and top output of the latest kill. Each half is loaded again
    /// only when its own pull changes, so a new wipe never re-reads the last kill. The card
    /// is still posted if any of it fails.
    /// </summary>
    private async Task<RaidRecapLiveHighlights?> HighlightsAsync(RaidRecapReport report, CancellationToken ct)
    {
        var wipe = RaidRecapLiveHighlights.LatestWipe(report);
        var kill = RaidRecapLiveHighlights.LatestKill(report);
        if (wipe == null && kill == null)
        {
            _highlights.Remove(report.Code);
            return null;
        }

        if (!_highlights.TryGetValue(report.Code, out var known))
        {
            if (_highlights.Count >= MaxLiveCards * 2)
            {
                _highlights.Clear();
            }

            known = new HighlightCache();
            _highlights[report.Code] = known;
        }

        var wipeKey = wipe == null ? null : FormattableString.Invariant($"{wipe.Id}:{wipe.EndMs}");
        if (wipeKey != known.WipeKey)
        {
            known.WipeKey = null;
            known.Deaths = new RaidRecapLiveHighlights();
            if (wipe != null)
            {
                try
                {
                    var deaths = await _service.GetDeathsAsync(report, wipe);
                    var roster = deaths.Deaths.Count > 0 ? await _service.GetRosterAsync(report, wipe) : null;
                    known.Deaths = new RaidRecapLiveHighlights
                    {
                        FirstDeaths = RaidRecapLiveHighlights.Deaths(deaths, roster!),
                        TotalDeaths = deaths.Deaths.Count,
                        DeathsComplete = deaths.Complete
                    };
                    known.WipeKey = wipeKey;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Not remembered, so it is tried again the next time the card is redrawn.
                    _logger.LogDebug("Raid recap live deaths unavailable for {Report} ({Type})", report.Code, ex.GetType().Name);
                }
            }
        }

        var killKey = kill == null ? null : FormattableString.Invariant($"{kill.Id}:{kill.EndMs}");
        if (killKey != known.KillKey)
        {
            known.KillKey = null;
            known.Output = new RaidRecapLiveHighlights();
            if (kill != null)
            {
                try
                {
                    var damage = await _service.GetOutputAsync(report, kill, healing: false);
                    var healing = await _service.GetOutputAsync(report, kill, healing: true);
                    known.Output = new RaidRecapLiveHighlights
                    {
                        TopDamage = RaidRecapLiveHighlights.Performers(damage),
                        TopHealing = RaidRecapLiveHighlights.Performers(healing)
                    };
                    known.KillKey = killKey;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Raid recap live output unavailable for {Report} ({Type})", report.Code, ex.GetType().Name);
                }
            }
        }

        return new RaidRecapLiveHighlights
        {
            Wipe = wipe!,
            FirstDeaths = known.Deaths.FirstDeaths,
            TotalDeaths = known.Deaths.TotalDeaths,
            DeathsComplete = known.Deaths.DeathsComplete,
            Kill = kill!,
            TopDamage = known.Output.TopDamage,
            TopHealing = known.Output.TopHealing
        };
    }

    private sealed class HighlightCache
    {
        public string? WipeKey { get; set; }
        public RaidRecapLiveHighlights Deaths { get; set; } = new();
        public string? KillKey { get; set; }
        public RaidRecapLiveHighlights Output { get; set; } = new();
    }

    private async Task<RaidRecapReport> LoadReportAsync(string code) => (RaidRecapReport)await _cache.GetAsync(
        RaidRecapService.ReportKey(code),
        async () => await _source.GetRaidRecapReportAsync(code),
        TimeSpan.FromSeconds(30));

    private RaidRecapLiveInfo Info(RaidRecapLiveCard card) => new(
        card.GuildName ?? "",
        card.Region ?? "",
        card.ZoneName ?? "",
        _discord.GuildIconUrl((ulong)card.DiscordGuildId) ?? "");

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
        var span = report.EndTime > report.StartTime
            ? (long)((report.EndTime.Value - report.StartTime!.Value) / TimeSpan.FromMinutes(5).TotalMilliseconds)
            : 0;
        return FormattableString.Invariant(
            $"{report.Fights.Count}:{report.Kills}:{report.Wipes}:{last?.Id}:{last?.EndMs}:{last?.Remaining}:{span}:{(ended ? 1 : 0)}");
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? Limit(string? value, int length) =>
        string.IsNullOrEmpty(value) || value.Length <= length ? value : value[..length];
}
