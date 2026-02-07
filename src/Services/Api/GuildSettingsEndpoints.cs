using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services.Api
{
    public static class GuildSettingsEndpoints
    {
        public static void MapGuildSettingsEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/guilds/{guildId}/log-monitoring - Get log monitoring settings for a guild
            group.MapGet("/api/guilds/{guildId}/log-monitoring", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.LogMonitoring
                    .FirstOrDefaultAsync(s => s.ServerId == guildIdLong);

                if (settings == null)
                {
                    // Return defaults
                    return Results.Json(new
                    {
                        success = true,
                        settings = new
                        {
                            guild_id = guildId,
                            channel_id = (string?)null,
                            channel_name = (string?)null,
                            monitor_logs = false,
                            latest_log_retail = (DateTime?)null
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
                        guild_id = settings.ServerId.ToString(),
                        channel_id = settings.ChannelId > 0 ? settings.ChannelId.ToString() : null,
                        channel_name = settings.ChannelName,
                        monitor_logs = settings.MonitorLogs,
                        latest_log_retail = settings.LatestLogRetail
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // PUT /api/guilds/{guildId}/log-monitoring - Update log monitoring settings for a guild
            group.MapPut("/api/guilds/{guildId}/log-monitoring", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateLogMonitoringRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.LogMonitoring
                    .FirstOrDefaultAsync(s => s.ServerId == guildIdLong);

                if (settings == null)
                {
                    // Create new
                    settings = new LogMonitoring
                    {
                        ServerId = guildIdLong,
                        ServerName = body.ServerName ?? "",
                        ChannelId = 0,
                        ChannelName = "",
                        MonitorLogs = false,
                        WatchLog = false
                    };
                    db.LogMonitoring.Add(settings);
                }

                // Update settings
                if (body.ChannelId != null)
                {
                    if (body.ChannelId == "")
                    {
                        settings.ChannelId = 0;
                        settings.ChannelName = "";
                    }
                    else if (long.TryParse(body.ChannelId, out var channelId))
                    {
                        settings.ChannelId = channelId;
                        settings.ChannelName = body.ChannelName ?? "";
                    }
                }

                if (body.MonitorLogs.HasValue)
                {
                    settings.MonitorLogs = body.MonitorLogs.Value;

                    // If enabling and no LatestLogRetail, set it to now
                    if (body.MonitorLogs.Value && !settings.LatestLogRetail.HasValue)
                    {
                        settings.LatestLogRetail = DateTime.UtcNow;
                    }
                }

                if (!string.IsNullOrEmpty(body.ServerName))
                {
                    settings.ServerName = body.ServerName;
                }

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    message = "Log monitoring settings updated",
                    settings = new
                    {
                        guild_id = settings.ServerId.ToString(),
                        channel_id = settings.ChannelId > 0 ? settings.ChannelId.ToString() : null,
                        channel_name = settings.ChannelName,
                        monitor_logs = settings.MonitorLogs,
                        latest_log_retail = settings.LatestLogRetail
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/guilds/{guildId}/greeting-settings - Get greeting settings for a guild
            group.MapGet("/api/guilds/{guildId}/greeting-settings", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ServerGreetings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                // Return defaults if no settings exist
                if (settings == null)
                {
                    return Results.Json(new
                    {
                        success = true,
                        settings = new
                        {
                            guild_id = guildId,
                            greet_users = false,
                            part_users = false,
                            greeting = (string?)null,
                            greeting_channel_id = (string?)null,
                            greeting_channel_name = (string?)null,
                            parting_message = (string?)null,
                            parting_channel_id = (string?)null,
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
                        greet_users = settings.GreetUsers ?? false,
                        part_users = settings.PartUsers ?? false,
                        greeting = settings.Greeting,
                        greeting_channel_id = settings.GreetingChannelId?.ToString(),
                        greeting_channel_name = settings.GreetingChannelName,
                        parting_message = settings.PartingMessage,
                        parting_channel_id = settings.PartingChannelId?.ToString(),
                        set_by_id = settings.SetById?.ToString(),
                        set_by_name = settings.SetByName,
                        time_set = settings.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // PUT /api/guilds/{guildId}/greeting-settings - Update greeting settings for a guild
            group.MapPut("/api/guilds/{guildId}/greeting-settings", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateGreetingSettingsRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ServerGreetings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                if (settings == null)
                {
                    // Create new settings
                    settings = new Database.ServerGreeting
                    {
                        DiscordGuildId = guildIdLong
                    };
                    db.ServerGreetings.Add(settings);
                }

                // Update fields
                if (body.GreetUsers.HasValue)
                    settings.GreetUsers = body.GreetUsers.Value;

                if (body.PartUsers.HasValue)
                    settings.PartUsers = body.PartUsers.Value;

                if (body.Greeting != null)
                    settings.Greeting = body.Greeting;

                if (body.GreetingChannelId != null)
                {
                    if (string.IsNullOrEmpty(body.GreetingChannelId))
                        settings.GreetingChannelId = null;
                    else if (long.TryParse(body.GreetingChannelId, out var channelId))
                        settings.GreetingChannelId = channelId;
                }

                if (body.GreetingChannelName != null)
                    settings.GreetingChannelName = body.GreetingChannelName;

                if (body.PartingMessage != null)
                    settings.PartingMessage = body.PartingMessage;

                if (body.PartingChannelId != null)
                {
                    if (string.IsNullOrEmpty(body.PartingChannelId))
                        settings.PartingChannelId = null;
                    else if (long.TryParse(body.PartingChannelId, out var channelId))
                        settings.PartingChannelId = channelId;
                }

                // Track who made the change
                if (!string.IsNullOrEmpty(body.SetById) && long.TryParse(body.SetById, out var setById))
                    settings.SetById = setById;

                if (!string.IsNullOrEmpty(body.SetByName))
                    settings.SetByName = body.SetByName;

                settings.TimeSet = DateTime.UtcNow;

                await db.SaveChangesAsync();

                // Invalidate greeting cache so bot picks up changes immediately
                var greetingCache = scope.ServiceProvider.GetRequiredService<WowCacheService>();
                greetingCache.InvalidateServerGreeting(guildIdLong);

                return Results.Json(new
                {
                    success = true,
                    settings = new
                    {
                        guild_id = settings.DiscordGuildId.ToString(),
                        greet_users = settings.GreetUsers ?? false,
                        part_users = settings.PartUsers ?? false,
                        greeting = settings.Greeting,
                        greeting_channel_id = settings.GreetingChannelId?.ToString(),
                        greeting_channel_name = settings.GreetingChannelName,
                        parting_message = settings.PartingMessage,
                        parting_channel_id = settings.PartingChannelId?.ToString(),
                        set_by_id = settings.SetById?.ToString(),
                        set_by_name = settings.SetByName,
                        time_set = settings.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/guilds/{guildId}/moderation-watcher - Get moderation watcher settings for a guild
            group.MapGet("/api/guilds/{guildId}/moderation-watcher", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ModerationWatcher
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                // Return defaults if no settings exist
                if (settings == null)
                {
                    return Results.Json(new
                    {
                        success = true,
                        settings = new
                        {
                            guild_id = guildId,
                            channel_id = (string?)null,
                            channel_name = (string?)null,
                            watch_voice = false,
                            watch_messages = false,
                            watch_roles = false,
                            watch_bans = false,
                            watch_nicknames = false,
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
                        channel_id = settings.ChannelId?.ToString(),
                        channel_name = settings.ChannelName,
                        watch_voice = settings.WatchVoice ?? false,
                        watch_messages = settings.WatchMessages ?? false,
                        watch_roles = settings.WatchRoles ?? false,
                        watch_bans = settings.WatchBans ?? false,
                        watch_nicknames = settings.WatchNicknames ?? false,
                        set_by_id = settings.SetById?.ToString(),
                        set_by_name = settings.SetByName,
                        time_set = settings.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // PUT /api/guilds/{guildId}/moderation-watcher - Update moderation watcher settings for a guild
            group.MapPut("/api/guilds/{guildId}/moderation-watcher", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateModerationWatcherRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ModerationWatcher
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                if (settings == null)
                {
                    // Create new settings
                    settings = new Database.ModerationWatcher
                    {
                        DiscordGuildId = guildIdLong
                    };
                    db.ModerationWatcher.Add(settings);
                }

                // Update fields
                if (body.ChannelId != null)
                {
                    if (string.IsNullOrEmpty(body.ChannelId))
                        settings.ChannelId = null;
                    else if (long.TryParse(body.ChannelId, out var channelId))
                        settings.ChannelId = channelId;
                }

                if (body.ChannelName != null)
                    settings.ChannelName = body.ChannelName;

                if (body.WatchVoice.HasValue)
                    settings.WatchVoice = body.WatchVoice.Value;

                if (body.WatchMessages.HasValue)
                    settings.WatchMessages = body.WatchMessages.Value;

                if (body.WatchRoles.HasValue)
                    settings.WatchRoles = body.WatchRoles.Value;

                if (body.WatchBans.HasValue)
                    settings.WatchBans = body.WatchBans.Value;

                if (body.WatchNicknames.HasValue)
                    settings.WatchNicknames = body.WatchNicknames.Value;

                // Track who made the change
                if (!string.IsNullOrEmpty(body.SetById) && long.TryParse(body.SetById, out var setById))
                    settings.SetById = setById;

                if (!string.IsNullOrEmpty(body.SetByName))
                    settings.SetByName = body.SetByName;

                settings.TimeSet = DateTime.UtcNow;

                await db.SaveChangesAsync();

                // Invalidate cache so changes take effect immediately
                var watcherService = scope.ServiceProvider.GetService<ModerationWatcherService>();
                watcherService?.InvalidateSettingsCache(guildIdLong);

                return Results.Json(new
                {
                    success = true,
                    settings = new
                    {
                        guild_id = settings.DiscordGuildId.ToString(),
                        channel_id = settings.ChannelId?.ToString(),
                        channel_name = settings.ChannelName,
                        watch_voice = settings.WatchVoice ?? false,
                        watch_messages = settings.WatchMessages ?? false,
                        watch_roles = settings.WatchRoles ?? false,
                        watch_bans = settings.WatchBans ?? false,
                        watch_nicknames = settings.WatchNicknames ?? false,
                        set_by_id = settings.SetById?.ToString(),
                        set_by_name = settings.SetByName,
                        time_set = settings.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/guilds/{guildId}/wow-association - Get WoW guild association for a Discord server
            group.MapGet("/api/guilds/{guildId}/wow-association", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var assoc = await db.WowGuildAssociations
                    .FirstOrDefaultAsync(a => a.ServerId == guildIdLong);

                if (assoc == null)
                {
                    return Results.Json(new
                    {
                        success = true,
                        association = (object?)null
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                }

                return Results.Json(new
                {
                    success = true,
                    association = new
                    {
                        guild_id = assoc.ServerId?.ToString(),
                        wow_guild_name = assoc.WowGuild,
                        wow_realm = assoc.WowRealm,
                        wow_realm_slug = assoc.LocalRealmSlug,
                        wow_region = assoc.WowRegion,
                        locale = assoc.Locale,
                        set_by_id = assoc.SetById?.ToString(),
                        set_by_name = assoc.SetBy,
                        time_set = assoc.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // PUT /api/guilds/{guildId}/wow-association - Set WoW guild association for a Discord server
            group.MapPut("/api/guilds/{guildId}/wow-association", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                }

                var body = await context.Request.ReadFromJsonAsync<UpdateWowAssociationRequest>(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                if (body == null)
                {
                    return Results.BadRequest(new { success = false, error = "Invalid request body" });
                }

                // Validate required fields
                if (string.IsNullOrWhiteSpace(body.WowGuildName) ||
                    string.IsNullOrWhiteSpace(body.WowRealm) ||
                    string.IsNullOrWhiteSpace(body.WowRegion))
                {
                    return Results.BadRequest(new { success = false, error = "wow_guild_name, wow_realm, and wow_region are required" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var assoc = await db.WowGuildAssociations
                    .FirstOrDefaultAsync(a => a.ServerId == guildIdLong);

                if (assoc == null)
                {
                    // Create new association
                    assoc = new Database.WowGuildAssociations
                    {
                        ServerId = guildIdLong
                    };
                    db.WowGuildAssociations.Add(assoc);
                }

                // Update fields
                assoc.WowGuild = body.WowGuildName;
                assoc.WowRealm = body.WowRealm;
                assoc.LocalRealmSlug = body.WowRealmSlug ?? "";
                assoc.WowRegion = body.WowRegion;
                assoc.Locale = body.Locale ?? "en_US";
                assoc.ServerName = body.ServerName ?? "";

                // Track who made the change
                if (!string.IsNullOrEmpty(body.SetById) && long.TryParse(body.SetById, out var setById))
                    assoc.SetById = setById;

                if (!string.IsNullOrEmpty(body.SetByName))
                    assoc.SetBy = body.SetByName;

                assoc.TimeSet = DateTime.UtcNow;

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    association = new
                    {
                        guild_id = assoc.ServerId?.ToString(),
                        wow_guild_name = assoc.WowGuild,
                        wow_realm = assoc.WowRealm,
                        wow_realm_slug = assoc.LocalRealmSlug,
                        wow_region = assoc.WowRegion,
                        locale = assoc.Locale,
                        set_by_id = assoc.SetById?.ToString(),
                        set_by_name = assoc.SetBy,
                        time_set = assoc.TimeSet
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });
        }
    }
}
