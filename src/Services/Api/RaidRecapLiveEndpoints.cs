#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services.Api
{
    public static class RaidRecapLiveEndpoints
    {
        /// <summary>How the health is written: snake_case names and keys.</summary>
        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public static void MapRaidRecapLiveEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // The live raid recap watcher at a glance, for the owner's admin page. The same
            // numbers as /raid-recap-rollout Status.
            group.MapGet("/api/raid-recap-live/health", async () =>
            {
                var coordinator = deps.ServiceProvider.GetService<RaidRecapLiveCoordinator>();
                var gate = deps.ServiceProvider.GetService<RaidRecapLiveGate>();
                if (coordinator == null || gate == null)
                {
                    return Results.NotFound(new { error = "Live raid recap is not running" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var status = await RaidRecapLiveStatus.BuildAsync(
                    db,
                    await gate.ModeAsync(),
                    coordinator.Health.Snapshot(),
                    deps.ServiceProvider.GetService<WarcraftLogsV2Client>()?.LastRateLimit,
                    DateTimeOffset.UtcNow,
                    deps.ServiceProvider.GetService<WarcraftLogsV2Client>()?.AuthHealth);
                return Results.Json(status, Json);
            });

            // ===== Rollout, for the bot owner's admin page. The same rules as /raid-recap-select. =====

            // The rollout mode and the selected servers.
            group.MapGet("/api/raid-recap-live/rollout", async () =>
            {
                var gate = deps.ServiceProvider.GetService<RaidRecapLiveGate>();
                var selection = deps.ServiceProvider.GetService<RaidRecapLiveSelection>();
                if (gate == null || selection == null)
                {
                    return NotRunning();
                }

                return Results.Json(new
                {
                    success = true,
                    rollout = await RolloutAsync(gate, selection)
                }, Json);
            });

            // Sets the rollout mode: off (kill switch), mine, selected or everyone.
            group.MapPut("/api/raid-recap-live/rollout", async (HttpContext context) =>
            {
                var gate = deps.ServiceProvider.GetService<RaidRecapLiveGate>();
                var selection = deps.ServiceProvider.GetService<RaidRecapLiveSelection>();
                if (gate == null || selection == null)
                {
                    return NotRunning();
                }

                var body = await ReadAsync<UpdateRaidRecapRolloutRequest>(context);
                if (body == null || ParseMode(body.Mode) is not { } mode)
                {
                    return Results.BadRequest(new { success = false, error = "mode must be off, mine, selected or everyone" });
                }

                if (await OwnerAsync(deps, body.UserId) == null)
                {
                    return NotOwner();
                }

                var previous = await gate.ModeAsync();
                if (mode != previous)
                {
                    await gate.SetModeAsync(mode);
                    deps.Logger.LogInformation("Raid recap rollout changed from {Previous} to {Mode} on the web", previous, mode);
                }

                return Results.Json(new
                {
                    success = true,
                    previous = ModeKey(previous),
                    rollout = await RolloutAsync(gate, selection)
                }, Json);
            });

            // Previews the most active servers. Changes nothing, but asks Warcraft Logs, through
            // the same cache as live discovery.
            group.MapGet("/api/raid-recap-live/candidates", async (int? count) =>
            {
                var selection = deps.ServiceProvider.GetService<RaidRecapLiveSelection>();
                if (selection == null)
                {
                    return NotRunning();
                }

                var (candidates, active) = await selection.MostActiveAsync(Math.Clamp(count ?? 20, 1, RaidRecapLiveSelection.MaxSelect));
                return Results.Json(new
                {
                    success = true,
                    active,
                    candidates = candidates.Select(Candidate)
                }, Json);
            });

            // Selects one server by ID, or the most active servers when no ID is given. Moves
            // "my servers" to "selected servers and mine" and refuses while the kill switch is on.
            group.MapPost("/api/raid-recap-live/selection", async (HttpContext context) =>
            {
                var selection = deps.ServiceProvider.GetService<RaidRecapLiveSelection>();
                var gate = deps.ServiceProvider.GetService<RaidRecapLiveGate>();
                if (selection == null || gate == null)
                {
                    return NotRunning();
                }

                var body = await ReadAsync<RaidRecapSelectRequest>(context);
                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                ulong serverId = 0;
                if (!string.IsNullOrWhiteSpace(body.ServerId) && !ulong.TryParse(body.ServerId.Trim(), out serverId))
                {
                    return Results.BadRequest(new { success = false, error = "server_id must be a Discord server ID" });
                }

                if (await OwnerAsync(deps, body.UserId) is not { } owner)
                {
                    return NotOwner();
                }

                var before = await selection.OpenForSelectedAsync();
                if (before == null)
                {
                    return Results.Json(new
                    {
                        success = false,
                        reason = "kill_switch",
                        error = "The rollout is off (kill switch). Choose Selected servers and mine first, so nobody is told about a card that won't post."
                    }, Json, statusCode: StatusCodes.Status409Conflict);
                }

                var name = body.Username ?? "";
                object[] results;
                int? active = null;
                if (serverId != 0)
                {
                    var status = await selection.SelectAsync(serverId, owner, name);
                    using var scope = deps.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                    var id = checked((long)serverId);
                    var serverName = await db.DiscordServers.AsNoTracking()
                        .Where(d => d.ServerId == id)
                        .Select(d => d.ServerName)
                        .FirstOrDefaultAsync();
                    results = new object[] { Result(id, serverName ?? id.ToString(), status) };
                }
                else
                {
                    var (picked, ranked) = await selection.SelectMostActiveAsync(
                        Math.Clamp(body.Count ?? 20, 1, RaidRecapLiveSelection.MaxSelect), owner, name);
                    results = picked.Select(c => Result(c.ServerId, c.Name, c.Status)).ToArray();
                    active = ranked;
                }

                return Results.Json(new
                {
                    success = true,
                    mode_changed_from = before == RaidRecapRolloutMode.OwnerServers ? ModeKey(before.Value) : null,
                    active,
                    results,
                    rollout = await RolloutAsync(gate, selection)
                }, Json);
            });

            // Takes a server out of the selection. A live card there stops unless the rollout
            // still covers it; its own on or off setting is kept.
            group.MapDelete("/api/raid-recap-live/selection/{serverId}", async (string serverId, [FromQuery(Name = "user_id")] string? userId) =>
            {
                var selection = deps.ServiceProvider.GetService<RaidRecapLiveSelection>();
                var gate = deps.ServiceProvider.GetService<RaidRecapLiveGate>();
                if (selection == null || gate == null)
                {
                    return NotRunning();
                }

                if (!ulong.TryParse(serverId, out var id))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid server ID" });
                }

                if (await OwnerAsync(deps, userId) == null)
                {
                    return NotOwner();
                }

                return Results.Json(new
                {
                    success = true,
                    removed = await selection.UnselectAsync(id),
                    rollout = await RolloutAsync(gate, selection)
                }, Json);
            });

            // ===== One server's card, for its officers on the web dashboard. The same rules as /raid-recap-live. =====

            group.MapGet("/api/guilds/{guildId}/raid-recap-live", async (string guildId) =>
            {
                var liveSwitch = deps.ServiceProvider.GetService<RaidRecapLiveSwitch>();
                if (liveSwitch == null)
                {
                    return NotRunning();
                }

                if (!ulong.TryParse(guildId, out var id))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                return Results.Json(new
                {
                    success = true,
                    settings = Server(id, await liveSwitch.StateAsync(id))
                }, Json);
            });

            // Turns the card on (in channel_id, else the /watchlogs channel) or off.
            group.MapPut("/api/guilds/{guildId}/raid-recap-live", async (HttpContext context, string guildId) =>
            {
                var liveSwitch = deps.ServiceProvider.GetService<RaidRecapLiveSwitch>();
                if (liveSwitch == null)
                {
                    return NotRunning();
                }

                if (!ulong.TryParse(guildId, out var id))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                var body = await ReadAsync<UpdateRaidRecapLiveRequest>(context);
                if (body?.Enabled == null || !ulong.TryParse(body.UserId, out var userId))
                {
                    return Results.BadRequest(new { success = false, error = "enabled and user_id are required" });
                }

                ulong? channel = null;
                if (!string.IsNullOrEmpty(body.ChannelId))
                {
                    if (!ulong.TryParse(body.ChannelId, out var channelId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid channel_id format" });
                    }

                    channel = channelId;
                }

                var name = body.Username ?? "";
                var result = body.Enabled.Value
                    ? await liveSwitch.TurnOnAsync(id, channel, null, userId, name)
                    : await liveSwitch.TurnOffAsync(id, userId, name);
                if (result.Status is not (RaidRecapLiveSwitchStatus.On or RaidRecapLiveSwitchStatus.Off))
                {
                    return Results.Json(new
                    {
                        success = false,
                        reason = Key(result.Status),
                        error = SwitchError(result.Status)
                    }, Json, statusCode: StatusCodes.Status409Conflict);
                }

                return Results.Json(new
                {
                    success = true,
                    settings = Server(id, await liveSwitch.StateAsync(id))
                }, Json);
            });
        }

        private static async Task<object> RolloutAsync(RaidRecapLiveGate gate, RaidRecapLiveSelection selection)
        {
            var mode = await gate.ModeAsync();
            var selected = await selection.SelectedAsync();
            return new
            {
                mode = ModeKey(mode),
                mode_text = RaidRecapLiveStatus.Describe(mode),
                max_selected = RaidRecapLiveSelection.MaxSelect,
                active_window_days = (int)RaidRecapLiveSelection.ActiveWindow.TotalDays,
                selected = selected.Select(s => new
                {
                    server_id = s.ServerId.ToString(),
                    name = s.Name,
                    enabled = s.Enabled,
                    channel_id = s.ChannelId?.ToString(),
                    notice_sent_at = Utc(s.NoticeSentAt),
                    bot_present = s.BotPresent
                })
            };
        }

        private static object Candidate(RaidRecapLiveCandidate c) => new
        {
            server_id = c.ServerId.ToString(),
            name = c.Name,
            logs_posted = c.LogsPosted,
            reports_this_week = c.ReportsThisWeek,
            // A full first page of reports means there may be more.
            more_reports = c.ReportsThisWeek >= RaidRecapLiveCoordinator.DiscoveryReports,
            latest_report = Utc(c.LatestReport),
            status = Key(c.Status),
            status_text = RaidRecapRolloutCommands.Describe(c.Status)
        };

        private static object Result(long serverId, string name, RaidRecapLiveSelectStatus status) => new
        {
            server_id = serverId.ToString(),
            name,
            status = Key(status),
            status_text = RaidRecapRolloutCommands.Describe(status)
        };

        private static object Server(ulong guildId, RaidRecapLiveServerState state) => new
        {
            guild_id = guildId.ToString(),
            available = state.Available,
            enabled = state.On,
            turned_off = state.TurnedOff,
            channel_id = state.ChannelId?.ToString(),
            selected = state.Selected,
            notice_sent_at = Utc(state.NoticeSentAt),
            set_by_name = state.SetByName,
            time_set = Utc(state.TimeSet),
            wow_guild = state.WowGuild,
            log_channel_id = state.LogChannelId?.ToString(),
            live = state.Live == null ? null : new
            {
                zone = state.Live.ZoneName,
                started_at = Utc(state.Live.StartedAt),
                link = state.Live.MessageId > 0
                    ? $"https://discord.com/channels/{guildId}/{state.Live.ChannelId}/{state.Live.MessageId}"
                    : null
            }
        };

        private static string SwitchError(RaidRecapLiveSwitchStatus status) => status switch
        {
            RaidRecapLiveSwitchStatus.NotAvailable => "The live raid recap is in a limited rollout and isn't available in this server yet.",
            RaidRecapLiveSwitchStatus.NoGuild => "This server has no WoW guild set. Link a WoW guild first, then turn the live raid recap on.",
            RaidRecapLiveSwitchStatus.NoChannel => "Pick a channel for the card, or turn on WarcraftLogs auto-posting first.",
            RaidRecapLiveSwitchStatus.CannotPost => "NinjaBot can't keep a card updated in that channel. Give it View Channel, Send Messages and Read Message History there, or pick another channel.",
            _ => "Something went wrong changing the live raid recap."
        };

        /// <summary>The bot owner's ID, when <paramref name="userId"/> is one of the owners.</summary>
        private static async Task<ulong?> OwnerAsync(ApiDependencies deps, string? userId)
        {
            var discord = deps.ServiceProvider.GetService<IRaidRecapLiveDiscord>();
            if (discord == null || !ulong.TryParse(userId, out var id))
            {
                return null;
            }

            return (await discord.OwnerIdsAsync()).Contains(id) ? id : null;
        }

        private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
        {
            try
            {
                return await context.Request.ReadFromJsonAsync<T>();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static RaidRecapRolloutMode? ParseMode(string? mode) => mode switch
        {
            "off" => RaidRecapRolloutMode.Off,
            "mine" => RaidRecapRolloutMode.OwnerServers,
            "selected" => RaidRecapRolloutMode.Selected,
            "everyone" => RaidRecapRolloutMode.Everyone,
            _ => null
        };

        /// <summary>The same keys as the <c>/raid-recap-rollout mode:</c> choices.</summary>
        private static string ModeKey(RaidRecapRolloutMode mode) => mode switch
        {
            RaidRecapRolloutMode.Off => "off",
            RaidRecapRolloutMode.OwnerServers => "mine",
            RaidRecapRolloutMode.Selected => "selected",
            RaidRecapRolloutMode.Everyone => "everyone",
            _ => "unknown"
        };

        private static string Key<T>(T value) where T : struct, Enum =>
            JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

        private static DateTime? Utc(DateTime? at) =>
            at is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;

        private static IResult NotRunning() =>
            Results.NotFound(new { success = false, error = "Live raid recap is not running" });

        private static IResult NotOwner() =>
            Results.Json(new { success = false, error = "Only the bot owner can change the rollout" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
