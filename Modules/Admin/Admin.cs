using Discord;
using Discord.Net;
using Discord.WebSocket;
using Discord.Commands;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Admin
{
    public class Admin : ModuleBase
    {

        private static DiscordSocketClient _client;
        private static ChannelCheck _cc;
        private readonly IConfigurationRoot _config;
        private string _prefix;

        public Admin(DiscordSocketClient client, ChannelCheck cc, IConfigurationRoot config)
        {            
            _client = client;            
            _cc = cc;
            _config = config;            
            _prefix = _config["prefix"];
            
            Console.WriteLine($"Admin module loaded");
        }

        [Command("change-prefix",RunMode = RunMode.Async)]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task ChangePrefix(char prefix)
        {
            using (var db = new NinjaBotEntities())
            {
                var currentPrefix = db.PrefixList.Where(p => p.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (currentPrefix != null)
                {
                    currentPrefix.Prefix = prefix;
                    currentPrefix.SetById = (long)Context.User.Id;
                }
                else
                {
                    db.PrefixList.Add(new PrefixList
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        Prefix = prefix,
                        SetById = (long)Context.User.Id
                    });
                }
                await db.SaveChangesAsync();
            }
            await _cc.Reply(Context, $"Prefix for [**{Context.Guild.Name}**] changed to [**{prefix}**]");
        }

        [Command("Show-Servers")]
        [Summary("Show the servers the bot is in")]
        [RequireOwner]
        public async Task ListGuilds()
        {
            StringBuilder sb = new StringBuilder();
            var guilds = _client.Guilds.ToList();
            foreach (var guild in guilds)
            {
                sb.AppendLine($"Name: {guild.Name} Id: {guild.Id} Owner: {guild.Owner}");
            }
            await _cc.Reply(Context, sb.ToString());
        }

        [Command("Announce", RunMode = RunMode.Async)]
        [RequireOwner]
        public async Task AnnounceMessage([Remainder] string message)
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
                    await messageChannel.SendMessageAsync("", false, embed);
                    System.Threading.Thread.Sleep(5000);
                }
            }
        }

        [Command("Leave-Server")]
        [Summary("Leave a server")]
        [RequireOwner]
        public async Task LeaveServer([Remainder] ulong serverId)
        {
            await _client.GetGuild(serverId).LeaveAsync();
        }

        [Command("AddWResource")]
        [RequireOwner]
        [Alias("awr")]
        public async Task AddWoWResource([Remainder] string args = null)
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
                    await _cc.Reply(Context, $"Error adding resource: [{ex.Message}]");
                }
            }
        }

        [Command("RemoveWResource")]
        [RequireOwner]
        [Alias("rwr")]
        public async Task RemoveWoWResource([Remainder] int resourceId = 0)
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
                    await _cc.Reply(Context, $"Error removing resource: [{ex.Message}]");
                }
            }
        }

        [Command("AdminListWowResources")]
        [Alias("alwr")]
        public async Task ListWoWResource([Remainder] string args = null)
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
                await _cc.Reply(Context, embed);
            }
        }

        [Command("kick", RunMode = RunMode.Async)]
        [Summary("Kick someone, not nice... but needed sometimes")]
        [RequireBotPermission(GuildPermission.KickMembers)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task KickUser(IGuildUser user, [Remainder] string reason = null)
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
                await user.SendMessageAsync($"You've been kicked from [**{Context.Guild.Name}**] by [**{Context.User.Username}**]: [**{reason}**]");
                sb.AppendLine($"Reason: [**{reason}**]");
            }
            catch (Exception ex)
            {
                embed.Title = $"Error attempting to kick {user.Username}";
                sb.AppendLine($"[{ex.Message}]");
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 0, 255));
            await _cc.Reply(Context, embed);
        }

        [Command("ban", RunMode = RunMode.Async)]
        [Summary("Ban someone, not nice... but needed sometimes")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.BanMembers)]
        public async Task BanUser(IGuildUser user, [Remainder] string args = null)
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
                await user.SendMessageAsync($"You have been banned from [**{Context.Guild.Name}**] -> [**{reason}**]");
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
            await _cc.Reply(Context, embed);
        }

        [Command("unban", RunMode = RunMode.Async)]
        [Summary("Unban someone... whew!")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.BanMembers)]
        public async Task UnBanUser(string user)
        {
            var embed = new EmbedBuilder();
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            StringBuilder sb = new StringBuilder();
            var currentBans = await Context.Guild.GetBansAsync();
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
            await _cc.Reply(Context, embed);
        }

        [Command("list-bans", RunMode = RunMode.Async)]
        [Summary("List the users currently banned on the server")]
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
                var bans = await Context.Guild.GetBansAsync();
                if (bans.Count > 0)
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
            await _cc.Reply(Context, embed);
        }

        [Command("set-join-message", RunMode = RunMode.Async)]
        [Alias("set-join")]
        [Summary("Change the greeting message for when someone joins the server")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeGreeting([Remainder] string args = null)
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
            await _cc.Reply(Context, embed);
        }

        [Command("set-part-message", RunMode = RunMode.Async)]
        [Alias("set-part")]
        [Summary("Change the message displayed when someone leaves the server")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ChangeParting([Remainder] string args = null)
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
            await _cc.Reply(Context, embed);
        }

        [Command("toggle-greetings", RunMode = RunMode.Async)]
        [Summary("Toogle greeting users that join/leave this server")]
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
            await _cc.Reply(Context, embed);
        }

        [Command("blacklist", RunMode = RunMode.Async)]
        [Summary("Blacklist someone (must be bot owner)")]
        [RequireOwner]
        public async Task BlackList(IGuildUser user, [Remainder] string reason = null)
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
            await _cc.Reply(Context, embed);
        }

        [Command("clear", RunMode = RunMode.Async)]
        [Summary("Clear an amount of messages in the channel")]
        [RequireBotPermission(GuildPermission.ManageMessages)]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task ClearMessage([Remainder] int numberOfMessages = 5)
        {
            if (numberOfMessages > 25)
            {
                numberOfMessages = 25;
            }
            var messagesToDelete = await Context.Channel.GetMessagesAsync(numberOfMessages).Flatten();
            await Context.Channel.DeleteMessagesAsync(messagesToDelete);
        }

        [Command("set-note", RunMode = RunMode.Async)]
        [Alias("snote")]
        [Summary("Set a note associated with a discord server")]
        [RequireUserPermission(GuildPermission.ManageMessages)]
        public async Task SetNote([Remainder] string note)
        {
            string result = await SetNoteInfo(Context, note);
            var embed = new EmbedBuilder();
            embed.Title = $":notepad_spiral:Notes for {Context.Guild.Name}:notepad_spiral:";
            embed.Description = result;
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.WithColor(new Color(0, 255, 0));
            await _cc.Reply(Context, embed);
        }

        [Command("get-note", RunMode = RunMode.Async)]
        [Alias("note")]
        [Summary("Get a note associated with a discord server")]
        public async Task GetNote()
        {
            string note = await GetNoteInfo(Context);
            var embed = new EmbedBuilder();
            embed.Title = $":notepad_spiral:Notes for {Context.Guild.Name}:notepad_spiral:";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.Description = note;
            embed.WithColor(new Color(0, 255, 0));
            await _cc.Reply(Context, embed);
        }

        [Command("warn",RunMode= RunMode.Async)]
        [Summary("Send a warning message to a user")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task WarnUser(IGuildUser user, [Remainder] string message = null)
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
            await _cc.Reply(Context,warnMessage.ToString());
            if (numWarnings >= 3)
            {
                await KickUser(user, "Maximum number of warnings reached!");
                ResetWarnings(currentWarnings);
            }            
        }

        [Command("reset-warnings",RunMode = RunMode.Async)]
        [Summary("Reset warnings for a user")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ResetWarning(IGuildUser user)
        {
            var warnings = await GetWarning(Context, user);
            if (warnings != null)
            {
                ResetWarnings(warnings);
                await _cc.Reply(Context, $"Warnings reset for **{user.Username}**");
            }
            else
            {
                await _cc.Reply(Context, $"No warnings found for **{user.Username}**!");
            }
        }

        private async void AddWarning(ICommandContext context, IGuildUser userWarned)
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

        private async Task<Warnings> GetWarning(ICommandContext context, IGuildUser userWarned)
        {
            Warnings warning = null;
            using (var db = new NinjaBotEntities())
            {
                warning = db.Warnings.Where(w => w.ServerId == (long)context.Guild.Id && w.UserWarnedId == (long)userWarned.Id).FirstOrDefault();
            }
            return warning;
        }

        private async Task<string> SetNoteInfo(ICommandContext Context, string noteText)
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

        private async Task<string> GetNoteInfo(ICommandContext Context)
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
        //[Command("Deafen")]
    }
}