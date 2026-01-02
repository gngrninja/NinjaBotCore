using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace NinjaBotCore.Services
{
    public class ModerationWatcherService
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;
        private readonly HashSet<ulong> _recentBulkDeletes;
        private readonly object _bulkDeleteLock;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

        public ModerationWatcherService(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<ModerationWatcherService>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _cache = services.GetRequiredService<IMemoryCache>();
            _recentBulkDeletes = new HashSet<ulong>();
            _bulkDeleteLock = new object();

            // Subscribe to all moderation events
            _client.UserVoiceStateUpdated += HandleVoiceStateUpdate;
            _client.MessageUpdated += HandleMessageUpdate;
            _client.MessageDeleted += HandleMessageDelete;
            _client.MessagesBulkDeleted += HandleMessagesBulkDelete;
            _client.GuildMemberUpdated += HandleMemberUpdate;
            _client.UserBanned += HandleBan;
            _client.UserUnbanned += HandleUnban;

            _logger.LogInformation("ModerationWatcherService loaded");
        }

        #region Helper Methods

        private async Task<ModerationWatcher> GetSettingsAsync(long guildId)
        {
            var cacheKey = $"modwatch_settings_{guildId}";

            // Try to get from cache first
            if (_cache.TryGetValue<ModerationWatcher>(cacheKey, out var cachedSettings))
            {
                return cachedSettings;
            }

            // Not in cache, fetch from database
            await using var repo = new Repository<ModerationWatcher>(_scopeFactory);
            var settings = await repo.FirstOrDefaultAsync(m => m.DiscordGuildId == guildId);

            // Store in cache with expiration
            if (settings != null)
            {
                _cache.Set(cacheKey, settings, CacheExpiration);
            }

            return settings;
        }

        /// <summary>
        /// Invalidates the cached settings for a specific guild
        /// </summary>
        public void InvalidateSettingsCache(long guildId)
        {
            var cacheKey = $"modwatch_settings_{guildId}";
            _cache.Remove(cacheKey);
        }

        private async Task<ISocketMessageChannel> GetNotificationChannel(SocketGuild guild, ModerationWatcher settings)
        {
            if (settings == null || settings.ChannelId == null)
                return null;

            try
            {
                return guild.GetChannel((ulong)settings.ChannelId) as ISocketMessageChannel;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting notification channel for {guild.Name}: {ex.Message}");
                return null;
            }
        }

        private async Task PostNotification(ISocketMessageChannel channel, EmbedBuilder embed)
        {
            if (channel == null || embed == null)
                return;

            try
            {
                await channel.SendMessageAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error posting notification: {ex.Message}");
            }
        }

        #endregion

        #region Voice Watcher

        private async Task HandleVoiceStateUpdate(
            SocketUser user,
            SocketVoiceState before,
            SocketVoiceState after)
        {
            if (user.IsBot) return;

            var guildUser = user as SocketGuildUser;
            if (guildUser == null) return;

            var settings = await GetSettingsAsync((long)guildUser.Guild.Id);
                if (settings?.WatchVoice != true) return;

                var channel = await GetNotificationChannel(guildUser.Guild, settings);
                if (channel == null) return;

                bool joined = before.VoiceChannel == null && after.VoiceChannel != null;
                bool left = before.VoiceChannel != null && after.VoiceChannel == null;
                bool moved = before.VoiceChannel != null && after.VoiceChannel != null
                    && before.VoiceChannel.Id != after.VoiceChannel.Id;

                EmbedBuilder embed = null;

                if (joined)
                {
                    embed = CreateVoiceEmbed(guildUser, after.VoiceChannel, "Joined Voice Channel", new Color(0, 255, 0));
                }
                else if (left)
                {
                    embed = CreateVoiceEmbed(guildUser, before.VoiceChannel, "Left Voice Channel", new Color(255, 0, 0));
                }
                else if (moved)
                {
                    embed = CreateVoiceMoveEmbed(guildUser, before.VoiceChannel, after.VoiceChannel);
                }

                if (embed != null)
                {
                    await PostNotification(channel, embed);
                }
        }

        private EmbedBuilder CreateVoiceEmbed(SocketGuildUser user, SocketVoiceChannel voiceChannel, string title, Color color)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = title;
            sb.AppendLine($"{user.Mention} ({user.Username})");
            sb.AppendLine($"Voice Channel: **{voiceChannel.Name}**");

            embed.Description = sb.ToString();
            embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            embed.WithColor(color);
            embed.WithCurrentTimestamp();

            return embed;
        }

        private EmbedBuilder CreateVoiceMoveEmbed(SocketGuildUser user, SocketVoiceChannel from, SocketVoiceChannel to)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = "Moved Between Voice Channels";
            sb.AppendLine($"{user.Mention} ({user.Username})");
            sb.AppendLine($"From: **{from.Name}**");
            sb.AppendLine($"To: **{to.Name}**");

            embed.Description = sb.ToString();
            embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            embed.WithColor(new Color(255, 165, 0)); // Orange
            embed.WithCurrentTimestamp();

            return embed;
        }

        #endregion

        #region Message Watcher

        private async Task HandleMessageUpdate(
            Cacheable<IMessage, ulong> before,
            SocketMessage after,
            ISocketMessageChannel messageChannel)
        {
            if (after.Author.IsBot) return;

            var guildChannel = messageChannel as SocketGuildChannel;
            if (guildChannel == null) return;

            var settings = await GetSettingsAsync((long)guildChannel.Guild.Id);
                if (settings?.WatchMessages != true) return;

                var notificationChannel = await GetNotificationChannel(guildChannel.Guild, settings);
                if (notificationChannel == null) return;

                var beforeMessage = await before.GetOrDownloadAsync();

                // Skip if both messages are empty/null (likely embed update)
                if (string.IsNullOrWhiteSpace(after.Content))
                {
                    if (beforeMessage == null || string.IsNullOrWhiteSpace(beforeMessage.Content))
                        return;
                }

                // Only log if content actually changed (ignore embed updates)
                if (beforeMessage != null && beforeMessage.Content == after.Content)
                    return;

                var embed = new EmbedBuilder();
                var sb = new StringBuilder();

                embed.Title = "Message Edited";
                sb.AppendLine($"{after.Author.Mention} ({after.Author.Username})");
                sb.AppendLine($"Channel: <#{messageChannel.Id}>");

                if (beforeMessage != null)
                {
                    sb.AppendLine($"\n**Before:**");
                    var beforeContent = string.IsNullOrWhiteSpace(beforeMessage.Content)
                        ? "*Empty message*"
                        : (beforeMessage.Content.Length > 1000
                            ? beforeMessage.Content.Substring(0, 1000) + "..."
                            : beforeMessage.Content);
                    sb.AppendLine(beforeContent);
                }
                else
                {
                    sb.AppendLine($"\n**Before:**");
                    sb.AppendLine("*Message not in cache*");
                }

                sb.AppendLine($"\n**After:**");
                var afterContent = string.IsNullOrWhiteSpace(after.Content)
                    ? "*Empty message*"
                    : (after.Content.Length > 1000
                        ? after.Content.Substring(0, 1000) + "..."
                        : after.Content);
                sb.AppendLine(afterContent);

                sb.AppendLine($"\n[Jump to Message]({after.GetJumpUrl()})");

                embed.Description = sb.ToString();
                embed.ThumbnailUrl = after.Author.GetAvatarUrl() ?? after.Author.GetDefaultAvatarUrl();
                embed.WithColor(new Color(255, 165, 0)); // Orange
                embed.WithCurrentTimestamp();

                await PostNotification(notificationChannel, embed);
        }

        private async Task HandleMessageDelete(
            Cacheable<IMessage, ulong> message,
            Cacheable<IMessageChannel, ulong> channel)
        {
            // Skip if this message was part of a bulk delete
            bool isBulkDelete;
            lock (_bulkDeleteLock)
            {
                isBulkDelete = _recentBulkDeletes.Contains(message.Id);
            }

            if (isBulkDelete) return;

            var msg = await message.GetOrDownloadAsync();

            // Skip bot messages
            if (msg?.Author.IsBot == true) return;

            // Skip messages not in cache (usually slash commands or very old messages)
            // These aren't useful to log since we can't show content
            if (msg == null) return;

            var guildChannel = (await channel.GetOrDownloadAsync()) as SocketGuildChannel;
            if (guildChannel == null) return;

            var settings = await GetSettingsAsync((long)guildChannel.Guild.Id);
                if (settings?.WatchMessages != true) return;

                var notificationChannel = await GetNotificationChannel(guildChannel.Guild, settings);
                if (notificationChannel == null) return;

                var embed = new EmbedBuilder();
                var sb = new StringBuilder();

                embed.Title = "Message Deleted";
                sb.AppendLine($"{msg.Author.Mention} ({msg.Author.Username})");
                sb.AppendLine($"Channel: <#{guildChannel.Id}>");
                sb.AppendLine($"\n**Content:**");
                sb.AppendLine(msg.Content?.Length > 1000
                    ? msg.Content.Substring(0, 1000) + "..."
                    : msg.Content ?? "*No content (may have been embeds/attachments)*");

                embed.ThumbnailUrl = msg.Author.GetAvatarUrl() ?? msg.Author.GetDefaultAvatarUrl();
                embed.Description = sb.ToString();
                embed.WithColor(new Color(255, 0, 0)); // Red
                embed.WithCurrentTimestamp();

                await PostNotification(notificationChannel, embed);
        }

        private async Task HandleMessagesBulkDelete(
            IReadOnlyCollection<Cacheable<IMessage, ulong>> messages,
            Cacheable<IMessageChannel, ulong> channel)
        {
            var guildChannel = (await channel.GetOrDownloadAsync()) as SocketGuildChannel;
            if (guildChannel == null) return;

            var settings = await GetSettingsAsync((long)guildChannel.Guild.Id);
                if (settings?.WatchMessages != true) return;

                var notificationChannel = await GetNotificationChannel(guildChannel.Guild, settings);
                if (notificationChannel == null) return;

                // Add all message IDs to the bulk delete set to prevent duplicate notifications
                lock (_bulkDeleteLock)
                {
                    foreach (var message in messages)
                    {
                        _recentBulkDeletes.Add(message.Id);
                    }
                }

                // Clean up the set after 5 seconds (messages should have already triggered individual events by then)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    lock (_bulkDeleteLock)
                    {
                        foreach (var message in messages)
                        {
                            _recentBulkDeletes.Remove(message.Id);
                        }
                    }
                });

                // Count messages and filter out bot messages
                var cachedMessages = new List<IMessage>();
                foreach (var msg in messages)
                {
                    var cached = await msg.GetOrDownloadAsync();
                    if (cached != null && !cached.Author.IsBot)
                    {
                        cachedMessages.Add(cached);
                    }
                }

                var embed = new EmbedBuilder();
                var sb = new StringBuilder();

                embed.Title = "Bulk Message Delete";
                sb.AppendLine($"Channel: <#{guildChannel.Id}>");
                sb.AppendLine($"**Messages deleted:** {messages.Count}");

                if (cachedMessages.Count > 0)
                {
                    sb.AppendLine($"\n**Affected users:**");
                    var userGroups = cachedMessages
                        .GroupBy(m => m.Author.Id)
                        .OrderByDescending(g => g.Count())
                        .Take(10);

                    foreach (var group in userGroups)
                    {
                        var user = group.First().Author;
                        sb.AppendLine($"• {user.Mention} ({user.Username}): {group.Count()} message{(group.Count() != 1 ? "s" : "")}");
                    }

                    if (cachedMessages.GroupBy(m => m.Author.Id).Count() > 10)
                    {
                        sb.AppendLine($"• *...and {cachedMessages.GroupBy(m => m.Author.Id).Count() - 10} more user{(cachedMessages.GroupBy(m => m.Author.Id).Count() - 10 != 1 ? "s" : "")}*");
                    }
                }
                else
                {
                    sb.AppendLine($"\n*Messages not in cache - unable to retrieve details*");
                }

                embed.Description = sb.ToString();
                embed.WithColor(new Color(255, 100, 0)); // Dark Orange
                embed.WithCurrentTimestamp();

                await PostNotification(notificationChannel, embed);
        }

        #endregion

        #region Role & Nickname Watcher

        private async Task HandleMemberUpdate(
            Cacheable<SocketGuildUser, ulong> before,
            SocketGuildUser after)
        {
            if (after.IsBot) return;

            var settings = await GetSettingsAsync((long)after.Guild.Id);
                if (settings == null) return;

                var notificationChannel = await GetNotificationChannel(after.Guild, settings);
                if (notificationChannel == null) return;

                var beforeUser = await before.GetOrDownloadAsync();
                if (beforeUser == null) return;

                // Check for role changes
                if (settings.WatchRoles == true)
                {
                    var addedRoles = after.Roles.Except(beforeUser.Roles).ToList();
                    var removedRoles = beforeUser.Roles.Except(after.Roles).ToList();

                    foreach (var role in addedRoles)
                    {
                        if (role.IsEveryone) continue;
                        var embed = CreateRoleEmbed(after, role, true);
                        await PostNotification(notificationChannel, embed);
                    }

                    foreach (var role in removedRoles)
                    {
                        if (role.IsEveryone) continue;
                        var embed = CreateRoleEmbed(after, role, false);
                        await PostNotification(notificationChannel, embed);
                    }
                }

                // Check for nickname change
                if (settings.WatchNicknames == true && beforeUser.Nickname != after.Nickname)
                {
                    var embed = CreateNicknameEmbed(after, beforeUser.Nickname, after.Nickname);
                    await PostNotification(notificationChannel, embed);
                }
        }

        private EmbedBuilder CreateRoleEmbed(SocketGuildUser user, SocketRole role, bool added)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = added ? "Role Added" : "Role Removed";
            sb.AppendLine($"{user.Mention} ({user.Username})");
            sb.AppendLine($"Role: {role.Mention}");

            embed.Description = sb.ToString();
            embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            embed.WithColor(added ? new Color(0, 255, 0) : new Color(255, 0, 0)); // Green/Red
            embed.WithCurrentTimestamp();

            return embed;
        }

        private EmbedBuilder CreateNicknameEmbed(SocketGuildUser user, string oldNick, string newNick)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = "Nickname Changed";
            sb.AppendLine($"{user.Mention} ({user.Username})");
            sb.AppendLine($"Before: **{oldNick ?? "(none)"}**");
            sb.AppendLine($"After: **{newNick ?? "(none)"}**");

            embed.Description = sb.ToString();
            embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            embed.WithColor(new Color(0, 200, 255)); // Blue
            embed.WithCurrentTimestamp();

            return embed;
        }

        #endregion

        #region Ban Watcher

        private async Task HandleBan(SocketUser user, SocketGuild guild)
        {
            var settings = await GetSettingsAsync((long)guild.Id);
                if (settings?.WatchBans != true) return;

                var notificationChannel = await GetNotificationChannel(guild, settings);
                if (notificationChannel == null) return;

                var embed = new EmbedBuilder();
                var sb = new StringBuilder();

                embed.Title = "User Banned";
                sb.AppendLine($"{user.Mention} ({user.Username})");
                sb.AppendLine($"User ID: {user.Id}");

                embed.Description = sb.ToString();
                embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
                embed.WithColor(new Color(255, 0, 0)); // Red
                embed.WithCurrentTimestamp();

                await PostNotification(notificationChannel, embed);
        }

        private async Task HandleUnban(SocketUser user, SocketGuild guild)
        {
            var settings = await GetSettingsAsync((long)guild.Id);
                if (settings?.WatchBans != true) return;

                var notificationChannel = await GetNotificationChannel(guild, settings);
                if (notificationChannel == null) return;

                var embed = new EmbedBuilder();
                var sb = new StringBuilder();

                embed.Title = "User Unbanned";
                sb.AppendLine($"{user.Mention} ({user.Username})");
                sb.AppendLine($"User ID: {user.Id}");

                embed.Description = sb.ToString();
                embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
                embed.WithColor(new Color(0, 255, 0)); // Green
                embed.WithCurrentTimestamp();

                await PostNotification(notificationChannel, embed);
        }

        #endregion
    }
}
