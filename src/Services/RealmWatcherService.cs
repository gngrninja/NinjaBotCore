using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Background service that monitors WoW realm status and sends alerts for state changes.
    /// Uses database (RealmStatusCache) as source of truth for last known status.
    /// </summary>
    public class RealmWatcherService : IHostedService, IDisposable
    {
        private readonly ILogger<RealmWatcherService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DiscordShardedClient _client;
        private Timer _timer;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        // Timing constants
        private const int CheckIntervalSeconds = 60;
        private const int InitialDelaySeconds = 60;
        private const int ApiCallDelayMs = 100;  // Delay between API calls to avoid rate limiting

        public RealmWatcherService(
            ILogger<RealmWatcherService> logger,
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _client = client;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RealmWatcherService starting - checking realms every {Interval} seconds", CheckIntervalSeconds);

            // Start timer with initial delay
            _timer = new Timer(
                _ => _ = CheckRealmStatusesAsync(_cts.Token),
                null,
                TimeSpan.FromSeconds(InitialDelaySeconds),
                Timeout.InfiniteTimeSpan  // One-shot timer (reschedules itself)
            );

            return Task.CompletedTask;
        }

        private async Task CheckRealmStatusesAsync(CancellationToken cancellationToken)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var wowApi = scope.ServiceProvider.GetRequiredService<WowApi>();

                // Get all unique connected realm IDs from subscriptions grouped by region
                var subscriptions = await db.RealmWatchSubscriptions.ToListAsync(cancellationToken);

                if (!subscriptions.Any())
                {
                    _logger.LogDebug("No realm watch subscriptions found");
                    ScheduleNextCheck();
                    return;
                }

                // Group by region and connected realm ID
                var realmsByRegion = subscriptions
                    .GroupBy(s => s.Region)
                    .ToDictionary(g => g.Key, g => g.Select(s => s.ConnectedRealmId).Distinct().ToList());

                foreach (var regionGroup in realmsByRegion)
                {
                    var region = regionGroup.Key;
                    var connectedRealmIds = regionGroup.Value;

                    foreach (var connectedRealmId in connectedRealmIds)
                    {
                        try
                        {
                            var status = await wowApi.GetConnectedRealmStatusAsync(connectedRealmId, region, cancellationToken);
                            if (status == null) continue;

                            var isOnline = status.Status?.Type?.ToLower() == "up";
                            var hasQueue = status.HasQueue;

                            // Get previous status from database
                            var cachedStatus = await db.RealmStatusCache
                                .FirstOrDefaultAsync(c =>
                                    c.Region == region &&
                                    c.ConnectedRealmId == connectedRealmId,
                                    cancellationToken);

                            var hadPreviousStatus = cachedStatus != null;
                            var wasOnline = cachedStatus?.IsOnline ?? false;
                            var wasQueue = cachedStatus?.HasQueue ?? false;

                            // Update or create cache entry
                            if (cachedStatus != null)
                            {
                                cachedStatus.IsOnline = isOnline;
                                cachedStatus.HasQueue = hasQueue;
                                cachedStatus.LastCheckedAt = DateTime.UtcNow;
                            }
                            else
                            {
                                // Get realm name from first subscription for this realm
                                var realmName = subscriptions
                                    .FirstOrDefault(s => s.Region == region && s.ConnectedRealmId == connectedRealmId)
                                    ?.RealmName ?? "Unknown";

                                db.RealmStatusCache.Add(new RealmStatusCache
                                {
                                    Region = region,
                                    ConnectedRealmId = connectedRealmId,
                                    RealmName = realmName,
                                    IsOnline = isOnline,
                                    HasQueue = hasQueue,
                                    LastCheckedAt = DateTime.UtcNow
                                });
                            }

                            await db.SaveChangesAsync(cancellationToken);

                            // Only send alerts if we had a previous status (skip first check to avoid spam on startup)
                            if (hadPreviousStatus && (wasOnline != isOnline || wasQueue != hasQueue))
                            {
                                var affectedSubs = subscriptions
                                    .Where(s => s.Region == region && s.ConnectedRealmId == connectedRealmId)
                                    .ToList();

                                foreach (var sub in affectedSubs)
                                {
                                    var change = new RealmStatusChange(
                                        sub.RealmName, region, wasOnline, isOnline, wasQueue, hasQueue);

                                    // Check if subscription wants this type of alert
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
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error checking status for connected realm {ConnectedRealmId} in {Region}",
                                connectedRealmId, region);
                        }

                        // Check for cancellation after each realm check
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        // Small delay between API calls to avoid rate limiting
                        await Task.Delay(ApiCallDelayMs, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RealmWatcherService check cycle");
            }
            finally
            {
                ScheduleNextCheck();
            }
        }

        private void ScheduleNextCheck()
        {
            if (!_disposed)
            {
                _timer?.Change(TimeSpan.FromSeconds(CheckIntervalSeconds), Timeout.InfiniteTimeSpan);
            }
        }

        private async Task SendAlertAsync(RealmWatchSubscription sub, RealmStatusChange change, NinjaBotEntities db, CancellationToken cancellationToken)
        {
            try
            {
                var embed = BuildAlertEmbed(change);
                var alertSent = false;

                // Send to channel if configured
                if (sub.ChannelId.HasValue)
                {
                    var channel = _client.GetChannel((ulong)sub.ChannelId.Value) as ITextChannel;
                    if (channel != null)
                    {
                        await channel.SendMessageAsync(embed: embed.Build());
                        _logger.LogInformation("Sent realm alert to channel {ChannelId} for {RealmName}",
                            sub.ChannelId, sub.RealmName);
                        alertSent = true;
                    }
                    else
                    {
                        _logger.LogWarning("Channel {ChannelId} not found for realm alert {SubId}",
                            sub.ChannelId, sub.Id);
                    }
                }

                // Send DM to user if no channel configured
                if (!sub.ChannelId.HasValue)
                {
                    var user = await _client.GetUserAsync((ulong)sub.UserId, CacheMode.AllowDownload, null);
                    if (user != null)
                    {
                        var dm = await user.CreateDMChannelAsync();
                        await dm.SendMessageAsync(embed: embed.Build());
                        _logger.LogInformation("Sent realm alert DM to user {UserId} for {RealmName}",
                            sub.UserId, sub.RealmName);
                        alertSent = true;
                    }
                    else
                    {
                        _logger.LogWarning("User {UserId} not found for realm alert DM {SubId}",
                            sub.UserId, sub.Id);
                    }
                }

                // Update last alert timestamp
                if (alertSent)
                {
                    sub.LastAlertAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending realm alert for subscription {SubId}", sub.Id);
            }
        }

        private EmbedBuilder BuildAlertEmbed(RealmStatusChange change)
        {
            var embed = new EmbedBuilder();

            // Determine the primary change
            string title;
            Color color;

            if (!change.WasOnline && change.IsOnline)
            {
                title = $"🟢 {change.RealmName} is now ONLINE";
                color = Color.Green;
            }
            else if (change.WasOnline && !change.IsOnline)
            {
                title = $"🔴 {change.RealmName} is now OFFLINE";
                color = Color.Red;
            }
            else if (!change.WasQueue && change.HasQueue)
            {
                title = $"⏳ {change.RealmName} now has a QUEUE";
                color = Color.Orange;
            }
            else if (change.WasQueue && !change.HasQueue)
            {
                title = $"✅ {change.RealmName} queue has CLEARED";
                color = Color.Green;
            }
            else
            {
                title = $"ℹ️ {change.RealmName} status changed";
                color = Color.Blue;
            }

            embed.WithTitle(title);
            embed.WithColor(color);

            // Status details
            var statusText = change.IsOnline ? "Online" : "Offline";
            var queueText = change.HasQueue ? "Yes" : "No";

            embed.AddField("Status", statusText, true);
            embed.AddField("Queue", queueText, true);
            embed.AddField("Region", change.Region.ToUpper(), true);

            embed.WithTimestamp(DateTime.UtcNow);
            embed.WithFooter("WoW Realm Watch");

            return embed;
        }

        /// <summary>
        /// Gets the current cached status for a realm from database
        /// </summary>
        public async Task<RealmStatusSnapshot> GetCachedStatusAsync(string region, long connectedRealmId)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var cached = await db.RealmStatusCache
                .FirstOrDefaultAsync(c => c.Region == region && c.ConnectedRealmId == connectedRealmId);

            return cached != null
                ? new RealmStatusSnapshot(cached.IsOnline, cached.HasQueue, cached.LastCheckedAt)
                : null;
        }

        /// <summary>
        /// Gets all cached realm statuses from database
        /// </summary>
        public async Task<IReadOnlyDictionary<string, RealmStatusSnapshot>> GetAllCachedStatusesAsync()
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var cached = await db.RealmStatusCache.ToListAsync();

            return cached.ToDictionary(
                c => $"{c.Region}:{c.ConnectedRealmId}",
                c => new RealmStatusSnapshot(c.IsOnline, c.HasQueue, c.LastCheckedAt));
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RealmWatcherService stopping...");
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _timer?.Dispose();
            _cts?.Dispose();
            _logger.LogInformation("RealmWatcherService disposed");
        }
    }

    public record RealmStatusSnapshot(bool IsOnline, bool HasQueue, DateTime CheckedAt);

    public record RealmStatusChange(
        string RealmName,
        string Region,
        bool WasOnline,
        bool IsOnline,
        bool WasQueue,
        bool HasQueue);
}
