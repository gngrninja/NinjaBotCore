using System;
using System.Linq;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Service responsible for filtering banned words from messages.
    /// Listens to message events and deletes messages containing blacklisted words.
    /// </summary>
    public class WordFilterService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MemoryCache _wordListCache;
        private bool _disposed;

        public WordFilterService(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WordFilterService>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

            // Create dedicated cache for word lists
            _wordListCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = 1000
            });

            // Subscribe to message events for word filtering
            _client.MessageReceived += HandleWordFilter;

            _logger.LogInformation("WordFilterService loaded - monitoring for banned words");
        }

        /// <summary>
        /// Handles incoming messages to check for banned words
        /// </summary>
        private async Task HandleWordFilter(SocketMessage messageDetails)
        {
            try
            {
                // Early returns - fast filtering
                if (messageDetails.Author.IsBot) return;
                if (!(messageDetails.Channel is SocketGuildChannel guildChannel)) return; // Skip DMs
                if (string.IsNullOrWhiteSpace(messageDetails.Content)) return;

                var serverId = (long)guildChannel.Guild.Id;
                var cacheKey = $"wordlist_{serverId}";

                // Get from cache or DB (15 minute cache)
                var bannedWords = await _wordListCache.GetOrCreateAsync(cacheKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                    entry.Size = 1;

                    // Fetch banned words from database
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    return await db.WordList
                        .Where(w => w.ServerId == serverId)
                        .Select(w => w.Word.ToLower())
                        .ToListAsync();
                });

                // Fast check - does message contain any banned word?
                var messageContent = messageDetails.Content.ToLower();
                if (bannedWords.Any(word => messageContent.Contains(word)))
                {
                    await messageDetails.DeleteAsync();
                    _logger.LogInformation("Deleted message from {User} in {Guild} - contained banned word",
                        messageDetails.Author.Username, guildChannel.Guild.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in word filter for channel {ChannelId}",
                    messageDetails.Channel.Id);
            }
        }

        /// <summary>
        /// Invalidate the word list cache for a server when words are added/removed.
        /// Call this after adding or removing words from the blacklist.
        /// </summary>
        public void InvalidateWordListCache(long serverId)
        {
            var cacheKey = $"wordlist_{serverId}";
            _wordListCache.Remove(cacheKey);
            _logger.LogInformation("Invalidated word list cache for server {ServerId}", serverId);
        }

        /// <summary>
        /// Disposes resources including the word list cache and event subscriptions
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _client.MessageReceived -= HandleWordFilter;
                _wordListCache?.Dispose();
                _logger.LogInformation("WordFilterService disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing WordFilterService");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
