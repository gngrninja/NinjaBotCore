using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Hosts a minimal Kestrel web server to expose the Commands API.
    /// Allows the web dashboard to fetch live command data.
    /// </summary>
    public class CommandsApiService : IHostedService, IDisposable
    {
        private readonly ILogger<CommandsApiService> _logger;
        private readonly IConfigurationRoot _config;
        private readonly HelpContentProvider _helpProvider;
        private readonly WowUtilities _wowUtilities;
        private readonly IServiceProvider _serviceProvider;
        private WebApplication? _app;
        private Task? _runTask;
        private readonly CancellationTokenSource _cts = new();

        public CommandsApiService(
            ILogger<CommandsApiService> logger,
            IConfigurationRoot config,
            HelpContentProvider helpProvider,
            WowUtilities wowUtilities,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _config = config;
            _helpProvider = helpProvider;
            _wowUtilities = wowUtilities;
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var enabled = _config.GetValue<bool>("CommandsApi:Enabled", false);
            if (!enabled)
            {
                _logger.LogInformation("Commands API is disabled");
                return Task.CompletedTask;
            }

            var port = _config.GetValue<int>("CommandsApi:Port", 5100);
            var host = _config.GetValue<string>("CommandsApi:Host") ?? "0.0.0.0";
            var apiKey = _config.GetValue<string>("CommandsApi:ApiKey") ?? "";

            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = new[] { "--urls", $"http://{host}:{port}" }
                });

                // Suppress noisy Kestrel logs
                builder.Logging.SetMinimumLevel(LogLevel.Warning);

                _app = builder.Build();

                // Health check endpoint (no auth required)
                _app.MapGet("/api/commands/health", () => Results.Ok(new
                {
                    status = "healthy",
                    timestamp = DateTime.UtcNow
                }));

                // Commands endpoint (requires API key)
                _app.MapGet("/api/commands", (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            _logger.LogWarning("Commands API request with invalid API key from {IP}",
                                context.Connection.RemoteIpAddress);
                            return Results.Unauthorized();
                        }
                    }

                    var content = _helpProvider.GetHelpContent();
                    if (content == null)
                    {
                        return Results.NotFound(new { error = "Help content not available" });
                    }

                    _logger.LogDebug("Commands API request served successfully");

                    return Results.Json(content, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                        WriteIndented = false
                    });
                });

                // Regenerate endpoint (requires API key, triggers refresh)
                _app.MapPost("/api/commands/regenerate", (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            return Results.Unauthorized();
                        }
                    }

                    _helpProvider.RegenerateHelpContent();

                    var content = _helpProvider.GetHelpContent();
                    return Results.Ok(new
                    {
                        success = true,
                        commands = content?.Metadata?.TotalCommands ?? 0,
                        categories = content?.Categories?.Count ?? 0,
                        last_updated = content?.Metadata?.LastUpdated
                    });
                });

                // Refresh guild roster endpoint (requires API key)
                _app.MapPost("/api/guilds/refresh-roster", async (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            _logger.LogWarning("Roster refresh request with invalid API key from {IP}",
                                context.Connection.RemoteIpAddress);
                            return Results.Unauthorized();
                        }
                    }

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
                    using var scope = _serviceProvider.CreateScope();
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
                        await _wowUtilities.RefreshGuildRosterAsync(guildObject, context.RequestAborted);

                        // Get member count for response
                        var count = await db.WowGuildRosterMembers
                            .CountAsync(m => m.GuildName == association.WowGuild
                                && m.GuildRealmSlug == association.LocalRealmSlug
                                && m.Region == association.WowRegion,
                                context.RequestAborted);

                        _logger.LogInformation("Roster refreshed for guild {Guild} on {Realm}: {Count} members",
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
                        _logger.LogError(ex, "Failed to refresh roster for guild {Guild}", association.WowGuild);
                        return Results.Problem($"Failed to refresh roster: {ex.Message}");
                    }
                });

                // Add character via API (for web dashboard)
                _app.MapPost("/api/characters/add", async (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            _logger.LogWarning("Add character request with invalid API key from {IP}",
                                context.Connection.RemoteIpAddress);
                            return Results.Unauthorized();
                        }
                    }

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

                    using var scope = _serviceProvider.CreateScope();
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
                        Locale = locale,
                        IsMain = false,
                        TimeSet = DateTime.UtcNow
                    };

                    db.WowCharAssociation.Add(character);
                    await db.SaveChangesAsync(context.RequestAborted);

                    _logger.LogInformation("Character added via API: {CharName} on {Realm} for user {UserId} server {ServerId}",
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

                // Start the web server in the background
                _runTask = Task.Run(async () =>
                {
                    try
                    {
                        await _app.RunAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Commands API server error");
                    }
                }, cancellationToken);

                _logger.LogInformation("Commands API started on http://{Host}:{Port}", host, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Commands API");
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_app == null) return;

            _logger.LogInformation("Stopping Commands API...");
            _cts.Cancel();

            try
            {
                await _app.StopAsync(cancellationToken);
                if (_runTask != null)
                {
                    await _runTask;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Commands API");
            }

            _logger.LogInformation("Commands API stopped");
        }

        public void Dispose()
        {
            _cts.Dispose();
            (_app as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Request body for the refresh-roster endpoint.
    /// </summary>
    public record RefreshRosterRequest(string DiscordGuildId);

    /// <summary>
    /// Request body for the add-character endpoint.
    /// </summary>
    public record AddCharacterRequest(
        string DiscordUserId,
        string? DiscordServerId,
        string CharacterName,
        string Realm,
        string? Region
    );
}
