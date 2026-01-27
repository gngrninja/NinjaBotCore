using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;

namespace NinjaBotHelpers.Workers;

/// <summary>
/// Background worker that syncs WoW static data (achievements, pets, mounts) on a schedule.
/// This offloads heavy API calls from the main bot to avoid blocking startup.
/// Also processes manual sync requests from bot/API.
/// </summary>
public class StaticDataSyncWorker : BackgroundService
{
    private readonly ILogger<StaticDataSyncWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HelpersConfiguration _config;
    private readonly BlizzardApiClient _blizzardClient;
    private DateTime? _lastScheduledSync;

    // Check for pending requests every 60 seconds
    private static readonly TimeSpan PendingRequestCheckInterval = TimeSpan.FromSeconds(60);

    public StaticDataSyncWorker(
        ILogger<StaticDataSyncWorker> logger,
        IServiceScopeFactory scopeFactory,
        HelpersConfiguration config,
        BlizzardApiClient blizzardClient)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _blizzardClient = blizzardClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.StaticDataSync.Enabled)
        {
            _logger.LogInformation("StaticDataSync is disabled via configuration");
            return;
        }

        _logger.LogInformation("StaticDataSync starting - scheduled syncs every {Interval} days, checking for pending requests every 60 seconds",
            _config.StaticDataSync.SyncIntervalDays);

        await Task.Delay(TimeSpan.FromSeconds(_config.StaticDataSync.InitialDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check for and process any pending manual requests
                await ProcessPendingRequestsAsync(stoppingToken);

                // Check if scheduled sync is due
                if (await IsScheduledSyncDueAsync(stoppingToken))
                {
                    _logger.LogInformation("Starting scheduled static data sync");
                    await RunScheduledSyncAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in StaticDataSync cycle");
            }

            // Wait before next check
            await Task.Delay(PendingRequestCheckInterval, stoppingToken);
        }

        _logger.LogInformation("StaticDataSync stopping");
    }

    private async Task<bool> IsScheduledSyncDueAsync(CancellationToken cancellationToken)
    {
        // Check in-memory cache first
        if (_lastScheduledSync != null)
        {
            var interval = TimeSpan.FromDays(_config.StaticDataSync.SyncIntervalDays);
            return DateTime.UtcNow - _lastScheduledSync.Value >= interval;
        }

        // On startup, check the database for last sync time
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        // Get the most recent completed sync across all types
        var lastSync = await db.StaticDataSyncStatus
            .Where(s => s.LastSyncCompleted != null)
            .OrderByDescending(s => s.LastSyncCompleted)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastSync?.LastSyncCompleted != null)
        {
            // Cache it for future checks
            _lastScheduledSync = lastSync.LastSyncCompleted;

            var interval = TimeSpan.FromDays(_config.StaticDataSync.SyncIntervalDays);
            var timeSinceLastSync = DateTime.UtcNow - lastSync.LastSyncCompleted.Value;

            _logger.LogInformation("Last sync was {TimeSince} ago (interval: {Interval} days)",
                timeSinceLastSync, _config.StaticDataSync.SyncIntervalDays);

            return timeSinceLastSync >= interval;
        }

        // No sync history = no scheduled syncs
        // Scheduled syncs only run AFTER an initial import has been done via API/slash command.
        // This prevents hammering the Blizzard API on fresh/exported databases.
        // Manual sync triggers (via /api/sync/trigger or /admin sync) still work anytime.
        var hasData = await db.WowAchievements.AnyAsync(cancellationToken)
                   || await db.WowPets.AnyAsync(cancellationToken)
                   || await db.WowMounts.AnyAsync(cancellationToken);

        if (hasData)
        {
            _logger.LogInformation("Found existing static data but no sync history - scheduled syncs disabled until manual sync is run");
        }
        else
        {
            _logger.LogInformation("No static data found - scheduled syncs disabled until manual import via API");
        }

        // Don't auto-sync - wait for manual import/sync to establish sync history
        _lastScheduledSync = DateTime.UtcNow; // Prevent repeated checks
        return false;
    }

    private async Task RunScheduledSyncAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        // Create a request record for tracking
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var request = new StaticDataSyncRequest
        {
            SyncType = "all",
            Status = "in_progress",
            RequestSource = "scheduled",
            RequestedAt = startTime,
            StartedAt = startTime
        };
        db.StaticDataSyncRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var (processed, skipped, failed) = await SyncAllAsync(cancellationToken);

            request.Status = "completed";
            request.ItemsProcessed = processed;
            request.ItemsSkipped = skipped;
            request.ItemsFailed = failed;
            request.CompletedAt = DateTime.UtcNow;

            _lastScheduledSync = DateTime.UtcNow;
            _logger.LogInformation("Scheduled sync complete. Next sync in {Days} days", _config.StaticDataSync.SyncIntervalDays);
        }
        catch (Exception ex)
        {
            request.Status = "failed";
            request.ErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            request.CompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Scheduled sync failed");
        }

        await db.SaveChangesAsync(cancellationToken);
        await UpdateSyncStatusAsync(db, "all", request, cancellationToken);
    }

    private async Task ProcessPendingRequestsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var pendingRequests = await db.StaticDataSyncRequests
            .Where(r => r.Status == "pending")
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(cancellationToken);

        if (pendingRequests.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} pending sync requests", pendingRequests.Count);

        foreach (var request in pendingRequests)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            _logger.LogInformation("Processing sync request #{Id} for {Type}", request.Id, request.SyncType);

            request.Status = "in_progress";
            request.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var (processed, skipped, failed) = request.SyncType switch
                {
                    "achievements" => await SyncAchievementsWithStatsAsync(cancellationToken),
                    "pets" => await SyncPetsWithStatsAsync(cancellationToken),
                    "mounts" => await SyncMountsWithStatsAsync(cancellationToken),
                    "mount_images" => await SyncMountImagesAsync(cancellationToken),
                    "all" => await SyncAllAsync(cancellationToken),
                    _ => (0, 0, 0)
                };

                request.Status = "completed";
                request.ItemsProcessed = processed;
                request.ItemsSkipped = skipped;
                request.ItemsFailed = failed;

                _logger.LogInformation("Sync request #{Id} completed: {Processed} processed, {Skipped} skipped, {Failed} failed",
                    request.Id, processed, skipped, failed);
            }
            catch (Exception ex)
            {
                request.Status = "failed";
                request.ErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                _logger.LogError(ex, "Sync request #{Id} failed", request.Id);
            }

            request.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            // Update status table
            await UpdateSyncStatusAsync(db, request.SyncType, request, cancellationToken);
        }
    }

    private async Task UpdateSyncStatusAsync(HelpersDbContext db, string syncType, StaticDataSyncRequest request, CancellationToken cancellationToken)
    {
        // Map sync types to status table entries
        // "mount_images" updates the "mounts" status since it's part of mount data
        var types = syncType switch
        {
            "all" => new[] { "achievements", "pets", "mounts" },
            "mount_images" => new[] { "mounts" },
            _ => new[] { syncType }
        };

        foreach (var type in types)
        {
            var status = await db.StaticDataSyncStatus.FindAsync(new object[] { type }, cancellationToken);
            if (status == null)
            {
                status = new StaticDataSyncStatus { SyncType = type };
                db.StaticDataSyncStatus.Add(status);
            }

            status.LastSyncStarted = request.StartedAt;
            status.LastSyncCompleted = request.CompletedAt;
            status.LastSyncStatus = request.Status == "completed" ? "success" : "failed";
            status.LastSyncItemCount = (request.ItemsProcessed ?? 0) + (request.ItemsSkipped ?? 0);
            status.NextScheduledSync = DateTime.UtcNow.AddDays(_config.StaticDataSync.SyncIntervalDays);

            // Update total count in database
            status.TotalItemsInDatabase = type switch
            {
                "achievements" => await db.WowAchievements.CountAsync(cancellationToken),
                "pets" => await db.WowPets.CountAsync(cancellationToken),
                "mounts" => await db.WowMounts.CountAsync(cancellationToken),
                _ => null
            };
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int processed, int skipped, int failed)> SyncAllAsync(CancellationToken cancellationToken)
    {
        var (achProcessed, achSkipped, achFailed) = await SyncAchievementsWithStatsAsync(cancellationToken);
        var (petProcessed, petSkipped, petFailed) = await SyncPetsWithStatsAsync(cancellationToken);
        var (mountProcessed, mountSkipped, mountFailed) = await SyncMountsWithStatsAsync(cancellationToken);

        return (
            achProcessed + petProcessed + mountProcessed,
            achSkipped + petSkipped + mountSkipped,
            achFailed + petFailed + mountFailed
        );
    }

    #region Achievement Sync

    private async Task<(int processed, int skipped, int failed)> SyncAchievementsWithStatsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting achievement sync");

        var index = await _blizzardClient.GetAchievementIndexAsync("us", cancellationToken);
        if (index?.Achievements == null)
        {
            _logger.LogWarning("Failed to get achievement index");
            return (0, 0, 0);
        }

        _logger.LogInformation("Found {Count} achievements in index", index.Achievements.Count);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        // Load existing achievement IDs to skip
        var existingIds = await db.WowAchievements
            .Select(a => a.Id)
            .ToHashSetAsync(cancellationToken);

        _logger.LogInformation("Found {Count} existing achievements in database", existingIds.Count);

        int imported = 0;
        int skipped = 0;
        int failed = 0;

        foreach (var achievementRef in index.Achievements)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (existingIds.Contains(achievementRef.Id))
            {
                skipped++;
                continue;
            }

            try
            {
                var achievement = await _blizzardClient.GetAchievementAsync(achievementRef.Id, "us", cancellationToken);
                if (achievement == null)
                {
                    failed++;
                    continue;
                }

                // Get media URL
                string? mediaUrl = null;
                var media = await _blizzardClient.GetAchievementMediaAsync(achievementRef.Id, "us", cancellationToken);
                mediaUrl = media?.GetIconUrl();

                var entity = new WowAchievements
                {
                    Id = achievement.Id,
                    Name = achievement.Name ?? "Unknown",
                    Description = achievement.Description,
                    Points = achievement.Points,
                    Category = achievement.Category?.Name,
                    CategoryId = achievement.Category?.Id,
                    IsAccountWide = achievement.IsAccountWide,
                    RewardDescription = achievement.RewardDescription,
                    DisplayOrder = achievement.DisplayOrder,
                    MediaUrl = mediaUrl,
                    LastUpdated = DateTime.UtcNow
                };

                db.WowAchievements.Add(entity);

                // Add criteria if present
                if (achievement.Criteria?.ChildCriteria != null)
                {
                    foreach (var criteria in achievement.Criteria.ChildCriteria)
                    {
                        db.WowAchievementCriteria.Add(new WowAchievementCriteria
                        {
                            Id = criteria.Id,
                            AchievementId = achievement.Id,
                            Description = criteria.Description,
                            OrderIndex = criteria.OrderIndex,
                            Amount = criteria.Amount,
                            LastUpdated = DateTime.UtcNow
                        });
                    }
                }

                imported++;

                if ((imported + skipped) % 100 == 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Achievement sync progress: {Imported} imported, {Skipped} skipped, {Failed} failed",
                        imported, skipped, failed);
                }

                await Task.Delay(_config.StaticDataSync.ApiCallDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import achievement {Id}", achievementRef.Id);
                failed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Achievement sync complete: {Imported} imported, {Skipped} skipped, {Failed} failed",
            imported, skipped, failed);

        return (imported, skipped, failed);
    }

    #endregion

    #region Pet Sync

    private async Task<(int processed, int skipped, int failed)> SyncPetsWithStatsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting pet sync");

        var index = await _blizzardClient.GetPetIndexAsync("us", cancellationToken);
        if (index?.Pets == null)
        {
            _logger.LogWarning("Failed to get pet index");
            return (0, 0, 0);
        }

        _logger.LogInformation("Found {Count} pets in index", index.Pets.Count);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var existingIds = await db.WowPets
            .Select(p => p.Id)
            .ToHashSetAsync(cancellationToken);

        _logger.LogInformation("Found {Count} existing pets in database", existingIds.Count);

        int imported = 0;
        int skipped = 0;
        int failed = 0;

        foreach (var petRef in index.Pets)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (existingIds.Contains(petRef.Id))
            {
                skipped++;
                continue;
            }

            try
            {
                var pet = await _blizzardClient.GetPetAsync(petRef.Id, "us", cancellationToken);
                if (pet == null)
                {
                    failed++;
                    continue;
                }

                // Get media URL
                string? iconUrl = null;
                var media = await _blizzardClient.GetPetMediaAsync(petRef.Id, "us", cancellationToken);
                iconUrl = media?.GetIconUrl();

                var entity = new WowPets
                {
                    Id = pet.Id,
                    Name = pet.Name ?? "Unknown",
                    Description = pet.Description,
                    PetType = pet.BattlePetType?.Name,
                    Source = pet.Source?.Name,
                    IsCapturable = pet.IsCapturable,
                    IsTradable = pet.IsTradable,
                    IsBattlePet = pet.IsBattlepet,
                    CreatureId = pet.Creature?.Id,
                    IconUrl = iconUrl,
                    LastUpdated = DateTime.UtcNow
                };

                db.WowPets.Add(entity);
                imported++;

                if ((imported + skipped) % 100 == 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Pet sync progress: {Imported} imported, {Skipped} skipped, {Failed} failed",
                        imported, skipped, failed);
                }

                await Task.Delay(_config.StaticDataSync.ApiCallDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import pet {Id}", petRef.Id);
                failed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Pet sync complete: {Imported} imported, {Skipped} skipped, {Failed} failed",
            imported, skipped, failed);

        return (imported, skipped, failed);
    }

    #endregion

    #region Mount Sync

    private async Task<(int processed, int skipped, int failed)> SyncMountsWithStatsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting mount sync");

        var index = await _blizzardClient.GetMountIndexAsync("us", cancellationToken);
        if (index?.Mounts == null)
        {
            _logger.LogWarning("Failed to get mount index");
            return (0, 0, 0);
        }

        _logger.LogInformation("Found {Count} mounts in index", index.Mounts.Count);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var existingIds = await db.WowMounts
            .Select(m => m.Id)
            .ToHashSetAsync(cancellationToken);

        _logger.LogInformation("Found {Count} existing mounts in database", existingIds.Count);

        int imported = 0;
        int skipped = 0;
        int failed = 0;

        foreach (var mountRef in index.Mounts)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (existingIds.Contains(mountRef.Id))
            {
                skipped++;
                continue;
            }

            try
            {
                var mount = await _blizzardClient.GetMountAsync(mountRef.Id, "us", cancellationToken);
                if (mount == null)
                {
                    failed++;
                    continue;
                }

                // Get media URL from creature display
                string? mediaUrl = null;
                if (mount.CreatureDisplays?.Count > 0)
                {
                    var displayId = mount.CreatureDisplays[0].Id;
                    var media = await _blizzardClient.GetCreatureDisplayMediaAsync(displayId, "us", cancellationToken);
                    mediaUrl = media?.Assets?.FirstOrDefault()?.Value;
                }

                var entity = new WowMounts
                {
                    Id = mount.Id,
                    Name = mount.Name ?? "Unknown",
                    Description = mount.Description,
                    Source = mount.Source?.Name,
                    Faction = mount.Faction?.Name,
                    CreatureDisplayId = mount.CreatureDisplays?.FirstOrDefault()?.Id,
                    MediaUrl = mediaUrl,
                    IsObtainable = !mount.ShouldExcludeIfUncollected,
                    LastUpdated = DateTime.UtcNow
                };

                db.WowMounts.Add(entity);
                imported++;

                if ((imported + skipped) % 100 == 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Mount sync progress: {Imported} imported, {Skipped} skipped, {Failed} failed",
                        imported, skipped, failed);
                }

                await Task.Delay(_config.StaticDataSync.ApiCallDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import mount {Id}", mountRef.Id);
                failed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Mount sync complete: {Imported} imported, {Skipped} skipped, {Failed} failed",
            imported, skipped, failed);

        return (imported, skipped, failed);
    }

    #endregion

    #region Mount Images Sync

    /// <summary>
    /// Fetches images for mounts that have CreatureDisplayId but no MediaUrl.
    /// Used after JSON import from in-game addon data.
    /// </summary>
    private async Task<(int processed, int skipped, int failed)> SyncMountImagesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting mount image sync");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        // Find mounts needing images (have CreatureDisplayId but no MediaUrl)
        var mountsNeedingImages = await db.WowMounts
            .Where(m => m.CreatureDisplayId != null && (m.MediaUrl == null || m.MediaUrl == ""))
            .ToListAsync(cancellationToken);

        if (mountsNeedingImages.Count == 0)
        {
            _logger.LogInformation("No mounts need images");
            return (0, 0, 0);
        }

        _logger.LogInformation("Found {Count} mounts needing images", mountsNeedingImages.Count);

        int processed = 0;
        int skipped = 0;
        int failed = 0;

        foreach (var mount in mountsNeedingImages)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (!mount.CreatureDisplayId.HasValue)
            {
                skipped++;
                continue;
            }

            try
            {
                var media = await _blizzardClient.GetCreatureDisplayMediaAsync(
                    mount.CreatureDisplayId.Value, "us", cancellationToken);

                var imageUrl = media?.Assets?.FirstOrDefault()?.Value;

                if (!string.IsNullOrEmpty(imageUrl))
                {
                    mount.MediaUrl = imageUrl;
                    processed++;
                }
                else
                {
                    skipped++;
                }

                // Save periodically
                if ((processed + skipped + failed) % 50 == 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Mount image sync progress: {Processed} updated, {Skipped} skipped, {Failed} failed",
                        processed, skipped, failed);
                }

                await Task.Delay(_config.StaticDataSync.ApiCallDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch image for mount {Id} (display: {DisplayId})",
                    mount.Id, mount.CreatureDisplayId);
                failed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Mount image sync complete: {Processed} updated, {Skipped} skipped, {Failed} failed",
            processed, skipped, failed);

        return (processed, skipped, failed);
    }

    #endregion
}
