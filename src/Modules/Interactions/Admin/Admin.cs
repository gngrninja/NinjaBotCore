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
    public class Admin : InteractionModuleBase<ShardedInteractionContext>
    {                
        private static bool _isLinked = false;        
        private static DiscordShardedClient _client;
        private static ChannelCheck _cc;
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private readonly ILogger<Admin> _logger;

        public Admin(IServiceProvider services)
        {            
            _client = services.GetRequiredService<DiscordShardedClient>();
            _logger = services.GetRequiredService<ILogger<Admin>>();
            if (!_isLinked)
            {
                _client.MessageReceived += WordFinder;
                _logger.LogInformation($"Hooked into message received for away commands.");
            }
            _isLinked = true;            
            _cc     = services.GetRequiredService<ChannelCheck>();
            _config = services.GetRequiredService<IConfigurationRoot>();            
            _prefix = _config["prefix"];                       
            _logger.LogInformation("Admin module loaded!");
        }

        private async Task WordFinder(SocketMessage messageDetails)
        {
            await Task.Run(async () =>
            {
                var message = messageDetails as SocketUserMessage;
                if (!messageDetails.Author.IsBot)
                {                              
                    List<NinjaBotCore.Database.WordList> serverWordList = null;
                    using (var db = new NinjaBotEntities())
                    {
                        SocketGuild guild = (message.Channel as SocketGuildChannel)?.Guild;
                        serverWordList = db.WordList.Where(w => w.ServerId == (long)guild.Id).ToList();                        
                    }                    
                    bool wordFound = false;
                    foreach (var singleWord in serverWordList)
                    {                  
                        foreach (var content in messageDetails.Content.ToLower().Split(' '))
                        {
                            if (singleWord.Word.ToLower().Contains(content))
                            {
                                wordFound = true;
                            }
                        }      
                    }
                    if (wordFound)
                    {
                        await messageDetails.DeleteAsync();
                    }
                }
            });
        }     

        [SlashCommand("show-servers", "show servers the bot is in")]        
        [RequireOwner]
        public async Task ListGuilds()
        {
            StringBuilder sb = new StringBuilder();
            var guilds = _client.Guilds.ToList();
            foreach (var guild in guilds)
            {
                sb.AppendLine($"Name: {guild.Name} Id: {guild.Id} Owner: {guild.Owner}");
            }
            await RespondAsync(sb.ToString(), ephemeral: true);
        }

        [SlashCommand("announce", "announce a message")]
        [RequireOwner]
        public async Task AnnounceMessage(string message)
        {
            var guilds = _client.Guilds.ToList();
            foreach (var guild in guilds)
            {                
                var messageChannel = guild.DefaultChannel as ISocketMessageChannel;
                if (messageChannel != null)
                {
                    var embed = new EmbedBuilder();
                    embed.Title = "NinjaBot Announcement";
                    embed.Description = message;
                    embed.ThumbnailUrl = Context.User.GetAvatarUrl();
                    await messageChannel.SendMessageAsync("", false, embed.Build());
                    System.Threading.Thread.Sleep(5000);
                }
            }
        }

        [SlashCommand("leave-server", "leave a server")]        
        [RequireOwner]
        public async Task LeaveServer(ulong serverId)
        {
            await _client.GetGuild(serverId).LeaveAsync();
        }

        [SlashCommand("add-wow-resource", "add a wow resource")]
        [RequireOwner]        
        public async Task AddWoWResource(string args = null)
        {
            if (args != null)
            {
                try
                {
                    int argCount = args.Split(',').Count();
                    if (argCount == 4)
                    {
                        using (var db = new NinjaBotEntities())
                        {
                            db.WowResources.Add(new WowResources
                            {
                                ClassName = args.Split(',')[0].Trim(),
                                Specialization = args.Split(',')[1].Trim(),
                                Resource = args.Split(',')[2].Trim(),
                                ResourceDescription = args.Split(',')[3].Trim(),
                            });
                            await db.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error adding resource: [{ex.Message}]", ephemeral: true);
                }
            }
        }

        [SlashCommand("remove-wow-resource", "remove wow resource")]
        [RequireOwner]        
        public async Task RemoveWoWResource(int resourceId = 0)
        {
            if (resourceId > 0)
            {
                try
                {
                    using (var db = new NinjaBotEntities())
                    {
                        db.WowResources.Remove(db.WowResources.Where(r => r.Id == resourceId).FirstOrDefault());
                        await db.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error removing resource: [{ex.Message}]", ephemeral: true);
                }
            }
        }

        [SlashCommand("list-wow-resources", "list wow resources")]        
        public async Task ListWoWResource(string args = null)
        {
            List<WowResources> resources = null;
            using (var db = new NinjaBotEntities())
            {
                resources = db.WowResources.Where(r => r.ClassName.ToLower().Contains(args)).ToList();
            }
            if (resources != null)
            {
                var embed = new EmbedBuilder();
                embed.Title = $"WoW Resource List Search: [{args}]";
                foreach (var resource in resources)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"Class: [{resource.ClassName}]");
                    sb.AppendLine($"Specialization: [{resource.Specialization}]");
                    sb.AppendLine($"Resource: [{resource.Resource}]");
                    sb.AppendLine($"ResourceDescription: [{resource.ResourceDescription}]");
                    embed.AddField(new EmbedFieldBuilder
                    {
                        Name = $"{resource.Id}",
                        Value = sb.ToString()
                    });
                }
                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
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
                //await user.SendMessageAsync($"You've been kicked from [**{Context.Guild.Name}**] by [**{Context.User.Username}**]: [**{reason}**]");
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
                catch (Exception ex)
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
                //await user.SendMessageAsync($"You have been banned from [**{Context.Guild.Name}**] -> [**{reason}**]");
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
            var currentBans = Context.Guild.GetBansAsync().FlattenAsync().Result;
            var bannedUser = currentBans.Where(c => c.User.Username.Contains(user)).FirstOrDefault();
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
            var embed = new EmbedBuilder();
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            StringBuilder sb = new StringBuilder();
            try
            {
                embed.Title = $"User bans on {Context.Guild.Name}";
                var bans = Context.Guild.GetBansAsync();               
                if (bans.FlattenAsync().Result.Count() > 0)
                {
                    foreach (var ban in bans.FlattenAsync().Result)
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
            //await _client.Log("test")
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("set-join-message", "set join message")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeGreeting(string args = null)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(args))
            {
                embed.Title = $"Join greeting change for {Context.Guild.Name}";
                sb.AppendLine("New message:");
                sb.AppendLine(args);
                using (var db = new NinjaBotEntities())
                {
                    try
                    {
                        var guildGreetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)Context.Guild.Id).FirstOrDefault();
                        if (guildGreetingInfo != null)
                        {
                            guildGreetingInfo.Greeting = args.Trim();
                            guildGreetingInfo.SetById = (long)Context.User.Id;
                            guildGreetingInfo.SetByName = Context.User.Username;
                            guildGreetingInfo.TimeSet = DateTime.Now;
                        }
                        else
                        {
                            db.ServerGreetings.Add(new ServerGreeting
                            {
                                DiscordGuildId = (long)Context.Guild.Id,
                                Greeting = args.Trim(),
                                SetById = (long)Context.User.Id,
                                SetByName = Context.User.Username,
                                TimeSet = DateTime.Now
                            });
                        }
                        await db.SaveChangesAsync();
                    }
                    catch (Exception)
                    {
                        embed.Title = $"Error changing message";
                        sb.AppendLine($"{Context.User.Mention},");
                        sb.AppendLine($"I've encounted an error, please contact the owner for help.");
                    }
                }
            }
            else
            {
                embed.Title = $"Error changing message";
                sb.AppendLine($"{Context.User.Mention},");
                sb.AppendLine($"Please provided a message!");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("set-part-message", "set a message to display when users leave the server")]        
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeParting(string args = null)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(args))
            {
                embed.Title = $"Parting message change for {Context.Guild.Name}";
                sb.AppendLine("New message:");
                sb.AppendLine(args);
                using (var db = new NinjaBotEntities())
                {
                    try
                    {
                        var guildGreetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)Context.Guild.Id).FirstOrDefault();
                        if (guildGreetingInfo != null)
                        {
                            guildGreetingInfo.PartingMessage = args.Trim();
                            guildGreetingInfo.SetById = (long)Context.User.Id;
                            guildGreetingInfo.SetByName = Context.User.Username;
                            guildGreetingInfo.TimeSet = DateTime.Now;
                        }
                        else
                        {
                            db.ServerGreetings.Add(new ServerGreeting
                            {
                                DiscordGuildId = (long)Context.Guild.Id,
                                PartingMessage = args.Trim(),
                                SetById = (long)Context.User.Id,
                                SetByName = Context.User.Username,
                                TimeSet = DateTime.Now
                            });
                        }
                        await db.SaveChangesAsync();
                    }
                    catch (Exception)
                    {
                        embed.Title = $"Error changing message";
                        sb.AppendLine($"{Context.User.Mention},");
                        sb.AppendLine($"I've encounted an error, please contact the owner for help.");
                    }
                }
            }
            else
            {
                embed.Title = $"Error changing message";
                sb.AppendLine($"{Context.User.Mention},");
                sb.AppendLine($"Please provided a message!");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("toggle-greetings", "toggle join/leave messages to be displayed in this channel")]        
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ToggleGreetings()
        {
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
                    Console.WriteLine($"Error toggling greetings -> [{ex.Message}]!");
                }
            }
            embed.Title = $"User greeting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("set-parting-channel", "if greetings are enabled, set the channel for parting messages")]        
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetPartingChannel()
        {
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
                    Console.WriteLine($"Error toggling greetings -> [{ex.Message}]!");
                }
            }
            embed.Title = $"User greeting settings for {Context.Guild.Name}";
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            await RespondAsync(embed: embed.Build(), ephemeral: true);
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
                                WhenBlacklisted = DateTime.Now
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
            if (numberOfMessages > 25)
            {
                numberOfMessages = 25;
            }
            var messagesToDelete = await Context.Channel.GetMessagesAsync(numberOfMessages).FlattenAsync();
            await (Context.Channel as ITextChannel).DeleteMessagesAsync(messagesToDelete);        
        }

        [SlashCommand("clearu", "clear x amount of messages from a specific user")]        
        [RequireBotPermission(GuildPermission.ManageMessages)]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task ClearMessageFromUser(IGuildUser user, int numberOfMessages = 5)
        {        
            if (numberOfMessages > 25)
            {
                numberOfMessages = 25;
            }            
            var messagesToDelete = await Context.Channel.GetMessagesAsync(numberOfMessages).FlattenAsync();
            var messagesFromUser = messagesToDelete.Where(a => a.Author.Id == user.Id);
            await (Context.Channel as ITextChannel).DeleteMessagesAsync(messagesFromUser);        
        }          

        [SlashCommand("set-note", "set a note for the server")]        
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task SetNote(string note)
        {
            string result = await SetNoteInfo(Context, note);
            var embed = new EmbedBuilder();
            embed.Title = $":notepad_spiral:Notes for {Context.Guild.Name}:notepad_spiral:";
            embed.Description = result;
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.WithColor(new Color(0, 255, 0));
            await RespondAsync(embed: embed.Build(), ephemeral: true);
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
                AddWarning(Context, user);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Unable to log warning in database -> [{ex.Message}]!");
            }
            await user.SendMessageAsync(warnMessage.ToString());
            await RespondAsync(warnMessage.ToString(), ephemeral: true);
            if (numWarnings >= 3)
            {
                await KickUser(user, "Maximum number of warnings reached!");
                ResetWarnings(currentWarnings);
            }      
        }

        [SlashCommand("reset-warnings", "reset warnings for a user")]        
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ResetWarning(IGuildUser user)
        {
            var warnings = await GetWarning(Context, user);
            if (warnings != null)
            {
                ResetWarnings(warnings);
                await RespondAsync($"Warnings reset for **{user.Username}**", ephemeral: true);
            }
            else
            {
                await RespondAsync($"No warnings found for **{user.Username}**!", ephemeral: true);
            }
        }

        [SlashCommand("numservers", "list number of servers the bot is in")]
        [RequireOwner]
        public async Task GetNumGuilds()
        {
            var client = (IDiscordClient)Context.Client;            
            var numGuilds = await client.GetGuildsAsync();
            await RespondAsync($"I am connected to {numGuilds.Count()} guilds!", ephemeral: true);
        }

        [SlashCommand("add-word", "add word to blacklist")]
        [RequireOwner]
        public async Task AddWord(string word) 
        {
            var sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                var words = db.WordList.Where(w => w.ServerId == (long)Context.Guild.Id).ToList();
                bool wordFound = false;
                foreach (var singleWord in words)
                {
                    if (singleWord.Word.ToLower().Contains(word.ToLower()))
                    {
                        wordFound = true;
                    }
                }
                if (wordFound)
                {
                    sb.AppendLine($"[{word}] is already in the list!");
                }
                else
                {
                    sb.AppendLine($"Adding [{word}] to the list!");
                    db.Add(new WordList
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        Word = word,
                        SetById = (long)Context.User.Id                        
                    });
                    await db.SaveChangesAsync();
                }

            }
            await RespondAsync(sb.ToString(), ephemeral: true);                        
        }        
        
        private async void AddWarning(ShardedInteractionContext context, IGuildUser userWarned)
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
                        TimeIssued = DateTime.Now,
                        NumWarnings = 1
                    });                    
                }
                await db.SaveChangesAsync();
            }
        }

        private async void ResetWarnings(Warnings warning)
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

        private async Task<string> SetNoteInfo(ShardedInteractionContext Context, string noteText)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                using (var db = new NinjaBotEntities())
                {
                    var currentNote = db.Notes.FirstOrDefault(c => c.ServerId == (long)Context.Guild.Id);
                    if (currentNote == null)
                    {
                        Note n = new Note()
                        {
                            Note1 = noteText,
                            ServerId = (long)Context.Guild.Id,
                            ServerName = Context.Guild.Name,
                            SetBy = Context.User.Username,
                            SetById = (long)Context.User.Id,
                            TimeSet = DateTime.Now
                        };
                        db.Notes.Add(n);
                    }
                    else
                    {
                        currentNote.Note1 = noteText;
                        currentNote.SetBy = Context.User.Username;
                        currentNote.SetById = (long)Context.User.Id;
                        currentNote.TimeSet = DateTime.Now;
                    }
                    await db.SaveChangesAsync();
                }
                sb.AppendLine($"Note successfully added for server [**{Context.Guild.Name}**] by [**{Context.User.Username}**]!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting note {ex.Message}");
                sb.AppendLine($"Something went wrong adding a note for server [**{Context.Guild.Name}**] :(");
            }
            return sb.ToString();
        }

        private async Task<string> GetNoteInfo(ShardedInteractionContext Context)
        {
            StringBuilder sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                var note = db.Notes.FirstOrDefault(n => n.ServerId == (long)Context.Guild.Id);
                if (note == null)
                {
                    sb.AppendLine($"Unable to find a note for server [{Context.Guild.Name}], perhaps try adding one by using {_prefix}set-note \"Note goes here!\"");
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
        
        [SlashCommand("force-greeting-clear", "force a greeting clear")]
        [RequireOwner]
        public async Task ForceGreetingClear(long serverId)
        {
            ServerGreeting greetingInfo = null;
            using (var db = new NinjaBotEntities())
            {
                greetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == serverId).FirstOrDefault();
            }
            if (greetingInfo != null)
            {
                try
                {
                    using (var db = new NinjaBotEntities())
                    {
                        db.Remove(db.ServerGreetings.Where(g => g.DiscordGuildId == serverId).FirstOrDefault());
                        await db.SaveChangesAsync();
                    }
                    await RespondAsync("Cleared!");
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error clearing greeting -> [{ex.Message}]", ephemeral: true);
                }
            }
            else
            {
                await RespondAsync($"No association found for [{serverId}]!", ephemeral: true);
            }
        }
    }
}
