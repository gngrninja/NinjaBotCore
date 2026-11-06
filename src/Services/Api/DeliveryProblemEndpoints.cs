#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services.DeliveryProblems;

namespace NinjaBotCore.Services.Api
{
    /// <summary>
    /// The bot owner's Actionable Alerts page: features Discord stopped letting the bot post
    /// for, and the actions the owner takes on them. Every change needs the owner's user ID.
    /// </summary>
    public static class DeliveryProblemEndpoints
    {
        public static void MapDeliveryProblemEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("/api/admin/delivery-problems").AddEndpointFilter(apiKeyFilter);

            group.MapGet("", async () =>
            {
                if (deps.ServiceProvider.GetService<DeliveryProblemService>() is not { } service)
                {
                    return NotRunning();
                }

                var (problems, counts) = await service.ListAsync();
                return Results.Json(new
                {
                    success = true,
                    counts = new
                    {
                        open = counts.Open,
                        notified = counts.Notified,
                        dismissed = counts.Dismissed,
                        resolved = counts.Resolved,
                        bot_left = counts.BotLeft
                    },
                    problems = problems.Select(Problem)
                }, RaidRecapLiveEndpoints.Json);
            });

            group.MapPost("/{id}/dm", async (string id, HttpContext context) =>
            {
                if (deps.ServiceProvider.GetService<DeliveryProblemService>() is not { } service)
                {
                    return NotRunning();
                }

                var body = await ReadAsync<DeliveryProblemDmRequest>(context);
                if (!long.TryParse(id, out var problemId) || body?.To is not (DeliveryProblemText.Setter or DeliveryProblemText.Owner))
                {
                    return Results.BadRequest(new { success = false, error = "to must be setter or owner" });
                }

                if (!await IsOwnerAsync(deps, body.UserId))
                {
                    return NotOwner();
                }

                var (result, view) = await service.SendDmAsync(problemId, body.To);
                if (view == null)
                {
                    return NotFound();
                }

                return Results.Json(new
                {
                    success = true,
                    result,
                    result_text = DmText(result),
                    problem = Problem(view)
                }, RaidRecapLiveEndpoints.Json);
            });

            group.MapPost("/{id}/recheck", async (string id, HttpContext context) =>
            {
                if (deps.ServiceProvider.GetService<DeliveryProblemService>() is not { } service)
                {
                    return NotRunning();
                }

                var body = await ReadAsync<DeliveryProblemOwnerRequest>(context);
                if (!long.TryParse(id, out var problemId))
                {
                    return NotFound();
                }

                if (!await IsOwnerAsync(deps, body?.UserId))
                {
                    return NotOwner();
                }

                var (isFixed, view) = await service.RecheckAsync(problemId);
                return view == null
                    ? NotFound()
                    : Results.Json(new { success = true, @fixed = isFixed, problem = Problem(view) }, RaidRecapLiveEndpoints.Json);
            });

            group.MapPost("/turn-off", async (HttpContext context) =>
            {
                if (deps.ServiceProvider.GetService<DeliveryProblemService>() is not { } service)
                {
                    return NotRunning();
                }

                var body = await ReadAsync<DeliveryProblemBatchRequest>(context);
                if (Ids(body?.Ids) is not { } ids)
                {
                    return BadIds();
                }

                if (!await IsOwnerAsync(deps, body!.UserId))
                {
                    return NotOwner();
                }

                var results = await service.TurnOffAsync(ids);
                return Results.Json(new
                {
                    success = true,
                    results = results.Select(r => new
                    {
                        id = r.Id.ToString(),
                        status = r.Status,
                        status_text = TurnOffText(r.Status)
                    })
                }, RaidRecapLiveEndpoints.Json);
            });

            group.MapPost("/dismiss", async (HttpContext context) =>
            {
                if (deps.ServiceProvider.GetService<DeliveryProblemService>() is not { } service)
                {
                    return NotRunning();
                }

                var body = await ReadAsync<DeliveryProblemBatchRequest>(context);
                if (Ids(body?.Ids) is not { } ids)
                {
                    return BadIds();
                }

                if (!await IsOwnerAsync(deps, body!.UserId))
                {
                    return NotOwner();
                }

                return Results.Json(new { success = true, dismissed = await service.DismissAsync(ids) }, RaidRecapLiveEndpoints.Json);
            });
        }

        /// <summary>Problem IDs as numbers, or null when the list is empty, too long or not all numbers.</summary>
        internal static IReadOnlyList<long>? Ids(List<string>? ids)
        {
            if (ids == null || ids.Count == 0 || ids.Count > DeliveryProblemService.MaxBatch)
            {
                return null;
            }

            var parsed = new List<long>(ids.Count);
            foreach (var id in ids)
            {
                if (!long.TryParse(id, out var value) || value <= 0)
                {
                    return null;
                }

                parsed.Add(value);
            }

            return parsed;
        }

        internal static object Problem(DeliveryProblemView v) => new
        {
            id = v.Row.Id.ToString(),
            server_id = v.Row.ServerId.ToString(),
            server_name = v.ServerName,
            bot_present = v.BotPresent,
            feature = v.Row.Feature,
            feature_text = DeliveryProblemText.Feature(v.Row.Feature),
            channel_id = v.Row.ChannelId > 0 ? v.Row.ChannelId.ToString() : null,
            channel_name = v.ChannelName,
            channel_exists = v.ChannelExists,
            problem = v.Row.Problem,
            problem_text = v.ProblemText,
            discord_code = v.Row.DiscordCode,
            missing_permissions = v.MissingPermissions,
            fixed_now = v.FixedNow,
            first_seen_at = Utc(v.Row.FirstSeenAt),
            last_seen_at = Utc(v.Row.LastSeenAt),
            failures = v.Row.Failures,
            state = v.State,
            resolved_at = Utc(v.Row.ResolvedAt),
            resolution = v.Row.Resolution,
            dismissed_at = Utc(v.Row.DismissedAt),
            setter = Person(v.Setter),
            owner = Person(v.Owner),
            notified_at = Utc(v.Row.NotifiedAt),
            notified_role = v.Row.NotifiedRole,
            last_attempt_at = Utc(v.Row.LastAttemptAt),
            last_attempt_role = v.Row.LastAttemptRole,
            last_attempt_result = v.Row.LastAttemptResult,
            can_turn_off = DeliveryProblemText.CanTurnOff(v.Row.Feature),
            dm_preview = v.DmPreview
        };

        private static object? Person(DeliveryPerson? person) => person == null ? null : new
        {
            user_id = person.UserId.ToString(),
            name = person.Name,
            in_server = person.InServer,
            via = person.Via
        };

        internal static string DmText(string result) => result switch
        {
            DeliveryDmResult.Sent => "DM sent.",
            DeliveryDmResult.DmsClosed => "They don't accept DMs from NinjaBot.",
            DeliveryDmResult.NotInServer => "They're no longer in that server, so nothing was sent.",
            DeliveryDmResult.NoRecipient => "There's nobody on record to DM.",
            DeliveryDmResult.BotLeft => "NinjaBot is no longer in that server, so nothing was sent.",
            DeliveryDmResult.Resolved => "This problem is already resolved, so nothing was sent.",
            DeliveryDmResult.LooksFixed => "NinjaBot can post there again, so nothing was sent. Re-check to resolve it.",
            DeliveryDmResult.RecentlySent => "They were DMed about this a few minutes ago, so nothing was sent.",
            DeliveryDmResult.CheckFailed => "Discord couldn't confirm they're still in the server, so nothing was sent. Try again shortly.",
            _ => "Discord didn't accept the DM. Try again later."
        };

        internal static string TurnOffText(string status) => status switch
        {
            DeliveryTurnOffStatus.TurnedOff => "Turned off.",
            DeliveryTurnOffStatus.AlreadyOff => "It was already off.",
            DeliveryTurnOffStatus.NotSupported => "This feature can't be turned off from here.",
            DeliveryTurnOffStatus.Resolved => "Already resolved, or the server moved or turned it off itself. Nothing was changed.",
            _ => "Not found."
        };

        private static async Task<bool> IsOwnerAsync(ApiDependencies deps, string? userId)
        {
            var discord = deps.ServiceProvider.GetService<IRaidRecapLiveDiscord>();
            if (discord == null || !ulong.TryParse(userId, out var id))
            {
                return false;
            }

            return (await discord.OwnerIdsAsync()).Contains(id);
        }

        private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
        {
            try
            {
                return await context.Request.ReadFromJsonAsync<T>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Bad JSON, or no JSON body at all.
                return null;
            }
        }

        private static DateTime? Utc(DateTime? at) =>
            at is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;

        private static IResult NotRunning() =>
            Results.NotFound(new { success = false, error = "Actionable alerts are not running" });

        private static IResult NotFound() =>
            Results.NotFound(new { success = false, error = "No such problem" });

        private static IResult BadIds() =>
            Results.BadRequest(new { success = false, error = $"ids must be 1 to {DeliveryProblemService.MaxBatch} problem IDs" });

        private static IResult NotOwner() =>
            Results.Json(new { success = false, error = "Only the bot owner can do this" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
