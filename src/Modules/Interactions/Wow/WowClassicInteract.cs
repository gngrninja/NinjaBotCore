using Discord;
using Discord.Interactions;
using NinjaBotCore.Attributes;
using System;
using System.Threading.Tasks;
using NinjaBotCore.Services;
using System.Linq;
using System.Text;
using Discord.Net;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using System.Threading;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Database;
using System.Collections.Generic;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    public class WowClassicInteract : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly ILogger<WowClassicInteract> _logger;
        private readonly List<String> _wclRegions = new List<String>{"US", "EU", "KR", "TW", "CN"};
        private readonly ChannelCheck _cc;
        private WarcraftLogs _wclLogsApi;        
        
        public WowClassicInteract(IServiceProvider services) 
        {
            _logger = services.GetRequiredService<ILogger<WowClassicInteract>>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _wclLogsApi = services.GetRequiredService<WarcraftLogs>();
        }

        [SlashCommand("getclassicguild", "get classic guild info")]
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
            } 
            embed.WithColor(0, 255, 155);
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("setclassicguild", "set classic guild")]
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
                await RespondAsync(embed: embed.Build(), ephemeral: true);
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
                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting classic guild for {Context.Guild.Name} -> [{ex.Message}]");
            }     
            
        }

        [SlashCommand("logsclassic", "get classic logs")]
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
                    for (int i = 0; i <= (guildLogs.Count) && i <= maxReturn ; i++)
                    {
                        sb.AppendLine($"[__**{guildLogs[i].title}** **/** **{guildLogs[i].zoneName}**__]({guildLogs[i].reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{_wclLogsApi.UnixTimeStampToDateTime(guildLogs[i].start).ToLocalTime()}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{_wclLogsApi.UnixTimeStampToDateTime(guildLogs[i].end).ToLocalTime()}**");
                        sb.AppendLine($"\t:pencil2: Created by [**{guildLogs[i].owner}**]"); 
                        sb.AppendLine();
                    }
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{wowClassicGuild.WowGuild}** on **{wowClassicGuild.WowRealm}**__:1234: ";
                    embed.Description = sb.ToString();
                    embed.WithColor(0, 255, 100);
                    await RespondAsync(embed: embed.Build(), ephemeral: true);                    
                }
            }
        }

        [SlashCommand("listclassicfights", "list classic fights")]
        [Discord.Interactions.RequireOwner]
        public async Task GetClassicZones(string args = null)         
        {
            var zones = await _wclLogsApi.GetClassicZones();
            Zones latest = null;
            if (args == null) 
            {
                latest = zones[zones.Count - 2];
            }
            else 
            {
                if (args.ToLower() == "bwl") 
                {
                    args = "Blackwing Lair";
                }
                if (args.ToLower() == "mc") 
                {
                    args = "Molten Core";
                }                
                latest = zones.Where(z => z.name.ToLower() == args.ToLower()).FirstOrDefault();
            }
            var sb = new StringBuilder();
            if (latest != null)
            {
                sb.AppendLine($"fights for [{latest.name}]");                
                foreach (var fight in latest.encounters)
                {
                    sb.AppendLine($"id [{fight.id}] name [{fight.name}]");
                }
            }
            await RespondAsync(sb.ToString());
        }
    }
}
