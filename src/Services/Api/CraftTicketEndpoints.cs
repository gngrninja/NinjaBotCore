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
using NinjaBotCore.Modules.Interactions.Crafting;

namespace NinjaBotCore.Services.Api
{
    public static class CraftTicketEndpoints
    {
        public static void MapCraftTicketEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/guilds/{guildId}/craft-tickets - List craft tickets for a guild
            group.MapGet("/api/guilds/{guildId}/craft-tickets", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                var statusFilter = context.Request.Query["status"].FirstOrDefault();
                var pageStr = context.Request.Query["page"].FirstOrDefault();
                var pageSizeStr = context.Request.Query["page_size"].FirstOrDefault();

                var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
                var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Clamp(ps, 1, 50) : 25;

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var query = db.CraftTickets.Where(t => t.GuildId == guildIdLong);

                if (!string.IsNullOrEmpty(statusFilter))
                {
                    // Support filtering by active tickets or specific status
                    if (statusFilter.Equals("active", StringComparison.OrdinalIgnoreCase))
                    {
                        query = query.Where(t =>
                            t.Status == "Open" || t.Status == "Claimed" || t.Status == "Crafted");
                    }
                    else
                    {
                        var normalized = char.ToUpper(statusFilter[0]) + statusFilter[1..].ToLower();
                        query = query.Where(t => t.Status == normalized);
                    }
                }

                var total = await query.CountAsync();

                var tickets = await query
                    .OrderByDescending(t => t.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(t => new
                    {
                        id = t.Id,
                        item_name = t.ItemName,
                        item_icon_url = t.ItemIconUrl,
                        status = t.Status,
                        requester_id = t.RequesterId.ToString(),
                        requester_name = t.RequesterName,
                        crafter_id = t.CrafterId.HasValue ? t.CrafterId.Value.ToString() : null,
                        crafter_name = t.CrafterName,
                        quality_desired = t.QualityDesired,
                        commission = t.Commission,
                        created_at = t.CreatedAt,
                        expires_at = t.ExpiresAt,
                        claimed_at = t.ClaimedAt,
                        completed_at = t.CompletedAt
                    })
                    .ToListAsync();

                return Results.Json(new
                {
                    success = true,
                    tickets,
                    total,
                    page,
                    page_size = pageSize,
                    total_pages = (int)Math.Ceiling((double)total / pageSize)
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // POST /api/guilds/{guildId}/craft-tickets/{ticketId}/cancel - Cancel a craft ticket
            group.MapPost("/api/guilds/{guildId}/craft-tickets/{ticketId}/cancel",
                async (HttpContext context, string guildId, string ticketId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                if (!long.TryParse(ticketId, out var ticketIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid ticket_id format" });
                }

                var body = await context.Request.ReadFromJsonAsync<CancelCraftTicketRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null || string.IsNullOrEmpty(body.CancelledById))
                {
                    return Results.BadRequest(new { success = false, error = "cancelled_by_id is required" });
                }

                if (!long.TryParse(body.CancelledById, out var cancelledByIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid cancelled_by_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Verify ticket belongs to this guild before cancelling
                var ticketCheck = await db.CraftTickets.FirstOrDefaultAsync(t => t.Id == ticketIdLong);
                if (ticketCheck == null)
                {
                    return Results.BadRequest(new { success = false, error = "Ticket not found" });
                }
                if (ticketCheck.GuildId != guildIdLong)
                {
                    return Results.BadRequest(new { success = false, error = "Ticket does not belong to this guild" });
                }

                // Use the shared cancel logic (same code path as Discord button handler)
                var (ticket, cancelledBy, error) = await CraftTicketUpdater.CancelTicketAsync(
                    db, ticketIdLong, cancelledByIdLong, isAdmin: body.IsAdmin ?? false);

                if (error != null)
                {
                    return Results.BadRequest(new { success = false, error });
                }

                // Discord operations: update embed + archive thread (best-effort)
                try
                {
                    var client = scope.ServiceProvider.GetService<DiscordShardedClient>();
                    if (client != null)
                    {
                        await CraftTicketUpdater.UpdateTicketAsync(client, ticket, deps.Logger,
                            threadNotification: $"This crafting request has been cancelled by {cancelledBy}.",
                            archiveThread: true);
                    }
                }
                catch (Exception ex)
                {
                    deps.Logger.LogWarning(ex, "Discord update failed for cancelled ticket {TicketId}", ticketIdLong);
                }

                return Results.Json(new
                {
                    success = true,
                    message = "Ticket cancelled",
                    ticket = new
                    {
                        id = ticket.Id,
                        item_name = ticket.ItemName,
                        status = ticket.Status,
                        cancelled_by = cancelledBy
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // POST /api/guilds/{guildId}/craft-tickets/cleanup - Delete old inactive tickets
            group.MapPost("/api/guilds/{guildId}/craft-tickets/cleanup",
                async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var inactiveTickets = await db.CraftTickets
                    .Where(t => t.GuildId == guildIdLong &&
                        (t.Status == "Complete" || t.Status == "Cancelled" || t.Status == "Expired"))
                    .ToListAsync();

                var count = inactiveTickets.Count;
                if (count > 0)
                {
                    db.CraftTickets.RemoveRange(inactiveTickets);
                    await db.SaveChangesAsync();
                }

                return Results.Json(new
                {
                    success = true,
                    message = $"Removed {count} inactive ticket(s)",
                    deleted_count = count
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });
        }
    }
}
