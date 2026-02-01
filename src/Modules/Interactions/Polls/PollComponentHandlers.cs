using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions;
using NinjaBotCore.Repositories;
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;
using DbServerPollSettings = NinjaBotCore.Database.ServerPollSettings;

namespace NinjaBotCore.Modules.Interactions.Polls
{
    /// <summary>
    /// Attribute-based handlers for poll interactions (votes, close, create modal).
    ///
    /// NOTE: This class intentionally has NO [Group] attribute because
    /// [ComponentInteraction] and [ModalInteraction] handlers don't work inside [Group] classes.
    /// </summary>
    public class PollComponentHandlers : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<PollComponentHandlers> _logger;

        public PollComponentHandlers(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<PollComponentHandlers> logger)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Handles poll vote button clicks.
        /// CustomId format: poll_vote~{userId}~{pollId}~{optionId}
        /// </summary>
        [ComponentInteraction("poll_vote~*~*~*")]
        public async Task HandlePollVote(string oderId, string pollIdStr, string optionIdStr)
        {
            _logger.LogInformation("[POLL] Attribute handler received poll vote: {PollId}~{OptionId}", pollIdStr, optionIdStr);

            await DeferAsync(ephemeral: true);

            try
            {
                if (!long.TryParse(pollIdStr, out var pollId) ||
                    !long.TryParse(optionIdStr, out var optionId))
                {
                    await FollowupAsync("Invalid poll data.", ephemeral: true);
                    return;
                }

                var userId = (long)Context.User.Id;
                var userName = Context.User.Username;

                // Process vote
                var result = await ProcessPollVoteAsync(pollId, optionId, userId, userName);

                // Update poll message
                await UpdatePollMessageAsync(pollId);

                await FollowupAsync(result, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POLL] Error processing poll vote");
                await FollowupAsync("An error occurred while processing your vote.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handles poll view voters button clicks.
        /// CustomId format: poll_voters~{pollId}
        /// Shows ephemeral message with per-option voter breakdown.
        /// </summary>
        [ComponentInteraction("poll_voters~*")]
        public async Task HandlePollViewVoters(string pollIdStr)
        {
            _logger.LogInformation("[POLL] Attribute handler received view voters: {PollId}", pollIdStr);

            await DeferAsync(ephemeral: true);

            try
            {
                if (!long.TryParse(pollIdStr, out var pollId))
                {
                    await FollowupAsync("Invalid poll data.", ephemeral: true);
                    return;
                }

                var embed = await BuildVotersEmbedAsync(pollId);
                if (embed == null)
                {
                    await FollowupAsync("Poll not found or is anonymous.", ephemeral: true);
                    return;
                }

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POLL] Error viewing poll voters");
                await FollowupAsync("An error occurred while fetching voters.", ephemeral: true);
            }
        }

        private async Task<EmbedBuilder?> BuildVotersEmbedAsync(long pollId)
        {
            return await WithDbAsync(async db =>
            {
                var poll = await db.Polls
                    .Include(p => p.PollOptions)
                    .Include(p => p.PollVotes)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == pollId);

                if (poll == null || poll.IsAnonymous)
                    return null;

                var totalVotes = poll.PollVotes.Count;

                var embed = new EmbedBuilder()
                    .WithTitle($"👥 Voters for: {poll.Question}")
                    .WithColor(Color.Gold)
                    .WithFooter($"{totalVotes} total vote{(totalVotes != 1 ? "s" : "")}")
                    .WithTimestamp(DateTime.UtcNow);

                foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
                {
                    var optionVotes = poll.PollVotes
                        .Where(v => v.OptionId == option.Id)
                        .OrderBy(v => v.VotedAt)
                        .ToList();

                    var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                    var voteCount = optionVotes.Count;

                    string voterList;
                    if (voteCount == 0)
                    {
                        voterList = "*No votes*";
                    }
                    else
                    {
                        var mentions = optionVotes
                            .Take(15)
                            .Select(v => $"<@{v.UserId}>")
                            .ToList();

                        voterList = string.Join(", ", mentions);
                        if (voteCount > 15)
                        {
                            voterList += $"\n*+{voteCount - 15} more*";
                        }
                    }

                    embed.AddField(
                        $"{emote}{option.OptionText} ({voteCount})",
                        voterList,
                        inline: false);
                }

                return embed;
            });
        }

        /// <summary>
        /// Handles poll close button clicks.
        /// CustomId format: poll_close~{creatorId}~{pollId}
        /// </summary>
        [ComponentInteraction("poll_close~*~*")]
        public async Task HandlePollClose(string creatorIdStr, string pollIdStr)
        {
            _logger.LogInformation("[POLL] Attribute handler received poll close: {PollId}", pollIdStr);

            await DeferAsync(ephemeral: true);

            try
            {
                if (!long.TryParse(pollIdStr, out var pollId))
                {
                    await FollowupAsync("Invalid poll data.", ephemeral: true);
                    return;
                }

                var userId = (long)Context.User.Id;
                var channel = Context.Channel as SocketGuildChannel;

                var (success, message) = await ClosePollAsync(pollId, userId, channel);

                await FollowupAsync(message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POLL] Error closing poll");
                await FollowupAsync("An error occurred while closing the poll.", ephemeral: true);
            }
        }

        private async Task<string> ProcessPollVoteAsync(long pollId, long optionId, long userId, string userName)
        {
            return await WithScopedUnitOfWorkAsync(async uow =>
            {
                var db = uow.Context;

                var poll = await db.Polls
                    .Include(p => p.PollOptions)
                    .FirstOrDefaultAsync(p => p.Id == pollId);

                if (poll == null)
                    return "Poll not found.";

                if (poll.IsClosed)
                    return "This poll is closed.";

                if (poll.ExpiresAt.HasValue && DateTime.UtcNow > poll.ExpiresAt.Value)
                    return "This poll has expired.";

                // Check role restrictions
                if (!string.IsNullOrEmpty(poll.AllowedRoleIds))
                {
                    var allowedRoles = poll.AllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(r => long.TryParse(r.Trim(), out var id) ? id : 0)
                        .Where(id => id > 0)
                        .ToHashSet();

                    var guild = _client.GetGuild((ulong)poll.GuildId);
                    var member = guild?.GetUser((ulong)userId);

                    if (member == null || !member.Roles.Any(r => allowedRoles.Contains((long)r.Id)))
                        return "You don't have permission to vote on this poll.";
                }

                // Check if option exists
                if (!poll.PollOptions.Any(o => o.Id == optionId))
                    return "Invalid option.";

                // Check existing votes
                var existingVotes = await db.PollVotes
                    .Where(v => v.PollId == pollId && v.UserId == userId)
                    .ToListAsync();

                if (poll.PollType == "SingleChoice" || poll.PollType == "YesNo")
                {
                    if (existingVotes.Any())
                    {
                        if (!poll.AllowVoteChange)
                            return "You've already voted and cannot change your vote.";

                        // Remove old votes
                        db.PollVotes.RemoveRange(existingVotes);
                    }
                }
                else if (poll.PollType == "MultipleChoice")
                {
                    var existingVote = existingVotes.FirstOrDefault(v => v.OptionId == optionId);
                    if (existingVote != null)
                    {
                        db.PollVotes.Remove(existingVote);
                        await uow.SaveChangesAsync();
                        return "Vote removed.";
                    }
                }

                // Add new vote
                var newVote = new PollVote
                {
                    PollId = pollId,
                    OptionId = optionId,
                    UserId = userId,
                    UserName = userName,
                    VotedAt = DateTime.UtcNow
                };

                await db.PollVotes.AddAsync(newVote);
                await uow.SaveChangesAsync();

                return "Vote recorded!";
            });
        }

        private async Task UpdatePollMessageAsync(long pollId)
        {
            var (poll, votes) = await WithDbAsync(async db =>
            {
                var p = await db.Polls
                    .Include(p => p.PollOptions)
                    .FirstOrDefaultAsync(p => p.Id == pollId);

                if (p == null)
                    return (null, null);

                var v = await db.PollVotes
                    .Where(vote => vote.PollId == pollId)
                    .ToListAsync();

                return (p, v);
            });

            if (poll == null)
                return;

            var totalVotes = votes?.Count ?? 0;
            var embed = new EmbedBuilder()
                .WithTitle($"{poll.Question}")
                .WithColor(poll.IsClosed ? Color.Red : Color.Blue)
                .WithFooter($"Created by {poll.CreatedByName} - {totalVotes} vote{(totalVotes != 1 ? "s" : "")}")
                .WithTimestamp(poll.CreatedAt);

            // Add fields for each option with vote counts
            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var optionVotes = votes?.Count(v => v.OptionId == option.Id) ?? 0;
                var percentage = totalVotes > 0 ? (optionVotes * 100.0 / totalVotes) : 0;
                var barLength = (int)(percentage / 5);
                var bar = new string('█', Math.Min(barLength, 20)) + new string('░', Math.Max(20 - barLength, 0));

                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField($"{emote}{option.OptionText}",
                    $"`{bar}` {percentage:F1}% ({optionVotes} vote{(optionVotes != 1 ? "s" : "")})",
                    inline: false);
            }

            if (poll.ExpiresAt.HasValue && !poll.IsClosed)
            {
                embed.AddField("Expires", $"<t:{new DateTimeOffset(poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
            }

            if (poll.IsAnonymous)
            {
                embed.AddField("Voting", "Anonymous", inline: true);
            }

            if (poll.IsClosed)
            {
                embed.WithDescription("**This poll is closed**");
            }

            var components = BuildPollComponents(poll);

            var guild = _client.GetGuild((ulong)poll.GuildId);
            var channel = guild?.GetChannel((ulong)poll.ChannelId) as IMessageChannel;
            if (channel == null)
                return;

            var message = await channel.GetMessageAsync((ulong)poll.MessageId);
            if (message is IUserMessage userMessage)
            {
                await userMessage.ModifyAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components.Build();
                });
            }
        }

        private ComponentBuilder BuildPollComponents(DbPoll poll)
        {
            var builder = new ComponentBuilder();

            if (poll.IsClosed)
            {
                // Still show "View Voters" button for closed non-anonymous polls
                if (!poll.IsAnonymous)
                {
                    var viewVotersButton = new ButtonBuilder()
                        .WithLabel("View Voters")
                        .WithCustomId($"{ModalConstants.PollViewVotersPrefix}{poll.Id}")
                        .WithStyle(ButtonStyle.Secondary)
                        .WithEmote(new Emoji("👥"));
                    builder.WithButton(viewVotersButton, row: 0);
                }
                return builder;
            }

            var options = poll.PollOptions.OrderBy(o => o.DisplayOrder).ToList();

            if (options.Count <= 25)
            {
                int currentRow = 0;
                int buttonsInRow = 0;

                foreach (var option in options)
                {
                    var customId = $"{ModalConstants.PollVotePrefix}0~{poll.Id}~{option.Id}";
                    var button = new ButtonBuilder()
                        .WithLabel(option.OptionText.Length > 80 ? option.OptionText.Substring(0, 77) + "..." : option.OptionText)
                        .WithCustomId(customId)
                        .WithStyle(ButtonStyle.Primary);

                    if (!string.IsNullOrEmpty(option.Emote))
                    {
                        try { button.WithEmote(new Emoji(option.Emote)); }
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

                if (currentRow < 4 || (currentRow == 4 && buttonsInRow == 0))
                {
                    var closeRow = buttonsInRow > 0 ? currentRow + 1 : currentRow;
                    var closeButton = new ButtonBuilder()
                        .WithLabel("Close Poll")
                        .WithCustomId($"{ModalConstants.PollClosePrefix}{poll.CreatedById}~{poll.Id}")
                        .WithStyle(ButtonStyle.Danger)
                        .WithEmote(new Emoji("🔒"));
                    builder.WithButton(closeButton, row: closeRow);

                    // Add "View Voters" button for non-anonymous polls
                    if (!poll.IsAnonymous)
                    {
                        var viewVotersButton = new ButtonBuilder()
                            .WithLabel("View Voters")
                            .WithCustomId($"{ModalConstants.PollViewVotersPrefix}{poll.Id}")
                            .WithStyle(ButtonStyle.Secondary)
                            .WithEmote(new Emoji("👥"));
                        builder.WithButton(viewVotersButton, row: closeRow);
                    }
                }
            }

            return builder;
        }

        private async Task<(bool success, string message)> ClosePollAsync(long pollId, long userId, SocketGuildChannel? channel)
        {
            return await WithScopedUnitOfWorkAsync(async uow =>
            {
                var db = uow.Context;

                var poll = await db.Polls
                    .Include(p => p.PollOptions)
                    .Include(p => p.PollVotes)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == pollId);

                if (poll == null)
                    return (false, "Poll not found.");

                if (poll.IsClosed)
                    return (false, "Poll is already closed.");

                // Check permissions
                var guildUser = channel?.Guild?.GetUser((ulong)userId);
                var isCreator = poll.CreatedById == userId;
                var isModerator = guildUser?.GuildPermissions.ManageMessages ?? false;

                if (!isCreator && !isModerator)
                    return (false, "Only the poll creator or moderators can close this poll.");

                poll.IsClosed = true;
                poll.ClosedAt = DateTime.UtcNow;
                await uow.SaveChangesAsync();

                // Update poll message
                await UpdatePollMessageAsync(pollId);

                // Post results
                await PostPollResultsAsync(poll, guildUser?.Username ?? "Unknown", db);

                var totalVotes = poll.PollVotes.Count;
                return (true, $"Poll closed successfully. Total votes: {totalVotes}");
            });
        }

        private async Task PostPollResultsAsync(DbPoll poll, string closedBy, NinjaBotEntities db)
        {
            try
            {
                var settings = await db.ServerPollSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == poll.GuildId);

                var targetChannelId = settings?.ResultsChannelId ?? poll.ChannelId;

                var guild = _client.GetGuild((ulong)poll.GuildId);
                if (guild == null) return;

                var channel = guild.GetTextChannel((ulong)targetChannelId);
                if (channel == null) return;

                var resultsBuilder = new PollResultsBuilder();
                var options = poll.PollOptions?.ToList() ?? new System.Collections.Generic.List<PollOption>();
                var votes = poll.PollVotes?.ToList() ?? new System.Collections.Generic.List<PollVote>();
                var embed = resultsBuilder.BuildResultsEmbed(poll, options, votes, closedBy: closedBy, wasExpired: false);

                string? content = null;
                if (settings?.MentionVotersOnClose == true && !poll.IsAnonymous)
                {
                    content = resultsBuilder.BuildVoterMentions(votes, poll.IsAnonymous);
                }

                var messageReference = new MessageReference(
                    messageId: (ulong)poll.MessageId,
                    channelId: (ulong)poll.ChannelId,
                    guildId: (ulong)poll.GuildId,
                    failIfNotExists: false);

                await channel.SendMessageAsync(
                    text: string.IsNullOrEmpty(content) ? null : content,
                    embed: embed,
                    messageReference: messageReference,
                    allowedMentions: AllowedMentions.All);

                _logger.LogInformation("[POLL] Posted poll results for poll {PollId}", poll.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POLL] Error posting poll results for poll {PollId}", poll.Id);
            }
        }

        #region Modal Handler

        private static readonly Regex DurationRegex = new(@"^(\d+)(h|d|w)$", RegexOptions.Compiled);

        /// <summary>
        /// Handles poll creation modal submission.
        /// </summary>
        [ModalInteraction("poll_create_modal")]
        public async Task HandlePollCreateModal(PollCreateModal modal)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Validate question
                if (string.IsNullOrWhiteSpace(modal.Question))
                {
                    await FollowupAsync("Poll question cannot be empty.", ephemeral: true);
                    return;
                }

                // Parse options
                List<string> options;
                string pollType;

                if (string.IsNullOrWhiteSpace(modal.Options))
                {
                    options = new List<string> { "Yes", "No" };
                    pollType = "YesNo";
                }
                else
                {
                    options = modal.Options.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(o => !string.IsNullOrWhiteSpace(o))
                        .Take(25)
                        .ToList();

                    if (options.Count < 2)
                    {
                        await FollowupAsync("Poll must have at least 2 options.", ephemeral: true);
                        return;
                    }

                    pollType = "SingleChoice";
                }

                // Parse duration
                DateTime? expiresAt = null;
                if (!string.IsNullOrWhiteSpace(modal.Duration))
                {
                    expiresAt = ParsePollDuration(modal.Duration);
                    if (!expiresAt.HasValue)
                    {
                        await FollowupAsync("Invalid duration. Use 1h-720h, 1d-30d, or 1w-4w (max 30 days).", ephemeral: true);
                        return;
                    }
                }

                // Create poll
                DbPoll poll = await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var settingsRepo = uow.Repository<DbServerPollSettings>();
                    var pollRepo = uow.Repository<DbPoll>();
                    var optionRepo = uow.Repository<DbPollOption>();

                    var serverSettings = await uow.Context.ServerPollSettings
                        .FirstOrDefaultAsync(s => s.DiscordGuildId == (long)Context.Guild.Id);

                    bool isAnonymous;
                    if (!string.IsNullOrWhiteSpace(modal.Anonymous))
                    {
                        isAnonymous = modal.Anonymous.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        isAnonymous = serverSettings?.DefaultAnonymous ?? false;
                    }

                    var allowedRoleIds = serverSettings?.DefaultAllowedRoleIds;

                    var newPoll = new DbPoll
                    {
                        Question = modal.Question.Trim(),
                        PollType = pollType,
                        AllowVoteChange = true,
                        IsAnonymous = isAnonymous,
                        IsClosed = false,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = expiresAt,
                        CreatedById = (long)Context.User.Id,
                        CreatedByName = Context.User.Username,
                        GuildId = (long)Context.Guild.Id,
                        ChannelId = (long)Context.Channel.Id,
                        MessageId = 0,
                        AllowedRoleIds = allowedRoleIds
                    };

                    await pollRepo.AddAsync(newPoll);

                    for (int i = 0; i < options.Count; i++)
                    {
                        var option = new DbPollOption
                        {
                            Poll = newPoll,
                            OptionText = options[i],
                            DisplayOrder = i,
                            Emote = GetPollEmote(i)
                        };
                        await optionRepo.AddAsync(option);
                    }

                    await uow.SaveChangesAsync();

                    return await uow.Context.Set<DbPoll>()
                        .Include(p => p.PollOptions)
                        .FirstOrDefaultAsync(p => p.Id == newPoll.Id);
                });

                if (poll == null)
                {
                    await FollowupAsync("Failed to create poll.", ephemeral: true);
                    return;
                }

                // Post poll message
                var embed = BuildPollEmbedForCreate(poll);
                var pollComponents = BuildPollComponents(poll);

                var guild = _client.GetGuild((ulong)Context.Guild.Id);
                var channel = guild?.GetChannel((ulong)Context.Channel.Id) as ISocketMessageChannel;
                if (channel == null)
                {
                    await CleanupOrphanedPollAsync(poll.Id);
                    await FollowupAsync("Could not access channel.", ephemeral: true);
                    return;
                }

                IUserMessage message;
                try
                {
                    message = await channel.SendMessageAsync(embed: embed.Build(), components: pollComponents.Build());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to post poll message, cleaning up poll {PollId}", poll.Id);
                    await CleanupOrphanedPollAsync(poll.Id);
                    await FollowupAsync("Failed to post poll message. Please try again.", ephemeral: true);
                    return;
                }

                // Update poll with message ID
                await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var pollToUpdate = await uow.Context.Polls.FirstOrDefaultAsync(p => p.Id == poll.Id);
                    if (pollToUpdate != null)
                    {
                        pollToUpdate.MessageId = (long)message.Id;
                        await uow.SaveChangesAsync();
                    }
                });

                _logger.LogInformation("Poll created: {PollId} by {UserId} in {GuildId}", poll.Id, Context.User.Id, Context.Guild.Id);

                await FollowupAsync($"Poll created successfully! Check <#{Context.Channel.Id}>", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling poll modal");
                await FollowupAsync("An error occurred while creating the poll.", ephemeral: true);
            }
        }

        private EmbedBuilder BuildPollEmbedForCreate(DbPoll poll)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"{poll.Question}")
                .WithColor(Color.Blue)
                .WithFooter($"Created by {poll.CreatedByName} - 0 votes")
                .WithTimestamp(poll.CreatedAt);

            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var bar = new string('░', 20);
                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField($"{emote}{option.OptionText}", $"`{bar}` 0.0% (0 votes)", inline: false);
            }

            if (poll.ExpiresAt.HasValue)
            {
                embed.AddField("Expires", $"<t:{new DateTimeOffset(poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
            }

            if (poll.IsAnonymous)
            {
                embed.AddField("Voting", "Anonymous", inline: true);
            }

            if (!string.IsNullOrEmpty(poll.AllowedRoleIds))
            {
                var roleIds = poll.AllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var roleMentions = roleIds.Select(id => $"<@&{id}>").Take(5).ToList();
                var roleText = string.Join(", ", roleMentions);
                if (roleIds.Length > 5) roleText += $" +{roleIds.Length - 5} more";
                embed.AddField("Restricted to", roleText, inline: true);
            }

            return embed;
        }

        private async Task CleanupOrphanedPollAsync(long pollId)
        {
            try
            {
                await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var options = await uow.Context.PollOptions.Where(o => o.PollId == pollId).ToListAsync();
                    uow.Context.PollOptions.RemoveRange(options);

                    var poll = await uow.Context.Polls.FirstOrDefaultAsync(p => p.Id == pollId);
                    if (poll != null) uow.Context.Polls.Remove(poll);

                    await uow.SaveChangesAsync();
                });
                _logger.LogInformation("Cleaned up orphaned poll {PollId}", pollId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup orphaned poll {PollId}", pollId);
            }
        }

        private DateTime? ParsePollDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration)) return null;

            var match = DurationRegex.Match(duration.Trim().ToLower());
            if (!match.Success) return null;

            var value = int.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value;

            var totalHours = unit switch
            {
                "h" => value,
                "d" => value * 24,
                "w" => value * 24 * 7,
                _ => 0
            };

            if (totalHours > 720 || totalHours <= 0) return null;

            return unit switch
            {
                "h" => DateTime.UtcNow.AddHours(value),
                "d" => DateTime.UtcNow.AddDays(value),
                "w" => DateTime.UtcNow.AddDays(value * 7),
                _ => null
            };
        }

        private string GetPollEmote(int index)
        {
            var emotes = new[] { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟",
                               "🇦", "🇧", "🇨", "🇩", "🇪", "🇫", "🇬", "🇭", "🇮", "🇯" };
            return index < emotes.Length ? emotes[index] : "▪️";
        }

        #endregion
    }

    #region Modal Definition

    public class PollCreateModal : IModal
    {
        public string Title => "Create a Poll";

        [InputLabel("Question")]
        [ModalTextInput("poll_question", placeholder: "What would you like to ask?", maxLength: 200)]
        public string Question { get; set; }

        [InputLabel("Options (one per line)")]
        [ModalTextInput("poll_options", TextInputStyle.Paragraph, placeholder: "Enter options separated by newlines. Leave empty for Yes/No poll.", maxLength: 1000)]
        [RequiredInput(false)]
        public string Options { get; set; }

        [InputLabel("Duration (optional)")]
        [ModalTextInput("poll_duration", placeholder: "1h-720h, 1d-30d, or 1w-4w (max 30 days)", maxLength: 20)]
        [RequiredInput(false)]
        public string Duration { get; set; }

        [InputLabel("Anonymous voting? (yes/no)")]
        [ModalTextInput("poll_anonymous", placeholder: "Leave empty for server default, 'yes' or 'no' to override", maxLength: 10)]
        [RequiredInput(false)]
        public string Anonymous { get; set; }
    }

    #endregion
}
