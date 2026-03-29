using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services.Api
{
    public static class CraftSettingsEndpoints
    {
        public static void MapCraftSettingsEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/guilds/{guildId}/craft-settings - Get craft settings for a guild
            group.MapGet("/api/guilds/{guildId}/craft-settings", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ServerCraftSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                if (settings == null)
                {
                    // Return defaults
                    return Results.Json(new
                    {
                        success = true,
                        settings = new
                        {
                            guild_id = guildId,
                            craft_channel_id = (string?)null,
                            max_open_tickets_per_user = 3,
                            ticket_expiration_hours = 48,
                            set_by_id = (string?)null,
                            set_by_name = (string?)null,
                            time_set = (DateTime?)null
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }

                return Results.Json(new
                {
                    success = true,
                    settings = new
                    {
                        guild_id = settings.DiscordGuildId.ToString(),
                        craft_channel_id = settings.CraftChannelId?.ToString(),
                        max_open_tickets_per_user = settings.MaxOpenTicketsPerUser,
                        ticket_expiration_hours = settings.TicketExpirationHours,
                        set_by_id = settings.SetById?.ToString(),
                        set_by_name = settings.SetByName,
                        time_set = settings.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // PUT /api/guilds/{guildId}/craft-settings - Update craft settings for a guild
            group.MapPut("/api/guilds/{guildId}/craft-settings", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateCraftSettingsRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                // Validate ranges
                if (body.MaxOpenTicketsPerUser.HasValue &&
                    (body.MaxOpenTicketsPerUser.Value < 1 || body.MaxOpenTicketsPerUser.Value > 25))
                {
                    return Results.BadRequest(new { success = false, error = "max_open_tickets_per_user must be between 1 and 25" });
                }

                if (body.TicketExpirationHours.HasValue &&
                    (body.TicketExpirationHours.Value < 1 || body.TicketExpirationHours.Value > 720))
                {
                    return Results.BadRequest(new { success = false, error = "ticket_expiration_hours must be between 1 and 720" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ServerCraftSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                if (settings == null)
                {
                    settings = new ServerCraftSettings
                    {
                        DiscordGuildId = guildIdLong
                    };
                    db.ServerCraftSettings.Add(settings);
                }

                // Update fields
                if (body.CraftChannelId != null)
                {
                    if (string.IsNullOrEmpty(body.CraftChannelId))
                        settings.CraftChannelId = null;
                    else if (long.TryParse(body.CraftChannelId, out var channelId))
                        settings.CraftChannelId = channelId;
                }

                if (body.MaxOpenTicketsPerUser.HasValue)
                    settings.MaxOpenTicketsPerUser = body.MaxOpenTicketsPerUser.Value;

                if (body.TicketExpirationHours.HasValue)
                    settings.TicketExpirationHours = body.TicketExpirationHours.Value;

                if (!string.IsNullOrEmpty(body.SetById) && long.TryParse(body.SetById, out var setById))
                    settings.SetById = setById;

                if (!string.IsNullOrEmpty(body.SetByName))
                    settings.SetByName = body.SetByName;

                settings.TimeSet = DateTime.UtcNow;

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    message = "Craft settings updated",
                    settings = new
                    {
                        guild_id = settings.DiscordGuildId.ToString(),
                        craft_channel_id = settings.CraftChannelId?.ToString(),
                        max_open_tickets_per_user = settings.MaxOpenTicketsPerUser,
                        ticket_expiration_hours = settings.TicketExpirationHours,
                        set_by_id = settings.SetById?.ToString(),
                        set_by_name = settings.SetByName,
                        time_set = settings.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });
        }
    }
}
