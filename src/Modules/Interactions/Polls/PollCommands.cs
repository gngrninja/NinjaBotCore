using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;
using DbServerPollSettings = NinjaBotCore.Database.ServerPollSettings;

namespace NinjaBotCore.Modules.Interactions.Polls
{
    [Group("poll", "Poll management commands")]
    [DefaultMemberPermissions(GuildPermission.ManageMessages)]
    public class PollCommands : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<PollCommands> _logger;

        public PollCommands(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<PollCommands> logger)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
        }

        [SlashCommand("create", "Create a new poll")]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task PollCreate()
        {
            // Note: Modal is handled by UserInteractions.HandleModal event handler (not Discord.Interactions framework)
            // This ensures immediate response timing to avoid "Unknown interaction" errors
            // We use ModalBuilder since the IModal class isn't cached by Discord.Interactions anymore
            var modal = new ModalBuilder()
                .WithTitle("Create a Poll")
                .WithCustomId("poll_create_modal")
                .AddTextInput("Question", "poll_question", placeholder: "What would you like to ask?", maxLength: 200)
                .AddTextInput("Options (one per line)", "poll_options", TextInputStyle.Paragraph,
                    placeholder: "Enter options separated by newlines. Leave empty for Yes/No poll.",
                    required: false, maxLength: 1000)
                .AddTextInput("Duration (optional)", "poll_duration", placeholder: "1h-720h, 1d-30d, or 1w-4w (max 30 days)",
                    required: false, maxLength: 20)
                .AddTextInput("Anonymous voting? (yes/no)", "poll_anonymous",
                    placeholder: "Leave empty for server default, 'yes' or 'no' to override",
                    required: false, maxLength: 10)
                .AddTextInput("Allow multiple selections? (yes/no)", "poll_multi_select",
                    placeholder: "Leave empty for single choice, 'yes' to allow multiple",
                    required: false, maxLength: 10)
                .Build();

            await Context.Interaction.RespondWithModalAsync(modal);
        }

        [SlashCommand("close", "Close a poll manually")]
        public async Task PollClose(long pollId)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var (success, message, poll) = await ClosePollAsync(pollId, (long)Context.User.Id);

                if (success && poll != null)
                {
                    _logger.LogInformation("Poll closed: {PollId} by {UserId}", pollId, Context.User.Id);

                    // Post results message
                    await PostPollResultsAsync(poll, closedBy: Context.User.Username, wasExpired: false);
                }

                await FollowupAsync(message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing poll {PollId}", pollId);
                await FollowupAsync("❌ An error occurred while closing the poll.", ephemeral: true);
            }
        }

        [SlashCommand("settings", "Configure poll settings for this server")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task PollSettings(
            [Summary("results_channel", "Channel where poll results are posted (leave empty for same channel)")] ITextChannel? resultsChannel = null,
            [Summary("mention_voters", "Mention voters when polls close")] bool? mentionVoters = null,
            [Summary("default_anonymous", "New polls default to anonymous voting")] bool? defaultAnonymous = null,
            [Summary("default_role", "Add a role that can vote on new polls (use multiple times to add more)")] IRole? defaultRole = null,
            [Summary("clear_roles", "Clear all default voting roles (everyone can vote)")] bool clearRoles = false)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var guildId = (long)Context.Guild.Id;

                // Get or create settings
                var settings = await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var settingsRepo = uow.Repository<DbServerPollSettings>();
                    var existing = await uow.Context.ServerPollSettings
                        .FirstOrDefaultAsync(s => s.DiscordGuildId == guildId);

                    if (existing == null)
                    {
                        existing = new DbServerPollSettings
                        {
                            DiscordGuildId = guildId,
                            MentionVotersOnClose = false,
                            ResultsChannelId = null,
                            DefaultAnonymous = false,
                            DefaultAllowedRoleIds = null
                        };
                        await settingsRepo.AddAsync(existing);
                    }

                    // Update settings if provided
                    bool changed = false;
                    if (resultsChannel != null)
                    {
                        existing.ResultsChannelId = (long)resultsChannel.Id;
                        changed = true;
                    }
                    if (mentionVoters.HasValue)
                    {
                        existing.MentionVotersOnClose = mentionVoters.Value;
                        changed = true;
                    }
                    if (defaultAnonymous.HasValue)
                    {
                        existing.DefaultAnonymous = defaultAnonymous.Value;
                        changed = true;
                    }
                    if (clearRoles)
                    {
                        existing.DefaultAllowedRoleIds = null;
                        changed = true;
                    }
                    else if (defaultRole != null)
                    {
                        // Add role to the list (avoid duplicates)
                        var roleId = ((long)defaultRole.Id).ToString();
                        if (string.IsNullOrEmpty(existing.DefaultAllowedRoleIds))
                        {
                            existing.DefaultAllowedRoleIds = roleId;
                        }
                        else
                        {
                            var existingRoles = existing.DefaultAllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (!existingRoles.Contains(roleId))
                            {
                                existing.DefaultAllowedRoleIds = existing.DefaultAllowedRoleIds + "," + roleId;
                            }
                        }
                        changed = true;
                    }

                    if (changed)
                    {
                        existing.SetById = (long)Context.User.Id;
                        existing.SetByName = Context.User.Username;
                        existing.TimeSet = DateTime.UtcNow;
                        await uow.SaveChangesAsync();
                    }

                    return existing;
                });

                // Build response embed
                var embed = new EmbedBuilder()
                    .WithTitle("📊 Poll Settings")
                    .WithColor(Color.Blue)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                // Results channel
                if (settings.ResultsChannelId.HasValue)
                {
                    embed.AddField("Results Channel", $"<#{settings.ResultsChannelId.Value}>", inline: true);
                }
                else
                {
                    embed.AddField("Results Channel", "Same as poll (default)", inline: true);
                }

                // Mention voters
                embed.AddField("Mention Voters", settings.MentionVotersOnClose ? "✅ Yes" : "❌ No", inline: true);

                // Default anonymous
                embed.AddField("Default Anonymous", settings.DefaultAnonymous ? "✅ Yes" : "❌ No", inline: true);

                // Default allowed roles
                if (!string.IsNullOrEmpty(settings.DefaultAllowedRoleIds))
                {
                    var roleIds = settings.DefaultAllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var roleMentions = roleIds.Select(id => $"<@&{id}>").ToList();
                    embed.AddField("Default Voting Roles", string.Join(", ", roleMentions), inline: false);
                }
                else
                {
                    embed.AddField("Default Voting Roles", "Everyone (no restrictions)", inline: false);
                }

                // Last updated
                if (settings.TimeSet.HasValue && !string.IsNullOrEmpty(settings.SetByName))
                {
                    embed.WithFooter($"Last updated by {settings.SetByName}");
                }

                var anyChanges = resultsChannel != null || mentionVoters.HasValue || defaultAnonymous.HasValue || defaultRole != null || clearRoles;
                var description = anyChanges
                    ? "✅ Settings updated successfully!"
                    : "Current poll settings for this server:";
                embed.WithDescription(description);

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating poll settings");
                await FollowupAsync("❌ An error occurred while updating settings.", ephemeral: true);
            }
        }

        // NOTE: Component interaction handlers (vote/close buttons) are handled via event system in UserInteractions.cs
        // This ensures immediate defer timing to avoid "Unknown interaction" errors

        // Helper methods for /poll close command

        private EmbedBuilder BuildPollEmbed(DbPoll poll, List<DbPollVote> votes)
        {
            var totalVotes = votes.Count;
            var embed = new EmbedBuilder()
                .WithTitle($"📊 {poll.Question}")
                .WithColor(poll.IsClosed ? Color.Red : Color.Blue)
                .WithFooter($"Created by {poll.CreatedByName} • {totalVotes} vote{(totalVotes != 1 ? "s" : "")}")
                .WithTimestamp(poll.CreatedAt);

            // Add fields for each option with vote counts
            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var optionVotes = votes.Count(v => v.OptionId == option.Id);
                var percentage = totalVotes > 0 ? (optionVotes * 100.0 / totalVotes) : 0;
                var barLength = (int)(percentage / 5); // 20 chars max
                var bar = new string('█', Math.Min(barLength, 20)) + new string('░', Math.Max(20 - barLength, 0));

                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField($"{emote}{option.OptionText}",
                    $"`{bar}` {percentage:F1}% ({optionVotes} vote{(optionVotes != 1 ? "s" : "")})",
                    inline: false);
            }

            // Add expiration info
            if (poll.ExpiresAt.HasValue && !poll.IsClosed)
            {
                embed.AddField("Expires", $"<t:{new DateTimeOffset(poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
            }

            if (poll.IsAnonymous && poll.PollType == "MultipleChoice")
                embed.AddField("Voting", "Anonymous \u2022 Multiple selections", inline: true);
            else if (poll.IsAnonymous)
                embed.AddField("Voting", "Anonymous", inline: true);
            else if (poll.PollType == "MultipleChoice")
                embed.AddField("Voting", "Multiple selections allowed", inline: true);

            if (poll.IsClosed)
            {
                embed.WithDescription("🔒 **This poll is closed**");
            }

            return embed;
        }

        private ComponentBuilder BuildPollComponents(DbPoll poll, ulong contextUserId)
        {
            var builder = new ComponentBuilder();

            if (poll.IsClosed)
            {
                // No buttons for closed polls
                return builder;
            }

            var options = poll.PollOptions.OrderBy(o => o.DisplayOrder).ToList();

            // Build vote buttons (up to 25 options across 5 rows)
            if (options.Count <= 25)
            {
                int currentRow = 0;
                int buttonsInRow = 0;

                foreach (var option in options)
                {
                    var customId = $"{ModalConstants.PollVotePrefix}{contextUserId}~{poll.Id}~{option.Id}";
                    var button = new ButtonBuilder()
                        .WithLabel(TruncateLabel(option.OptionText, 80))
                        .WithCustomId(customId)
                        .WithStyle(ButtonStyle.Primary);

                    if (!string.IsNullOrEmpty(option.Emote))
                    {
                        try
                        {
                            button.WithEmote(new Emoji(option.Emote));
                        }
                        catch
                        {
                            // Ignore invalid emotes
                        }
                    }

                    builder.WithButton(button, row: currentRow);

                    buttonsInRow++;
                    if (buttonsInRow >= 5)
                    {
                        currentRow++;
                        buttonsInRow = 0;
                    }
                }

                // Add close button on next available row (if space)
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
            }

            return builder;
        }

        private string TruncateLabel(string label, int maxLength)
        {
            if (label.Length <= maxLength)
                return label;
            return label.Substring(0, maxLength - 3) + "...";
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

            var embed = BuildPollEmbed(poll, votes);
            var components = BuildPollComponents(poll, 0); // Use 0 as placeholder

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

        private async Task<(bool success, string message, DbPoll? poll)> ClosePollAsync(long pollId, long userId)
        {
            return await WithScopedUnitOfWorkAsync(async uow =>
            {
                var pollRepo = uow.Repository<DbPoll>();
                var poll = await uow.Context.Polls
                    .Include(p => p.PollOptions)
                    .Include(p => p.PollVotes)
                    .AsSplitQuery() // Split query to avoid EF Core warning for multiple collection includes
                    .FirstOrDefaultAsync(p => p.Id == pollId);

                if (poll == null)
                    return (false, "❌ Poll not found.", null);

                if (poll.IsClosed)
                    return (false, "❌ Poll is already closed.", null);

                // Check permissions - only creator or moderators can close
                var guildUser = Context.User as SocketGuildUser;
                var isCreator = poll.CreatedById == userId;
                var isModerator = guildUser?.GuildPermissions.ManageMessages ?? false;

                if (!isCreator && !isModerator)
                    return (false, "❌ Only the poll creator or moderators can close this poll.", null);

                poll.IsClosed = true;
                poll.ClosedAt = DateTime.UtcNow;
                await uow.SaveChangesAsync();

                // Update poll message
                await UpdatePollMessageAsync(pollId);

                var totalVotes = poll.PollVotes.Count;
                return (true, $"✅ Poll closed successfully. Total votes: {totalVotes}", poll);
            });
        }

        /// <summary>
        /// Posts a poll results message to the appropriate channel.
        /// </summary>
        public async Task PostPollResultsAsync(DbPoll poll, string? closedBy = null, bool wasExpired = false)
        {
            try
            {
                var guildId = (long)poll.GuildId;

                // Get server poll settings
                var settings = await WithDbAsync(async db =>
                    await db.ServerPollSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == guildId));

                // Determine target channel
                var targetChannelId = settings?.ResultsChannelId ?? poll.ChannelId;

                var guild = _client.GetGuild((ulong)poll.GuildId);
                var channel = guild?.GetChannel((ulong)targetChannelId) as IMessageChannel;

                if (channel == null)
                {
                    _logger.LogWarning("Could not find channel {ChannelId} for poll results", targetChannelId);
                    return;
                }

                // Build results embed
                var resultsBuilder = new PollResultsBuilder();
                var options = poll.PollOptions?.ToList() ?? new List<DbPollOption>();
                var votes = poll.PollVotes?.ToList() ?? new List<DbPollVote>();
                var embed = resultsBuilder.BuildResultsEmbed(poll, options, votes, closedBy, wasExpired);

                // Build voter mentions if enabled
                string? content = null;
                if (settings?.MentionVotersOnClose == true && !poll.IsAnonymous)
                {
                    content = resultsBuilder.BuildVoterMentions(votes, poll.IsAnonymous);
                }

                // Send results message as a reply to the original poll
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

                _logger.LogInformation("Posted poll results for poll {PollId} to channel {ChannelId}", poll.Id, targetChannelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error posting poll results for poll {PollId}", poll.Id);
            }
        }
    }
}
