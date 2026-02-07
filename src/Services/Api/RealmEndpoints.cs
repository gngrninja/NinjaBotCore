using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services.Api
{
    public static class RealmEndpoints
    {
        public static void MapRealmEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/guilds/{guildId}/realm-watches - Get all realm watches for a guild
            group.MapGet("/api/guilds/{guildId}/realm-watches", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var watches = await db.RealmWatchSubscriptions
                    .Where(w => w.GuildId == guildIdLong)
                    .OrderBy(w => w.Region)
                    .ThenBy(w => w.RealmName)
                    .ToListAsync();

                return Results.Json(new
                {
                    success = true,
                    watches = watches.Select(w => new
                    {
                        id = w.Id,
                        realm_slug = w.RealmSlug,
                        realm_name = w.RealmName,
                        region = w.Region,
                        channel_id = w.ChannelId?.ToString(),
                        user_id = w.UserId.ToString(),
                        alert_on_online = w.AlertOnOnline,
                        alert_on_offline = w.AlertOnOffline,
                        alert_on_queue = w.AlertOnQueue,
                        created_at = w.CreatedAt,
                        last_alert_at = w.LastAlertAt
                    })
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // POST /api/guilds/{guildId}/realm-watches - Add a realm watch
            group.MapPost("/api/guilds/{guildId}/realm-watches", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<AddRealmWatchRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null || string.IsNullOrEmpty(body.RealmSlug) || string.IsNullOrEmpty(body.UserId))
                {
                    return Results.BadRequest(new { success = false, error = "realm_slug and user_id are required" });
                }

                if (!long.TryParse(body.UserId, out var userIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid user_id" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var region = (body.Region ?? "us").ToLower();
                var regionUpper = region.ToUpper();

                // Get realm info (WowRealms stores region as uppercase)
                var realmInfo = await db.WowRealms
                    .FirstOrDefaultAsync(r => r.Slug == body.RealmSlug && r.Region == regionUpper);

                if (realmInfo == null)
                {
                    return Results.BadRequest(new { success = false, error = "Realm not found" });
                }

                // If ConnectedRealmId is not cached, fetch it from Blizzard API
                if (!realmInfo.ConnectedRealmId.HasValue)
                {
                    try
                    {
                        var wowApi = scope.ServiceProvider.GetRequiredService<WowApi>();
                        var singleRealmInfo = await wowApi.GetSingleRealmInfoAsync(body.RealmSlug, region);

                        if (singleRealmInfo?.ConnectedRealm?.Href == null)
                        {
                            return Results.BadRequest(new { success = false, error = "Could not get connected realm data from Blizzard API" });
                        }

                        var connectedRealmInfo = await wowApi.GetConnectedRealmInfoAsync(
                            singleRealmInfo.ConnectedRealm.Href.ToString(), region);

                        if (connectedRealmInfo == null)
                        {
                            return Results.BadRequest(new { success = false, error = "Could not get connected realm info from Blizzard API" });
                        }

                        // Cache the ConnectedRealmId for future use
                        realmInfo.ConnectedRealmId = connectedRealmInfo.Id;
                        await db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        deps.Logger.LogWarning(ex, "Failed to fetch ConnectedRealmId for {RealmSlug}", body.RealmSlug);
                        return Results.BadRequest(new { success = false, error = "Could not verify realm with Blizzard API. Please try again." });
                    }
                }

                // Check for existing subscription (subscriptions use lowercase region)
                var existing = await db.RealmWatchSubscriptions
                    .FirstOrDefaultAsync(s =>
                        s.GuildId == guildIdLong &&
                        s.UserId == userIdLong &&
                        s.RealmSlug == body.RealmSlug &&
                        s.Region == region);

                if (existing != null)
                {
                    return Results.BadRequest(new { success = false, error = "Watch already exists for this realm" });
                }

                // Parse channel ID first to determine which limit to check
                long? channelIdLong = null;
                if (!string.IsNullOrEmpty(body.ChannelId) && long.TryParse(body.ChannelId, out var parsedChannelId))
                {
                    channelIdLong = parsedChannelId;
                }

                // Check limits based on alert type
                const int MaxChannelWatchesPerGuild = 5;
                const int MaxDmWatchesPerUser = 4;

                if (channelIdLong.HasValue)
                {
                    // Channel alerts: count only channel-based watches for this guild
                    var channelWatchCount = await db.RealmWatchSubscriptions
                        .CountAsync(s => s.GuildId == guildIdLong && s.ChannelId.HasValue);

                    if (channelWatchCount >= MaxChannelWatchesPerGuild)
                    {
                        return Results.BadRequest(new { success = false, error = $"Guild has reached the maximum of {MaxChannelWatchesPerGuild} channel realm watches" });
                    }
                }
                else
                {
                    // DM alerts: count only DM watches for this user (across all guilds)
                    var dmWatchCount = await db.RealmWatchSubscriptions
                        .CountAsync(s => s.UserId == userIdLong && !s.ChannelId.HasValue);

                    if (dmWatchCount >= MaxDmWatchesPerUser)
                    {
                        return Results.BadRequest(new { success = false, error = $"You have reached the maximum of {MaxDmWatchesPerUser} DM realm watches" });
                    }
                }

                var watch = new Database.RealmWatchSubscription
                {
                    GuildId = guildIdLong,
                    UserId = userIdLong,
                    ChannelId = channelIdLong,
                    RealmSlug = body.RealmSlug,
                    RealmName = realmInfo.Name,
                    Region = region,
                    ConnectedRealmId = (int)realmInfo.ConnectedRealmId.Value,
                    AlertOnOnline = body.AlertOnOnline ?? true,
                    AlertOnOffline = body.AlertOnOffline ?? true,
                    AlertOnQueue = body.AlertOnQueue ?? true,
                    CreatedAt = DateTime.UtcNow
                };

                db.RealmWatchSubscriptions.Add(watch);
                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    watch = new
                    {
                        id = watch.Id,
                        realm_slug = watch.RealmSlug,
                        realm_name = watch.RealmName,
                        region = watch.Region
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // DELETE /api/guilds/{guildId}/realm-watches/{watchId} - Delete a realm watch
            group.MapDelete("/api/guilds/{guildId}/realm-watches/{watchId}", async (HttpContext context, string guildId, string watchId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                if (!long.TryParse(watchId, out var watchIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid watch ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var watch = await db.RealmWatchSubscriptions
                    .FirstOrDefaultAsync(w => w.Id == watchIdLong && w.GuildId == guildIdLong);

                if (watch == null)
                {
                    return Results.NotFound(new { success = false, error = "Watch not found" });
                }

                db.RealmWatchSubscriptions.Remove(watch);
                await db.SaveChangesAsync();

                return Results.Json(new { success = true, message = "Watch deleted" },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            });

            // PUT /api/guilds/{guildId}/realm-watches/{watchId} - Update a realm watch
            group.MapPut("/api/guilds/{guildId}/realm-watches/{watchId}", async (HttpContext context, string guildId, string watchId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                if (!long.TryParse(watchId, out var watchIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid watch ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateRealmWatchRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var watch = await db.RealmWatchSubscriptions
                    .FirstOrDefaultAsync(w => w.Id == watchIdLong && w.GuildId == guildIdLong);

                if (watch == null)
                {
                    return Results.NotFound(new { success = false, error = "Watch not found" });
                }

                // Update fields if provided
                if (body.ChannelId != null)
                {
                    if (body.ChannelId == "")
                    {
                        watch.ChannelId = null; // Switch to DM
                    }
                    else if (long.TryParse(body.ChannelId, out var channelIdLong))
                    {
                        watch.ChannelId = channelIdLong;
                    }
                }

                if (body.AlertOnOnline.HasValue)
                    watch.AlertOnOnline = body.AlertOnOnline.Value;

                if (body.AlertOnOffline.HasValue)
                    watch.AlertOnOffline = body.AlertOnOffline.Value;

                if (body.AlertOnQueue.HasValue)
                    watch.AlertOnQueue = body.AlertOnQueue.Value;

                // At least one alert type must be enabled
                if (!watch.AlertOnOnline && !watch.AlertOnOffline && !watch.AlertOnQueue)
                {
                    return Results.BadRequest(new { success = false, error = "At least one alert type must be enabled" });
                }

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    watch = new
                    {
                        id = watch.Id,
                        realm_slug = watch.RealmSlug,
                        realm_name = watch.RealmName,
                        region = watch.Region,
                        guild_id = watch.GuildId.ToString(),
                        channel_id = watch.ChannelId?.ToString(),
                        user_id = watch.UserId.ToString(),
                        alert_on_online = watch.AlertOnOnline,
                        alert_on_offline = watch.AlertOnOffline,
                        alert_on_queue = watch.AlertOnQueue,
                        created_at = watch.CreatedAt,
                        last_alert_at = watch.LastAlertAt
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/users/{userId}/realm-watches - Get all realm watches for a user across guilds
            group.MapGet("/api/users/{userId}/realm-watches", async (HttpContext context, string userId) =>
            {
                if (!long.TryParse(userId, out var userIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid user ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var watches = await db.RealmWatchSubscriptions
                    .Where(w => w.UserId == userIdLong)
                    .OrderBy(w => w.Region)
                    .ThenBy(w => w.RealmName)
                    .ToListAsync();

                return Results.Json(new
                {
                    success = true,
                    watches = watches.Select(w => new
                    {
                        id = w.Id,
                        realm_slug = w.RealmSlug,
                        realm_name = w.RealmName,
                        region = w.Region,
                        guild_id = w.GuildId.ToString(),
                        channel_id = w.ChannelId?.ToString(),
                        user_id = w.UserId.ToString(),
                        alert_on_online = w.AlertOnOnline,
                        alert_on_offline = w.AlertOnOffline,
                        alert_on_queue = w.AlertOnQueue,
                        created_at = w.CreatedAt,
                        last_alert_at = w.LastAlertAt
                    })
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/realms/{region}/status - Get realm statuses
            // Note: Realm status cache moved to NinjaBotHelpers service
            // This endpoint now returns empty - use Blizzard API directly for live status
            group.MapGet("/api/realms/{region}/status", (HttpContext context, string region) =>
            {
                // Status cache is now in NinjaBotHelpers container
                return Results.Json(new
                {
                    success = true,
                    statuses = Array.Empty<object>(),
                    message = "Realm status cache moved to NinjaBotHelpers service"
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });
        }
    }
}
