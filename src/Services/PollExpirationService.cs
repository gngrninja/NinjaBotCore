using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Polls;
using NinjaBotCore.Repositories;

// Type aliases to avoid naming conflict with Discord.Poll
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Background service that periodically checks for expired polls and automatically closes them
    /// </summary>
    public class PollExpirationService : IHostedService, IDisposable
    {
        private readonly ILogger<PollExpirationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DiscordShardedClient _client;
        private Timer _timer;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public PollExpirationService(
            ILogger<PollExpirationService> logger,
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _client = client;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PollExpirationService starting - using smart timer based on next poll expiration");

            // Start one-shot timer - will reschedule itself dynamically
            _timer = new Timer(
                _ => _ = CheckExpiredPollsAsync(_cts.Token),
                null,
                TimeSpan.FromSeconds(30), // Initial delay (30 seconds after startup)
                Timeout.InfiniteTimeSpan  // One-shot timer (reschedules itself)
            );

            return Task.CompletedTask;
        }

        private async Task CheckExpiredPollsAsync(CancellationToken cancellationToken)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // First, query for the next poll expiration time (lightweight query)
                var nextExpiration = await db.Polls
                    .Where(p => !p.IsClosed && p.ExpiresAt.HasValue)
                    .OrderBy(p => p.ExpiresAt)
                    .Select(p => p.ExpiresAt.Value)
                    .FirstOrDefaultAsync(cancellationToken);

                // If no polls with expiration, check again in 5 minutes
                if (nextExpiration == default)
                {
                    _logger.LogDebug("No polls with expiration found - checking again in 5 minutes");
                    _timer?.Change(TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
                    return;
                }

                var timeUntilExpiration = nextExpiration - DateTime.UtcNow;

                // If poll(s) have already expired, process them immediately
                if (timeUntilExpiration <= TimeSpan.Zero)
                {
                    // Find all polls that are expired but not yet closed
                    var expiredPolls = await db.Polls
                        .Include(p => p.PollOptions)
                        .Include(p => p.PollVotes)
                        .AsSplitQuery() // Split query to avoid EF Core warning for multiple collection includes
                        .Where(p => !p.IsClosed && p.ExpiresAt.HasValue && p.ExpiresAt.Value <= DateTime.UtcNow)
                        .ToListAsync(cancellationToken);

                    if (expiredPolls.Any())
                    {
                        _logger.LogInformation("Found {Count} expired polls to close", expiredPolls.Count);

                        foreach (var poll in expiredPolls)
                        {
                            try
                            {
                                // Close the poll
                                poll.IsClosed = true;
                                poll.ClosedAt = DateTime.UtcNow;
                                await db.SaveChangesAsync(cancellationToken);

                                _logger.LogInformation("Closed expired poll {PollId} in guild {GuildId}", poll.Id, poll.GuildId);

                                // Update Discord message
                                await UpdatePollMessageAsync(poll);

                                // Post poll results
                                await PostPollResultsAsync(poll, db);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error closing expired poll {PollId}", poll.Id);
                            }
                        }
                    }

                    // Check again immediately for next batch of expired polls
                    _logger.LogDebug("Processed expired polls - checking immediately for more");
                    _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    // Schedule timer to fire when next poll expires (max 5 minutes to stay responsive)
                    var delay = timeUntilExpiration > TimeSpan.FromMinutes(5)
                        ? TimeSpan.FromMinutes(5)
                        : timeUntilExpiration;

                    _logger.LogDebug("Next poll expires in {TimeUntil:hh\\:mm\\:ss} - scheduling check in {Delay:hh\\:mm\\:ss}",
                        timeUntilExpiration, delay);

                    _timer?.Change(delay, Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PollExpirationService check cycle - retrying in 1 minute");
                // On error, retry in 1 minute
                _timer?.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            }
        }

        private async Task UpdatePollMessageAsync(DbPoll poll)
        {
            try
            {
                var guild = _client.GetGuild((ulong)poll.GuildId);
                if (guild == null)
                {
                    _logger.LogWarning("Guild {GuildId} not found for poll {PollId}", poll.GuildId, poll.Id);
                    return;
                }

                var channel = guild.GetTextChannel((ulong)poll.ChannelId);
                if (channel == null)
                {
                    _logger.LogWarning("Channel {ChannelId} not found for poll {PollId}", poll.ChannelId, poll.Id);
                    return;
                }

                var message = await channel.GetMessageAsync((ulong)poll.MessageId);
                if (message is not IUserMessage userMessage)
                {
                    _logger.LogWarning("Message {MessageId} not found for poll {PollId}", poll.MessageId, poll.Id);
                    return;
                }

                // Build updated embed (red color, closed status)
                var embed = BuildClosedPollEmbed(poll);

                // Disable all buttons, but keep View Voters for non-anonymous polls
                var components = BuildDisabledComponents(poll);

                await userMessage.ModifyAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components.Build();
                });

                _logger.LogDebug("Updated Discord message for closed poll {PollId}", poll.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Discord message for poll {PollId}", poll.Id);
            }
        }

        private async Task PostPollResultsAsync(DbPoll poll, NinjaBotEntities db)
        {
            try
            {
                // Get server poll settings
                var settings = await db.ServerPollSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == poll.GuildId);

                // Determine target channel
                var targetChannelId = settings?.ResultsChannelId ?? poll.ChannelId;

                var guild = _client.GetGuild((ulong)poll.GuildId);
                if (guild == null)
                {
                    _logger.LogWarning("Guild {GuildId} not found for poll results {PollId}", poll.GuildId, poll.Id);
                    return;
                }

                var channel = guild.GetTextChannel((ulong)targetChannelId);
                if (channel == null)
                {
                    _logger.LogWarning("Channel {ChannelId} not found for poll results {PollId}", targetChannelId, poll.Id);
                    return;
                }

                // Build results embed
                var resultsBuilder = new PollResultsBuilder();
                var options = poll.PollOptions?.ToList() ?? new List<DbPollOption>();
                var votes = poll.PollVotes?.ToList() ?? new List<DbPollVote>();
                var embed = resultsBuilder.BuildResultsEmbed(poll, options, votes, closedBy: null, wasExpired: true);

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

                _logger.LogInformation("Posted poll results for expired poll {PollId} to channel {ChannelId}", poll.Id, targetChannelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error posting poll results for poll {PollId}", poll.Id);
            }
        }

        private EmbedBuilder BuildClosedPollEmbed(DbPoll poll)
        {
            var totalVotes = poll.PollVotes.Count;
            var embed = new EmbedBuilder()
                .WithTitle($"📊 {poll.Question}")
                .WithColor(Color.Red) // Red for closed
                .WithFooter($"Created by {poll.CreatedByName} • {totalVotes} total votes • Closed")
                .WithTimestamp(poll.CreatedAt);

            // Add options with vote counts
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

                embed.AddField(
                    $"{option.Emote} {option.OptionText}",
                    $"{progressBar} {percentage:F1}% ({optionVotes} votes){votersList}",
                    inline: false
                );
            }

            if (poll.ExpiresAt.HasValue)
            {
                embed.AddField("Expired", $"<t:{((DateTimeOffset)poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
            }

            return embed;
        }

        private ComponentBuilder BuildDisabledComponents(DbPoll poll)
        {
            var builder = new ComponentBuilder();

            // Add a single disabled button indicating poll is closed
            builder.WithButton("Poll Closed", "poll_closed", ButtonStyle.Secondary, disabled: true);

            // Keep "View Voters" button for non-anonymous polls
            if (!poll.IsAnonymous)
            {
                builder.WithButton("View Voters", $"{ModalConstants.PollViewVotersPrefix}{poll.Id}",
                    ButtonStyle.Secondary, emote: new Emoji("👥"));
            }

            return builder;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PollExpirationService stopping...");
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _timer?.Dispose();
            _cts?.Dispose();
            _logger.LogInformation("PollExpirationService disposed");
        }
    }
}
