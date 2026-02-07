using System;
using System.Text.Json;
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
    public static class AdminEndpoints
    {
        public static void MapAdminEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            // Health check endpoint (no auth required)
            app.MapGet("/api/commands/health", () => Results.Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow
            }));

            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // Commands endpoint
            group.MapGet("/api/commands", (HttpContext context) =>
            {
                var content = deps.HelpProvider.GetHelpContent();
                if (content == null)
                {
                    return Results.NotFound(new { error = "Help content not available" });
                }

                deps.Logger.LogDebug("Commands API request served successfully");

                return Results.Json(content, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = false
                });
            });

            // Regenerate endpoint
            group.MapPost("/api/commands/regenerate", (HttpContext context) =>
            {
                deps.HelpProvider.RegenerateHelpContent();

                var content = deps.HelpProvider.GetHelpContent();
                return Results.Ok(new
                {
                    success = true,
                    commands = content?.Metadata?.TotalCommands ?? 0,
                    categories = content?.Categories?.Count ?? 0,
                    last_updated = content?.Metadata?.LastUpdated
                });
            });

            // Refresh guild roster endpoint
            group.MapPost("/api/guilds/refresh-roster", async (HttpContext context) =>
            {
                // Parse request body
                RefreshRosterRequest? request;
                try
                {
                    request = await context.Request.ReadFromJsonAsync<RefreshRosterRequest>();
                }
                catch
                {
                    return Results.BadRequest(new { error = "Invalid JSON body" });
                }

                if (request == null || string.IsNullOrEmpty(request.DiscordGuildId))
                {
                    return Results.BadRequest(new { error = "DiscordGuildId is required" });
                }

                // Parse guild ID
                if (!long.TryParse(request.DiscordGuildId, out var guildId))
                {
                    return Results.BadRequest(new { error = "Invalid DiscordGuildId format" });
                }

                // Use a scope for the DbContext
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Look up guild association
                var association = await db.WowGuildAssociations
                    .FirstOrDefaultAsync(g => g.ServerId == guildId, context.RequestAborted);

                if (association == null)
                {
                    return Results.NotFound(new { error = "No WoW guild association found for this Discord server" });
                }

                // Build GuildObject
                var guildObject = new NinjaObjects.GuildObject
                {
                    guildName = association.WowGuild,
                    realmSlug = association.LocalRealmSlug,
                    realmName = association.WowRealm,
                    regionName = association.WowRegion,
                    locale = association.Locale
                };

                // Refresh roster
                try
                {
                    await deps.WowUtilities.RefreshGuildRosterAsync(guildObject, context.RequestAborted);

                    // Get member count for response
                    var count = await db.WowGuildRosterMembers
                        .CountAsync(m => m.GuildName == association.WowGuild
                            && m.GuildRealmSlug == association.LocalRealmSlug
                            && m.Region == association.WowRegion,
                            context.RequestAborted);

                    deps.Logger.LogInformation("Roster refreshed for guild {Guild} on {Realm}: {Count} members",
                        association.WowGuild, association.WowRealm, count);

                    return Results.Ok(new
                    {
                        success = true,
                        guild = association.WowGuild,
                        realm = association.WowRealm,
                        memberCount = count
                    });
                }
                catch (Exception ex)
                {
                    deps.Logger.LogError(ex, "Failed to refresh roster for guild {Guild}", association.WowGuild);
                    return Results.Problem($"Failed to refresh roster: {ex.Message}");
                }
            });

            // Invalidate WCL caches
            group.MapPost("/api/cache/wcl-invalidate", async (HttpContext context) =>
            {
                // Parse request body
                WclCacheInvalidateRequest? request;
                try
                {
                    request = await context.Request.ReadFromJsonAsync<WclCacheInvalidateRequest>();
                }
                catch
                {
                    return Results.BadRequest(new { error = "Invalid JSON body" });
                }

                if (request == null || string.IsNullOrEmpty(request.GuildName) ||
                    string.IsNullOrEmpty(request.RealmSlug) || string.IsNullOrEmpty(request.Region))
                {
                    return Results.BadRequest(new { error = "GuildName, RealmSlug, and Region are required" });
                }

                // Get cache service and invalidate
                using var scope = deps.ServiceProvider.CreateScope();
                var wowCache = scope.ServiceProvider.GetRequiredService<WowCacheService>();

                var invalidatedCount = wowCache.InvalidateGuildWclCaches(
                    request.GuildName, request.RealmSlug, request.Region);

                deps.Logger.LogInformation("WCL cache invalidated via API for guild {Guild} on {Realm}-{Region}: {Count} entries",
                    request.GuildName, request.RealmSlug, request.Region, invalidatedCount);

                return Results.Ok(new
                {
                    success = true,
                    invalidatedCount,
                    guild = request.GuildName,
                    realm = request.RealmSlug,
                    region = request.Region
                });
            });
        }
    }
}
