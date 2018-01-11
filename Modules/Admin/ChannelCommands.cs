using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Net;
using Discord.Commands;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Admin
{
    public class SetChanCommands : ModuleBase
    {
        private ChannelCheck _cc;        

        public SetChanCommands(ChannelCheck cc)
        {
            _cc = cc;
        }

        [Command("set-channel")]
        [Summary("Change the bot's default reply channel to the channel you issue this command from")]
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
                await ReplyAsync($"Attempting to set reply to channel to **{channelName}**");

                await _cc.SetGuildBotChannelAsync(channelId, channelName, userId, userName, guildName, guildId);

                await ReplyAsync($"Reply to channel set to **{channelName}**");                
            }
            catch (Exception ex)
            {
                await ReplyAsync("Something went wrong :(");

                Console.WriteLine($"Error while setting reply to channel {ex.Message} {ex.Source} {ex.InnerException}");

                await ReplyAsync(sb.ToString());
            }
        }

        [Command("get-channel")]
        [Summary("Shows the channel name the bot will reply to on this server")]
        public async Task GetChannel()
        {
            ulong guildId = Context.Guild.Id;
            string guildName = Context.Guild.Name;

            var channelInfo = _cc.GetGuildBotChannel(guildId);

            if (!string.IsNullOrEmpty(channelInfo.ChannelName))
            {
                await ReplyAsync($"Reply to channel for guild **{channelInfo.ServerName}** is **{channelInfo.ChannelName}**");
            }
            else
            {
                await ReplyAsync($"No channel set for guild **{guildName}**");
            }            
        }
    }
}
