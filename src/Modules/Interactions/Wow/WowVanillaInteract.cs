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
    public class WowVanillaInteract : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly ILogger<WowVanillaInteract> _logger;
        private readonly List<String> _wclRegions = new List<String>{"US", "EU", "KR", "TW", "CN"};
        private readonly ChannelCheck _cc;
        private WarcraftLogs _wclLogsApi;        
        
        public WowVanillaInteract(IServiceProvider services) 
        {
            _logger = services.GetRequiredService<ILogger<WowVanillaInteract>>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _wclLogsApi = services.GetRequiredService<WarcraftLogs>();
        }

        [SlashCommand("getvanillaguild", "get vanilla guild info")]
        public async Task GetvanillaGuild()
        {
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            WowVanillaGuild wowvanillaGuild = null;

            embed.Title = $"[{Context.Guild.Name}] WoW Vanilla Guild Association";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            using (var db = new NinjaBotEntities())
            {
                wowvanillaGuild = db.WowVanillaGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefault();
            }
            if (wowvanillaGuild != null)
            {                
                sb.AppendLine($"**Guild Name:** {wowvanillaGuild.WowGuild}");
                sb.AppendLine($"**Realm:** {wowvanillaGuild.WowRealm}");
                sb.AppendLine($"**Region:** {wowvanillaGuild.WowRegion}");                
            }
            else
            {
                sb.AppendLine($"There is no guild associated to this server!");
            } 
            embed.WithColor(0, 255, 155);
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("setvanillaguild", "set vanilla guild")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetvanillaGuild(string guildName, string realm, string region = "US")
        {
            var embed = new EmbedBuilder();
            var wowvanillaGuild = new WowVanillaGuild();
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

            wowvanillaGuild.ServerId = (long)Context.Guild.Id;
            wowvanillaGuild.SetById = (long)Context.User.Id;
            wowvanillaGuild.WowGuild = guildName;
            wowvanillaGuild.WowRealm = realm;
            wowvanillaGuild.WowRegion = region;
            wowvanillaGuild.SetBy = Context.User.Username;
            wowvanillaGuild.TimeSet = DateTime.Now;  
            wowvanillaGuild.ServerName = Context.Guild.Name;          

            try
            {
                using (var db = new NinjaBotEntities())
                {
                    var currentGuild = db.WowVanillaGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                    if (currentGuild != null)
                    {
                        db.Remove(currentGuild);                                    
                    }
                    db.WowVanillaGuild.Add(wowvanillaGuild);
                    await db.SaveChangesAsync();
                }   
                embed.Title = $"[{Context.Guild.Name}] WoW Vanilla Guild Association";
                sb.AppendLine($"**Guild Name:** {wowvanillaGuild.WowGuild}");
                sb.AppendLine($"**Realm:** {wowvanillaGuild.WowRealm}");
                sb.AppendLine($"**Region:** {wowvanillaGuild.WowRegion}");
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
                _logger.LogError($"Error setting vanilla guild for {Context.Guild.Name} -> [{ex.Message}]");
            }     
            
        }

        [SlashCommand("logsvanilla", "get vanilla logs")]
        public async Task GetLogsvanilla()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            int maxReturn = 2;
            WowVanillaGuild wowvanillaGuild = null;
            using (var db = new NinjaBotEntities())
            {                
                wowvanillaGuild = db.WowVanillaGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefault();                
            }
            if (wowvanillaGuild != null)
            {
                var guildLogs = await _wclLogsApi.GetReportsFromGuildVanilla(wowvanillaGuild.WowGuild, wowvanillaGuild.WowRealm, wowvanillaGuild.WowRegion);
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
                    embed.Title = $":1234: __Logs for **{wowvanillaGuild.WowGuild}** on **{wowvanillaGuild.WowRealm}**__:1234: ";
                    embed.Description = sb.ToString();
                    embed.WithColor(0, 255, 100);
                    await RespondAsync(embed: embed.Build(), ephemeral: true);                    
                }
            }
        }

        [SlashCommand("listvanillafights", "list vanilla fights")]
        [Discord.Interactions.RequireOwner]
        public async Task GetvanillaZones(string args = null)         
        {
            var zones = await _wclLogsApi.GetVanillaZones();
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
