using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    /// Service responsible for filtering blocked words from messages.
    /// Only active for servers that have explicitly added words to their blocklist.
    /// Never logs or stores message content - checks are performed in memory only.
    /// </summary>
    public class WordFilterService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MemoryCache _wordListCache;
        private bool _disposed;

        // Leet speak character substitutions
        private static readonly Dictionary<char, char> LeetMap = new()
        {
            {'@', 'a'}, {'4', 'a'},
            {'3', 'e'},
            {'!', 'i'}, {'1', 'i'},
            {'0', 'o'},
            {'$', 's'}, {'5', 's'},
            {'7', 't'},
            {'+', 't'},
        };

        // Zero-width and invisible Unicode characters to strip
        private static readonly Regex InvisibleCharsRegex = new(
            @"[\u200B\u200C\u200D\u200E\u200F\uFEFF\u00AD\u034F\u2060\u2061\u2062\u2063\u2064\u2066\u2067\u2068\u2069\u206A\u206B\u206C\u206D\u206E\u206F]",
            RegexOptions.Compiled);

        // Repeated character collapsing (3+ of same char → 2)
        private static readonly Regex RepeatedCharsRegex = new(
            @"(.)\1{2,}",
            RegexOptions.Compiled);

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

                // Get word list from cache or DB (15 minute cache)
                var blockedWords = await _wordListCache.GetOrCreateAsync(cacheKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                    entry.Size = 1;

                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    return await db.WordList
                        .Where(w => w.ServerId == serverId)
                        .Select(w => w.Word.ToLower())
                        .ToListAsync();
                });

                // No words configured = filter is off for this server
                // We never read message content unless the server has opted in
                if (blockedWords == null || blockedWords.Count == 0) return;

                // Normalize message in memory only - never logged or stored
                var normalized = NormalizeText(messageDetails.Content);
                if (blockedWords.Any(word => normalized.Contains(word)))
                {
                    await messageDetails.DeleteAsync();
                    _logger.LogInformation("Deleted message from {User} in {Guild} - contained blocked word",
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
        /// Normalizes text to catch common obfuscation techniques.
        /// Strips invisible chars, converts leet speak, normalizes accented chars,
        /// and collapses repeated characters. Result is discarded after check.
        /// </summary>
        internal static string NormalizeText(string input)
        {
            // 1. Strip zero-width / invisible characters
            var text = InvisibleCharsRegex.Replace(input, string.Empty);

            // 2. Normalize Unicode accented characters to ASCII (fück → fuck, shït → shit)
            text = new string(text
                .Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray())
                .Normalize(NormalizationForm.FormC);

            // 3. Lowercase
            text = text.ToLower();

            // 4. Apply leet speak substitutions
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                sb.Append(LeetMap.TryGetValue(c, out var replacement) ? replacement : c);
            }
            text = sb.ToString();

            // 5. Collapse repeated characters (shiiit → shiit, but allow normal doubles like "bass")
            text = RepeatedCharsRegex.Replace(text, "$1$1");

            return text;
        }

        /// <summary>
        /// Invalidate the word list cache for a server when words are added/removed.
        /// Call this after adding or removing words from the blocklist.
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
