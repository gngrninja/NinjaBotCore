using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
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

                // ==== Poll API Endpoints ====

                // GET /api/polls - List polls for a guild
                _app.MapGet("/api/polls", async (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            _logger.LogWarning("Poll API request with invalid API key from {IP}",
                                context.Connection.RemoteIpAddress);
                            return Results.Unauthorized();
                        }
                    }

                    var guildIdStr = context.Request.Query["guild_id"].ToString();
                    if (string.IsNullOrEmpty(guildIdStr) || !long.TryParse(guildIdStr, out var guildId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id parameter" });
                    }

                    var page = int.TryParse(context.Request.Query["page"], out var p) ? p : 1;
                    var pageSize = int.TryParse(context.Request.Query["page_size"], out var ps) ? Math.Min(ps, 100) : 20;

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var totalCount = await db.Polls.CountAsync(poll => poll.GuildId == guildId);
                    var polls = await db.Polls
                        .Where(poll => poll.GuildId == guildId)
                        .OrderByDescending(poll => poll.CreatedAt)
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(poll => new
                        {
                            id = poll.Id,
                            question = poll.Question,
                            poll_type = poll.PollType,
                            is_closed = poll.IsClosed,
                            vote_count = poll.PollVotes.Count,
                            created_by = poll.CreatedByName,
                            created_at = poll.CreatedAt,
                            expires_at = poll.ExpiresAt
                        })
                        .ToListAsync();

                    return Results.Json(new
                    {
                        success = true,
                        polls,
                        total_count = totalCount,
                        page,
                        page_size = pageSize
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // GET /api/polls/{pollId} - Get specific poll with full details
                _app.MapGet("/api/polls/{pollId:long}", async (HttpContext context, long pollId) =>
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

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var poll = await db.Polls
                        .Include(p => p.PollOptions)
                        .Include(p => p.PollVotes)
                        .AsSplitQuery() // Split query to avoid EF Core warning for multiple collection includes
                        .FirstOrDefaultAsync(p => p.Id == pollId);

                    if (poll == null)
                    {
                        return Results.NotFound(new { success = false, error = "Poll not found" });
                    }

                    var totalVotes = poll.PollVotes.Count;
                    var options = poll.PollOptions.OrderBy(o => o.DisplayOrder).Select(option =>
                    {
                        var voteCount = poll.PollVotes.Count(v => v.OptionId == option.Id);
                        var percentage = totalVotes > 0 ? (double)voteCount / totalVotes * 100 : 0;

                        return new
                        {
                            id = option.Id,
                            option_text = option.OptionText,
                            vote_count = voteCount,
                            percentage
                        };
                    }).ToList();

                    return Results.Json(new
                    {
                        success = true,
                        poll = new
                        {
                            id = poll.Id,
                            question = poll.Question,
                            poll_type = poll.PollType,
                            allow_vote_change = poll.AllowVoteChange,
                            is_anonymous = poll.IsAnonymous,
                            is_closed = poll.IsClosed,
                            created_at = poll.CreatedAt,
                            expires_at = poll.ExpiresAt,
                            created_by_id = poll.CreatedById.ToString(),
                            created_by_name = poll.CreatedByName,
                            guild_id = poll.GuildId.ToString(),
                            options,
                            total_votes = totalVotes
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // POST /api/polls/{pollId}/vote - Cast a vote
                _app.MapPost("/api/polls/{pollId:long}/vote", async (HttpContext context, long pollId) =>
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

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var body = await context.Request.ReadFromJsonAsync<VotePollRequest>();
                    if (body == null || body.UserId == null || body.OptionId == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    if (!long.TryParse(body.UserId, out var userId) || !long.TryParse(body.OptionId, out var optionId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user_id or option_id" });
                    }

                    var poll = await db.Polls
                        .Include(p => p.PollOptions)
                        .FirstOrDefaultAsync(p => p.Id == pollId);

                    if (poll == null)
                    {
                        return Results.NotFound(new { success = false, error = "Poll not found" });
                    }

                    if (poll.IsClosed)
                    {
                        return Results.BadRequest(new { success = false, error = "Poll is closed" });
                    }

                    if (poll.ExpiresAt.HasValue && DateTime.UtcNow > poll.ExpiresAt.Value)
                    {
                        return Results.BadRequest(new { success = false, error = "Poll has expired" });
                    }

                    var option = poll.PollOptions.FirstOrDefault(o => o.Id == optionId);
                    if (option == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid option_id" });
                    }

                    // Check existing votes
                    var existingVotes = await db.PollVotes
                        .Where(v => v.PollId == pollId && v.UserId == userId)
                        .ToListAsync();

                    if (poll.PollType == "SingleChoice" || poll.PollType == "YesNo")
                    {
                        if (existingVotes.Any() && !poll.AllowVoteChange)
                        {
                            return Results.BadRequest(new { success = false, error = "Vote already cast and changes not allowed" });
                        }

                        // Remove old votes
                        if (existingVotes.Any())
                        {
                            db.PollVotes.RemoveRange(existingVotes);
                        }
                    }
                    else if (poll.PollType == "MultipleChoice")
                    {
                        var existingVote = existingVotes.FirstOrDefault(v => v.OptionId == optionId);
                        if (existingVote != null)
                        {
                            // Toggle off
                            db.PollVotes.Remove(existingVote);
                            await db.SaveChangesAsync();

                            var remainingVotes = await db.PollVotes.CountAsync(v => v.PollId == pollId && v.OptionId == optionId);
                            var totalVotes = await db.PollVotes.CountAsync(v => v.PollId == pollId);
                            var percentage = totalVotes > 0 ? (double)remainingVotes / totalVotes * 100 : 0;

                            return Results.Json(new
                            {
                                success = true,
                                message = "Vote removed",
                                current_results = new
                                {
                                    option_id = optionId,
                                    vote_count = remainingVotes,
                                    percentage
                                }
                            }, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                            });
                        }
                    }

                    // Add new vote
                    var newVote = new PollVote
                    {
                        PollId = pollId,
                        OptionId = optionId,
                        UserId = userId,
                        UserName = body.UserName ?? "API User",
                        VotedAt = DateTime.UtcNow
                    };

                    db.PollVotes.Add(newVote);
                    await db.SaveChangesAsync();

                    var optionVotes = await db.PollVotes.CountAsync(v => v.PollId == pollId && v.OptionId == optionId);
                    var pollTotalVotes = await db.PollVotes.CountAsync(v => v.PollId == pollId);
                    var votePercentage = pollTotalVotes > 0 ? (double)optionVotes / pollTotalVotes * 100 : 0;

                    return Results.Json(new
                    {
                        success = true,
                        message = "Vote recorded",
                        current_results = new
                        {
                            option_id = optionId,
                            vote_count = optionVotes,
                            percentage = votePercentage
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // POST /api/polls/{pollId}/close - Close a poll
                _app.MapPost("/api/polls/{pollId:long}/close", async (HttpContext context, long pollId) =>
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

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var body = await context.Request.ReadFromJsonAsync<ClosePollRequest>();
                    if (body == null || body.UserId == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    if (!long.TryParse(body.UserId, out var userId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user_id" });
                    }

                    var poll = await db.Polls
                        .Include(p => p.PollOptions)
                        .Include(p => p.PollVotes)
                        .AsSplitQuery() // Split query to avoid EF Core warning for multiple collection includes
                        .FirstOrDefaultAsync(p => p.Id == pollId);

                    if (poll == null)
                    {
                        return Results.NotFound(new { success = false, error = "Poll not found" });
                    }

                    if (poll.IsClosed)
                    {
                        return Results.BadRequest(new { success = false, error = "Poll is already closed" });
                    }

                    // Check permissions - only creator can close via API
                    if (poll.CreatedById != userId)
                    {
                        return Results.Forbid();
                    }

                    poll.IsClosed = true;
                    poll.ClosedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    var totalVotes = poll.PollVotes.Count;
                    var results = poll.PollOptions.OrderBy(o => o.DisplayOrder).Select(option =>
                    {
                        var voteCount = poll.PollVotes.Count(v => v.OptionId == option.Id);
                        var percentage = totalVotes > 0 ? (double)voteCount / totalVotes * 100 : 0;

                        return new
                        {
                            option_id = option.Id,
                            option_text = option.OptionText,
                            vote_count = voteCount,
                            percentage
                        };
                    }).ToList();

                    // Post results to Discord
                    try
                    {
                        var client = _serviceProvider.GetService<DiscordShardedClient>();
                        var guild = client?.GetGuild((ulong)poll.GuildId);
                        var channel = guild?.GetTextChannel((ulong)poll.ChannelId);

                        if (channel != null)
                        {
                            // Update original poll message (change color to red, disable buttons)
                            if (poll.MessageId > 0)
                            {
                                var message = await channel.GetMessageAsync((ulong)poll.MessageId);
                                if (message is IUserMessage userMessage)
                                {
                                    var closedEmbed = BuildClosedPollEmbed(poll, totalVotes);
                                    var disabledComponents = new ComponentBuilder()
                                        .WithButton("Poll Closed", "poll_closed", ButtonStyle.Secondary, disabled: true);

                                    await userMessage.ModifyAsync(msg =>
                                    {
                                        msg.Embed = closedEmbed.Build();
                                        msg.Components = disabledComponents.Build();
                                    });
                                }
                            }

                            // Post a notification with results
                            var notificationEmbed = new EmbedBuilder()
                                .WithColor(Color.Orange)
                                .WithTitle("📊 Poll Closed")
                                .WithDescription($"The poll **\"{poll.Question}\"** has been closed by {poll.CreatedByName}.")
                                .WithFooter($"Poll ID: {poll.Id} • {totalVotes} total votes")
                                .WithTimestamp(DateTimeOffset.UtcNow)
                                .Build();

                            await channel.SendMessageAsync(embed: notificationEmbed);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update Discord for closed poll {PollId}", poll.Id);
                        // Don't fail the API call - poll is already closed in DB
                    }

                    return Results.Json(new
                    {
                        success = true,
                        message = "Poll closed",
                        final_results = new
                        {
                            total_votes = totalVotes,
                            options = results
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // GET /api/polls/{pollId}/results - Get detailed results
                _app.MapGet("/api/polls/{pollId:long}/results", async (HttpContext context, long pollId) =>
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

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var poll = await db.Polls
                        .Include(p => p.PollOptions)
                        .Include(p => p.PollVotes)
                        .AsSplitQuery() // Split query to avoid EF Core warning for multiple collection includes
                        .FirstOrDefaultAsync(p => p.Id == pollId);

                    if (poll == null)
                    {
                        return Results.NotFound(new { success = false, error = "Poll not found" });
                    }

                    var totalVotes = poll.PollVotes.Count;
                    var results = poll.PollOptions.OrderBy(o => o.DisplayOrder).Select(option =>
                    {
                        var optionVotes = poll.PollVotes.Where(v => v.OptionId == option.Id).ToList();
                        var voteCount = optionVotes.Count;
                        var percentage = totalVotes > 0 ? (double)voteCount / totalVotes * 100 : 0;

                        var resultObj = new
                        {
                            option_id = option.Id,
                            option_text = option.OptionText,
                            vote_count = voteCount,
                            percentage,
                            voters = poll.IsAnonymous ? null : (object)optionVotes.Select(v => v.UserName).ToList()
                        };

                        return resultObj;
                    }).ToList();

                    return Results.Json(new
                    {
                        success = true,
                        poll = new
                        {
                            question = poll.Question,
                            is_closed = poll.IsClosed,
                            total_votes = totalVotes
                        },
                        results
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // DELETE /api/polls/{pollId} - Delete a poll (admin only)
                _app.MapDelete("/api/polls/{pollId:long}", async (HttpContext context, long pollId) =>
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

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var body = await context.Request.ReadFromJsonAsync<DeletePollRequest>();
                    if (body == null || string.IsNullOrEmpty(body.UserId) || string.IsNullOrEmpty(body.GuildId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    if (!long.TryParse(body.UserId, out var userId) || !long.TryParse(body.GuildId, out var guildId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user_id or guild_id" });
                    }

                    var poll = await db.Polls
                        .FirstOrDefaultAsync(p => p.Id == pollId);

                    if (poll == null)
                    {
                        return Results.NotFound(new { success = false, error = "Poll not found" });
                    }

                    // Verify poll belongs to this guild
                    if (poll.GuildId != guildId)
                    {
                        return Results.Forbid();
                    }

                    // Check admin permission via Discord API
                    var discordClient = scope.ServiceProvider.GetRequiredService<DiscordShardedClient>();
                    try
                    {
                        var guild = discordClient.GetGuild((ulong)guildId);
                        if (guild == null)
                        {
                            return Results.Json(new { success = false, error = "Guild not found" },
                                statusCode: 404);
                        }

                        var guildUser = guild.GetUser((ulong)userId);
                        if (guildUser == null)
                        {
                            return Results.Json(new { success = false, error = "User not found in guild" },
                                statusCode: 403);
                        }

                        // Check for Administrator permission or owner
                        var isOwner = guild.OwnerId == (ulong)userId;
                        var isAdmin = guildUser.GuildPermissions.Administrator;

                        if (!isOwner && !isAdmin)
                        {
                            return Results.Json(new { success = false, error = "Only server admins can delete polls" },
                                statusCode: 403);
                        }
                    }
                    catch
                    {
                        // If we can't verify permissions, deny access
                        return Results.Json(new { success = false, error = "Could not verify permissions" },
                            statusCode: 403);
                    }

                    // Delete votes first, then options, then poll
                    var votes = await db.PollVotes.Where(v => v.PollId == pollId).ToListAsync();
                    db.PollVotes.RemoveRange(votes);

                    var options = await db.PollOptions.Where(o => o.PollId == pollId).ToListAsync();
                    db.PollOptions.RemoveRange(options);

                    db.Polls.Remove(poll);
                    await db.SaveChangesAsync();

                    return Results.Json(new { success = true, message = "Poll deleted" }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // POST /api/guilds/{guildId}/polls/cleanup - Delete all closed/expired polls (admin only)
                _app.MapPost("/api/guilds/{guildId}/polls/cleanup", async (HttpContext context, string guildId) =>
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

                    if (!long.TryParse(guildId, out var guildIdLong))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var body = await context.Request.ReadFromJsonAsync<CleanupPollsRequest>();
                    if (body == null || string.IsNullOrEmpty(body.UserId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    if (!long.TryParse(body.UserId, out var userId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user_id" });
                    }

                    // Check admin permission via Discord API
                    var discordClient = scope.ServiceProvider.GetRequiredService<DiscordShardedClient>();
                    try
                    {
                        var guild = discordClient.GetGuild((ulong)guildIdLong);
                        if (guild == null)
                        {
                            return Results.Json(new { success = false, error = "Guild not found" },
                                statusCode: 404);
                        }

                        var guildUser = guild.GetUser((ulong)userId);
                        if (guildUser == null)
                        {
                            return Results.Json(new { success = false, error = "User not found in guild" },
                                statusCode: 403);
                        }

                        var isOwner = guild.OwnerId == (ulong)userId;
                        var isAdmin = guildUser.GuildPermissions.Administrator;

                        if (!isOwner && !isAdmin)
                        {
                            return Results.Json(new { success = false, error = "Only server admins can cleanup polls" },
                                statusCode: 403);
                        }
                    }
                    catch
                    {
                        return Results.Json(new { success = false, error = "Could not verify permissions" },
                            statusCode: 403);
                    }

                    // Find closed or expired polls for this guild
                    var now = DateTime.UtcNow;
                    var pollsToDelete = await db.Polls
                        .Where(p => p.GuildId == guildIdLong &&
                                    (p.IsClosed || (p.ExpiresAt.HasValue && p.ExpiresAt.Value <= now)))
                        .ToListAsync();

                    if (!pollsToDelete.Any())
                    {
                        return Results.Json(new { success = true, deleted_count = 0, message = "No polls to cleanup" },
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }

                    var pollIds = pollsToDelete.Select(p => p.Id).ToList();

                    // Delete votes first
                    var votes = await db.PollVotes.Where(v => pollIds.Contains(v.PollId)).ToListAsync();
                    db.PollVotes.RemoveRange(votes);

                    // Delete options
                    var options = await db.PollOptions.Where(o => pollIds.Contains(o.PollId)).ToListAsync();
                    db.PollOptions.RemoveRange(options);

                    // Delete polls
                    db.Polls.RemoveRange(pollsToDelete);
                    await db.SaveChangesAsync();

                    return Results.Json(new
                    {
                        success = true,
                        deleted_count = pollsToDelete.Count,
                        message = $"Deleted {pollsToDelete.Count} poll(s)"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // POST /api/polls/create - Create a new poll and post to Discord
                _app.MapPost("/api/polls/create", async (HttpContext context) =>
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

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var body = await context.Request.ReadFromJsonAsync<CreatePollRequest>(
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    // Validate required fields
                    if (string.IsNullOrWhiteSpace(body.Question))
                    {
                        return Results.BadRequest(new { success = false, error = "Question is required" });
                    }

                    if (body.Question.Length > 200)
                    {
                        return Results.BadRequest(new { success = false, error = "Question must be 200 characters or less" });
                    }

                    if (string.IsNullOrEmpty(body.GuildId) || string.IsNullOrEmpty(body.ChannelId) || string.IsNullOrEmpty(body.UserId))
                    {
                        return Results.BadRequest(new { success = false, error = "guild_id, channel_id, and user_id are required" });
                    }

                    if (!long.TryParse(body.GuildId, out var guildId) ||
                        !long.TryParse(body.ChannelId, out var channelId) ||
                        !long.TryParse(body.UserId, out var userId))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id, channel_id, or user_id" });
                    }

                    // Check admin permission via Discord API
                    var discordClient = scope.ServiceProvider.GetRequiredService<DiscordShardedClient>();
                    try
                    {
                        var guild = discordClient.GetGuild((ulong)guildId);
                        if (guild == null)
                        {
                            return Results.Json(new { success = false, error = "Guild not found" },
                                statusCode: 404);
                        }

                        var guildUser = guild.GetUser((ulong)userId);
                        if (guildUser == null)
                        {
                            return Results.Json(new { success = false, error = "User not found in guild" },
                                statusCode: 403);
                        }

                        // Check for Administrator or ManageMessages permission
                        var isOwner = guild.OwnerId == (ulong)userId;
                        var isAdmin = guildUser.GuildPermissions.Administrator;
                        var canManageMessages = guildUser.GuildPermissions.ManageMessages;

                        if (!isOwner && !isAdmin && !canManageMessages)
                        {
                            return Results.Json(new { success = false, error = "You need Administrator or Manage Messages permission to create polls" },
                                statusCode: 403);
                        }

                        // Verify channel exists and is accessible
                        var channel = guild.GetChannel((ulong)channelId) as ISocketMessageChannel;
                        if (channel == null)
                        {
                            return Results.Json(new { success = false, error = "Channel not found or not a text channel" },
                                statusCode: 404);
                        }

                        // Parse options
                        List<string> options;
                        string pollType;

                        if (body.Options == null || body.Options.Count == 0)
                        {
                            // Default to Yes/No poll
                            options = new List<string> { "Yes", "No" };
                            pollType = "YesNo";
                        }
                        else
                        {
                            options = body.Options
                                .Where(o => !string.IsNullOrWhiteSpace(o))
                                .Select(o => o.Trim())
                                .Take(25)
                                .ToList();

                            if (options.Count < 2)
                            {
                                return Results.BadRequest(new { success = false, error = "Poll must have at least 2 options" });
                            }

                            pollType = "SingleChoice";
                        }

                        // Parse duration
                        DateTime? expiresAt = null;
                        if (!string.IsNullOrWhiteSpace(body.Duration))
                        {
                            expiresAt = ParsePollDuration(body.Duration);
                            if (!expiresAt.HasValue)
                            {
                                return Results.BadRequest(new { success = false, error = "Invalid duration format. Use: 1h, 12h, 24h, 1d, 3d, 7d, 1w" });
                            }
                        }

                        // Create poll in database
                        var newPoll = new Database.Poll
                        {
                            Question = body.Question.Trim(),
                            PollType = pollType,
                            AllowVoteChange = body.AllowVoteChange ?? true,
                            IsAnonymous = false,
                            IsClosed = false,
                            CreatedAt = DateTime.UtcNow,
                            ExpiresAt = expiresAt,
                            CreatedById = userId,
                            CreatedByName = guildUser.Username,
                            GuildId = guildId,
                            ChannelId = channelId,
                            MessageId = 0
                        };

                        db.Polls.Add(newPoll);
                        await db.SaveChangesAsync();

                        // Add options
                        var emotes = new[] { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟",
                                            "🇦", "🇧", "🇨", "🇩", "🇪", "🇫", "🇬", "🇭", "🇮", "🇯" };

                        for (int i = 0; i < options.Count; i++)
                        {
                            var option = new PollOption
                            {
                                PollId = newPoll.Id,
                                OptionText = options[i],
                                DisplayOrder = i,
                                Emote = i < emotes.Length ? emotes[i] : "▪️"
                            };
                            db.PollOptions.Add(option);
                        }
                        await db.SaveChangesAsync();

                        // Reload poll with options
                        var poll = await db.Polls
                            .Include(p => p.PollOptions)
                            .FirstOrDefaultAsync(p => p.Id == newPoll.Id);

                        if (poll == null)
                        {
                            return Results.Json(new { success = false, error = "Failed to create poll" },
                                statusCode: 500);
                        }

                        // Build embed
                        var embed = new Discord.EmbedBuilder()
                            .WithTitle($"📊 {poll.Question}")
                            .WithColor(Discord.Color.Blue)
                            .WithFooter($"Created by {poll.CreatedByName} • 0 votes")
                            .WithTimestamp(poll.CreatedAt);

                        foreach (var opt in poll.PollOptions.OrderBy(o => o.DisplayOrder))
                        {
                            var bar = new string('░', 20);
                            var emote = !string.IsNullOrEmpty(opt.Emote) ? opt.Emote + " " : "";
                            embed.AddField($"{emote}{opt.OptionText}",
                                $"`{bar}` 0.0% (0 votes)",
                                inline: false);
                        }

                        if (poll.ExpiresAt.HasValue)
                        {
                            embed.AddField("Expires", $"<t:{new DateTimeOffset(poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
                        }

                        // Build components (vote buttons)
                        var builder = new Discord.ComponentBuilder();
                        var pollOptions = poll.PollOptions.OrderBy(o => o.DisplayOrder).ToList();

                        int currentRow = 0;
                        int buttonsInRow = 0;

                        foreach (var opt in pollOptions)
                        {
                            var customId = $"poll_vote~{userId}~{poll.Id}~{opt.Id}";
                            var label = opt.OptionText.Length > 80 ? opt.OptionText.Substring(0, 77) + "..." : opt.OptionText;
                            var button = new Discord.ButtonBuilder()
                                .WithLabel(label)
                                .WithCustomId(customId)
                                .WithStyle(Discord.ButtonStyle.Primary);

                            if (!string.IsNullOrEmpty(opt.Emote))
                            {
                                try
                                {
                                    button.WithEmote(new Discord.Emoji(opt.Emote));
                                }
                                catch { }
                            }

                            builder.WithButton(button, row: currentRow);

                            buttonsInRow++;
                            if (buttonsInRow >= 5)
                            {
                                currentRow++;
                                buttonsInRow = 0;
                            }
                        }

                        // Add close button
                        if (currentRow < 4 || (currentRow == 4 && buttonsInRow == 0))
                        {
                            var closeRow = buttonsInRow > 0 ? currentRow + 1 : currentRow;
                            var closeButton = new Discord.ButtonBuilder()
                                .WithLabel("Close Poll")
                                .WithCustomId($"poll_close~{poll.CreatedById}~{poll.Id}")
                                .WithStyle(Discord.ButtonStyle.Danger)
                                .WithEmote(new Discord.Emoji("🔒"));
                            builder.WithButton(closeButton, row: closeRow);
                        }

                        // Send message to Discord
                        var message = await channel.SendMessageAsync(embed: embed.Build(), components: builder.Build());

                        // Update poll with message ID
                        poll.MessageId = (long)message.Id;
                        db.Polls.Update(poll);
                        await db.SaveChangesAsync();

                        var messageUrl = $"https://discord.com/channels/{guildId}/{channelId}/{message.Id}";

                        return Results.Json(new
                        {
                            success = true,
                            poll_id = poll.Id,
                            message_url = messageUrl
                        }, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error creating poll via API");
                        return Results.Json(new { success = false, error = "An error occurred while creating the poll" },
                            statusCode: 500);
                    }
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

        /// <summary>
        /// Builds an embed showing closed poll results with progress bars.
        /// </summary>
        private EmbedBuilder BuildClosedPollEmbed(Database.Poll poll, int totalVotes)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"📊 {poll.Question}")
                .WithColor(Color.Red) // Red for closed
                .WithFooter($"Created by {poll.CreatedByName} • {totalVotes} total votes • Closed")
                .WithTimestamp(poll.CreatedAt);

            // Add options with vote counts and progress bars
            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var optionVotes = poll.PollVotes.Count(v => v.OptionId == option.Id);
                var percentage = totalVotes > 0 ? (double)optionVotes / totalVotes * 100 : 0;
                var barLength = 10;
                var filledBars = (int)Math.Round(percentage / 100 * barLength);
                var progressBar = new string('█', filledBars) + new string('░', barLength - filledBars);

                var votersList = "";
                if (!poll.IsAnonymous && optionVotes > 0)
                {
                    var voters = poll.PollVotes
                        .Where(v => v.OptionId == option.Id)
                        .Select(v => v.UserName)
                        .Take(5);
                    votersList = $"\n*{string.Join(", ", voters)}*";
                    if (optionVotes > 5)
                        votersList += $" *+{optionVotes - 5} more*";
                }

                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField(
                    $"{emote}{option.OptionText}",
                    $"{progressBar} {percentage:F1}% ({optionVotes} votes){votersList}",
                    inline: false
                );
            }

            return embed;
        }

        public void Dispose()
        {
            _cts.Dispose();
            (_app as IDisposable)?.Dispose();
        }

        /// <summary>
        /// Parses a duration string like "1h", "24h", "1d", "7d", "1w" into a DateTime.
        /// </summary>
        private static DateTime? ParsePollDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                return null;

            duration = duration.Trim().ToLowerInvariant();

            // Try to parse number + unit
            if (duration.Length < 2)
                return null;

            var unit = duration[^1];
            if (!int.TryParse(duration[..^1], out var value) || value <= 0)
                return null;

            return unit switch
            {
                'h' => DateTime.UtcNow.AddHours(value),
                'd' => DateTime.UtcNow.AddDays(value),
                'w' => DateTime.UtcNow.AddDays(value * 7),
                _ => null
            };
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

    /// <summary>
    /// Request body for the vote poll endpoint.
    /// </summary>
    public record VotePollRequest(
        string? UserId,
        string? OptionId,
        string? UserName
    );

    /// <summary>
    /// Request body for the close poll endpoint.
    /// </summary>
    public record ClosePollRequest(string? UserId);
    public record DeletePollRequest(string? UserId, string? GuildId);
    public record CleanupPollsRequest(string? UserId);

    /// <summary>
    /// Request body for the create poll endpoint.
    /// </summary>
    public record CreatePollRequest(
        string? GuildId,
        string? ChannelId,
        string? UserId,
        string? Question,
        System.Collections.Generic.List<string>? Options,
        string? Duration,
        bool? AllowVoteChange
    );
}
