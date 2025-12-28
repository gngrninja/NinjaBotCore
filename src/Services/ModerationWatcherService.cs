using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using NinjaBotCore.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services
{
    public class ModerationWatcherService
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;

        public ModerationWatcherService(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<ModerationWatcherService>>();
            _client = services.GetRequiredService<DiscordShardedClient>();

            // Subscribe to all moderation events
            _client.UserVoiceStateUpdated += HandleVoiceStateUpdate;
            _client.MessageUpdated += HandleMessageUpdate;
            _client.MessageDeleted += HandleMessageDelete;
            _client.GuildMemberUpdated += HandleMemberUpdate;
            _client.UserBanned += HandleBan;
            _client.UserUnbanned += HandleUnban;

            _logger.LogInformation("ModerationWatcherService loaded");
        }

        #region Helper Methods

        private ModerationWatcher GetSettings(long guildId)
        {
            using (var db = new NinjaBotEntities())
            {
                return db.ModerationWatcher
                    .Where(m => m.DiscordGuildId == guildId)
                    .FirstOrDefault();
            }
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
            await Task.Run(async () =>
            {
                if (user.IsBot) return;

                var guildUser = user as SocketGuildUser;
                if (guildUser == null) return;

                var settings = GetSettings((long)guildUser.Guild.Id);
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
            });
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
            await Task.Run(async () =>
            {
                if (after.Author.IsBot) return;

                var guildChannel = messageChannel as SocketGuildChannel;
                if (guildChannel == null) return;

                var settings = GetSettings((long)guildChannel.Guild.Id);
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
            });
        }

        private async Task HandleMessageDelete(
            Cacheable<IMessage, ulong> message,
            Cacheable<IMessageChannel, ulong> channel)
        {
            await Task.Run(async () =>
            {
                var msg = await message.GetOrDownloadAsync();
                if (msg?.Author.IsBot == true) return;

                var guildChannel = (await channel.GetOrDownloadAsync()) as SocketGuildChannel;
                if (guildChannel == null) return;

                var settings = GetSettings((long)guildChannel.Guild.Id);
                if (settings?.WatchMessages != true) return;

                var notificationChannel = await GetNotificationChannel(guildChannel.Guild, settings);
                if (notificationChannel == null) return;

                var embed = new EmbedBuilder();
                var sb = new StringBuilder();

                embed.Title = "Message Deleted";

                if (msg != null)
                {
                    sb.AppendLine($"{msg.Author.Mention} ({msg.Author.Username})");
                    sb.AppendLine($"Channel: <#{guildChannel.Id}>");
                    sb.AppendLine($"\n**Content:**");
                    sb.AppendLine(msg.Content?.Length > 1000
                        ? msg.Content.Substring(0, 1000) + "..."
                        : msg.Content ?? "*No content (may have been embeds/attachments)*");

                    embed.ThumbnailUrl = msg.Author.GetAvatarUrl() ?? msg.Author.GetDefaultAvatarUrl();
                }
                else
                {
                    sb.AppendLine($"Channel: <#{guildChannel.Id}>");
                    sb.AppendLine($"Message ID: {message.Id}");
                    sb.AppendLine($"\n*Message not in cache - unable to retrieve content*");
                }

                embed.Description = sb.ToString();
                embed.WithColor(new Color(255, 0, 0)); // Red
                embed.WithCurrentTimestamp();

                await PostNotification(notificationChannel, embed);
            });
        }

        #endregion

        #region Role & Nickname Watcher

        private async Task HandleMemberUpdate(
            Cacheable<SocketGuildUser, ulong> before,
            SocketGuildUser after)
        {
            await Task.Run(async () =>
            {
                if (after.IsBot) return;

                var settings = GetSettings((long)after.Guild.Id);
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
            });
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
            await Task.Run(async () =>
            {
                var settings = GetSettings((long)guild.Id);
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
            });
        }

        private async Task HandleUnban(SocketUser user, SocketGuild guild)
        {
            await Task.Run(async () =>
            {
                var settings = GetSettings((long)guild.Id);
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
            });
        }

        #endregion
    }
}
