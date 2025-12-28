using Discord;
using Discord.Net;
using Discord.WebSocket;
using Discord.Interactions;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class DiscordHelpers : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;
        private readonly ILogger<DiscordHelpers> _logger;

        public DiscordHelpers(DiscordShardedClient client, IServiceProvider services)
        {
            _client = client;
            _config = services.GetRequiredService<IConfigurationRoot>();
            _logger = services.GetRequiredService<ILogger<DiscordHelpers>>();
        }

        [SlashCommand("watch", "Manage moderation watchers")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        [DefaultMemberPermissions(GuildPermission.KickMembers)]
        public async Task WatchCommand(
            [Summary("action", "Action to perform")]
            [Choice("Enable", "enable")]
            [Choice("Disable", "disable")]
            [Choice("Status", "status")]
            string action,

            [Summary("type", "Watcher type (required for enable/disable)")]
            [Choice("Voice (join/leave/move)", "voice")]
            [Choice("Messages (edit/delete)", "messages")]
            [Choice("Roles (add/remove)", "roles")]
            [Choice("Bans (ban/unban)", "bans")]
            [Choice("Nicknames (changes)", "nicknames")]
            string watcherType = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            using (var db = new NinjaBotEntities())
            {
                try
                {
                    var currentSetting = db.ModerationWatcher
                        .Where(m => m.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefault();

                    switch (action.ToLower())
                    {
                        case "enable":
                            if (string.IsNullOrEmpty(watcherType))
                            {
                                sb.AppendLine("Error: You must specify a watcher type when enabling.");
                                break;
                            }

                            if (currentSetting == null)
                            {
                                currentSetting = new ModerationWatcher
                                {
                                    DiscordGuildId = (long)Context.Guild.Id,
                                    ChannelId = (long)Context.Channel.Id,
                                    ChannelName = Context.Channel.Name,
                                    SetById = (long)Context.User.Id,
                                    SetByName = Context.User.Username,
                                    TimeSet = DateTime.UtcNow
                                };
                                db.ModerationWatcher.Add(currentSetting);
                            }
                            else
                            {
                                currentSetting.ChannelId = (long)Context.Channel.Id;
                                currentSetting.ChannelName = Context.Channel.Name;
                                currentSetting.SetById = (long)Context.User.Id;
                                currentSetting.SetByName = Context.User.Username;
                                currentSetting.TimeSet = DateTime.UtcNow;
                            }

                            switch (watcherType.ToLower())
                            {
                                case "voice":
                                    currentSetting.WatchVoice = true;
                                    sb.AppendLine($"Voice watcher enabled in {Context.Channel.Name}!");
                                    break;
                                case "messages":
                                    currentSetting.WatchMessages = true;
                                    sb.AppendLine($"Message watcher enabled in {Context.Channel.Name}!");
                                    break;
                                case "roles":
                                    currentSetting.WatchRoles = true;
                                    sb.AppendLine($"Role watcher enabled in {Context.Channel.Name}!");
                                    break;
                                case "bans":
                                    currentSetting.WatchBans = true;
                                    sb.AppendLine($"Ban watcher enabled in {Context.Channel.Name}!");
                                    break;
                                case "nicknames":
                                    currentSetting.WatchNicknames = true;
                                    sb.AppendLine($"Nickname watcher enabled in {Context.Channel.Name}!");
                                    break;
                            }
                            break;

                        case "disable":
                            if (string.IsNullOrEmpty(watcherType))
                            {
                                sb.AppendLine("Error: You must specify a watcher type when disabling.");
                                break;
                            }

                            if (currentSetting == null)
                            {
                                sb.AppendLine("No watchers are configured for this server.");
                                break;
                            }

                            switch (watcherType.ToLower())
                            {
                                case "voice":
                                    currentSetting.WatchVoice = false;
                                    sb.AppendLine("Voice watcher disabled!");
                                    break;
                                case "messages":
                                    currentSetting.WatchMessages = false;
                                    sb.AppendLine("Message watcher disabled!");
                                    break;
                                case "roles":
                                    currentSetting.WatchRoles = false;
                                    sb.AppendLine("Role watcher disabled!");
                                    break;
                                case "bans":
                                    currentSetting.WatchBans = false;
                                    sb.AppendLine("Ban watcher disabled!");
                                    break;
                                case "nicknames":
                                    currentSetting.WatchNicknames = false;
                                    sb.AppendLine("Nickname watcher disabled!");
                                    break;
                            }
                            break;

                        case "status":
                            if (currentSetting == null)
                            {
                                sb.AppendLine("No watchers configured for this server.");
                            }
                            else
                            {
                                var channel = Context.Guild.GetChannel((ulong)currentSetting.ChannelId);
                                sb.AppendLine($"**Notification Channel:** {channel?.Name ?? "Unknown"}");
                                sb.AppendLine($"\n**Active Watchers:**");

                                if (currentSetting.WatchVoice == true)
                                    sb.AppendLine("✅ Voice (join/leave/move)");
                                else
                                    sb.AppendLine("❌ Voice");

                                if (currentSetting.WatchMessages == true)
                                    sb.AppendLine("✅ Messages (edit/delete)");
                                else
                                    sb.AppendLine("❌ Messages");

                                if (currentSetting.WatchRoles == true)
                                    sb.AppendLine("✅ Roles (add/remove)");
                                else
                                    sb.AppendLine("❌ Roles");

                                if (currentSetting.WatchBans == true)
                                    sb.AppendLine("✅ Bans (ban/unban)");
                                else
                                    sb.AppendLine("❌ Bans");

                                if (currentSetting.WatchNicknames == true)
                                    sb.AppendLine("✅ Nicknames (changes)");
                                else
                                    sb.AppendLine("❌ Nicknames");

                                sb.AppendLine($"\n**Last modified by:** {currentSetting.SetByName}");
                                sb.AppendLine($"**Last modified:** {currentSetting.TimeSet}");
                            }
                            break;
                    }

                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Error: {ex.Message}");
                    _logger.LogError($"Error in watch command: {ex.Message}");
                }
            }

            embed.Title = $"Moderation Watchers - {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 255));
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("migrate-watchers", "Migrate old voice watcher data to new moderation watcher system (one-time use)")]
        [RequireUserPermission(GuildPermission.Administrator)]
        [DefaultMemberPermissions(GuildPermission.Administrator)]
        public async Task MigrateWatchersCommand()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            using (var db = new NinjaBotEntities())
            {
                try
                {
                    var oldVoiceWatcher = db.VoiceWatcher
                        .Where(v => v.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefault();

                    if (oldVoiceWatcher == null)
                    {
                        sb.AppendLine("No old voice watcher data found for this server.");
                    }
                    else
                    {
                        var existingModWatcher = db.ModerationWatcher
                            .Where(m => m.DiscordGuildId == (long)Context.Guild.Id)
                            .FirstOrDefault();

                        if (existingModWatcher != null)
                        {
                            sb.AppendLine("Migration already completed or moderation watcher already exists!");
                            sb.AppendLine($"\nUse `/watch status` to view current configuration.");
                        }
                        else
                        {
                            var newModWatcher = new ModerationWatcher
                            {
                                DiscordGuildId = oldVoiceWatcher.DiscordGuildId,
                                ChannelId = oldVoiceWatcher.ChannelId,
                                ChannelName = oldVoiceWatcher.ChannelName,
                                WatchVoice = oldVoiceWatcher.WatchVoice,
                                SetById = oldVoiceWatcher.SetById,
                                SetByName = oldVoiceWatcher.SetByName,
                                TimeSet = oldVoiceWatcher.TimeSet
                            };

                            db.ModerationWatcher.Add(newModWatcher);
                            await db.SaveChangesAsync();

                            sb.AppendLine("✅ Migration successful!");
                            sb.AppendLine($"\nMigrated voice watcher settings:");
                            sb.AppendLine($"- Voice watching: {(oldVoiceWatcher.WatchVoice == true ? "Enabled" : "Disabled")}");
                            sb.AppendLine($"- Notification channel: {oldVoiceWatcher.ChannelName}");
                            sb.AppendLine($"\nYou can now use `/watch` commands to manage all watchers!");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Error during migration: {ex.Message}");
                    _logger.LogError($"Error in migrate-watchers command: {ex.Message}");
                }
            }

            embed.Title = $"Watcher Migration - {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 255));
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }
    }
}