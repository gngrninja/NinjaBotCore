using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Services.Api
{
    public static class StaticDataEndpoints
    {
        public static void MapStaticDataEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/static-data/stats - Get statistics for all static WoW data
            group.MapGet("/api/static-data/stats", async (HttpContext context) =>
            {
                try
                {
                    using var scope = deps.ServiceProvider.CreateScope();
                    var wowStaticData = scope.ServiceProvider.GetService<WowStaticDataService>();

                    if (wowStaticData == null)
                    {
                        return Results.Json(new
                        {
                            success = false,
                            error = "WowStaticDataService not available"
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }

                    var realms = await wowStaticData.GetAllRealmsAsync();
                    var classes = await wowStaticData.GetAllClassesAsync();
                    var races = await wowStaticData.GetAllRacesAsync();
                    var mounts = await wowStaticData.GetAllMountsAsync();
                    var achievements = await wowStaticData.GetAllAchievementsAsync();
                    var pets = await wowStaticData.GetAllPetsAsync();

                    // Items - query directly from database
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                    var itemsCount = await db.WowItems.CountAsync();
                    var oldestItem = await db.WowItems.OrderBy(i => i.LastUpdated).FirstOrDefaultAsync();

                    // Housing Decor
                    var housingDecorCount = await db.HousingDecor.CountAsync();
                    var housingDecorWithIcons = await db.HousingDecor.CountAsync(h => h.IconUrl != null && h.IconUrl != "");
                    var oldestHousingDecor = await db.HousingDecor.OrderBy(h => h.LastUpdated).FirstOrDefaultAsync();

                    // Realms by region
                    var realmsByRegion = realms.GroupBy(r => r.Region)
                        .OrderBy(g => g.Key)
                        .ToDictionary(g => g.Key, g => g.Count());

                    // Races by faction
                    var racesByFaction = races.GroupBy(r => r.Faction ?? "Unknown")
                        .OrderBy(g => g.Key)
                        .ToDictionary(g => g.Key, g => g.Select(r => r.Name).ToList());

                    // Achievements by category (top 5)
                    var achievementsByCategory = achievements
                        .GroupBy(a => a.ParentCategory ?? a.Category ?? "Uncategorized")
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .ToDictionary(g => g.Key, g => g.Count());

                    // Pets by type
                    var petsByType = pets.GroupBy(p => p.PetType ?? "Unknown")
                        .OrderByDescending(g => g.Count())
                        .ToDictionary(g => g.Key, g => g.Count());

                    // Oldest updates
                    var oldestRealm = realms.OrderBy(r => r.LastUpdated).FirstOrDefault();
                    var oldestClass = classes.OrderBy(c => c.LastUpdated).FirstOrDefault();
                    var oldestRace = races.OrderBy(r => r.LastUpdated).FirstOrDefault();
                    var oldestMount = mounts.OrderBy(m => m.LastUpdated).FirstOrDefault();
                    var oldestAchievement = achievements.OrderBy(a => a.LastUpdated).FirstOrDefault();
                    var oldestPet = pets.OrderBy(p => p.LastUpdated).FirstOrDefault();

                    return Results.Json(new
                    {
                        success = true,
                        realms = new
                        {
                            total = realms.Count,
                            by_region = realmsByRegion,
                            oldest_update = oldestRealm?.LastUpdated
                        },
                        classes = new
                        {
                            total = classes.Count,
                            names = classes.OrderBy(c => c.Name).Select(c => c.Name).ToList(),
                            oldest_update = oldestClass?.LastUpdated
                        },
                        races = new
                        {
                            total = races.Count,
                            by_faction = racesByFaction,
                            oldest_update = oldestRace?.LastUpdated
                        },
                        mounts = new
                        {
                            total = mounts.Count,
                            oldest_update = oldestMount?.LastUpdated
                        },
                        achievements = new
                        {
                            total = achievements.Count,
                            top_categories = achievementsByCategory,
                            oldest_update = oldestAchievement?.LastUpdated
                        },
                        pets = new
                        {
                            total = pets.Count,
                            by_type = petsByType,
                            oldest_update = oldestPet?.LastUpdated
                        },
                        items = new
                        {
                            total = itemsCount,
                            oldest_update = oldestItem?.LastUpdated
                        },
                        housing_decor = new
                        {
                            total = housingDecorCount,
                            with_icons = housingDecorWithIcons,
                            missing_icons = housingDecorCount - housingDecorWithIcons,
                            oldest_update = oldestHousingDecor?.LastUpdated
                        }
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error getting static data stats via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });

            // POST /api/sync/trigger - Queue a sync request
            group.MapPost("/api/sync/trigger", async (HttpContext context) =>
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<TriggerSyncRequest>();
                    if (body == null || string.IsNullOrEmpty(body.SyncType))
                    {
                        return Results.BadRequest(new { error = "sync_type is required" });
                    }

                    var syncType = body.SyncType.ToLower();
                    var queuedTypes = new[] { "achievements", "pets", "mounts", "mount_images", "items", "housing_decor", "recipes", "all" };
                    var directTypes = new[] { "realms", "classes", "races", "static" };
                    var validTypes = queuedTypes.Concat(directTypes).ToArray();

                    if (!validTypes.Contains(syncType))
                    {
                        return Results.BadRequest(new { error = "sync_type must be one of: achievements, pets, mounts, mount_images, items, housing_decor, recipes, realms, classes, races, static, all" });
                    }

                    using var scope = deps.ServiceProvider.CreateScope();

                    // Handle direct sync types (realms, classes, races, static) via WowStaticDataService
                    if (directTypes.Contains(syncType))
                    {
                        var wowStaticData = scope.ServiceProvider.GetService<WowStaticDataService>();
                        if (wowStaticData == null)
                        {
                            return Results.Json(new
                            {
                                success = false,
                                error = "service_unavailable",
                                message = "WowStaticDataService is not available"
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                        }

                        var typesToSync = syncType == "static"
                            ? new[] { "realms", "classes", "races" }
                            : new[] { syncType };

                        var results = new List<object>();
                        foreach (var type in typesToSync)
                        {
                            try
                            {
                                if (type == "realms")
                                    await wowStaticData.ImportAllRealmsAsync(CancellationToken.None);
                                else if (type == "classes")
                                    await wowStaticData.ImportAllClassesAsync(CancellationToken.None);
                                else if (type == "races")
                                    await wowStaticData.ImportAllRacesAsync(CancellationToken.None);

                                results.Add(new { type, success = true });
                                deps.Logger.LogInformation("Direct sync for {Type} completed via API", type);
                            }
                            catch (Exception ex)
                            {
                                deps.Logger.LogError(ex, "Direct sync for {Type} failed via API", type);
                                results.Add(new { type, success = false, error = ex.Message });
                            }
                        }

                        return Results.Json(new
                        {
                            success = results.All(r => ((dynamic)r).success),
                            sync_type = syncType,
                            results,
                            message = "Direct sync completed"
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }

                    // Handle queued types (achievements, pets, mounts, all) via StaticDataSyncRequest
                    long? userId = null;
                    if (!string.IsNullOrEmpty(body.UserId) && long.TryParse(body.UserId, out var parsedUserId))
                    {
                        userId = parsedUserId;
                    }

                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    // Check for existing pending request
                    var existing = await db.StaticDataSyncRequests
                        .FirstOrDefaultAsync(r => r.SyncType == syncType && r.Status == "pending");

                    if (existing != null)
                    {
                        return Results.Json(new
                        {
                            success = false,
                            error = "pending_exists",
                            message = $"A sync request for {syncType} is already pending",
                            request_id = existing.Id
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }

                    // Validate source if provided for item sync
                    string? requestedSource = null;
                    if ((syncType == "items" || syncType == "all") && !string.IsNullOrEmpty(body.Source))
                    {
                        var validSources = new[] { "auto", "wago", "blizzard" };
                        requestedSource = body.Source.ToLower();
                        if (!validSources.Contains(requestedSource))
                        {
                            return Results.BadRequest(new { error = "source must be one of: auto, wago, blizzard" });
                        }
                    }

                    var request = new StaticDataSyncRequest
                    {
                        SyncType = syncType,
                        Status = "pending",
                        RequestedByUserId = userId,
                        RequestSource = "api",
                        RequestedAt = DateTime.UtcNow,
                        RequestedSource = requestedSource
                    };

                    db.StaticDataSyncRequests.Add(request);
                    await db.SaveChangesAsync();

                    deps.Logger.LogInformation("Sync request #{Id} queued for {Type} via API", request.Id, request.SyncType);

                    return Results.Json(new
                    {
                        success = true,
                        request_id = request.Id,
                        sync_type = request.SyncType,
                        status = request.Status,
                        message = "Sync request queued"
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error creating sync request via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });

            // GET /api/sync/status - Get current sync status for all types
            group.MapGet("/api/sync/status", async (HttpContext context) =>
            {
                try
                {
                    using var scope = deps.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var statuses = await db.StaticDataSyncStatus.ToListAsync();
                    var pendingRequests = await db.StaticDataSyncRequests
                        .Where(r => r.Status == "pending" || r.Status == "in_progress")
                        .OrderBy(r => r.RequestedAt)
                        .ToListAsync();

                    var result = new Dictionary<string, object>();

                    foreach (var type in new[] { "achievements", "pets", "mounts", "items" })
                    {
                        var status = statuses.FirstOrDefault(s => s.SyncType == type);
                        result[type] = new
                        {
                            last_sync = status?.LastSyncCompleted,
                            last_status = status?.LastSyncStatus,
                            item_count = status?.TotalItemsInDatabase,
                            next_scheduled = status?.NextScheduledSync,
                            last_source = type == "items" ? status?.LastSyncSource : null
                        };
                    }

                    result["pending_requests"] = pendingRequests.Select(r => new
                    {
                        id = r.Id,
                        sync_type = r.SyncType,
                        status = r.Status,
                        requested_at = r.RequestedAt,
                        started_at = r.StartedAt
                    });

                    return Results.Json(result, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error getting sync status via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });

            // GET /api/sync/requests - Get sync request history
            group.MapGet("/api/sync/requests", async (HttpContext context) =>
            {
                try
                {
                    var statusFilter = context.Request.Query["status"].ToString();
                    var limitStr = context.Request.Query["limit"].ToString();
                    var limit = int.TryParse(limitStr, out var l) ? Math.Min(l, 100) : 25;

                    using var scope = deps.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var query = db.StaticDataSyncRequests.AsQueryable();

                    if (!string.IsNullOrEmpty(statusFilter))
                    {
                        query = query.Where(r => r.Status == statusFilter);
                    }

                    var requests = await query
                        .OrderByDescending(r => r.RequestedAt)
                        .Take(limit)
                        .ToListAsync();

                    return Results.Json(new
                    {
                        requests = requests.Select(r => new
                        {
                            id = r.Id,
                            sync_type = r.SyncType,
                            status = r.Status,
                            requested_by = r.RequestedByUserId,
                            request_source = r.RequestSource,
                            requested_at = r.RequestedAt,
                            started_at = r.StartedAt,
                            completed_at = r.CompletedAt,
                            items_processed = r.ItemsProcessed,
                            items_skipped = r.ItemsSkipped,
                            items_failed = r.ItemsFailed,
                            error_message = r.ErrorMessage
                        })
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error getting sync requests via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });

            // DELETE /api/sync/requests/{id} - Cancel a pending sync request
            group.MapDelete("/api/sync/requests/{id:long}", async (HttpContext context, long id) =>
            {
                try
                {
                    using var scope = deps.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var request = await db.StaticDataSyncRequests.FindAsync(id);
                    if (request == null)
                    {
                        return Results.NotFound(new { error = "Request not found" });
                    }

                    if (request.Status != "pending")
                    {
                        return Results.Json(new
                        {
                            success = false,
                            error = "cannot_cancel",
                            message = $"Cannot cancel request with status '{request.Status}'"
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower },
                        statusCode: 400);
                    }

                    request.Status = "cancelled";
                    request.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    deps.Logger.LogInformation("Sync request #{Id} cancelled via API", id);

                    return Results.Json(new
                    {
                        success = true,
                        message = $"Sync request #{id} cancelled"
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error cancelling sync request via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });

            // GET /api/mounts/stats - Get mount statistics including missing images count
            group.MapGet("/api/mounts/stats", async (HttpContext context) =>
            {
                try
                {
                    using var scope = deps.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var mounts = await db.WowMounts.ToListAsync();
                    var total = mounts.Count;
                    var missingImages = mounts.Count(m => m.CreatureDisplayId.HasValue && string.IsNullOrEmpty(m.MediaUrl));
                    var hasImages = mounts.Count(m => !string.IsNullOrEmpty(m.MediaUrl));

                    // Group by source
                    var bySource = mounts
                        .GroupBy(m => m.Source ?? "UNKNOWN")
                        .OrderByDescending(g => g.Count())
                        .ToDictionary(g => g.Key, g => g.Count());

                    // Group by expansion
                    var byExpansion = mounts
                        .Where(m => !string.IsNullOrEmpty(m.Expansion))
                        .GroupBy(m => m.Expansion)
                        .OrderByDescending(g => g.Count())
                        .ToDictionary(g => g.Key!, g => g.Count());

                    return Results.Json(new
                    {
                        success = true,
                        total,
                        missing_images = missingImages,
                        has_images = hasImages,
                        by_source = bySource,
                        by_expansion = byExpansion
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error fetching mount stats via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });

            // POST /api/mounts/import-json - Import mounts from in-game addon JSON data
            // Mount data is saved immediately, image fetching is queued for helpers service
            group.MapPost("/api/mounts/import-json", async (HttpContext context) =>
            {
                try
                {
                    using var scope = deps.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    // Read and parse JSON body
                    using var reader = new System.IO.StreamReader(context.Request.Body);
                    var jsonContent = await reader.ReadToEndAsync();

                    if (string.IsNullOrWhiteSpace(jsonContent))
                    {
                        return Results.BadRequest(new { error = "Request body is empty" });
                    }

                    var scrapedData = Newtonsoft.Json.JsonConvert.DeserializeObject<ScrapedMountData>(jsonContent);
                    if (scrapedData?.Mounts == null || scrapedData.Mounts.Count == 0)
                    {
                        return Results.BadRequest(new { error = "No mount data found in JSON" });
                    }

                    deps.Logger.LogInformation("Starting mount import from JSON: {Count} mounts (scanned: {Timestamp})",
                        scrapedData.Mounts.Count, scrapedData.Metadata?.ScanTimestamp);

                    // Get existing mounts
                    var existingMounts = await db.WowMounts.ToDictionaryAsync(m => m.Id);

                    int created = 0;
                    int updated = 0;
                    int needsImages = 0;

                    // Process each mount from JSON
                    foreach (var kvp in scrapedData.Mounts)
                    {
                        var scraped = kvp.Value;

                        try
                        {
                            var (sourceType, sourceDetail) = scraped.Source?.GetPrimarySource() ?? ("UNKNOWN", null);
                            var faction = scraped.Faction switch
                            {
                                0 => "Horde",
                                1 => "Alliance",
                                _ => null
                            };

                            if (existingMounts.TryGetValue(scraped.MountId, out var existing))
                            {
                                // Update existing mount
                                existing.Name = scraped.Name ?? existing.Name;
                                existing.Description = scraped.Description ?? existing.Description;
                                existing.Source = sourceType;
                                existing.SourceDetail = sourceDetail;
                                existing.InstanceName = scraped.Source?.Zone;
                                existing.DropLocation = scraped.Source?.Zone;
                                existing.EncounterName = scraped.Source?.Drop;
                                existing.Faction = faction ?? existing.Faction;
                                existing.CreatureDisplayId = scraped.CreatureDisplayId ?? existing.CreatureDisplayId;
                                existing.IsObtainable = scraped.Source?.IsLegacy() != true;

                                // Recalculate expansion using smart detection
                                existing.Expansion = WowStaticDataService.DetermineExpansion(
                                    existing.Id,
                                    existing.Description,
                                    scraped.Source?.Zone,
                                    scraped.Source?.Category,
                                    scraped.Source?.Clean ?? scraped.Source?.Achievement
                                );
                                existing.LastUpdated = DateTime.UtcNow;

                                if (string.IsNullOrEmpty(existing.MediaUrl) && existing.CreatureDisplayId.HasValue)
                                {
                                    needsImages++;
                                }

                                updated++;
                            }
                            else
                            {
                                // Create new mount
                                var newMount = new WowMounts
                                {
                                    Id = scraped.MountId,
                                    Name = scraped.Name ?? "Unknown",
                                    Description = scraped.Description,
                                    Source = sourceType,
                                    SourceDetail = sourceDetail,
                                    InstanceName = scraped.Source?.Zone,
                                    DropLocation = scraped.Source?.Zone,
                                    EncounterName = scraped.Source?.Drop,
                                    Faction = faction,
                                    CreatureDisplayId = scraped.CreatureDisplayId,
                                    IsObtainable = scraped.Source?.IsLegacy() != true,
                                    Expansion = WowStaticDataService.DetermineExpansion(
                                        scraped.MountId,
                                        scraped.Description,
                                        scraped.Source?.Zone,
                                        scraped.Source?.Category,
                                        scraped.Source?.Clean ?? scraped.Source?.Achievement
                                    ),
                                    LastUpdated = DateTime.UtcNow
                                };

                                db.WowMounts.Add(newMount);

                                if (scraped.CreatureDisplayId.HasValue)
                                {
                                    needsImages++;
                                }

                                created++;
                            }
                        }
                        catch (Exception ex)
                        {
                            deps.Logger.LogWarning(ex, "Failed to process mount {Id} from JSON", scraped.MountId);
                        }
                    }

                    // Save mount data
                    await db.SaveChangesAsync();
                    deps.Logger.LogInformation("Mount data saved: {Created} created, {Updated} updated", created, updated);

                    // Update sync status for mounts to reflect the import
                    var mountCount = await db.WowMounts.CountAsync();
                    var mountStatus = await db.StaticDataSyncStatus.FindAsync("mounts");
                    if (mountStatus == null)
                    {
                        mountStatus = new StaticDataSyncStatus { SyncType = "mounts" };
                        db.StaticDataSyncStatus.Add(mountStatus);
                    }
                    mountStatus.LastSyncStarted = DateTime.UtcNow;
                    mountStatus.LastSyncCompleted = DateTime.UtcNow;
                    mountStatus.LastSyncStatus = "success";
                    mountStatus.LastSyncItemCount = created + updated;
                    mountStatus.TotalItemsInDatabase = mountCount;
                    await db.SaveChangesAsync();

                    // Queue image fetch request for helpers service if needed
                    long? imageRequestId = null;
                    if (needsImages > 0)
                    {
                        var imageRequest = new StaticDataSyncRequest
                        {
                            SyncType = "mount_images",
                            Status = "pending",
                            RequestSource = "api",
                            RequestedAt = DateTime.UtcNow
                        };
                        db.StaticDataSyncRequests.Add(imageRequest);
                        await db.SaveChangesAsync();
                        imageRequestId = imageRequest.Id;

                        deps.Logger.LogInformation("Queued mount image sync request #{Id} for {Count} mounts",
                            imageRequest.Id, needsImages);
                    }

                    return Results.Json(new
                    {
                        success = true,
                        created,
                        updated,
                        mounts_needing_images = needsImages,
                        image_sync_request_id = imageRequestId,
                        total_in_json = scrapedData.Mounts.Count,
                        scan_timestamp = scrapedData.Metadata?.ScanTimestamp,
                        message = needsImages > 0
                            ? $"Import complete: {created} created, {updated} updated. Image fetch queued (request #{imageRequestId})"
                            : $"Import complete: {created} created, {updated} updated. No images needed."
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    deps.Logger.LogError(ex, "Invalid JSON in mount import request");
                    return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Error importing mounts from JSON via API");
                    return Results.Problem($"Error: {ex.Message}");
                }
            });
        }
    }
}
