using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Polls;
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

                    // Check permissions - creator OR moderators (ManageMessages) can close
                    var isCreator = poll.CreatedById == userId;
                    var isModerator = false;

                    var client = _serviceProvider.GetService<DiscordShardedClient>();
                    var guild = client?.GetGuild((ulong)poll.GuildId);
                    var member = guild?.GetUser((ulong)userId);
                    if (member != null)
                    {
                        isModerator = member.GuildPermissions.ManageMessages;
                    }

                    if (!isCreator && !isModerator)
                    {
                        return Results.Json(new { success = false, error = "Only the poll creator or moderators can close this poll" }, statusCode: 403);
                    }

                    // Mark as closed in memory (but don't save until Discord update succeeds)
                    poll.IsClosed = true;
                    poll.ClosedAt = DateTime.UtcNow;

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

                    // Update Discord and save to DB
                    try
                    {
                        // Get server poll settings
                        var settings = await db.ServerPollSettings
                            .FirstOrDefaultAsync(s => s.DiscordGuildId == poll.GuildId);

                        // Determine target channel (settings override or poll channel)
                        var targetChannelId = settings?.ResultsChannelId ?? poll.ChannelId;

                        // Use guild reference from earlier permission check
                        var pollChannel = guild?.GetTextChannel((ulong)poll.ChannelId);
                        var resultsChannel = guild?.GetTextChannel((ulong)targetChannelId);

                        // Update original poll message (change color to red, disable buttons)
                        if (pollChannel != null && poll.MessageId > 0)
                        {
                            var message = await pollChannel.GetMessageAsync((ulong)poll.MessageId);
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

                        // Save to DB only after Discord update succeeds
                        await db.SaveChangesAsync();

                        // Post full results using PollResultsBuilder (non-critical, don't fail if this errors)
                        try
                        {
                            if (resultsChannel != null)
                            {
                                var resultsBuilder = new PollResultsBuilder();
                                var options = poll.PollOptions?.ToList() ?? new List<PollOption>();
                                var votes = poll.PollVotes?.ToList() ?? new List<PollVote>();
                                var resultsEmbed = resultsBuilder.BuildResultsEmbed(poll, options, votes, closedBy: poll.CreatedByName, wasExpired: false);

                                // Build voter mentions if enabled
                                string? content = null;
                                if (settings?.MentionVotersOnClose == true && !poll.IsAnonymous)
                                {
                                    content = resultsBuilder.BuildVoterMentions(votes, poll.IsAnonymous);
                                }

                                // Send as reply to original poll
                                var messageReference = new MessageReference(
                                    messageId: (ulong)poll.MessageId,
                                    channelId: (ulong)poll.ChannelId,
                                    guildId: (ulong)poll.GuildId,
                                    failIfNotExists: false);

                                await resultsChannel.SendMessageAsync(
                                    text: string.IsNullOrEmpty(content) ? null : content,
                                    embed: resultsEmbed,
                                    messageReference: messageReference,
                                    allowedMentions: AllowedMentions.All);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to post results message for poll {PollId}", poll.Id);
                            // Non-critical - poll is already closed
                        }
                    }
                    catch (Exception ex)
                    {
                        // Discord update failed - revert in-memory changes and don't save
                        poll.IsClosed = false;
                        poll.ClosedAt = null;
                        _logger.LogError(ex, "Failed to close poll {PollId} - Discord update failed", poll.Id);
                        return Results.Json(new { success = false, error = "Failed to update Discord message. Poll not closed." }, statusCode: 500);
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

                // GET /api/guilds/{guildId}/poll-settings - Get poll settings for a guild
                _app.MapGet("/api/guilds/{guildId}/poll-settings", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var settings = await db.ServerPollSettings
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
                                results_channel_id = (string?)null,
                                mention_voters_on_close = false,
                                default_anonymous = false,
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
                            results_channel_id = settings.ResultsChannelId?.ToString(),
                            mention_voters_on_close = settings.MentionVotersOnClose,
                            default_anonymous = settings.DefaultAnonymous,
                            set_by_id = settings.SetById?.ToString(),
                            set_by_name = settings.SetByName,
                            time_set = settings.TimeSet
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // PUT /api/guilds/{guildId}/poll-settings - Update poll settings for a guild
                _app.MapPut("/api/guilds/{guildId}/poll-settings", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                    }

                    var body = await context.Request.ReadFromJsonAsync<UpdatePollSettingsRequest>(
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var settings = await db.ServerPollSettings
                        .FirstOrDefaultAsync(s => s.DiscordGuildId == guildIdLong);

                    if (settings == null)
                    {
                        settings = new ServerPollSettings
                        {
                            DiscordGuildId = guildIdLong,
                            MentionVotersOnClose = false,
                            ResultsChannelId = null
                        };
                        db.ServerPollSettings.Add(settings);
                    }

                    // Update settings
                    if (body.ResultsChannelId != null)
                    {
                        if (body.ResultsChannelId == "")
                        {
                            settings.ResultsChannelId = null; // Clear the setting
                        }
                        else if (long.TryParse(body.ResultsChannelId, out var channelId))
                        {
                            settings.ResultsChannelId = channelId;
                        }
                    }

                    if (body.MentionVotersOnClose.HasValue)
                    {
                        settings.MentionVotersOnClose = body.MentionVotersOnClose.Value;
                    }

                    if (body.DefaultAnonymous.HasValue)
                    {
                        settings.DefaultAnonymous = body.DefaultAnonymous.Value;
                    }

                    if (!string.IsNullOrEmpty(body.UserId) && long.TryParse(body.UserId, out var userId))
                    {
                        settings.SetById = userId;
                    }
                    if (!string.IsNullOrEmpty(body.UserName))
                    {
                        settings.SetByName = body.UserName;
                    }
                    settings.TimeSet = DateTime.UtcNow;

                    await db.SaveChangesAsync();

                    return Results.Json(new
                    {
                        success = true,
                        message = "Poll settings updated",
                        settings = new
                        {
                            guild_id = settings.DiscordGuildId.ToString(),
                            results_channel_id = settings.ResultsChannelId?.ToString(),
                            mention_voters_on_close = settings.MentionVotersOnClose,
                            default_anonymous = settings.DefaultAnonymous,
                            set_by_id = settings.SetById?.ToString(),
                            set_by_name = settings.SetByName,
                            time_set = settings.TimeSet
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // GET /api/guilds/{guildId}/log-monitoring - Get log monitoring settings for a guild
                _app.MapGet("/api/guilds/{guildId}/log-monitoring", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapPut("/api/guilds/{guildId}/log-monitoring", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                    }

                    var body = await context.Request.ReadFromJsonAsync<UpdateLogMonitoringRequest>(
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapGet("/api/guilds/{guildId}/greeting-settings", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapPut("/api/guilds/{guildId}/greeting-settings", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    var body = await context.Request.ReadFromJsonAsync<UpdateGreetingSettingsRequest>(
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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

                    return Results.Json(new
                    {
                        success = true,
                        settings = new
                        {
                            guild_id = settings.DiscordGuildId.ToString(),
                            greet_users = settings.GreetUsers ?? false,
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
                _app.MapGet("/api/guilds/{guildId}/moderation-watcher", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapPut("/api/guilds/{guildId}/moderation-watcher", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    var body = await context.Request.ReadFromJsonAsync<UpdateModerationWatcherRequest>(
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapGet("/api/guilds/{guildId}/wow-association", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapPut("/api/guilds/{guildId}/wow-association", async (HttpContext context, string guildId) =>
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

                    using var scope = _serviceProvider.CreateScope();
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

                // GET /api/users/{userId}/away-status - Get away status for a user
                _app.MapGet("/api/users/{userId}/away-status", async (HttpContext context, string userId) =>
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

                    if (!long.TryParse(userId, out var userIdParsed))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
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
                _app.MapPut("/api/users/{userId}/away-status", async (HttpContext context, string userId) =>
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

                    using var scope = _serviceProvider.CreateScope();
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

                // PUT /api/characters/{characterId}/main - Set a character as main
                _app.MapPut("/api/characters/{characterId}/main", async (HttpContext context, string characterId) =>
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

                    using var scope = _serviceProvider.CreateScope();
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

                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();

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
                        _logger.LogError(ex, "Failed to set main character {CharId} for user {UserId}", charIdLong, userIdLong);
                        return Results.Json(new { success = false, error = "Failed to set main character" },
                            statusCode: 500);
                    }
                });

                // DELETE /api/characters/{characterId} - Remove a character association
                _app.MapDelete("/api/characters/{characterId}", async (HttpContext context, string characterId) =>
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

                    using var scope = _serviceProvider.CreateScope();
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

                    return Results.Json(new
                    {
                        success = true,
                        message = $"Character {charName} ({realm}) removed"
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
                            IsAnonymous = body.IsAnonymous ?? false,
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
                            var customId = $"{ModalConstants.PollVotePrefix}{userId}~{poll.Id}~{opt.Id}";
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
                                .WithCustomId($"{ModalConstants.PollClosePrefix}{poll.CreatedById}~{poll.Id}")
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

                // GET /api/guilds/{guildId}/realm-watches - Get all realm watches for a guild
                _app.MapGet("/api/guilds/{guildId}/realm-watches", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var watches = await db.RealmWatchSubscriptions
                        .Where(w => w.GuildId == guildIdLong)
                        .OrderBy(w => w.Region)
                        .ThenBy(w => w.RealmName)
                        .ToListAsync();

                    return Results.Json(new
                    {
                        success = true,
                        watches = watches.Select(w => new
                        {
                            id = w.Id,
                            realm_slug = w.RealmSlug,
                            realm_name = w.RealmName,
                            region = w.Region,
                            channel_id = w.ChannelId?.ToString(),
                            user_id = w.UserId.ToString(),
                            alert_on_online = w.AlertOnOnline,
                            alert_on_offline = w.AlertOnOffline,
                            alert_on_queue = w.AlertOnQueue,
                            created_at = w.CreatedAt,
                            last_alert_at = w.LastAlertAt
                        })
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // POST /api/guilds/{guildId}/realm-watches - Add a realm watch
                _app.MapPost("/api/guilds/{guildId}/realm-watches", async (HttpContext context, string guildId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    var body = await context.Request.ReadFromJsonAsync<AddRealmWatchRequest>(
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

                    if (body == null || string.IsNullOrEmpty(body.RealmSlug) || string.IsNullOrEmpty(body.UserId))
                    {
                        return Results.BadRequest(new { success = false, error = "realm_slug and user_id are required" });
                    }

                    if (!long.TryParse(body.UserId, out var userIdLong))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user_id" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var region = (body.Region ?? "us").ToLower();
                    var regionUpper = region.ToUpper();

                    // Get realm info (WowRealms stores region as uppercase)
                    var realmInfo = await db.WowRealms
                        .FirstOrDefaultAsync(r => r.Slug == body.RealmSlug && r.Region == regionUpper);

                    if (realmInfo == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Realm not found" });
                    }

                    // If ConnectedRealmId is not cached, fetch it from Blizzard API
                    if (!realmInfo.ConnectedRealmId.HasValue)
                    {
                        try
                        {
                            var wowApi = scope.ServiceProvider.GetRequiredService<WowApi>();
                            var singleRealmInfo = await wowApi.GetSingleRealmInfoAsync(body.RealmSlug, region);

                            if (singleRealmInfo?.ConnectedRealm?.Href == null)
                            {
                                return Results.BadRequest(new { success = false, error = "Could not get connected realm data from Blizzard API" });
                            }

                            var connectedRealmInfo = await wowApi.GetConnectedRealmInfoAsync(
                                singleRealmInfo.ConnectedRealm.Href.ToString(), region);

                            if (connectedRealmInfo == null)
                            {
                                return Results.BadRequest(new { success = false, error = "Could not get connected realm info from Blizzard API" });
                            }

                            // Cache the ConnectedRealmId for future use
                            realmInfo.ConnectedRealmId = connectedRealmInfo.Id;
                            await db.SaveChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch ConnectedRealmId for {RealmSlug}", body.RealmSlug);
                            return Results.BadRequest(new { success = false, error = "Could not verify realm with Blizzard API. Please try again." });
                        }
                    }

                    // Check for existing subscription (subscriptions use lowercase region)
                    var existing = await db.RealmWatchSubscriptions
                        .FirstOrDefaultAsync(s =>
                            s.GuildId == guildIdLong &&
                            s.UserId == userIdLong &&
                            s.RealmSlug == body.RealmSlug &&
                            s.Region == region);

                    if (existing != null)
                    {
                        return Results.BadRequest(new { success = false, error = "Watch already exists for this realm" });
                    }

                    long? channelIdLong = null;
                    if (!string.IsNullOrEmpty(body.ChannelId) && long.TryParse(body.ChannelId, out var parsedChannelId))
                    {
                        channelIdLong = parsedChannelId;
                    }

                    var watch = new Database.RealmWatchSubscription
                    {
                        GuildId = guildIdLong,
                        UserId = userIdLong,
                        ChannelId = channelIdLong,
                        RealmSlug = body.RealmSlug,
                        RealmName = realmInfo.Name,
                        Region = region,
                        ConnectedRealmId = (int)realmInfo.ConnectedRealmId.Value,
                        AlertOnOnline = body.AlertOnOnline ?? true,
                        AlertOnOffline = body.AlertOnOffline ?? true,
                        AlertOnQueue = body.AlertOnQueue ?? true,
                        CreatedAt = DateTime.UtcNow
                    };

                    db.RealmWatchSubscriptions.Add(watch);
                    await db.SaveChangesAsync();

                    return Results.Json(new
                    {
                        success = true,
                        watch = new
                        {
                            id = watch.Id,
                            realm_slug = watch.RealmSlug,
                            realm_name = watch.RealmName,
                            region = watch.Region
                        }
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // DELETE /api/guilds/{guildId}/realm-watches/{watchId} - Delete a realm watch
                _app.MapDelete("/api/guilds/{guildId}/realm-watches/{watchId}", async (HttpContext context, string guildId, string watchId) =>
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
                        return Results.BadRequest(new { success = false, error = "Invalid guild ID" });
                    }

                    if (!long.TryParse(watchId, out var watchIdLong))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid watch ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var watch = await db.RealmWatchSubscriptions
                        .FirstOrDefaultAsync(w => w.Id == watchIdLong && w.GuildId == guildIdLong);

                    if (watch == null)
                    {
                        return Results.NotFound(new { success = false, error = "Watch not found" });
                    }

                    db.RealmWatchSubscriptions.Remove(watch);
                    await db.SaveChangesAsync();

                    return Results.Json(new { success = true, message = "Watch deleted" },
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                });

                // GET /api/users/{userId}/realm-watches - Get all realm watches for a user across guilds
                _app.MapGet("/api/users/{userId}/realm-watches", async (HttpContext context, string userId) =>
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

                    if (!long.TryParse(userId, out var userIdLong))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid user ID" });
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var watches = await db.RealmWatchSubscriptions
                        .Where(w => w.UserId == userIdLong)
                        .OrderBy(w => w.Region)
                        .ThenBy(w => w.RealmName)
                        .ToListAsync();

                    return Results.Json(new
                    {
                        success = true,
                        watches = watches.Select(w => new
                        {
                            id = w.Id,
                            realm_slug = w.RealmSlug,
                            realm_name = w.RealmName,
                            region = w.Region,
                            guild_id = w.GuildId.ToString(),
                            channel_id = w.ChannelId?.ToString(),
                            user_id = w.UserId.ToString(),
                            alert_on_online = w.AlertOnOnline,
                            alert_on_offline = w.AlertOnOffline,
                            alert_on_queue = w.AlertOnQueue,
                            created_at = w.CreatedAt,
                            last_alert_at = w.LastAlertAt
                        })
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // GET /api/realms/{region}/status - Get realm statuses
                // Note: Realm status cache moved to NinjaBotHelpers service
                // This endpoint now returns empty - use Blizzard API directly for live status
                _app.MapGet("/api/realms/{region}/status", (HttpContext context, string region) =>
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

                    // Status cache is now in NinjaBotHelpers container
                    return Results.Json(new
                    {
                        success = true,
                        statuses = Array.Empty<object>(),
                        message = "Realm status cache moved to NinjaBotHelpers service"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });
                });

                // ===== Static Data Stats Endpoint =====

                // GET /api/static-data/stats - Get statistics for all static WoW data
                _app.MapGet("/api/static-data/stats", async (HttpContext context) =>
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

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var wowStaticData = scope.ServiceProvider.GetService<WowStaticDataService>();

                        if (wowStaticData == null)
                        {
                            return Results.Json(new
                            {
                                success = false,
                                error = "WowStaticDataService not available"
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                        }

                        var realms = await wowStaticData.GetAllRealmsAsync();
                        var classes = await wowStaticData.GetAllClassesAsync();
                        var races = await wowStaticData.GetAllRacesAsync();
                        var mounts = await wowStaticData.GetAllMountsAsync();
                        var achievements = await wowStaticData.GetAllAchievementsAsync();
                        var pets = await wowStaticData.GetAllPetsAsync();

                        // Items - query directly from database
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                        var itemsCount = await db.WowItems.CountAsync();
                        var oldestItem = await db.WowItems.OrderBy(i => i.LastUpdated).FirstOrDefaultAsync();

                        // Realms by region
                        var realmsByRegion = realms.GroupBy(r => r.Region)
                            .OrderBy(g => g.Key)
                            .ToDictionary(g => g.Key, g => g.Count());

                        // Races by faction
                        var racesByFaction = races.GroupBy(r => r.Faction ?? "Unknown")
                            .OrderBy(g => g.Key)
                            .ToDictionary(g => g.Key, g => g.Select(r => r.Name).ToList());

                        // Achievements by category (top 5)
                        var achievementsByCategory = achievements
                            .GroupBy(a => a.ParentCategory ?? a.Category ?? "Uncategorized")
                            .OrderByDescending(g => g.Count())
                            .Take(5)
                            .ToDictionary(g => g.Key, g => g.Count());

                        // Pets by type
                        var petsByType = pets.GroupBy(p => p.PetType ?? "Unknown")
                            .OrderByDescending(g => g.Count())
                            .ToDictionary(g => g.Key, g => g.Count());

                        // Oldest updates
                        var oldestRealm = realms.OrderBy(r => r.LastUpdated).FirstOrDefault();
                        var oldestClass = classes.OrderBy(c => c.LastUpdated).FirstOrDefault();
                        var oldestRace = races.OrderBy(r => r.LastUpdated).FirstOrDefault();
                        var oldestMount = mounts.OrderBy(m => m.LastUpdated).FirstOrDefault();
                        var oldestAchievement = achievements.OrderBy(a => a.LastUpdated).FirstOrDefault();
                        var oldestPet = pets.OrderBy(p => p.LastUpdated).FirstOrDefault();

                        return Results.Json(new
                        {
                            success = true,
                            realms = new
                            {
                                total = realms.Count,
                                by_region = realmsByRegion,
                                oldest_update = oldestRealm?.LastUpdated
                            },
                            classes = new
                            {
                                total = classes.Count,
                                names = classes.OrderBy(c => c.Name).Select(c => c.Name).ToList(),
                                oldest_update = oldestClass?.LastUpdated
                            },
                            races = new
                            {
                                total = races.Count,
                                by_faction = racesByFaction,
                                oldest_update = oldestRace?.LastUpdated
                            },
                            mounts = new
                            {
                                total = mounts.Count,
                                oldest_update = oldestMount?.LastUpdated
                            },
                            achievements = new
                            {
                                total = achievements.Count,
                                top_categories = achievementsByCategory,
                                oldest_update = oldestAchievement?.LastUpdated
                            },
                            pets = new
                            {
                                total = pets.Count,
                                by_type = petsByType,
                                oldest_update = oldestPet?.LastUpdated
                            },
                            items = new
                            {
                                total = itemsCount,
                                oldest_update = oldestItem?.LastUpdated
                            }
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error getting static data stats via API");
                        return Results.Problem($"Error: {ex.Message}");
                    }
                });

                // ===== Static Data Sync Control Endpoints =====

                // POST /api/sync/trigger - Queue a sync request
                _app.MapPost("/api/sync/trigger", async (HttpContext context) =>
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

                    try
                    {
                        var body = await context.Request.ReadFromJsonAsync<TriggerSyncRequest>();
                        if (body == null || string.IsNullOrEmpty(body.SyncType))
                        {
                            return Results.BadRequest(new { error = "sync_type is required" });
                        }

                        var syncType = body.SyncType.ToLower();
                        var queuedTypes = new[] { "achievements", "pets", "mounts", "mount_images", "items", "all" };
                        var directTypes = new[] { "realms", "classes", "races", "static" };
                        var validTypes = queuedTypes.Concat(directTypes).ToArray();

                        if (!validTypes.Contains(syncType))
                        {
                            return Results.BadRequest(new { error = "sync_type must be one of: achievements, pets, mounts, mount_images, items, realms, classes, races, static, all" });
                        }

                        using var scope = _serviceProvider.CreateScope();

                        // Handle direct sync types (realms, classes, races, static) via WowStaticDataService
                        if (directTypes.Contains(syncType))
                        {
                            var wowStaticData = scope.ServiceProvider.GetService<WowStaticDataService>();
                            if (wowStaticData == null)
                            {
                                return Results.Json(new
                                {
                                    success = false,
                                    error = "service_unavailable",
                                    message = "WowStaticDataService is not available"
                                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                            }

                            var typesToSync = syncType == "static"
                                ? new[] { "realms", "classes", "races" }
                                : new[] { syncType };

                            var results = new List<object>();
                            foreach (var type in typesToSync)
                            {
                                try
                                {
                                    if (type == "realms")
                                        await wowStaticData.ImportAllRealmsAsync(CancellationToken.None);
                                    else if (type == "classes")
                                        await wowStaticData.ImportAllClassesAsync(CancellationToken.None);
                                    else if (type == "races")
                                        await wowStaticData.ImportAllRacesAsync(CancellationToken.None);

                                    results.Add(new { type, success = true });
                                    _logger.LogInformation("Direct sync for {Type} completed via API", type);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Direct sync for {Type} failed via API", type);
                                    results.Add(new { type, success = false, error = ex.Message });
                                }
                            }

                            return Results.Json(new
                            {
                                success = results.All(r => ((dynamic)r).success),
                                sync_type = syncType,
                                results,
                                message = "Direct sync completed"
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                        }

                        // Handle queued types (achievements, pets, mounts, all) via StaticDataSyncRequest
                        long? userId = null;
                        if (!string.IsNullOrEmpty(body.UserId) && long.TryParse(body.UserId, out var parsedUserId))
                        {
                            userId = parsedUserId;
                        }

                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        // Check for existing pending request
                        var existing = await db.StaticDataSyncRequests
                            .FirstOrDefaultAsync(r => r.SyncType == syncType && r.Status == "pending");

                        if (existing != null)
                        {
                            return Results.Json(new
                            {
                                success = false,
                                error = "pending_exists",
                                message = $"A sync request for {syncType} is already pending",
                                request_id = existing.Id
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                        }

                        var request = new StaticDataSyncRequest
                        {
                            SyncType = syncType,
                            Status = "pending",
                            RequestedByUserId = userId,
                            RequestSource = "api",
                            RequestedAt = DateTime.UtcNow
                        };

                        db.StaticDataSyncRequests.Add(request);
                        await db.SaveChangesAsync();

                        _logger.LogInformation("Sync request #{Id} queued for {Type} via API", request.Id, request.SyncType);

                        return Results.Json(new
                        {
                            success = true,
                            request_id = request.Id,
                            sync_type = request.SyncType,
                            status = request.Status,
                            message = "Sync request queued"
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error creating sync request via API");
                        return Results.Problem($"Error: {ex.Message}");
                    }
                });

                // GET /api/sync/status - Get current sync status for all types
                _app.MapGet("/api/sync/status", async (HttpContext context) =>
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

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var statuses = await db.StaticDataSyncStatus.ToListAsync();
                        var pendingRequests = await db.StaticDataSyncRequests
                            .Where(r => r.Status == "pending" || r.Status == "in_progress")
                            .OrderBy(r => r.RequestedAt)
                            .ToListAsync();

                        var result = new Dictionary<string, object>();

                        foreach (var type in new[] { "achievements", "pets", "mounts", "items" })
                        {
                            var status = statuses.FirstOrDefault(s => s.SyncType == type);
                            result[type] = new
                            {
                                last_sync = status?.LastSyncCompleted,
                                last_status = status?.LastSyncStatus,
                                item_count = status?.TotalItemsInDatabase,
                                next_scheduled = status?.NextScheduledSync
                            };
                        }

                        result["pending_requests"] = pendingRequests.Select(r => new
                        {
                            id = r.Id,
                            sync_type = r.SyncType,
                            status = r.Status,
                            requested_at = r.RequestedAt,
                            started_at = r.StartedAt
                        });

                        return Results.Json(result, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error getting sync status via API");
                        return Results.Problem($"Error: {ex.Message}");
                    }
                });

                // GET /api/sync/requests - Get sync request history
                _app.MapGet("/api/sync/requests", async (HttpContext context) =>
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

                    try
                    {
                        var statusFilter = context.Request.Query["status"].ToString();
                        var limitStr = context.Request.Query["limit"].ToString();
                        var limit = int.TryParse(limitStr, out var l) ? Math.Min(l, 100) : 25;

                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var query = db.StaticDataSyncRequests.AsQueryable();

                        if (!string.IsNullOrEmpty(statusFilter))
                        {
                            query = query.Where(r => r.Status == statusFilter);
                        }

                        var requests = await query
                            .OrderByDescending(r => r.RequestedAt)
                            .Take(limit)
                            .ToListAsync();

                        return Results.Json(new
                        {
                            requests = requests.Select(r => new
                            {
                                id = r.Id,
                                sync_type = r.SyncType,
                                status = r.Status,
                                requested_by = r.RequestedByUserId,
                                request_source = r.RequestSource,
                                requested_at = r.RequestedAt,
                                started_at = r.StartedAt,
                                completed_at = r.CompletedAt,
                                items_processed = r.ItemsProcessed,
                                items_skipped = r.ItemsSkipped,
                                items_failed = r.ItemsFailed,
                                error_message = r.ErrorMessage
                            })
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error getting sync requests via API");
                        return Results.Problem($"Error: {ex.Message}");
                    }
                });

                // DELETE /api/sync/requests/{id} - Cancel a pending sync request
                _app.MapDelete("/api/sync/requests/{id:long}", async (HttpContext context, long id) =>
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

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var request = await db.StaticDataSyncRequests.FindAsync(id);
                        if (request == null)
                        {
                            return Results.NotFound(new { error = "Request not found" });
                        }

                        if (request.Status != "pending")
                        {
                            return Results.Json(new
                            {
                                success = false,
                                error = "cannot_cancel",
                                message = $"Cannot cancel request with status '{request.Status}'"
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower },
                            statusCode: 400);
                        }

                        request.Status = "cancelled";
                        request.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();

                        _logger.LogInformation("Sync request #{Id} cancelled via API", id);

                        return Results.Json(new
                        {
                            success = true,
                            message = $"Sync request #{id} cancelled"
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error cancelling sync request via API");
                        return Results.Problem($"Error: {ex.Message}");
                    }
                });

                // GET /api/mounts/stats - Get mount statistics including missing images count
                _app.MapGet("/api/mounts/stats", async (HttpContext context) =>
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

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var mounts = await db.WowMounts.ToListAsync();
                        var total = mounts.Count;
                        var missingImages = mounts.Count(m => m.CreatureDisplayId.HasValue && string.IsNullOrEmpty(m.MediaUrl));
                        var hasImages = mounts.Count(m => !string.IsNullOrEmpty(m.MediaUrl));

                        // Group by source
                        var bySource = mounts
                            .GroupBy(m => m.Source ?? "UNKNOWN")
                            .OrderByDescending(g => g.Count())
                            .ToDictionary(g => g.Key, g => g.Count());

                        // Group by expansion
                        var byExpansion = mounts
                            .Where(m => !string.IsNullOrEmpty(m.Expansion))
                            .GroupBy(m => m.Expansion)
                            .OrderByDescending(g => g.Count())
                            .ToDictionary(g => g.Key!, g => g.Count());

                        return Results.Json(new
                        {
                            success = true,
                            total,
                            missing_images = missingImages,
                            has_images = hasImages,
                            by_source = bySource,
                            by_expansion = byExpansion
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error fetching mount stats via API");
                        return Results.Problem($"Error: {ex.Message}");
                    }
                });

                // POST /api/mounts/import-json - Import mounts from in-game addon JSON data
                // Mount data is saved immediately, image fetching is queued for helpers service
                _app.MapPost("/api/mounts/import-json", async (HttpContext context) =>
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

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        // Read and parse JSON body
                        using var reader = new System.IO.StreamReader(context.Request.Body);
                        var jsonContent = await reader.ReadToEndAsync();

                        if (string.IsNullOrWhiteSpace(jsonContent))
                        {
                            return Results.BadRequest(new { error = "Request body is empty" });
                        }

                        var scrapedData = Newtonsoft.Json.JsonConvert.DeserializeObject<ScrapedMountData>(jsonContent);
                        if (scrapedData?.Mounts == null || scrapedData.Mounts.Count == 0)
                        {
                            return Results.BadRequest(new { error = "No mount data found in JSON" });
                        }

                        _logger.LogInformation("Starting mount import from JSON: {Count} mounts (scanned: {Timestamp})",
                            scrapedData.Mounts.Count, scrapedData.Metadata?.ScanTimestamp);

                        // Get existing mounts
                        var existingMounts = await db.WowMounts.ToDictionaryAsync(m => m.Id);

                        int created = 0;
                        int updated = 0;
                        int needsImages = 0;

                        // Process each mount from JSON
                        foreach (var kvp in scrapedData.Mounts)
                        {
                            var scraped = kvp.Value;

                            try
                            {
                                var (sourceType, sourceDetail) = scraped.Source?.GetPrimarySource() ?? ("UNKNOWN", null);
                                var faction = scraped.Faction switch
                                {
                                    0 => "Horde",
                                    1 => "Alliance",
                                    _ => null
                                };

                                if (existingMounts.TryGetValue(scraped.MountId, out var existing))
                                {
                                    // Update existing mount
                                    existing.Name = scraped.Name ?? existing.Name;
                                    existing.Description = scraped.Description ?? existing.Description;
                                    existing.Source = sourceType;
                                    existing.SourceDetail = sourceDetail;
                                    existing.InstanceName = scraped.Source?.Zone;
                                    existing.DropLocation = scraped.Source?.Zone;
                                    existing.EncounterName = scraped.Source?.Drop;
                                    existing.Faction = faction ?? existing.Faction;
                                    existing.CreatureDisplayId = scraped.CreatureDisplayId ?? existing.CreatureDisplayId;
                                    existing.IsObtainable = scraped.Source?.IsLegacy() != true;

                                    // Recalculate expansion using smart detection
                                    existing.Expansion = WowStaticDataService.DetermineExpansion(
                                        existing.Id,
                                        existing.Description,
                                        scraped.Source?.Zone,
                                        scraped.Source?.Category,
                                        scraped.Source?.Clean ?? scraped.Source?.Achievement
                                    );
                                    existing.LastUpdated = DateTime.UtcNow;

                                    if (string.IsNullOrEmpty(existing.MediaUrl) && existing.CreatureDisplayId.HasValue)
                                    {
                                        needsImages++;
                                    }

                                    updated++;
                                }
                                else
                                {
                                    // Create new mount
                                    var newMount = new WowMounts
                                    {
                                        Id = scraped.MountId,
                                        Name = scraped.Name ?? "Unknown",
                                        Description = scraped.Description,
                                        Source = sourceType,
                                        SourceDetail = sourceDetail,
                                        InstanceName = scraped.Source?.Zone,
                                        DropLocation = scraped.Source?.Zone,
                                        EncounterName = scraped.Source?.Drop,
                                        Faction = faction,
                                        CreatureDisplayId = scraped.CreatureDisplayId,
                                        IsObtainable = scraped.Source?.IsLegacy() != true,
                                        Expansion = WowStaticDataService.DetermineExpansion(
                                            scraped.MountId,
                                            scraped.Description,
                                            scraped.Source?.Zone,
                                            scraped.Source?.Category,
                                            scraped.Source?.Clean ?? scraped.Source?.Achievement
                                        ),
                                        LastUpdated = DateTime.UtcNow
                                    };

                                    db.WowMounts.Add(newMount);

                                    if (scraped.CreatureDisplayId.HasValue)
                                    {
                                        needsImages++;
                                    }

                                    created++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to process mount {Id} from JSON", scraped.MountId);
                            }
                        }

                        // Save mount data
                        await db.SaveChangesAsync();
                        _logger.LogInformation("Mount data saved: {Created} created, {Updated} updated", created, updated);

                        // Update sync status for mounts to reflect the import
                        var mountCount = await db.WowMounts.CountAsync();
                        var mountStatus = await db.StaticDataSyncStatus.FindAsync("mounts");
                        if (mountStatus == null)
                        {
                            mountStatus = new StaticDataSyncStatus { SyncType = "mounts" };
                            db.StaticDataSyncStatus.Add(mountStatus);
                        }
                        mountStatus.LastSyncStarted = DateTime.UtcNow;
                        mountStatus.LastSyncCompleted = DateTime.UtcNow;
                        mountStatus.LastSyncStatus = "success";
                        mountStatus.LastSyncItemCount = created + updated;
                        mountStatus.TotalItemsInDatabase = mountCount;
                        await db.SaveChangesAsync();

                        // Queue image fetch request for helpers service if needed
                        long? imageRequestId = null;
                        if (needsImages > 0)
                        {
                            var imageRequest = new StaticDataSyncRequest
                            {
                                SyncType = "mount_images",
                                Status = "pending",
                                RequestSource = "api",
                                RequestedAt = DateTime.UtcNow
                            };
                            db.StaticDataSyncRequests.Add(imageRequest);
                            await db.SaveChangesAsync();
                            imageRequestId = imageRequest.Id;

                            _logger.LogInformation("Queued mount image sync request #{Id} for {Count} mounts",
                                imageRequest.Id, needsImages);
                        }

                        return Results.Json(new
                        {
                            success = true,
                            created,
                            updated,
                            mounts_needing_images = needsImages,
                            image_sync_request_id = imageRequestId,
                            total_in_json = scrapedData.Mounts.Count,
                            scan_timestamp = scrapedData.Metadata?.ScanTimestamp,
                            message = needsImages > 0
                                ? $"Import complete: {created} created, {updated} updated. Image fetch queued (request #{imageRequestId})"
                                : $"Import complete: {created} created, {updated} updated. No images needed."
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    }
                    catch (Newtonsoft.Json.JsonException ex)
                    {
                        _logger.LogError(ex, "Invalid JSON in mount import request");
                        return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error importing mounts from JSON via API");
                        return Results.Problem($"Error: {ex.Message}");
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
        bool? AllowVoteChange,
        bool? IsAnonymous
    );

    /// <summary>
    /// Request body for updating poll settings.
    /// </summary>
    public record UpdatePollSettingsRequest(
        string? ResultsChannelId,
        bool? MentionVotersOnClose,
        bool? DefaultAnonymous,
        string? UserId,
        string? UserName
    );

    /// <summary>
    /// Request body for updating log monitoring settings.
    /// </summary>
    public record UpdateLogMonitoringRequest(
        string? ChannelId,
        string? ChannelName,
        bool? MonitorLogs,
        string? ServerName
    );

    /// <summary>
    /// Request body for updating greeting settings.
    /// </summary>
    public record UpdateGreetingSettingsRequest(
        bool? GreetUsers,
        string? Greeting,
        string? GreetingChannelId,
        string? GreetingChannelName,
        string? PartingMessage,
        string? PartingChannelId,
        string? SetById,
        string? SetByName
    );

    /// <summary>
    /// Request body for updating moderation watcher settings.
    /// </summary>
    public record UpdateModerationWatcherRequest(
        string? ChannelId,
        string? ChannelName,
        bool? WatchVoice,
        bool? WatchMessages,
        bool? WatchRoles,
        bool? WatchBans,
        bool? WatchNicknames,
        string? SetById,
        string? SetByName
    );

    /// <summary>
    /// Request body for updating WoW guild association.
    /// </summary>
    public record UpdateWowAssociationRequest(
        string? WowGuildName,
        string? WowRealm,
        string? WowRealmSlug,
        string? WowRegion,
        string? Locale,
        string? ServerName,
        string? SetById,
        string? SetByName
    );

    /// <summary>
    /// Request body for updating away status.
    /// </summary>
    public record UpdateAwayStatusRequest(
        string? UserName,
        bool? IsAway,
        string? Message
    );

    /// <summary>
    /// Request body for setting a character as main.
    /// </summary>
    public record SetMainCharacterRequest(
        string? UserId
    );

    /// <summary>
    /// Request body for adding a realm watch.
    /// </summary>
    public record AddRealmWatchRequest(
        string? RealmSlug,
        string? Region,
        string? UserId,
        string? ChannelId,
        bool? AlertOnOnline,
        bool? AlertOnOffline,
        bool? AlertOnQueue
    );

    /// <summary>
    /// Request body for triggering a static data sync.
    /// </summary>
    public record TriggerSyncRequest(
        [property: JsonPropertyName("sync_type")] string? SyncType,
        [property: JsonPropertyName("user_id")] string? UserId
    );
}
