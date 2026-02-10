using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Services.Api
{
    public static class UserEndpoints
    {
        public static void MapUserEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // POST /api/characters/add - Add character via API (for web dashboard)
            group.MapPost("/api/characters/add", async (HttpContext context) =>
            {
                // Parse request body
                AddCharacterRequest? request;
                try
                {
                    request = await context.Request.ReadFromJsonAsync<AddCharacterRequest>();
                }
                catch
                {
                    return Results.BadRequest(new { error = "Invalid JSON body" });
                }

                if (request == null || string.IsNullOrEmpty(request.DiscordUserId) ||
                    string.IsNullOrEmpty(request.CharacterName) || string.IsNullOrEmpty(request.Realm))
                {
                    return Results.BadRequest(new { error = "DiscordUserId, CharacterName, and Realm are required" });
                }

                // Parse Discord user ID
                if (!long.TryParse(request.DiscordUserId, out var userId))
                {
                    return Results.BadRequest(new { error = "Invalid DiscordUserId format" });
                }

                // Parse Discord server ID (optional)
                long? serverId = null;
                if (!string.IsNullOrEmpty(request.DiscordServerId) &&
                    long.TryParse(request.DiscordServerId, out var sid))
                {
                    serverId = sid;
                }

                var region = request.Region ?? "us";
                var locale = region.ToLower() switch
                {
                    "us" => "en_US",
                    "eu" => "en_GB",
                    "kr" => "ko_KR",
                    "tw" => "zh_TW",
                    _ => "en_US"
                };

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Check if character already exists for this user+server
                var existing = await db.WowCharAssociation
                    .FirstOrDefaultAsync(c => c.UserId == userId &&
                                               c.ServerId == serverId &&
                                               c.CharName.ToLower() == request.CharacterName.ToLower() &&
                                               c.WowRealm.ToLower() == request.Realm.ToLower() &&
                                               c.WowRegion == region,
                                         context.RequestAborted);

                if (existing != null)
                {
                    return Results.Conflict(new { error = "Character already saved for this server" });
                }

                // Add character
                var character = new WowCharAssociation
                {
                    UserId = userId,
                    ServerId = serverId,
                    CharName = request.CharacterName,
                    WowRealm = request.Realm,
                    WowRegion = region,
                    LocalRealmSlug = CharViewHelpers.ToRealmSlug(request.Realm),
                    Locale = locale,
                    IsMain = false,
                    TimeSet = DateTime.UtcNow
                };

                db.WowCharAssociation.Add(character);
                await db.SaveChangesAsync(context.RequestAborted);

                // Invalidate cache so bot picks up the new character
                var wowCache = scope.ServiceProvider.GetRequiredService<WowCacheService>();
                wowCache.InvalidateUserCharacters(userId);

                deps.Logger.LogInformation("Character added via API: {CharName} on {Realm} for user {UserId} server {ServerId}",
                    character.CharName, character.WowRealm, userId, serverId);

                return Results.Ok(new
                {
                    success = true,
                    character = new
                    {
                        id = character.Id,
                        name = character.CharName,
                        realm = character.WowRealm,
                        region = character.WowRegion,
                        serverId = character.ServerId
                    }
                });
            });

            // PUT /api/characters/{characterId}/main - Set a character as main
            group.MapPut("/api/characters/{characterId}/main", async (HttpContext context, string characterId) =>
            {
                if (!long.TryParse(characterId, out var charIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid character ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<SetMainCharacterRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null || string.IsNullOrEmpty(body.UserId))
                {
                    return Results.BadRequest(new { success = false, error = "user_id is required" });
                }

                if (!long.TryParse(body.UserId, out var userIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid user_id" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Find the character and verify ownership
                var character = await db.WowCharAssociation
                    .FirstOrDefaultAsync(c => c.Id == charIdLong && c.UserId == userIdLong);

                if (character == null)
                {
                    return Results.NotFound(new { success = false, error = "Character not found or not owned by user" });
                }

                // Use transaction to ensure atomicity
                using var transaction = await db.Database.BeginTransactionAsync();
                try
                {
                    // Unset any existing main character for this user
                    var existingMains = await db.WowCharAssociation
                        .Where(c => c.UserId == userIdLong && c.IsMain)
                        .ToListAsync();

                    foreach (var existing in existingMains)
                    {
                        existing.IsMain = false;
                    }

                    // Set the new main
                    character.IsMain = true;
                    character.TimeSet = DateTime.UtcNow;

                    // Backfill realm slug if missing
                    if (string.IsNullOrEmpty(character.LocalRealmSlug))
                    {
                        character.LocalRealmSlug = CharViewHelpers.ToRealmSlug(character.WowRealm);
                    }

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Invalidate cache so bot picks up the new main
                    var wowCache = scope.ServiceProvider.GetRequiredService<WowCacheService>();
                    wowCache.InvalidateUserCharacters(userIdLong);

                    return Results.Json(new
                    {
                        success = true,
                        character = new
                        {
                            id = character.Id,
                            name = character.CharName,
                            realm = character.WowRealm,
                            region = character.WowRegion,
                            is_main = character.IsMain
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    deps.Logger.LogError(ex, "Failed to set main character {CharId} for user {UserId}", charIdLong, userIdLong);
                    return Results.Json(new { success = false, error = "Failed to set main character" },
                        statusCode: 500);
                }
            });

            // DELETE /api/characters/{characterId} - Remove a character association
            group.MapDelete("/api/characters/{characterId}", async (HttpContext context, string characterId) =>
            {
                if (!long.TryParse(characterId, out var charIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid character ID" });
                }

                // Get user_id from query string
                var userIdStr = context.Request.Query["user_id"].ToString();
                if (string.IsNullOrEmpty(userIdStr) || !long.TryParse(userIdStr, out var userIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "user_id query parameter is required" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Find the character and verify ownership
                var character = await db.WowCharAssociation
                    .FirstOrDefaultAsync(c => c.Id == charIdLong && c.UserId == userIdLong);

                if (character == null)
                {
                    return Results.NotFound(new { success = false, error = "Character not found or not owned by user" });
                }

                var charName = character.CharName;
                var realm = character.WowRealm;

                db.WowCharAssociation.Remove(character);
                await db.SaveChangesAsync();

                // Invalidate cache so bot picks up the removal
                var wowCache = scope.ServiceProvider.GetRequiredService<WowCacheService>();
                wowCache.InvalidateUserCharacters(userIdLong);

                return Results.Json(new
                {
                    success = true,
                    message = $"Character {charName} ({realm}) removed"
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/users/{userId}/away-status - Get away status for a user
            group.MapGet("/api/users/{userId}/away-status", async (HttpContext context, string userId) =>
            {
                if (!long.TryParse(userId, out var userIdParsed))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid user ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var status = await db.AwaySystem
                    .FirstOrDefaultAsync(a => a.UserId == userIdParsed);

                if (status == null)
                {
                    return Results.Json(new
                    {
                        success = true,
                        status = (object?)null
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }

                return Results.Json(new
                {
                    success = true,
                    status = new
                    {
                        user_id = status.UserId.ToString(),
                        user_name = status.UserName,
                        is_away = status.Status ?? false,
                        message = status.Message,
                        time_away = status.TimeAway
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // PUT /api/users/{userId}/away-status - Set away status for a user
            group.MapPut("/api/users/{userId}/away-status", async (HttpContext context, string userId) =>
            {
                if (!long.TryParse(userId, out var userIdParsed))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid user ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateAwayStatusRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var status = await db.AwaySystem
                    .FirstOrDefaultAsync(a => a.UserId == userIdParsed);

                if (status == null)
                {
                    // Create new status
                    status = new Database.AwaySystem
                    {
                        UserId = userIdParsed
                    };
                    db.AwaySystem.Add(status);
                }

                // Update fields
                if (!string.IsNullOrEmpty(body.UserName))
                    status.UserName = body.UserName;

                if (body.IsAway.HasValue)
                {
                    status.Status = body.IsAway.Value;
                    status.TimeAway = body.IsAway.Value ? DateTime.UtcNow : null;
                }

                if (body.Message != null)
                    status.Message = body.Message;

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    status = new
                    {
                        user_id = status.UserId.ToString(),
                        user_name = status.UserName,
                        is_away = status.Status ?? false,
                        message = status.Message,
                        time_away = status.TimeAway
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });
        }
    }
}
