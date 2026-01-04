using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Service responsible for handling away system functionality.
    /// Listens to message events and notifies when away users are mentioned.
    /// </summary>
    public class AwaySystemService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private bool _disposed;

        public AwaySystemService(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<AwaySystemService>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

            // Subscribe to message events for away mention detection
            _client.MessageReceived += HandleAwayMentions;

            _logger.LogInformation("AwaySystemService loaded - monitoring for away user mentions");
        }

        /// <summary>
        /// Handles messages to check if any mentioned users are away
        /// </summary>
        private async Task HandleAwayMentions(SocketMessage messageDetails)
        {
            try
            {
                // Early returns - fast filtering
                if (messageDetails.Author.IsBot) return;
                if (!messageDetails.MentionedUsers.Any()) return;

                var mentionedUsers = messageDetails.MentionedUsers.ToList();

                // Single repository for all users - reuse DbContext
                await using var awayRepo = new Repository<AwaySystem>(_scopeFactory);

                foreach (var user in mentionedUsers)
                {
                    var awayUser = await awayRepo.FirstOrDefaultAsync(a =>
                        a.UserId == user.Id && a.Status == true);

                    if (awayUser == null) continue;

                    // Calculate away duration
                    string awayDuration = string.Empty;
                    if (awayUser.TimeAway.HasValue)
                    {
                        var duration = DateTime.UtcNow - awayUser.TimeAway.Value;
                        awayDuration = $"**{duration.Days}** days, **{duration.Hours}** hours, **{duration.Minutes}** minutes, and **{duration.Seconds}** seconds";
                    }

                    _logger.LogInformation("Mentioned user {Username} is away (Status: {Status})",
                        user.Username, awayUser.Status);

                    // Build embed
                    var embed = new EmbedBuilder()
                        .WithColor(new Color(0, 71, 171))
                        .WithThumbnailUrl(user.GetAvatarUrl())
                        .WithTitle($":clock: {awayUser.UserName} is away! :clock:")
                        .WithDescription($"Since: **{awayUser.TimeAway}**\nDuration: {awayDuration}\nMessage: {awayUser.Message}")
                        .Build();

                    await messageDetails.Channel.SendMessageAsync(embed: embed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking away mentions in channel {ChannelId}",
                    messageDetails.Channel.Id);
            }
        }

        /// <summary>
        /// Disposes resources and unsubscribes from event handlers
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _client.MessageReceived -= HandleAwayMentions;
                _logger.LogInformation("AwaySystemService disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing AwaySystemService");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
