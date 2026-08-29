using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.WarcraftLogs;

namespace NinjaBotHelpers.Workers;

/// <summary>
/// Background worker that monitors WarcraftLogs for new reports and posts them to Discord.
/// Uses tiered checking based on guild activity to optimize API usage.
/// </summary>
public class LogMonitoringWorker : BackgroundService
{
    private readonly ILogger<LogMonitoringWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HelpersConfiguration _config;
    private readonly DiscordRestClient _discordClient;
    private readonly WarcraftLogsClient _wclClient;
    private readonly IHttpClientFactory _httpClientFactory;

    private const int ColorBlue = 0x3498DB;
    private const int MaxBatchSize = 50;

    public LogMonitoringWorker(
        ILogger<LogMonitoringWorker> logger,
        IServiceScopeFactory scopeFactory,
        HelpersConfiguration config,
        DiscordRestClient discordClient,
        WarcraftLogsClient wclClient,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _discordClient = discordClient;
        _wclClient = wclClient;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.LogMonitoring.Enabled)
        {
            _logger.LogInformation("LogMonitoring is disabled via configuration");
            return;
        }

        if (!_wclClient.IsConfigured)
        {
            _logger.LogWarning("LogMonitoring disabled - WarcraftLogs credentials not configured");
            return;
        }

        _logger.LogInformation("LogMonitoring starting - checking every {Interval} minutes",
            _config.LogMonitoring.CheckIntervalMinutes);

        await Task.Delay(TimeSpan.FromSeconds(_config.LogMonitoring.InitialDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForNewLogsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LogMonitoring check cycle");
            }

            await Task.Delay(TimeSpan.FromMinutes(_config.LogMonitoring.CheckIntervalMinutes), stoppingToken);
        }

        _logger.LogInformation("LogMonitoring stopping");
    }

    private async Task CheckForNewLogsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        // Get all servers with monitoring enabled
        var monitoringConfigs = await db.LogMonitoring
            .Where(m => m.MonitorLogs)
            .ToListAsync(cancellationToken);

        if (monitoringConfigs.Count == 0)
        {
            _logger.LogDebug("No servers have log monitoring enabled");
            return;
        }

        _logger.LogInformation("[LogMonitoring] Checking {Count} servers for new logs", monitoringConfigs.Count);

        // Check each game version separately
        await CheckGameVersionAsync(WowGameVersion.Retail, monitoringConfigs, db, cancellationToken);
        await CheckGameVersionAsync(WowGameVersion.Classic, monitoringConfigs, db, cancellationToken);
        await CheckGameVersionAsync(WowGameVersion.Vanilla, monitoringConfigs, db, cancellationToken);
    }

    private async Task CheckGameVersionAsync(
        WowGameVersion gameVersion,
        List<LogMonitoring> monitoringConfigs,
        HelpersDbContext db,
        CancellationToken cancellationToken)
    {
        // Get guilds to check based on game version
        var guildsToCheck = await GetGuildsToCheckAsync(gameVersion, monitoringConfigs, db, cancellationToken);

        if (guildsToCheck.Count == 0)
        {
            _logger.LogDebug("[LogMonitoring] No {GameVersion} guilds need checking at this time", gameVersion);
            return;
        }

        _logger.LogInformation("[LogMonitoring] Checking {Count} {GameVersion} guilds", guildsToCheck.Count, gameVersion);

        // Process in batches
        for (int i = 0; i < guildsToCheck.Count; i += MaxBatchSize)
        {
            var batch = guildsToCheck.Skip(i).Take(MaxBatchSize).ToList();
            await ProcessBatchAsync(gameVersion, batch, monitoringConfigs, db, cancellationToken);
        }
    }

    internal async Task<List<GuildCheckInfo>> GetGuildsToCheckAsync(
        WowGameVersion gameVersion,
        List<LogMonitoring> monitoringConfigs,
        HelpersDbContext db,
        CancellationToken cancellationToken)
    {
        var result = new List<GuildCheckInfo>();
        var now = DateTime.UtcNow;
        var serverIds = monitoringConfigs.Select(m => m.ServerId).ToHashSet();

        // Get the appropriate guild associations based on game version
        IEnumerable<GuildCheckInfo> guildAssociations;

        switch (gameVersion)
        {
            case WowGameVersion.Retail:
                var retailGuilds = await db.WowGuildAssociations
                    .Where(g => g.ServerId != null && serverIds.Contains(g.ServerId.Value))
                    .ToListAsync(cancellationToken);

                guildAssociations = retailGuilds
                    .Where(g => !string.IsNullOrEmpty(g.WowGuild) && !string.IsNullOrEmpty(g.LocalRealmSlug))
                    .Select(g => new GuildCheckInfo
                    {
                        ServerId = g.ServerId!.Value,
                        GuildName = g.WowGuild!,
                        ServerSlug = g.LocalRealmSlug!,
                        ServerRegion = g.WowRegion ?? "us",
                        GuildKey = $"retail_{g.ServerId}_{g.WowGuild}_{g.LocalRealmSlug}"
                    });
                break;

            case WowGameVersion.Classic:
                var classicGuilds = await db.WowClassicGuild
                    .Where(g => g.ServerId != null && serverIds.Contains(g.ServerId.Value))
                    .ToListAsync(cancellationToken);

                guildAssociations = classicGuilds
                    .Where(g => !string.IsNullOrEmpty(g.WowGuild) && !string.IsNullOrEmpty(g.WowRealm))
                    .Select(g => new GuildCheckInfo
                    {
                        ServerId = g.ServerId!.Value,
                        GuildName = g.WowGuild!,
                        ServerSlug = g.WowRealm!.ToLower().Replace(" ", "-").Replace("'", ""),
                        ServerRegion = g.WowRegion ?? "us",
                        GuildKey = $"classic_{g.ServerId}_{g.WowGuild}_{g.WowRealm}"
                    });
                break;

            case WowGameVersion.Vanilla:
                var vanillaGuilds = await db.WowVanillaGuild
                    .Where(g => g.ServerId != null && serverIds.Contains(g.ServerId.Value))
                    .ToListAsync(cancellationToken);

                guildAssociations = vanillaGuilds
                    .Where(g => !string.IsNullOrEmpty(g.WowGuild) && !string.IsNullOrEmpty(g.WowRealm))
                    .Select(g => new GuildCheckInfo
                    {
                        ServerId = g.ServerId!.Value,
                        GuildName = g.WowGuild!,
                        ServerSlug = g.WowRealm!.ToLower().Replace(" ", "-").Replace("'", ""),
                        ServerRegion = g.WowRegion ?? "us",
                        GuildKey = $"vanilla_{g.ServerId}_{g.WowGuild}_{g.WowRealm}"
                    });
                break;

            default:
                return result;
        }

        // Apply tiered checking based on activity level and last check time
        foreach (var guild in guildAssociations)
        {
            var config = monitoringConfigs.FirstOrDefault(m => m.ServerId == guild.ServerId);
            if (config == null) continue;

            // When was a log last found? (determines activity tier)
            var lastLogFound = gameVersion switch
            {
                WowGameVersion.Retail => config.LatestLogRetail ?? config.LatestLog,
                WowGameVersion.Classic => config.LatestLogClassic,
                WowGameVersion.Vanilla => config.LatestLogVanilla,
                _ => null
            };

            // When did we last check WCL? (determines if check is due)
            var lastChecked = gameVersion switch
            {
                WowGameVersion.Retail => config.LastCheckedRetail,
                WowGameVersion.Classic => config.LastCheckedClassic,
                WowGameVersion.Vanilla => config.LastCheckedVanilla,
                _ => null
            };

            // Determine if this guild should be checked this cycle
            if (ShouldCheckGuild(lastLogFound, lastChecked, now))
            {
                guild.MonitoringConfig = config;
                result.Add(guild);
            }
        }

        return result;
    }

    /// <summary>
    /// Determines if a guild should be checked based on tiered intervals.
    /// Tier is determined by when a log was last found (activity level).
    /// Check frequency is determined by when we last checked WCL.
    /// </summary>
    /// <param name="lastLogFound">When a log was last found for this guild (determines tier)</param>
    /// <param name="lastChecked">When we last checked WCL for this guild (determines if check is due)</param>
    /// <param name="now">Current UTC time</param>
    internal bool ShouldCheckGuild(DateTime? lastLogFound, DateTime? lastChecked, DateTime now)
    {
        var settings = _config.LogMonitoring;

        // Never checked - always check
        if (lastChecked == null)
        {
            return true;
        }

        var minutesSinceLastCheck = (now - lastChecked.Value).TotalMinutes;

        // Determine tier based on when a log was last found
        var daysSinceLastLog = lastLogFound.HasValue
            ? (now - lastLogFound.Value).TotalDays
            : double.MaxValue; // Never found a log = treat as inactive

        // Tier 1: Active guilds (logged in last N days) - check every cycle
        if (daysSinceLastLog <= settings.Tier1ThresholdDays)
        {
            return true;
        }

        // Tier 2: Semi-active guilds - check every Tier2IntervalHours
        if (daysSinceLastLog <= settings.Tier2ThresholdDays)
        {
            return minutesSinceLastCheck >= settings.Tier2IntervalHours * 60;
        }

        // Tier 3: Inactive guilds - check every Tier3IntervalHours
        return minutesSinceLastCheck >= settings.Tier3IntervalHours * 60;
    }

    internal async Task ProcessBatchAsync(
        WowGameVersion gameVersion,
        List<GuildCheckInfo> guilds,
        List<LogMonitoring> monitoringConfigs,
        HelpersDbContext db,
        CancellationToken cancellationToken)
    {
        var guildTuples = guilds
            .Select(g => (g.GuildName, g.ServerSlug, g.ServerRegion, g.GuildKey))
            .ToList();

        WclV2BatchResult batchResult;
        try
        {
            batchResult = await _wclClient.GetBatchGuildReportsAsync(guildTuples, gameVersion, cancellationToken);
        }
        catch (WclRateLimitException ex)
        {
            _logger.LogWarning("[LogMonitoring] WCL rate limit reached for {GameVersion} batch ({Percent:F1}% used). Will retry next cycle.",
                gameVersion, ex.RateLimitData.UsagePercent);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LogMonitoring] Batch query failed for {GameVersion}", gameVersion);
            return;
        }

        // Update LastChecked timestamp for ALL guilds in this batch (regardless of results)
        // This ensures tiered checking works correctly based on actual check times
        var now = DateTime.UtcNow;
        var serverIds = guilds.Select(g => g.ServerId).Distinct().ToList();
        var configsToUpdate = await db.LogMonitoring
            .Where(m => serverIds.Contains(m.ServerId))
            .ToListAsync(cancellationToken);

        foreach (var config in configsToUpdate)
        {
            switch (gameVersion)
            {
                case WowGameVersion.Retail:
                    config.LastCheckedRetail = now;
                    break;
                case WowGameVersion.Classic:
                    config.LastCheckedClassic = now;
                    break;
                case WowGameVersion.Vanilla:
                    config.LastCheckedVanilla = now;
                    break;
            }
        }
        await db.SaveChangesAsync(cancellationToken);

        // Process each guild's result
        foreach (var guild in guilds)
        {
            if (!batchResult.Reports.TryGetValue(guild.GuildKey, out var report))
            {
                continue;
            }

            // Check if this report was already posted
            var alreadyPosted = await db.WclPosted
                .AnyAsync(w => w.ServerId == guild.ServerId && w.ReportId == report.Code, cancellationToken);

            if (alreadyPosted)
            {
                _logger.LogDebug("[LogMonitoring] Report {ReportCode} already posted for server {ServerId}",
                    report.Code, guild.ServerId);
                continue;
            }

            // Check if this is a new report
            var lastReportId = gameVersion switch
            {
                WowGameVersion.Retail => guild.MonitoringConfig?.RetailReportId ?? guild.MonitoringConfig?.ReportId,
                WowGameVersion.Classic => guild.MonitoringConfig?.ClassicReportId,
                WowGameVersion.Vanilla => guild.MonitoringConfig?.VanillaReportId,
                _ => null
            };

            if (report.Code == lastReportId)
            {
                continue;
            }

            // New report found - post it
            await PostNewLogAsync(guild, report, gameVersion, db, cancellationToken);
        }
    }

    internal async Task PostNewLogAsync(
        GuildCheckInfo guild,
        WclV2Report report,
        WowGameVersion gameVersion,
        HelpersDbContext db,
        CancellationToken cancellationToken)
    {
        if (guild.MonitoringConfig == null)
        {
            _logger.LogWarning("[LogMonitoring] No monitoring config for guild {GuildName}", guild.GuildName);
            return;
        }

        var channelId = (ulong)guild.MonitoringConfig.ChannelId;

        // Build the embed
        var gameVersionLabel = gameVersion switch
        {
            WowGameVersion.Classic => " (Classic)",
            WowGameVersion.Vanilla => " (Vanilla)",
            _ => ""
        };

        var embed = DiscordEmbed.Create($"New log for [{guild.GuildName}]{gameVersionLabel}!", ColorBlue)
            .WithDescription($"[**{report.Title}** / **{report.ZoneName}**]({report.ReportURL})")
            .WithField("Start Time", $"<t:{report.StartTime / 1000}:f>", true)
            .WithField("Created By", report.OwnerName, true)
            .WithField("Links",
                $"[WoWAnalyzer](https://wowanalyzer.com/report/{report.Code}) | [WipeFest](https://www.wipefest.net/report/{report.Code})")
            .WithFooter("WarcraftLogs Monitor | NinjaBotHelpers");

        // Send to Discord. The client owns detailed/throttled failure logging.
        var delivery = await _discordClient.SendChannelMessageWithResultAsync(
            channelId,
            embed,
            cancellationToken);

        if (delivery.Success)
        {
            _logger.LogInformation("[LogMonitoring] Posted new {GameVersion} log for [{GuildName}]: {ReportCode}",
                gameVersion, guild.GuildName, report.Code);

            // Update the monitoring config with the new report ID
            var config = await db.LogMonitoring.FindAsync(new object[] { guild.MonitoringConfig.Id }, cancellationToken);
            if (config != null)
            {
                switch (gameVersion)
                {
                    case WowGameVersion.Retail:
                        config.RetailReportId = report.Code;
                        config.LatestLogRetail = DateTime.UtcNow;
                        break;
                    case WowGameVersion.Classic:
                        config.ClassicReportId = report.Code;
                        config.LatestLogClassic = DateTime.UtcNow;
                        break;
                    case WowGameVersion.Vanilla:
                        config.VanillaReportId = report.Code;
                        config.LatestLogVanilla = DateTime.UtcNow;
                        break;
                }
            }

            // Track that we posted this report
            db.WclPosted.Add(new WclPosted
            {
                ServerId = guild.ServerId,
                ChannelId = guild.MonitoringConfig.ChannelId,
                ChannelName = guild.MonitoringConfig.ChannelName,
                ServerName = guild.MonitoringConfig.ServerName,
                ReportId = report.Code
            });

            await db.SaveChangesAsync(cancellationToken);

            // Invalidate WCL caches on the main bot (fire and forget, don't block on failure)
            _ = InvalidateBotCachesAsync(guild.GuildName, guild.ServerSlug, guild.ServerRegion, cancellationToken);
        }
        else
        {
            _logger.LogDebug(
                "[LogMonitoring] Delivery failed for channel {ChannelId} and {GuildName} (DiscordCode: {DiscordCode}, HTTP: {HttpStatus})",
                channelId,
                guild.GuildName,
                delivery.DiscordCode,
                delivery.HttpStatusCode);
        }
    }

    /// <summary>
    /// Calls the main bot's API to invalidate WCL caches when a new log is detected.
    /// This is fire-and-forget to avoid blocking the log poster on cache invalidation.
    /// </summary>
    private async Task InvalidateBotCachesAsync(string guildName, string realmSlug, string region, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_config.LogMonitoring.BotApiBaseUrl))
        {
            _logger.LogDebug("[LogMonitoring] Bot API URL not configured, skipping cache invalidation");
            return;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(_config.LogMonitoring.BotApiBaseUrl);

            if (!string.IsNullOrEmpty(_config.LogMonitoring.BotApiKey))
            {
                httpClient.DefaultRequestHeaders.Add("X-Api-Key", _config.LogMonitoring.BotApiKey);
            }

            var request = new { GuildName = guildName, RealmSlug = realmSlug, Region = region };
            var response = await httpClient.PostAsJsonAsync("/api/cache/wcl-invalidate", request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[LogMonitoring] Invalidated bot WCL caches for {Guild} on {Realm}-{Region}",
                    guildName, realmSlug, region);
            }
            else
            {
                _logger.LogWarning("[LogMonitoring] Failed to invalidate bot caches: {StatusCode}",
                    response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - cache invalidation is best-effort
            _logger.LogWarning(ex, "[LogMonitoring] Error calling bot cache invalidation API");
        }
    }
}

/// <summary>
/// Internal class for tracking guild check information
/// </summary>
internal class GuildCheckInfo
{
    public long ServerId { get; set; }
    public string GuildName { get; set; } = string.Empty;
    public string ServerSlug { get; set; } = string.Empty;
    public string ServerRegion { get; set; } = string.Empty;
    public string GuildKey { get; set; } = string.Empty;
    public LogMonitoring? MonitoringConfig { get; set; }
}
