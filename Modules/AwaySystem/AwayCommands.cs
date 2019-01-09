using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Commands;
using Discord;
using Discord.WebSocket;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Away
{
    public class AwayCommands : ModuleBase
    {
        private static bool _isLinked = false;
        private static ChannelCheck _cc = null;
        private static DiscordSocketClient _client;
        private readonly ILogger _logger;
        //Work on way to do this when bot starts
        public AwayCommands(DiscordSocketClient client, ILogger<AwayCommands> logger)
        {
            _logger = logger;
            if (!_isLinked)
            {
                client.MessageReceived += AwayMentionFinder;
                _logger.LogInformation($"Hooked into message received for away commands.");
            }
            _isLinked = true;
            if (_cc == null)
            {
                _cc = new ChannelCheck();
            }
            if (_client == null)
            {
                _client = client;
            }
        }

        [Command("away", RunMode = RunMode.Async)]
        [Alias("afk")]
        [Summary("Set yourself as away, replying to anyone that @mentions you")]
        public async Task SetAway([Remainder] string input)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                var message = input;
                var user = Context.User;
                string userName = string.Empty;
                string userMentionName = string.Empty;
                if (user != null)
                {
                    userName = user.Username;
                    userMentionName = user.Mention;
                }
                var data = new AwayData();
                var away = new AwaySystem();
                var attempt = data.getAwayUser(userName);

                if (string.IsNullOrEmpty(message.ToString()))
                {
                    message = "No message set!";
                }
                if (attempt != null)
                {
                    away.UserName = attempt.UserName;
                    away.Status = attempt.Status;
                    if ((bool)away.Status)
                    {
                        sb.AppendLine($"You're already away, **{userMentionName}**!");
                    }
                    else
                    {
                        sb.AppendLine($"Marking you as away, **{userMentionName}**, with the message: *{message.ToString()}*");
                        away.Status = true;
                        away.Message = message;
                        away.UserName = userName;
                        away.TimeAway = DateTime.Now;

                        var awayData = new AwayData();
                        awayData.setAwayUser(away);
                    }
                }
                else
                {
                    sb.AppendLine($"Marking you as away, **{userMentionName}**, with the message: *{message.ToString()}*");
                    away.Status = true;
                    away.Message = message;
                    away.UserName = userName;
                    away.TimeAway = DateTime.Now;

                    var awayData = new AwayData();
                    awayData.setAwayUser(away);
                }
                await _cc.Reply(Context, sb.ToString());
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Something went wrong setting you away :(");
                _logger.LogError($"Away command error {ex.Message}");
                await _cc.Reply(Context, sb.ToString());
            }
        }

        [Command("back", RunMode = RunMode.Async)]
        [Summary("Set yourself as back from being away")]
        public async Task SetBack(bool forced = false, IGuildUser forceUser = null)
        {
            try
            {
                IGuildUser user = null;
                StringBuilder sb = new StringBuilder();
                var data = new AwayData();
                if (forced)
                {
                    user = forceUser;
                }
                else
                {
                    user = Context.User as IGuildUser;
                }                

                string userName = string.Empty;
                string userMentionName = string.Empty;
                if (user != null)
                {
                    userName = user.Username;
                    userMentionName = user.Mention;
                }
                var attempt = data.getAwayUser(userName);
                var away = new AwaySystem();

                if (attempt != null)
                {
                    away.UserName = attempt.UserName;
                    away.Status = attempt.Status;
                    if (!(bool)away.Status)
                    {
                        sb.AppendLine($"You're not even away yet, **{userMentionName}**");
                    }
                    else
                    {
                        away.Status = false;
                        away.Message = string.Empty;
                        var awayData = new AwayData();
                        awayData.setAwayUser(away);
                        string awayDuration = string.Empty;
                        if (attempt.TimeAway.HasValue)
                        {
                            var awayTime = DateTime.Now - attempt.TimeAway;
                            if (awayTime.HasValue)
                            {
                                awayDuration = $"**{awayTime.Value.Days}** days, **{awayTime.Value.Hours}** hours, **{awayTime.Value.Minutes}** minutes, and **{awayTime.Value.Seconds}** seconds";
                            }
                        }
                        if (forced)
                        {
                            sb.AppendLine($"You're now set as back **{userMentionName}** (forced by: **{Context.User.Username}**)!");
                        }
                        else
                        {
                            sb.AppendLine($"You're now set as back, **{userMentionName}**!");
                        }                        
                        sb.AppendLine($"You were away for: [{awayDuration}]");
                    }
                    await _cc.Reply(Context, sb.ToString());
                }
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Something went wrong marking you as back :(");
                _logger.LogError($"Back command error {ex.Message}");
                await _cc.Reply(Context, sb.ToString());
            }
        }

        [Command("set-back", RunMode = RunMode.Async)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetBack(IGuildUser user)
        {
            await SetBack(forced: true, forceUser: user);
        }

        private async Task AwayMentionFinder(SocketMessage messageDetails)
        {
            if (messageDetails.Channel is IDMChannel)
            {
                //do stuff
            }
            else if (messageDetails.Channel is IGuildChannel)
            {
                //do other stuff
            }
            var message = messageDetails as SocketUserMessage;
            if (!messageDetails.Author.IsBot)
            {
                var userMentioned = messageDetails.MentionedUsers.FirstOrDefault();
                if (userMentioned != null)
                {
                    var awayData = new AwayData();
                    var awayUser = awayData.getAwayUser(userMentioned.Username);
                    if (awayUser != null)
                    {
                        string awayDuration = string.Empty;
                        if (awayUser.TimeAway.HasValue)
                        {
                            var awayTime = DateTime.Now - awayUser.TimeAway;
                            if (awayTime.HasValue)
                            {
                                awayDuration = $"**{awayTime.Value.Days}** days, **{awayTime.Value.Hours}** hours, **{awayTime.Value.Minutes}** minutes, and **{awayTime.Value.Seconds}** seconds";
                            }
                        }

                        _logger.LogInformation($"Mentioned user {userMentioned.Username} -> {awayUser.UserName} -> {awayUser.Status}");
                        if ((bool)awayUser.Status)
                        {
                            if (userMentioned.Username == (awayUser.UserName))
                            {
                                SocketGuild guild = (message.Channel as SocketGuildChannel)?.Guild;                                
                                CommandContext context = new CommandContext(_client, message);
                                //SocketGuild guildInfo = messageDetails.Discord.GetGuild(context.Guild.Id);
                                //var client = context.Client as DiscordSocketClient;
                                EmbedBuilder embed = new EmbedBuilder();
                                embed.WithColor(new Color(0, 71, 171));

                                if (!string.IsNullOrWhiteSpace(guild.IconUrl))
                                {
                                    embed.ThumbnailUrl = userMentioned.GetAvatarUrl();
                                }

                                embed.Title = $":clock: {awayUser.UserName} is away! :clock:";
                                embed.Description = $"Since: **{awayUser.TimeAway}\n**Duration: {awayDuration}\nMessage: {awayUser.Message}";
                                await messageDetails.Channel.SendMessageAsync("", false, embed);
                            }
                        }
                    }
                }
            }
        }
    }
}
