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
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions;
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;

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
                .AddTextInput("Duration (optional)", "poll_duration", placeholder: "1h, 12h, 24h, 1w, or leave empty",
                    required: false, maxLength: 20)
                .Build();

            await Context.Interaction.RespondWithModalAsync(modal);
        }

        [SlashCommand("close", "Close a poll manually")]
        public async Task PollClose(long pollId)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var (success, message) = await ClosePollAsync(pollId, (long)Context.User.Id);

                if (success)
                {
                    _logger.LogInformation("Poll closed: {PollId} by {UserId}", pollId, Context.User.Id);
                }

                await FollowupAsync(message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing poll {PollId}", pollId);
                await FollowupAsync("❌ An error occurred while closing the poll.", ephemeral: true);
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
                    var customId = $"poll_vote~{contextUserId}~{poll.Id}~{option.Id}";
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
                        .WithCustomId($"poll_close~{poll.CreatedById}~{poll.Id}")
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

        private async Task<(bool success, string message)> ClosePollAsync(long pollId, long userId)
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
                    return (false, "❌ Poll not found.");

                if (poll.IsClosed)
                    return (false, "❌ Poll is already closed.");

                // Check permissions - only creator or moderators can close
                var guildUser = Context.User as SocketGuildUser;
                var isCreator = poll.CreatedById == userId;
                var isModerator = guildUser?.GuildPermissions.ManageMessages ?? false;

                if (!isCreator && !isModerator)
                    return (false, "❌ Only the poll creator or moderators can close this poll.");

                poll.IsClosed = true;
                poll.ClosedAt = DateTime.UtcNow;
                await uow.SaveChangesAsync();

                // Update poll message
                await UpdatePollMessageAsync(pollId);

                var totalVotes = poll.PollVotes.Count;
                return (true, $"✅ Poll closed successfully. Total votes: {totalVotes}");
            });
        }
    }
}
