using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Polls;

namespace NinjaBotCore.Services.Api
{
    public static class PollEndpoints
    {
        public static void MapPollEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // GET /api/polls - List polls for a guild
            group.MapGet("/api/polls", async (HttpContext context) =>
            {
                var guildIdStr = context.Request.Query["guild_id"].ToString();
                if (string.IsNullOrEmpty(guildIdStr) || !long.TryParse(guildIdStr, out var guildId))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id parameter" });
                }

                var page = int.TryParse(context.Request.Query["page"], out var p) ? p : 1;
                var pageSize = int.TryParse(context.Request.Query["page_size"], out var ps) ? Math.Min(ps, 100) : 20;

                using var scope = deps.ServiceProvider.CreateScope();
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
            group.MapGet("/api/polls/{pollId:long}", async (HttpContext context, long pollId) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var poll = await db.Polls
                    .Include(p => p.PollOptions)
                    .Include(p => p.PollVotes)
                    .AsSplitQuery()
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
            group.MapPost("/api/polls/{pollId:long}/vote", async (HttpContext context, long pollId) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
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
            group.MapPost("/api/polls/{pollId:long}/close", async (HttpContext context, long pollId) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
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
                    .AsSplitQuery()
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

                var client = deps.ServiceProvider.GetService<DiscordShardedClient>();
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

                var totalVotesClose = poll.PollVotes.Count;
                var results = poll.PollOptions.OrderBy(o => o.DisplayOrder).Select(option =>
                {
                    var voteCount = poll.PollVotes.Count(v => v.OptionId == option.Id);
                    var percentage = totalVotesClose > 0 ? (double)voteCount / totalVotesClose * 100 : 0;

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
                            var closedEmbed = PollHelpers.BuildClosedPollEmbed(poll, totalVotesClose);
                            var disabledComponents = new ComponentBuilder()
                                .WithButton("Poll Closed", "poll_closed", ButtonStyle.Secondary, disabled: true);

                            // Keep "View Voters" button for non-anonymous polls
                            if (!poll.IsAnonymous)
                            {
                                disabledComponents.WithButton("View Voters", $"{ModalConstants.PollViewVotersPrefix}{poll.Id}",
                                    ButtonStyle.Secondary, emote: new Emoji("👥"));
                            }

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
                        deps.Logger.LogWarning(ex, "Failed to post results message for poll {PollId}", poll.Id);
                        // Non-critical - poll is already closed
                    }
                }
                catch (Exception ex)
                {
                    // Discord update failed - revert in-memory changes and don't save
                    poll.IsClosed = false;
                    poll.ClosedAt = null;
                    deps.Logger.LogError(ex, "Failed to close poll {PollId} - Discord update failed", poll.Id);
                    return Results.Json(new { success = false, error = "Failed to update Discord message. Poll not closed." }, statusCode: 500);
                }

                return Results.Json(new
                {
                    success = true,
                    message = "Poll closed",
                    final_results = new
                    {
                        total_votes = totalVotesClose,
                        options = results
                    }
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // GET /api/polls/{pollId}/results - Get detailed results
            group.MapGet("/api/polls/{pollId:long}/results", async (HttpContext context, long pollId) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var poll = await db.Polls
                    .Include(p => p.PollOptions)
                    .Include(p => p.PollVotes)
                    .AsSplitQuery()
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

            // GET /api/polls/{pollId}/voters - Get voters grouped by option (non-anonymous polls only)
            group.MapGet("/api/polls/{pollId:long}/voters", async (HttpContext context, long pollId) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var poll = await db.Polls
                    .Include(p => p.PollOptions)
                    .Include(p => p.PollVotes)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == pollId);

                if (poll == null)
                {
                    return Results.NotFound(new { success = false, error = "Poll not found" });
                }

                if (poll.IsAnonymous)
                {
                    return Results.Json(new { success = false, error = "This poll is anonymous. Voter information is not available." },
                        statusCode: 403);
                }

                var options = poll.PollOptions.OrderBy(o => o.DisplayOrder).Select(option =>
                {
                    var optionVotes = poll.PollVotes
                        .Where(v => v.OptionId == option.Id)
                        .OrderBy(v => v.VotedAt)
                        .ToList();

                    return new
                    {
                        option_id = option.Id,
                        option_text = option.OptionText,
                        vote_count = optionVotes.Count,
                        voters = optionVotes.Select(v => new
                        {
                            user_id = v.UserId.ToString(),
                            user_name = v.UserName,
                            voted_at = v.VotedAt
                        }).ToList()
                    };
                }).ToList();

                return Results.Json(new
                {
                    success = true,
                    poll = new
                    {
                        id = poll.Id,
                        question = poll.Question,
                        is_closed = poll.IsClosed,
                        total_votes = poll.PollVotes.Count
                    },
                    options
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            });

            // DELETE /api/polls/{pollId} - Delete a poll (admin only)
            group.MapDelete("/api/polls/{pollId:long}", async (HttpContext context, long pollId) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
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
            group.MapPost("/api/guilds/{guildId}/polls/cleanup", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
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
            group.MapGet("/api/guilds/{guildId}/poll-settings", async (HttpContext context, string guildId) =>
            {
                if (!long.TryParse(guildId, out var guildIdLong))
                {
                    return Results.BadRequest(new { success = false, error = "Invalid guild_id format" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
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
            group.MapPut("/api/guilds/{guildId}/poll-settings", async (HttpContext context, string guildId) =>
            {
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

                using var scope = deps.ServiceProvider.CreateScope();
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

            // POST /api/polls/create - Create a new poll and post to Discord
            group.MapPost("/api/polls/create", async (HttpContext context) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
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

                        pollType = (body.AllowMultipleSelections == true) ? "MultipleChoice" : "SingleChoice";
                    }

                    // Parse duration
                    DateTime? expiresAt = null;
                    if (!string.IsNullOrWhiteSpace(body.Duration))
                    {
                        expiresAt = PollHelpers.ParsePollDuration(body.Duration);
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
                    var embed = new EmbedBuilder()
                        .WithTitle($"📊 {poll.Question}")
                        .WithColor(Color.Blue)
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

                    if (poll.IsAnonymous && poll.PollType == "MultipleChoice")
                        embed.AddField("Voting", "Anonymous \u2022 Multiple selections", inline: true);
                    else if (poll.IsAnonymous)
                        embed.AddField("Voting", "Anonymous", inline: true);
                    else if (poll.PollType == "MultipleChoice")
                        embed.AddField("Voting", "Multiple selections allowed", inline: true);

                    // Build components (vote buttons)
                    var builder = new ComponentBuilder();
                    var pollOptions = poll.PollOptions.OrderBy(o => o.DisplayOrder).ToList();

                    int currentRow = 0;
                    int buttonsInRow = 0;

                    foreach (var opt in pollOptions)
                    {
                        var customId = $"{ModalConstants.PollVotePrefix}{userId}~{poll.Id}~{opt.Id}";
                        var label = opt.OptionText.Length > 80 ? opt.OptionText.Substring(0, 77) + "..." : opt.OptionText;
                        var button = new ButtonBuilder()
                            .WithLabel(label)
                            .WithCustomId(customId)
                            .WithStyle(ButtonStyle.Primary);

                        if (!string.IsNullOrEmpty(opt.Emote))
                        {
                            try
                            {
                                button.WithEmote(new Emoji(opt.Emote));
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
                        var closeButton = new ButtonBuilder()
                            .WithLabel("Close Poll")
                            .WithCustomId($"{ModalConstants.PollClosePrefix}{poll.CreatedById}~{poll.Id}")
                            .WithStyle(ButtonStyle.Danger)
                            .WithEmote(new Emoji("🔒"));
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
                    deps.Logger?.LogError(ex, "Error creating poll via API");
                    return Results.Json(new { success = false, error = "An error occurred while creating the poll" },
                        statusCode: 500);
                }
            });
        }
    }
}
