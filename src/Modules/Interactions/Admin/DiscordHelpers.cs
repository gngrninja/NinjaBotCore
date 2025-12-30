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
        [DefaultMemberPermissions(GuildPermission.KickMembers)]
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
        [DefaultMemberPermissions(GuildPermission.KickMembers)]
        public async Task UnBanUser(string user)
        {
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
            await RespondAsync(embed: embed.Build());
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
            string curGreeting = string.Empty;
            using (var db = new NinjaBotEntities())
            {
                var guildGreetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)Context.Guild.Id).FirstOrDefault();
                if (guildGreetingInfo != null)
                {
                    curGreeting = guildGreetingInfo.Greeting;
                }                
            }
            if (string.IsNullOrEmpty(curGreeting))
            {
                curGreeting = "Hello!";
            }            
            var mb = new ModalBuilder()
                .WithTitle("Greeting message")
                .WithCustomId("joining_message")
                .AddTextInput("Message:", "joining_message", TextInputStyle.Paragraph, $"{curGreeting}");
            await Context.Interaction.RespondWithModalAsync(mb.Build());
        }

        [SlashCommand("set-part-message", "set a message to display when users leave the server")]        
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeParting()
        {
            string curParting = string.Empty;
            using (var db = new NinjaBotEntities())
            {
                var guildGreetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)Context.Guild.Id).FirstOrDefault();
                if (guildGreetingInfo != null)
                {
                    curParting = guildGreetingInfo.PartingMessage;
                }                
            }
            if (string.IsNullOrEmpty(curParting))
            {
                curParting = "Goodbye!";
            }                        
            var mb = new ModalBuilder()
                .WithTitle("Parting message")
                .WithCustomId("parting_message")
                .AddTextInput("Message:", "parting_message", TextInputStyle.Paragraph, $"{curParting}");
            await Context.Interaction.RespondWithModalAsync(mb.Build());
        }

        [SlashCommand("toggle-greetings", "toggle join/leave messages to be displayed in this channel")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ToggleGreetings()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                try
                {
                    var currentSetting = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)Context.Guild.Id).FirstOrDefault();
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
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error toggling greetings in {GuildName}", Context.Guild.Name);
                }
            }
            embed.Title = $"User greeting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("set-parting-channel", "if greetings are enabled, set the channel for parting messages")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetPartingChannel()
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                try
                {
                    var currentSetting = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)Context.Guild.Id).FirstOrDefault();
                    if (currentSetting != null)
                    {
                        if (currentSetting.GreetUsers == true)
                        {
                            currentSetting.PartingChannelId = (long)Context.Channel.Id;                            
                            sb.AppendLine($"Parting messages channel set to {Context.Channel.Name}!");
                        }
                        else
                        {
                            sb.AppendLine("Please enable greetings first via /toggle-greetings");
                        }
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting parting channel in {GuildName}", Context.Guild.Name);
                }
            }
            embed.Title = $"User greeting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
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
                using (var db = new NinjaBotEntities())
                {
                    var blacklist = db.Blacklist;
                    if (blacklist != null)
                    {
                        var getUser = blacklist.Where(b => b.DiscordUserId == (long)user.Id).FirstOrDefault();
                        if (getUser != null)
                        {
                            sb.AppendLine($"Unblacklisting {user.Username}");
                            blacklist.Remove(getUser);
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(reason))
                            {
                                reason = "just because";
                            }
                            blacklist.Add(new Blacklist
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
                    }
                }
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
            var curNote = await GetNoteInfo(Context);
            var mb = new ModalBuilder()
                .WithTitle($"Note for Discord server: [{Context.Guild.Name}]")
                .WithCustomId("discord_server_note")
                .AddTextInput("Note:", "note_text", TextInputStyle.Paragraph, $"{curNote}");
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
        
        private async Task AddWarning(ShardedInteractionContext context, IGuildUser userWarned)
        {
            using (var db = new NinjaBotEntities())
            {
                var warnings = db.Warnings.Where(w => w.ServerId == (long)context.Guild.Id && w.UserWarnedId == (long)userWarned.Id).FirstOrDefault();
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
            }
        }

        private async Task ResetWarnings(Warnings warning)
        {
            using (var db = new NinjaBotEntities())
            {
                var currentWarning = db.Warnings.Where(w => w.Warnid == warning.Warnid).FirstOrDefault();
                if (currentWarning != null)
                {
                    db.Warnings.Remove(currentWarning);
                    await db.SaveChangesAsync();
                }
            }
        }

        private async Task<Warnings> GetWarning(ShardedInteractionContext context, IGuildUser userWarned)
        {
            Warnings warning = null;
            using (var db = new NinjaBotEntities())
            {
                warning = db.Warnings.Where(w => w.ServerId == (long)context.Guild.Id && w.UserWarnedId == (long)userWarned.Id).FirstOrDefault();
            }
            return warning;
        }

        private async Task<string> GetNoteInfo(ShardedInteractionContext Context)
        {
            StringBuilder sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                var note = db.Notes.FirstOrDefault(n => n.ServerId == (long)Context.Guild.Id);
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
            }
            return sb.ToString();
        }
    }
}