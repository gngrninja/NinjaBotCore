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
using Microsoft.EntityFrameworkCore;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class DiscordHelpers : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;
        private readonly ILogger<DiscordHelpers> _logger;
        private readonly ModerationWatcherService _moderationWatcher;
        private readonly WowCacheService _greetingCache;

        public DiscordHelpers(DiscordShardedClient client, IServiceProvider services, ModerationWatcherService moderationWatcher, WowCacheService greetingCache)
            : base(services.GetRequiredService<IServiceScopeFactory>())
        {
            _client = client;
            _config = services.GetRequiredService<IConfigurationRoot>();
            _logger = services.GetRequiredService<ILogger<DiscordHelpers>>();
            _moderationWatcher = moderationWatcher;
            _greetingCache = greetingCache;
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

            try
            {
                await WithDbAsync(async db =>
                {
                    var currentSetting = await db.ModerationWatcher
                        .Where(m => m.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefaultAsync();

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

                    // Invalidate cache after updating settings
                    _moderationWatcher.InvalidateSettingsCache((long)Context.Guild.Id);
                });
            }
            catch (DbUpdateException ex)
            {
                sb.AppendLine("Failed to update moderation watcher settings in database.");
                _logger.LogError(ex, "Database error in watch command for guild {GuildId}", Context.Guild.Id);
            }
            catch (Exception ex)
            {
                sb.AppendLine("An unexpected error occurred while updating watcher settings.");
                _logger.LogError(ex, "Unexpected error in watch command for guild {GuildId}", Context.Guild.Id);
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

            try
            {
                await WithDbAsync(async db =>
                {
                    var oldVoiceWatcher = await db.VoiceWatcher
                        .Where(v => v.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefaultAsync();

                    if (oldVoiceWatcher == null)
                    {
                        sb.AppendLine("No old voice watcher data found for this server.");
                    }
                    else
                    {
                        var existingModWatcher = await db.ModerationWatcher
                            .Where(m => m.DiscordGuildId == (long)Context.Guild.Id)
                            .FirstOrDefaultAsync();

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
                });
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error during migration: {ex.Message}");
                _logger.LogError(ex, "Error in migrate-watchers command");
            }

            embed.Title = $"Watcher Migration - {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 255));
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }
        [SlashCommand("kick", "kick someone!")]        
        [RequireBotPermission(GuildPermission.KickMembers)]
        [DefaultMemberPermissions(GuildPermission.KickMembers)]
        public async Task KickUser(IGuildUser user, string reason = null)
        {
            var embed = new EmbedBuilder();
            embed.ThumbnailUrl = user.GetAvatarUrl();
            StringBuilder sb = new StringBuilder();
            try
            {
                await user.KickAsync();
                embed.Title = $"Kicking {user.Username}";
                if (string.IsNullOrEmpty(reason))
                {
                    reason = "Buh bye.";
                }                
                sb.AppendLine($"Reason: [**{reason}**]");
            }
            catch (Exception ex)
            {
                embed.Title = $"Error attempting to kick {user.Username}";
                sb.AppendLine($"[{ex.Message}]");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 0, 255));
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("ban", "ban someone!")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.BanMembers)]
        [DefaultMemberPermissions(GuildPermission.BanMembers)]
        public async Task BanUser(IGuildUser user, string args = null)
        {
            int pruneDays = 0;
            string reason = "Buy bye!";            
            if (args != null)
            {
                try
                {
                    pruneDays = int.Parse(args.Split(" ")[0]);
                }
                catch (Exception)
                {
                    pruneDays = 0;
                }
                var numArgs = args.Split(" ").Count();
                if (numArgs > 1)
                {
                    int iValue = 0;
                    if (pruneDays > 0)
                    {
                        iValue = 1;                        
                    }
                    reason = string.Empty;
                    for (int i = iValue; i <= numArgs - 1; i++)
                    {
                        if (i + 1 == numArgs - 1)
                        {
                            reason += $"{args.Split(" ")[i]}";
                        }
                        else
                        {
                            reason += $" {args.Split(" ")[i]} ";
                        }
                    }
                    reason = reason.Trim();
                }
                else if (pruneDays == 0) 
                {
                    reason = args;
                }
            }
            var embed = new EmbedBuilder();
            embed.ThumbnailUrl = user.GetAvatarUrl();
            StringBuilder sb = new StringBuilder();
            try
            {
                await Context.Guild.AddBanAsync(user, pruneDays, reason);
                embed.Title = $"Banning {user.Username}";
                sb.AppendLine($"Reason: [**{reason}**]");
            }
            catch (Exception ex)
            {
                embed.Title = $"Error attempting to ban {user.Username}";
                sb.AppendLine($"[{ex.Message}]");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 0, 255));
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("unban", "unban someone!")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.BanMembers)]
        [DefaultMemberPermissions(GuildPermission.BanMembers)]
        public async Task UnBanUser(string user)
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            StringBuilder sb = new StringBuilder();

            var currentBans = await Context.Guild.GetBansAsync().FlattenAsync();
            var bannedUser = currentBans.Where(c => c.User.Username.ToLower().Contains(user.ToLower())).FirstOrDefault();

            if (bannedUser != null)
            {
                try
                {
                    await Context.Guild.RemoveBanAsync(bannedUser.User.Id);
                    embed.Title = $"UnBanning {bannedUser.User.Username}";
                }
                catch (Exception ex)
                {
                    embed.Title = $"Error attempting to unban {bannedUser.User.Username}";
                    sb.AppendLine($"[{ex.Message}]");
                }
            }
            else
            {
                embed.Title = $"{user} not found!";
                sb.AppendLine($"Unable to find [{user}] in the ban list!");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 0, 255));
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("list-bans", "list bans!")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.BanMembers)]
        public async Task ListBans()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            StringBuilder sb = new StringBuilder();
            try
            {
                embed.Title = $"User bans on {Context.Guild.Name}";
                var bans = await Context.Guild.GetBansAsync().FlattenAsync();

                if (bans.Any())
                {
                    foreach (var ban in bans)
                    {
                        string reason = ban.Reason;
                        if (string.IsNullOrEmpty(reason))
                        {
                            reason = "/shrug";
                        }
                        sb.AppendLine($":black_medium_small_square: **{ban.User.Username}** (*{reason}*)");
                    }
                }
                else
                {
                    sb.AppendLine($"Much empty, such space!");
                }
            }
            catch (Exception ex)
            {
                embed.Title = $"Error attempting to list bans for **{Context.Guild.Name}**";
                sb.AppendLine($"[{ex.Message}]");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 0, 255));
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("set-join-message", "set join message")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeGreeting()
        {
            var guildGreetingInfo = await _greetingCache.GetServerGreetingAsync((long)Context.Guild.Id);
            string curGreeting = guildGreetingInfo?.Greeting ?? "";

            var mb = new ModalBuilder()
                .WithTitle("Greeting message")
                .WithCustomId("joining_message")
                .AddTextInput("Message:", "joining_message", TextInputStyle.Paragraph,
                    placeholder: "{user} {username} {server} {membercount} {#channel} {&role} {:emoji:}",
                    required: false,
                    value: curGreeting);
            await Context.Interaction.RespondWithModalAsync(mb.Build());
        }

        [SlashCommand("set-part-message", "set a message to display when users leave the server")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeParting()
        {
            var guildGreetingInfo = await _greetingCache.GetServerGreetingAsync((long)Context.Guild.Id);
            string curParting = guildGreetingInfo?.PartingMessage ?? "";

            var mb = new ModalBuilder()
                .WithTitle("Parting message")
                .WithCustomId("parting_message")
                .AddTextInput("Message:", "parting_message", TextInputStyle.Paragraph,
                    placeholder: "{user} {username} {server} {membercount} {#channel} {&role} {:emoji:}",
                    required: false,
                    value: curParting);
            await Context.Interaction.RespondWithModalAsync(mb.Build());
        }

        [SlashCommand("toggle-greetings", "toggle join/leave messages to be displayed in this channel")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ToggleGreetings()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            try
            {
                await WithDbAsync(async db =>
                {
                    var currentSetting = await db.ServerGreetings
                        .Where(g => g.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefaultAsync();
                    if (currentSetting != null)
                    {
                        if (currentSetting.GreetUsers == true)
                        {
                            currentSetting.GreetUsers = false;
                            sb.AppendLine("Greetings have been disabled!");
                        }
                        else
                        {
                            currentSetting.GreetUsers = true;
                            currentSetting.GreetingChannelId = (long)Context.Channel.Id;
                            currentSetting.GreetingChannelName = Context.Channel.Name;
                            sb.AppendLine("Greetings have been enabled!");
                        }
                    }
                    else
                    {
                        db.ServerGreetings.Add(new ServerGreeting
                        {
                            DiscordGuildId = (long)Context.Guild.Id,
                            GreetingChannelId = (long)Context.Channel.Id,
                            GreetingChannelName = Context.Channel.Name,
                            GreetUsers = true
                        });
                        sb.AppendLine("Greetings have been enabled!");
                    }
                    await db.SaveChangesAsync();
                    _greetingCache.InvalidateServerGreeting((long)Context.Guild.Id);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling greetings in {GuildName}", Context.Guild.Name);
            }
            embed.Title = $"User greeting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("set-parting-channel", "set the channel for parting messages (requires partings enabled)")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetPartingChannel()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            try
            {
                await WithDbAsync(async db =>
                {
                    var currentSetting = await db.ServerGreetings
                        .Where(g => g.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefaultAsync();
                    if (currentSetting != null)
                    {
                        if (currentSetting.PartUsers == true)
                        {
                            currentSetting.PartingChannelId = (long)Context.Channel.Id;
                            sb.AppendLine($"Parting messages channel set to {Context.Channel.Name}!");
                            await db.SaveChangesAsync();
                            _greetingCache.InvalidateServerGreeting((long)Context.Guild.Id);
                        }
                        else
                        {
                            sb.AppendLine("Please enable parting messages first via /toggle-partings");
                        }
                    }
                    else
                    {
                        sb.AppendLine("No settings found. Please enable parting messages first via /toggle-partings");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting parting channel in {GuildName}", Context.Guild.Name);
            }
            embed.Title = $"User greeting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("toggle-partings", "toggle goodbye messages when users leave")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TogglePartings()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            try
            {
                await WithDbAsync(async db =>
                {
                    var currentSetting = await db.ServerGreetings
                        .Where(g => g.DiscordGuildId == (long)Context.Guild.Id)
                        .FirstOrDefaultAsync();
                    if (currentSetting != null)
                    {
                        if (currentSetting.PartUsers == true)
                        {
                            currentSetting.PartUsers = false;
                            sb.AppendLine("Parting messages have been disabled!");
                        }
                        else
                        {
                            currentSetting.PartUsers = true;
                            // Use parting channel if set, otherwise use greeting channel, otherwise use current channel
                            if (currentSetting.PartingChannelId == null)
                            {
                                currentSetting.PartingChannelId = currentSetting.GreetingChannelId ?? (long)Context.Channel.Id;
                            }
                            sb.AppendLine("Parting messages have been enabled!");
                        }
                    }
                    else
                    {
                        db.ServerGreetings.Add(new ServerGreeting
                        {
                            DiscordGuildId = (long)Context.Guild.Id,
                            PartingChannelId = (long)Context.Channel.Id,
                            PartUsers = true
                        });
                        sb.AppendLine("Parting messages have been enabled!");
                    }
                    await db.SaveChangesAsync();
                    _greetingCache.InvalidateServerGreeting((long)Context.Guild.Id);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling partings in {GuildName}", Context.Guild.Name);
            }
            embed.Title = $"User parting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("greeting-status", "show current greeting/parting message settings")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task GreetingStatus()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            embed.Title = $"Greeting/Parting Settings for {Context.Guild.Name}";
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            try
            {
                var settings = await _greetingCache.GetServerGreetingAsync((long)Context.Guild.Id);

                if (settings == null)
                {
                    embed.Description = "No greeting/parting settings configured for this server.";
                    embed.WithColor(new Color(128, 128, 128));
                }
                else
                {
                    var sb = new StringBuilder();

                    // Greetings status
                    var greetStatus = settings.GreetUsers == true ? "Enabled" : "Disabled";
                    sb.AppendLine($"**Greetings:** {greetStatus}");
                    if (settings.GreetUsers == true)
                    {
                        var greetChannel = settings.GreetingChannelId != null
                            ? $"<#{settings.GreetingChannelId}>"
                            : "Not set";
                        sb.AppendLine($"  Channel: {greetChannel}");
                        var greetMsg = string.IsNullOrEmpty(settings.Greeting)
                            ? "(default message)"
                            : (settings.Greeting.Length > 50 ? settings.Greeting[..50] + "..." : settings.Greeting);
                        sb.AppendLine($"  Message: {greetMsg}");
                    }

                    sb.AppendLine();

                    // Partings status
                    var partStatus = settings.PartUsers == true ? "Enabled" : "Disabled";
                    sb.AppendLine($"**Partings:** {partStatus}");
                    if (settings.PartUsers == true)
                    {
                        var partChannel = settings.PartingChannelId != null
                            ? $"<#{settings.PartingChannelId}>"
                            : (settings.GreetingChannelId != null ? $"<#{settings.GreetingChannelId}> (greeting channel)" : "Not set");
                        sb.AppendLine($"  Channel: {partChannel}");
                        var partMsg = string.IsNullOrEmpty(settings.PartingMessage)
                            ? "(default message)"
                            : (settings.PartingMessage.Length > 50 ? settings.PartingMessage[..50] + "..." : settings.PartingMessage);
                        sb.AppendLine($"  Message: {partMsg}");
                    }

                    embed.Description = sb.ToString();
                    embed.WithColor(new Color(0, 255, 0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting greeting status in {GuildName}", Context.Guild.Name);
                embed.Description = "Error retrieving settings.";
                embed.WithColor(new Color(255, 0, 0));
            }

            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("blacklist", "blacklist a user from using the bot")]
        [RequireOwner]
        public async Task BlackList(IGuildUser user, string reason = null)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            try
            {
                await WithDbAsync(async db =>
                {
                    var getUser = await db.Blacklist
                        .Where(b => b.DiscordUserId == (long)user.Id)
                        .FirstOrDefaultAsync();
                    if (getUser != null)
                    {
                        sb.AppendLine($"Unblacklisting {user.Username}");
                        db.Blacklist.Remove(getUser);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(reason))
                        {
                            reason = "just because";
                        }
                        db.Blacklist.Add(new Blacklist
                        {
                            DiscordUserId = (long)user.Id,
                            DiscordUserName = user.Username,
                            Reason = reason,
                            WhenBlacklisted = DateTime.UtcNow
                        });
                        sb.AppendLine($"Blacklisting [**{user.Username}**] -> [*{reason}*]");
                    }
                    embed.Title = "[Blacklist]";
                    embed.Description = sb.ToString();
                    await db.SaveChangesAsync();
                });
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error attempting to blacklist [{user.Username}] -> [{ex.Message}]");
            }
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("clear", "clear x amount of messages from a channel")]
        [RequireBotPermission(GuildPermission.ManageMessages)]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task ClearMessage(int numberOfMessages = 5)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                if (numberOfMessages < 1)
                {
                    await FollowupAsync("Please specify at least 1 message to delete.", ephemeral: true);
                    return;
                }

                if (numberOfMessages > 100)
                {
                    numberOfMessages = 100;
                }

                if (Context.Channel is not ITextChannel textChannel)
                {
                    await FollowupAsync("This command can only be used in text channels.", ephemeral: true);
                    return;
                }

                var messagesToDelete = await Context.Channel.GetMessagesAsync(numberOfMessages).FlattenAsync();
                var messageList = messagesToDelete.ToList();

                if (messageList.Count == 0)
                {
                    await FollowupAsync("No messages found to delete.", ephemeral: true);
                    return;
                }

                await textChannel.DeleteMessagesAsync(messageList);

                var embed = new EmbedBuilder
                {
                    Title = "Messages Cleared",
                    Description = $"Successfully deleted **{messageList.Count}** message{(messageList.Count != 1 ? "s" : "")} from {Context.Channel.Name}.",
                    Color = new Color(0, 255, 0)
                };
                embed.WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                _logger.LogInformation("User {Username} deleted {Count} messages in {GuildName}/{ChannelName}",
                    Context.User.Username, messageList.Count, Context.Guild.Name, Context.Channel.Name);
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.MissingPermissions)
            {
                await FollowupAsync("I don't have permission to delete messages in this channel.", ephemeral: true);
                _logger.LogWarning("Missing permissions to delete messages in {GuildName}/{ChannelName}",
                    Context.Guild.Name, Context.Channel.Name);
            }
            catch (Exception ex)
            {
                await FollowupAsync($"An error occurred while deleting messages: {ex.Message}", ephemeral: true);
                _logger.LogError(ex, "Error deleting messages in {GuildName}/{ChannelName}",
                    Context.Guild.Name, Context.Channel.Name);
            }
        }

        [SlashCommand("clearu", "clear x amount of messages from a specific user")]
        [RequireBotPermission(GuildPermission.ManageMessages)]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task ClearMessageFromUser(IGuildUser user, int numberOfMessages = 5)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                if (numberOfMessages < 1)
                {
                    await FollowupAsync("Please specify at least 1 message to delete.", ephemeral: true);
                    return;
                }

                if (numberOfMessages > 100)
                {
                    numberOfMessages = 100;
                }

                if (Context.Channel is not ITextChannel textChannel)
                {
                    await FollowupAsync("This command can only be used in text channels.", ephemeral: true);
                    return;
                }

                // Fetch more messages to ensure we can find enough from the specific user
                // We'll search through up to 500 messages to find the requested amount from the user
                var searchLimit = Math.Min(500, numberOfMessages * 10);
                var allMessages = await Context.Channel.GetMessagesAsync(searchLimit).FlattenAsync();
                var messagesFromUser = allMessages
                    .Where(m => m.Author.Id == user.Id)
                    .Take(numberOfMessages)
                    .ToList();

                if (messagesFromUser.Count == 0)
                {
                    await FollowupAsync($"No messages found from {user.Mention} in the last {searchLimit} messages.", ephemeral: true);
                    return;
                }

                await textChannel.DeleteMessagesAsync(messagesFromUser);

                var embed = new EmbedBuilder
                {
                    Title = "User Messages Cleared",
                    Description = $"Successfully deleted **{messagesFromUser.Count}** message{(messagesFromUser.Count != 1 ? "s" : "")} from {user.Mention} in {Context.Channel.Name}.",
                    Color = new Color(0, 255, 0)
                };
                embed.WithCurrentTimestamp();

                if (messagesFromUser.Count < numberOfMessages)
                {
                    embed.WithFooter($"Only found {messagesFromUser.Count} message{(messagesFromUser.Count != 1 ? "s" : "")} from this user in the last {searchLimit} messages.");
                }

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                _logger.LogInformation("User {Username} deleted {Count} messages from {TargetUser} in {GuildName}/{ChannelName}",
                    Context.User.Username, messagesFromUser.Count, user.Username, Context.Guild.Name, Context.Channel.Name);
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.MissingPermissions)
            {
                await FollowupAsync("I don't have permission to delete messages in this channel.", ephemeral: true);
                _logger.LogWarning("Missing permissions to delete messages in {GuildName}/{ChannelName}",
                    Context.Guild.Name, Context.Channel.Name);
            }
            catch (Exception ex)
            {
                await FollowupAsync($"An error occurred while deleting messages: {ex.Message}", ephemeral: true);
                _logger.LogError(ex, "Error deleting messages from user in {GuildName}/{ChannelName}",
                    Context.Guild.Name, Context.Channel.Name);
            }
        }          

        [SlashCommand("set-note", "set a note for the server")]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task SetNote()
        {
            // Get current note directly (not using GetNoteInfo which returns long error message)
            string curNote = await WithDbAsync(async db =>
            {
                var note = await db.Notes
                    .FirstOrDefaultAsync(n => n.ServerId == (long)Context.Guild.Id);
                return note?.Note1 ?? string.Empty;
            });

            var mb = new ModalBuilder()
                .WithTitle($"Note for Discord server: [{Context.Guild.Name}]")
                .WithCustomId("discord_server_note")
                .AddTextInput("Note:", "note_text", TextInputStyle.Paragraph, curNote);
            await Context.Interaction.RespondWithModalAsync(mb.Build());
        }

        [SlashCommand("get-note", "get a note associated with a discord server")]                
        public async Task GetNote()
        {
            string note = await GetNoteInfo(Context);
            var embed = new EmbedBuilder();
            embed.Title = $":notepad_spiral:Notes for {Context.Guild.Name}:notepad_spiral:";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.Description = note;
            embed.WithColor(new Color(0, 255, 0));
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("warn", "warn a user")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task WarnUser(IGuildUser user, string message = null)
        {
            int numWarnings = 0;
            var currentWarnings = await GetWarning(Context, user);
            var warnMessage = new StringBuilder();
            if (message == null)
            {
                warnMessage.AppendLine($"{user.Mention},");
                message = $":warning: You have been issued a warning from: {Context.User.Username}! :warning:";
            }
            else
            {
                warnMessage.AppendLine($":warning: {user.Mention}, you have been issued the following warning (from: {Context.User.Username}) :warning:");

            }
            if (currentWarnings != null)
            {
                numWarnings = currentWarnings.NumWarnings + 1;
            }
            else
            {
                numWarnings = 1;
            }
            warnMessage.AppendLine(message);
            switch (numWarnings)
            {
                case 1:
                {
                    warnMessage.AppendLine("This is your first warning. At three warnings, you will be kicked!");
                    break;
                }
                case 2:
                {
                    warnMessage.AppendLine("This is your second warning. At three warnings, you will be kicked!");
                    break;
                }
                case 3:
                {
                    warnMessage.AppendLine("This was your final warning, goodbye!");
                    break;
                }
            }

            try
            {
                await AddWarning(Context, user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to log warning in database for user {UserName} in {GuildName}",
                    user.Username, Context.Guild.Name);
            }

            try
            {
                await user.SendMessageAsync(warnMessage.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to DM warning to user {UserName}", user.Username);
            }

            await RespondAsync(warnMessage.ToString(), ephemeral: true);

            if (numWarnings >= 3)
            {
                try
                {
                    await user.KickAsync("Maximum number of warnings reached!");
                    await ResetWarnings(currentWarnings);

                    var embed = new EmbedBuilder
                    {
                        Title = $"Kicking {user.Username}",
                        Description = $"Reason: [**Maximum number of warnings reached!**]",
                        ThumbnailUrl = user.GetAvatarUrl(),
                        Color = new Color(0, 0, 255)
                    };

                    await FollowupAsync(embed: embed.Build());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error kicking user {UserName} after warnings in {GuildName}",
                        user.Username, Context.Guild.Name);
                    await FollowupAsync($"Failed to kick {user.Username}: {ex.Message}", ephemeral: true);
                }
            }
        }

        [SlashCommand("reset-warnings", "reset warnings for a user")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ResetWarning(IGuildUser user)
        {
            var warnings = await GetWarning(Context, user);
            if (warnings != null)
            {
                await ResetWarnings(warnings);
                await RespondAsync($"Warnings reset for **{user.Username}**", ephemeral: true);
            }
            else
            {
                await RespondAsync($"No warnings found for **{user.Username}**!", ephemeral: true);
            }
        }

        [SlashCommand("yoink", "Move users from one voice channel to another")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task Yoink(
            [Summary("to", "Destination voice channel")]
            SocketVoiceChannel to,
            [Summary("from", "Source voice channel")]
            SocketVoiceChannel from)
        {
            await DeferAsync(ephemeral: true);

            if (from.Id == to.Id)
            {
                await FollowupAsync("Please pick two different voice channels.", ephemeral: true);
                return;
            }

            var usersToMove = from.ConnectedUsers.ToList();

            if (usersToMove.Count == 0)
            {
                await FollowupAsync($"No users currently in **{from.Name}** to move.", ephemeral: true);
                return;
            }

            var movedUsers = 0;
            var skippedUsers = new List<string>();

            foreach (var user in usersToMove)
            {
                try
                {
                    await user.ModifyAsync(u => u.Channel = to);
                    movedUsers++;
                }
                catch (HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40032)
                {
                    skippedUsers.Add(user.Username);
                }

                await Task.Delay(750);
            }

            var message = $"Moved **{movedUsers}** user(s) from **{from.Name}** to **{to.Name}**!";

            if (skippedUsers.Count > 0)
            {
                message += $"\nSkipped {skippedUsers.Count} user(s) no longer in voice: {string.Join(", ", skippedUsers)}";
            }

            await FollowupAsync(message, ephemeral: true);
        }

        private async Task AddWarning(ShardedInteractionContext context, IGuildUser userWarned)
        {
            await WithDbAsync(async db =>
            {
                var warnings = await db.Warnings
                    .Where(w => w.ServerId == (long)context.Guild.Id && w.UserWarnedId == (long)userWarned.Id)
                    .FirstOrDefaultAsync();
                if (warnings != null)
                {
                    warnings.NumWarnings = warnings.NumWarnings + 1;
                }
                else
                {
                    db.Warnings.Add(new Warnings
                    {
                        ServerId = (long)context.Guild.Id,
                        ServerName = context.Guild.Name,
                        UserWarnedId = (long)userWarned.Id,
                        UserWarnedName = userWarned.Username,
                        IssuerId = (long)context.User.Id,
                        IssuerName = context.User.Username,
                        TimeIssued = DateTime.UtcNow,
                        NumWarnings = 1
                    });
                }
                await db.SaveChangesAsync();
            });
        }

        private async Task ResetWarnings(Warnings warning)
        {
            await WithDbAsync(async db =>
            {
                var currentWarning = await db.Warnings
                    .Where(w => w.Warnid == warning.Warnid)
                    .FirstOrDefaultAsync();
                if (currentWarning != null)
                {
                    db.Warnings.Remove(currentWarning);
                    await db.SaveChangesAsync();
                }
            });
        }

        private async Task<Warnings> GetWarning(ShardedInteractionContext context, IGuildUser userWarned)
        {
            return await WithDbAsync(async db =>
            {
                return await db.Warnings
                    .Where(w => w.ServerId == (long)context.Guild.Id && w.UserWarnedId == (long)userWarned.Id)
                    .FirstOrDefaultAsync();
            });
        }

        private async Task<string> GetNoteInfo(ShardedInteractionContext Context)
        {
            StringBuilder sb = new StringBuilder();
            await WithDbAsync(async db =>
            {
                var note = await db.Notes
                    .FirstOrDefaultAsync(n => n.ServerId == (long)Context.Guild.Id);
                if (note == null)
                {
                    sb.AppendLine($"Unable to find a note for server [{Context.Guild.Name}], perhaps try adding one by using /set-note \"Note goes here!\"");
                }
                else
                {
                    sb.AppendLine(note.Note1);
                    sb.AppendLine();
                    sb.Append($"*Note set by [**{note.SetBy}**] on [**{note.TimeSet}**]*");
                }
            });
            return sb.ToString();
        }
    }
}