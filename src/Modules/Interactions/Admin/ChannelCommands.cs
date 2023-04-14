using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Net;
using Discord.Interactions;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class SetChanCommands : InteractionModuleBase<ShardedInteractionContext>
    {
        private ChannelCheck _cc;        
        private readonly ILogger _logger;

        public SetChanCommands(ChannelCheck cc, ILogger<SetChanCommands> logger)
        {
            _cc     = cc;
            _logger = logger;
        }

        [SlashCommand("set-channel", "Change the bot's default reply to channel to the one you use this command in")]        
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetChannel()
        {
            StringBuilder sb = new StringBuilder();            

            ulong guildId = Context.Guild.Id;
            string guildName = Context.Guild.Name;

            ulong userId = Context.User.Id;
            string userName = Context.User.Username;

            ulong channelId = Context.Channel.Id;
            string channelName = Context.Channel.Name;
            
            try
            {                
                await _cc.SetGuildBotChannelAsync(channelId, channelName, userId, userName, guildName, guildId);
                await RespondAsync($"Reply to channel set to **{channelName}**");                
            }
            catch (Exception ex)
            {
                await RespondAsync("Something went wrong :(");

                _logger.LogError($"Error while setting reply to channel {ex.Message} {ex.Source} {ex.InnerException}");

                await RespondAsync(sb.ToString());
            }
        }

        [SlashCommand("get-channel", "show the channel the bot is currently sending replies to")]
        public async Task GetChannel()
        {
            ulong guildId = Context.Guild.Id;
            string guildName = Context.Guild.Name;

            var channelInfo = _cc.GetGuildBotChannel(guildId);

            if (!string.IsNullOrEmpty(channelInfo.ChannelName))
            {
                await RespondAsync($"Reply to channel for guild **{channelInfo.ServerName}** is **{channelInfo.ChannelName}**");
            }
            else
            {
                await RespondAsync($"No channel set for guild **{guildName}**");
            }            
        }
    }
}
