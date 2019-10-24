using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using Discord;
using Discord.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Net;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaBotCore.Modules.Wow
{
    public class WowClassic : ModuleBase
    {

        private readonly ILogger<WowClassic> _logger;
        private readonly List<String> _wclRegions = new List<String>{"US", "EU", "KR", "TW", "CN"};
        private readonly ChannelCheck _cc;
        private WarcraftLogs _wclLogsApi;        
        
        public WowClassic(IServiceProvider services) 
        {
            _logger = services.GetRequiredService<ILogger<WowClassic>>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _wclLogsApi = services.GetRequiredService<WarcraftLogs>();
        }

        [Command("get-guildc")]
        public async Task GetClassicGuild()
        {
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            WowClassicGuild wowClassicGuild = null;

            embed.Title = $"[{Context.Guild.Name}] WoW Classic Guild Association";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            using (var db = new NinjaBotEntities())
            {
                wowClassicGuild = db.WowClassicGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefault();
            }
            if (wowClassicGuild != null)
            {                
                sb.AppendLine($"**Guild Name:** {wowClassicGuild.WowGuild}");
                sb.AppendLine($"**Realm:** {wowClassicGuild.WowRealm}");
                sb.AppendLine($"**Region:** {wowClassicGuild.WowRegion}");                
            }
            else
            {
                sb.AppendLine($"There is no guild associated to this server!");
                sb.AppendLine("Placeholder for help text");
            } 
            embed.WithColor(0, 255, 155);
            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);
        }

        [Command("set-guildc")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetClassicGuild(string guildName, string realm, string region = "US")
        {
            var embed = new EmbedBuilder();
            var wowClassicGuild = new WowClassicGuild();
            var sb = new StringBuilder();

            embed.ThumbnailUrl = Context.Guild.IconUrl;

            if (!_wclRegions.Contains(region.ToUpper()))
            {
                embed.Title = "Error setting guild!";
                embed.WithColor(255, 0 , 0);
                sb.AppendLine("Please specify a valid region.");
                sb.AppendLine();
                sb.AppendLine("**Possible regions:**");            
                foreach (var reg in _wclRegions)
                {
                    sb.AppendLine(reg);
                }
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
                return;
            } 

            wowClassicGuild.ServerId = (long)Context.Guild.Id;
            wowClassicGuild.SetById = (long)Context.User.Id;
            wowClassicGuild.WowGuild = guildName;
            wowClassicGuild.WowRealm = realm;
            wowClassicGuild.WowRegion = region;
            wowClassicGuild.SetBy = Context.User.Username;
            wowClassicGuild.TimeSet = DateTime.Now;  
            wowClassicGuild.ServerName = Context.Guild.Name;          

            try
            {
                using (var db = new NinjaBotEntities())
                {
                    var currentGuild = db.WowClassicGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                    if (currentGuild != null)
                    {
                        db.Remove(currentGuild);                                    
                    }
                    db.WowClassicGuild.Add(wowClassicGuild);
                    await db.SaveChangesAsync();
                }   
                embed.Title = $"[{Context.Guild.Name}] WoW Classic Guild Association";
                sb.AppendLine($"**Guild Name:** {wowClassicGuild.WowGuild}");
                sb.AppendLine($"**Realm:** {wowClassicGuild.WowRealm}");
                sb.AppendLine($"**Region:** {wowClassicGuild.WowRegion}");
                embed.Description = sb.ToString();
                embed.WithFooter(new EmbedFooterBuilder
                {
                    Text = $"Change made by [{Context.User.Username}]",
                    IconUrl = Context.User.GetAvatarUrl()                    
                });
                embed.WithColor(0, 255, 155);
                await _cc.Reply(Context, embed);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting classic guild for {Context.Guild.Name} -> [{ex.Message}]");
            }     
            
        }

        [Command("logsc")]
        public async Task GetLogsClassic()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            int maxReturn = 2;
            WowClassicGuild wowClassicGuild = null;
            using (var db = new NinjaBotEntities())
            {                
                wowClassicGuild = db.WowClassicGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefault();                
            }
            if (wowClassicGuild != null)
            {
                var guildLogs = await _wclLogsApi.GetReportsFromGuildClassic(wowClassicGuild.WowGuild, wowClassicGuild.WowRealm, wowClassicGuild.WowRegion);
                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i <= maxReturn && i <= guildLogs.Count; i++)
                    {
                        sb.AppendLine($"[__**{guildLogs[i].title}** **/** **{guildLogs[i].zoneName}**__]({guildLogs[i].reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{_wclLogsApi.UnixTimeStampToDateTime(guildLogs[i].start)}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{_wclLogsApi.UnixTimeStampToDateTime(guildLogs[i].end)}**");
                        sb.AppendLine($"\t:pencil2: Created by [**{guildLogs[i].owner}**]"); 
                        sb.AppendLine();
                    }
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{wowClassicGuild.WowGuild}** on **{wowClassicGuild.WowRealm}**__:1234: ";
                    embed.Description = sb.ToString();
                    embed.WithColor(0, 255, 100);
                    await _cc.Reply(Context, embed);                    
                }
            }
        }
    }
}