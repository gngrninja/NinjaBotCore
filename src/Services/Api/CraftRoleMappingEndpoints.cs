using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services.Api
{
    public static class CraftRoleMappingEndpoints
    {
        public static void MapCraftRoleMappingEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/guilds/{guildId}/craft-role-mappings - List all profession-to-role mappings
            group.MapGet("/api/guilds/{guildId}/craft-role-mappings", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var mappings = await db.CraftProfessionRoleMappings
                    .Where(m => m.GuildId == guildIdLong)
                    .OrderBy(m => m.Profession)
                    .Select(m => new
                    {
                        id = m.Id,
                        profession = m.Profession,
                        role_id = m.RoleId.ToString(),
                        role_name = m.RoleName,
                        set_by_id = m.SetById.HasValue ? m.SetById.Value.ToString() : null,
                        set_by_name = m.SetByName,
                        created_at = m.CreatedAt
                    })
                    .ToListAsync();

                return Results.Json(new
                {
                    success = true,
                    mappings,
                    count = mappings.Count
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // POST /api/guilds/{guildId}/craft-role-mappings - Add or update a profession-to-role mapping
            group.MapPost("/api/guilds/{guildId}/craft-role-mappings",
                async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpsertCraftRoleMappingRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null || string.IsNullOrEmpty(body.Profession) || string.IsNullOrEmpty(body.RoleId))
                {
                    return Results.BadRequest(new { success = false, error = "profession and role_id are required" });
                }

                if (!long.TryParse(body.RoleId, out var roleIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid role_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var existing = await db.CraftProfessionRoleMappings
                    .FirstOrDefaultAsync(m => m.GuildId == guildIdLong && m.Profession == body.Profession);

                if (existing != null)
                {
                    existing.RoleId = roleIdLong;
                    if (!string.IsNullOrEmpty(body.RoleName))
                        existing.RoleName = body.RoleName;
                    if (!string.IsNullOrEmpty(body.SetById) && long.TryParse(body.SetById, out var setById))
                        existing.SetById = setById;
                    if (!string.IsNullOrEmpty(body.SetByName))
                        existing.SetByName = body.SetByName;
                }
                else
                {
                    var mapping = new CraftProfessionRoleMapping
                    {
                        GuildId = guildIdLong,
                        Profession = body.Profession,
                        RoleId = roleIdLong,
                        RoleName = body.RoleName,
                        CreatedAt = DateTime.UtcNow
                    };
                    if (!string.IsNullOrEmpty(body.SetById) && long.TryParse(body.SetById, out var setById))
                        mapping.SetById = setById;
                    if (!string.IsNullOrEmpty(body.SetByName))
                        mapping.SetByName = body.SetByName;

                    db.CraftProfessionRoleMappings.Add(mapping);
                }

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    message = existing != null ? "Mapping updated" : "Mapping created"
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // DELETE /api/guilds/{guildId}/craft-role-mappings/{profession} - Remove a mapping
            group.MapDelete("/api/guilds/{guildId}/craft-role-mappings/{profession}",
                async (HttpContext context, string guildId, string profession) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                profession = Uri.UnescapeDataString(profession);

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var mapping = await db.CraftProfessionRoleMappings
                    .FirstOrDefaultAsync(m => m.GuildId == guildIdLong && m.Profession == profession);

                if (mapping == null)
                {
                    return Results.BadRequest(new { success = false, error = "Mapping not found" });
                }

                db.CraftProfessionRoleMappings.Remove(mapping);
                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    message = $"Removed mapping for {profession}"
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // POST /api/guilds/{guildId}/members/{userId}/roles/{roleId} - Assign a craft profession role to a user
            group.MapPost("/api/guilds/{guildId}/members/{userId}/roles/{roleId}",
                async (HttpContext context, string guildId, string userId, string roleId) =>
            {
                if (!ulong.TryParse(guildId, out var guildIdUlong) ||
                    !ulong.TryParse(userId, out var userIdUlong) ||
                    !ulong.TryParse(roleId, out var roleIdUlong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid ID format" });
                }

                // Validate role is a configured craft profession mapping
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var isValidCraftRole = await db.CraftProfessionRoleMappings
                    .AnyAsync(m => m.GuildId == (long)guildIdUlong && m.RoleId == (long)roleIdUlong);
                if (!isValidCraftRole)
                {
                    return Results.BadRequest(new { success = false, error = "This role is not a configured craft profession role" });
                }

                var client = deps.ServiceProvider.GetService<DiscordShardedClient>();
                if (client == null)
                {
                    return Results.Json(new { success = false, error = "Discord client unavailable" },
                        statusCode: 503);
                }

                var guild = client.GetGuild(guildIdUlong);
                if (guild == null)
                {
                    return Results.BadRequest(new { success = false, error = "Guild not found" });
                }

                var member = guild.GetUser(userIdUlong);
                if (member == null)
                {
                    // Try downloading the user
                    await guild.DownloadUsersAsync();
                    member = guild.GetUser(userIdUlong);
                    if (member == null)
                    {
                        return Results.BadRequest(new { success = false, error = "User not found in guild" });
                    }
                }

                var role = guild.GetRole(roleIdUlong);
                if (role == null)
                {
                    return Results.BadRequest(new { success = false, error = "Role not found" });
                }

                try
                {
                    await member.AddRoleAsync(role);
                    return Results.Json(new
                    {
                        success = true,
                        message = $"Assigned {role.Name} to {member.Username}"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogWarning(ex, "Failed to assign role {RoleId} to user {UserId} in guild {GuildId}",
                        roleId, userId, guildId);
                    return Results.Json(new { success = false, error = "Failed to assign role. Check bot permissions." },
                        statusCode: 500);
                }
            });

            // DELETE /api/guilds/{guildId}/members/{userId}/roles/{roleId} - Remove a craft profession role from a user
            group.MapDelete("/api/guilds/{guildId}/members/{userId}/roles/{roleId}",
                async (HttpContext context, string guildId, string userId, string roleId) =>
            {
                if (!ulong.TryParse(guildId, out var guildIdUlong) ||
                    !ulong.TryParse(userId, out var userIdUlong) ||
                    !ulong.TryParse(roleId, out var roleIdUlong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid ID format" });
                }

                // Validate role is a configured craft profession mapping
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var isValidCraftRole = await db.CraftProfessionRoleMappings
                    .AnyAsync(m => m.GuildId == (long)guildIdUlong && m.RoleId == (long)roleIdUlong);
                if (!isValidCraftRole)
                {
                    return Results.BadRequest(new { success = false, error = "This role is not a configured craft profession role" });
                }

                var client = deps.ServiceProvider.GetService<DiscordShardedClient>();
                if (client == null)
                {
                    return Results.Json(new { success = false, error = "Discord client unavailable" },
                        statusCode: 503);
                }

                var guild = client.GetGuild(guildIdUlong);
                if (guild == null)
                {
                    return Results.BadRequest(new { success = false, error = "Guild not found" });
                }

                var member = guild.GetUser(userIdUlong);
                if (member == null)
                {
                    return Results.BadRequest(new { success = false, error = "User not found in guild" });
                }

                var role = guild.GetRole(roleIdUlong);
                if (role == null)
                {
                    return Results.BadRequest(new { success = false, error = "Role not found" });
                }

                try
                {
                    await member.RemoveRoleAsync(role);
                    return Results.Json(new
                    {
                        success = true,
                        message = $"Removed {role.Name} from {member.Username}"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogWarning(ex, "Failed to remove role {RoleId} from user {UserId} in guild {GuildId}",
                        roleId, userId, guildId);
                    return Results.Json(new { success = false, error = "Failed to remove role. Check bot permissions." },
                        statusCode: 500);
                }
            });
        }
    }
}
