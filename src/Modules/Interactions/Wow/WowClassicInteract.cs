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
using Microsoft.EntityFrameworkCore;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    public class WowClassicInteract : NinjaBotBaseModule
    {
        private readonly ILogger<WowClassicInteract> _logger;
        private readonly List<String> _wclRegions = new List<String>{"US", "EU", "KR", "TW", "CN"};
        private WarcraftLogs _wclLogsApi;
        private WarcraftLogsV2Client _wclLogsV2Api;

        // Pattern #3: Constructor injection instead of service locator
        public WowClassicInteract(
            IServiceScopeFactory scopeFactory,
            ILogger<WowClassicInteract> logger,
            WarcraftLogs wclLogsApi,
            WarcraftLogsV2Client wclLogsV2Api)
            : base(scopeFactory)
        {
            _logger = logger;
            _wclLogsApi = wclLogsApi;
            _wclLogsV2Api = wclLogsV2Api;
        }

        [SlashCommand("getclassicguild", "get classic guild info")]
        public async Task GetClassicGuild()
        {
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();

            embed.Title = $"[{Context.Guild.Name}] WoW Classic Guild Association";
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            var wowClassicGuild = await WithDbAsync(db =>
                db.WowClassicGuild
                    .Where(g => g.ServerId == (long)Context.Guild.Id)
                    .FirstOrDefaultAsync());

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
            var sb = new StringBuilder();

            embed.ThumbnailUrl = Context.Guild.IconUrl;

            if (!_wclRegions.Contains(region.ToUpper()))
            {
                embed.Title = "Error setting guild!";
                embed.WithColor(255, 0, 0);
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

            var wowClassicGuild = new WowClassicGuild
            {
                ServerId = (long)Context.Guild.Id,
                SetById = (long)Context.User.Id,
                WowGuild = guildName,
                WowRealm = realm,
                WowRegion = region,
                SetBy = Context.User.Username,
                TimeSet = DateTime.UtcNow,
                ServerName = Context.Guild.Name
            };

            try
            {
                await WithDbAsync(async db =>
                {
                    var currentGuild = await db.WowClassicGuild
                        .Where(g => g.ServerId == (long)Context.Guild.Id)
                        .FirstOrDefaultAsync();

                    if (currentGuild != null)
                    {
                        db.Remove(currentGuild);
                    }

                    db.WowClassicGuild.Add(wowClassicGuild);
                    await db.SaveChangesAsync();
                });

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
                _logger.LogError(ex, "Error setting classic guild for {GuildName}", Context.Guild.Name);
            }
        }

        [SlashCommand("logsclassic", "get classic logs")]
        public async Task GetLogsClassic()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            int maxReturn = 3;

            var wowClassicGuild = await WithDbAsync(db =>
                db.WowClassicGuild
                    .Where(g => g.ServerId == (long)Context.Guild.Id)
                    .FirstOrDefaultAsync());

            if (wowClassicGuild != null)
            {
                var guildLogs = await _wclLogsV2Api.GetGuildReportsAsync(
                    wowClassicGuild.WowGuild, wowClassicGuild.WowRealm, wowClassicGuild.WowRegion, gameVersion: WowGameVersion.Classic);

                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i < guildLogs.Count && i < maxReturn; i++)
                    {
                        sb.AppendLine($"[__**{guildLogs[i].Title}** **/** **{guildLogs[i].ZoneName}**__]({guildLogs[i].ReportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{guildLogs[i].StartTime.UnixTimeStampToDateTime().ToLocalTime()}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{guildLogs[i].EndTime.UnixTimeStampToDateTime().ToLocalTime()}**");
                        sb.AppendLine($"\t:pencil2: Created by [**{guildLogs[i].Owner.Name}**]");
                        sb.AppendLine();
                    }
                    _logger.LogInformation("Sending logs to {ChannelName}, requested by {UserName}", Context.Channel.Name, Context.User.Username);
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
