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

    private const int ColorBlue = 0x3498DB;
    private const int MaxBatchSize = 50;

    public LogMonitoringWorker(
        ILogger<LogMonitoringWorker> logger,
        IServiceScopeFactory scopeFactory,
        HelpersConfiguration config,
        DiscordRestClient discordClient,
        WarcraftLogsClient wclClient)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _discordClient = discordClient;
        _wclClient = wclClient;
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

    private async Task<List<GuildCheckInfo>> GetGuildsToCheckAsync(
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

        // Apply tiered checking based on last log timestamp
        foreach (var guild in guildAssociations)
        {
            var config = monitoringConfigs.FirstOrDefault(m => m.ServerId == guild.ServerId);
            if (config == null) continue;

            var lastLog = gameVersion switch
            {
                WowGameVersion.Retail => config.LatestLogRetail ?? config.LatestLog,
                WowGameVersion.Classic => config.LatestLogClassic,
                WowGameVersion.Vanilla => config.LatestLogVanilla,
                _ => null
            };

            // Determine tier and check interval
            if (ShouldCheckGuild(lastLog, now))
            {
                guild.MonitoringConfig = config;
                result.Add(guild);
            }
        }

        return result;
    }

    private bool ShouldCheckGuild(DateTime? lastLog, DateTime now)
    {
        if (lastLog == null)
        {
            // Never checked - always check
            return true;
        }

        var daysSinceLastLog = (now - lastLog.Value).TotalDays;
        var settings = _config.LogMonitoring;

        // Tier 1: Active guilds (logged recently)
        if (daysSinceLastLog <= settings.Tier1ThresholdDays)
        {
            // Check if enough time has passed since we last ran
            // We assume this method is called every CheckIntervalMinutes,
            // so for Tier1 we check every time if interval <= Tier1IntervalMinutes
            return true;
        }

        // Tier 2: Semi-active guilds
        if (daysSinceLastLog <= settings.Tier2ThresholdDays)
        {
            // Check less frequently
            var minutesSinceLastLog = (now - lastLog.Value).TotalMinutes;
            var tier2IntervalMinutes = settings.Tier2IntervalHours * 60;
            return minutesSinceLastLog % tier2IntervalMinutes < settings.CheckIntervalMinutes;
        }

        // Tier 3: Inactive guilds
        var tier3IntervalMinutes = settings.Tier3IntervalHours * 60;
        var minutesSinceLastLogT3 = (now - lastLog.Value).TotalMinutes;
        return minutesSinceLastLogT3 % tier3IntervalMinutes < settings.CheckIntervalMinutes;
    }

    private async Task ProcessBatchAsync(
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LogMonitoring] Batch query failed for {GameVersion}", gameVersion);
            return;
        }

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

    private async Task PostNewLogAsync(
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

        // Send to Discord
        var success = await _discordClient.SendChannelMessageAsync(channelId, embed, cancellationToken);

        if (success)
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
        }
        else
        {
            _logger.LogWarning("[LogMonitoring] Failed to post log to channel {ChannelId} for {GuildName}",
                channelId, guild.GuildName);
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
