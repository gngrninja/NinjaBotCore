using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Discord;

namespace NinjaBotHelpers.Workers;

/// <summary>
/// Background worker that monitors WoW realm status and sends alerts for state changes.
/// Status is persisted in database for durability across restarts.
/// </summary>
public class RealmWatcherWorker : BackgroundService
{
    private readonly ILogger<RealmWatcherWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HelpersConfiguration _config;
    private readonly DiscordRestClient _discordClient;
    private readonly BlizzardApiClient _blizzardClient;

    private const int ColorGreen = 0x2ECC71;
    private const int ColorRed = 0xE74C3C;
    private const int ColorOrange = 0xF39C12;
    private const int ColorBlue = 0x3498DB;

    public RealmWatcherWorker(
        ILogger<RealmWatcherWorker> logger,
        IServiceScopeFactory scopeFactory,
        HelpersConfiguration config,
        DiscordRestClient discordClient,
        BlizzardApiClient blizzardClient)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _discordClient = discordClient;
        _blizzardClient = blizzardClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.RealmWatcher.Enabled)
        {
            _logger.LogInformation("RealmWatcher is disabled via configuration");
            return;
        }

        _logger.LogInformation("RealmWatcher starting - checking realms every {Interval} seconds",
            _config.RealmWatcher.CheckIntervalSeconds);

        await Task.Delay(TimeSpan.FromSeconds(_config.RealmWatcher.InitialDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckRealmStatusesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RealmWatcher check cycle");
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.RealmWatcher.CheckIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("RealmWatcher stopping");
    }

    private async Task CheckRealmStatusesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var subscriptions = await db.RealmWatchSubscriptions.ToListAsync(cancellationToken);

        if (!subscriptions.Any())
        {
            _logger.LogDebug("No realm watch subscriptions found");
            return;
        }

        // Group by region and connected realm ID to avoid duplicate API calls
        var realmsByRegion = subscriptions
            .GroupBy(s => s.Region.ToLower())
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => s.ConnectedRealmId).Distinct().ToList());

        foreach (var regionGroup in realmsByRegion)
        {
            var region = regionGroup.Key;

            foreach (var connectedRealmId in regionGroup.Value)
            {
                try
                {
                    var status = await _blizzardClient.GetConnectedRealmStatusAsync(connectedRealmId, region, cancellationToken);
                    if (status == null) continue;

                    var isOnline = status.Status?.Type?.ToLower() == "up";
                    var hasQueue = status.HasQueue;

                    // Get or create cached status from DB (case-insensitive region match)
                    var cached = await db.RealmStatusCache
                        .FirstOrDefaultAsync(c => c.Region.ToLower() == region && c.ConnectedRealmId == connectedRealmId, cancellationToken);

                    // Get a sample realm name for logging from subscriptions
                    var sampleRealmName = subscriptions
                        .FirstOrDefault(s => s.ConnectedRealmId == connectedRealmId)?.RealmName ?? $"ConnectedRealm-{connectedRealmId}";

                    if (cached == null)
                    {
                        // First time seeing this realm - just cache it, no alert
                        cached = new RealmStatusCache
                        {
                            Region = region,
                            ConnectedRealmId = connectedRealmId,
                            RealmName = sampleRealmName,
                            IsOnline = isOnline,
                            HasQueue = hasQueue,
                            LastCheckedAt = DateTime.UtcNow
                        };
                        db.RealmStatusCache.Add(cached);
                        await db.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Cached initial status for {RealmName}: Online={IsOnline}, Queue={HasQueue}",
                            sampleRealmName, isOnline, hasQueue);
                        continue;
                    }

                    // Check for state changes
                    var wasOnline = cached.IsOnline;
                    var wasQueue = cached.HasQueue;

                    if (wasOnline != isOnline || wasQueue != hasQueue)
                    {
                        _logger.LogInformation("Status change for {RealmName}: Online {WasOnline}->{IsOnline}, Queue {WasQueue}->{HasQueue}",
                            cached.RealmName, wasOnline, isOnline, wasQueue, hasQueue);

                        // Send alerts to affected subscriptions (case-insensitive region match)
                        var affectedSubs = subscriptions
                            .Where(s => s.Region.ToLower() == region && s.ConnectedRealmId == connectedRealmId)
                            .ToList();

                        foreach (var sub in affectedSubs)
                        {
                            var change = new RealmStatusChange(sub.RealmName, region, wasOnline, isOnline, wasQueue, hasQueue);

                            var shouldAlert = false;
                            if (!wasOnline && isOnline && sub.AlertOnOnline) shouldAlert = true;
                            if (wasOnline && !isOnline && sub.AlertOnOffline) shouldAlert = true;
                            if (wasQueue != hasQueue && sub.AlertOnQueue) shouldAlert = true;

                            if (shouldAlert)
                            {
                                await SendAlertAsync(sub, change, db, cancellationToken);
                            }
                        }
                    }

                    // Update cached status
                    cached.IsOnline = isOnline;
                    cached.HasQueue = hasQueue;
                    cached.LastCheckedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking status for connected realm {ConnectedRealmId} in {Region}",
                        connectedRealmId, region);
                }

                await Task.Delay(_config.RealmWatcher.ApiCallDelayMs, cancellationToken);
            }
        }
    }

    private async Task SendAlertAsync(
        RealmWatchSubscription sub,
        RealmStatusChange change,
        HelpersDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            var embed = BuildAlertEmbed(change);
            var alertSent = false;

            if (sub.ChannelId.HasValue)
            {
                var success = await _discordClient.SendChannelMessageAsync(
                    (ulong)sub.ChannelId.Value, embed, cancellationToken);

                if (success)
                {
                    _logger.LogInformation("Sent realm alert to channel {ChannelId} for {RealmName}",
                        sub.ChannelId, sub.RealmName);
                    alertSent = true;
                }
                else
                {
                    _logger.LogWarning("Failed to send alert to channel {ChannelId} for subscription {SubId}",
                        sub.ChannelId, sub.Id);
                }
            }

            if (!sub.ChannelId.HasValue)
            {
                var success = await _discordClient.SendDMAsync(
                    (ulong)sub.UserId, embed, cancellationToken);

                if (success)
                {
                    _logger.LogInformation("Sent realm alert DM to user {UserId} for {RealmName}",
                        sub.UserId, sub.RealmName);
                    alertSent = true;
                }
                else
                {
                    _logger.LogWarning("Failed to send alert DM to user {UserId} for subscription {SubId}",
                        sub.UserId, sub.Id);
                }
            }

            if (alertSent)
            {
                var subToUpdate = await db.RealmWatchSubscriptions.FindAsync(
                    new object[] { sub.Id }, cancellationToken);
                if (subToUpdate != null)
                {
                    subToUpdate.LastAlertAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending realm alert for subscription {SubId}", sub.Id);
        }
    }

    private DiscordEmbed BuildAlertEmbed(RealmStatusChange change)
    {
        string title;
        int color;

        if (!change.WasOnline && change.IsOnline)
        {
            title = $"🟢 {change.RealmName} is now ONLINE";
            color = ColorGreen;
        }
        else if (change.WasOnline && !change.IsOnline)
        {
            title = $"🔴 {change.RealmName} is now OFFLINE";
            color = ColorRed;
        }
        else if (!change.WasQueue && change.HasQueue)
        {
            title = $"⏳ {change.RealmName} now has a QUEUE";
            color = ColorOrange;
        }
        else if (change.WasQueue && !change.HasQueue)
        {
            title = $"✅ {change.RealmName} queue has CLEARED";
            color = ColorGreen;
        }
        else
        {
            title = $"ℹ️ {change.RealmName} status changed";
            color = ColorBlue;
        }

        var statusText = change.IsOnline ? "Online" : "Offline";
        var queueText = change.HasQueue ? "Yes" : "No";

        return DiscordEmbed.Create(title, color)
            .WithField("Status", statusText, true)
            .WithField("Queue", queueText, true)
            .WithField("Region", change.Region.ToUpper(), true)
            .WithFooter("WoW Realm Watch | NinjaBotHelpers");
    }
}

public record RealmStatusChange(
    string RealmName,
    string Region,
    bool WasOnline,
    bool IsOnline,
    bool WasQueue,
    bool HasQueue);
