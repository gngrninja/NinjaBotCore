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
        private readonly WarcraftLogsV2Client _wclLogsV2Api;

        public WowClassicInteract(
            IServiceScopeFactory scopeFactory,
            ILogger<WowClassicInteract> logger,
            WarcraftLogsV2Client wclLogsV2Api)
            : base(scopeFactory)
        {
            _logger = logger;
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
                    wowClassicGuild.WowGuild,
                    wowClassicGuild.WowRealm.ToLower().Replace(" ", "-").Replace("'", ""),
                    wowClassicGuild.WowRegion?.ToLower() ?? "us",
                    gameVersion: WowGameVersion.Classic);

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
        public async Task GetClassicZones(string zoneName = null)
        {
            await DeferAsync();
            var sb = new StringBuilder();

            try
            {
                // Get all expansions for Classic
                var expansions = await _wclLogsV2Api.GetExpansionsAsync(WowGameVersion.Classic);

                if (expansions == null || expansions.Count == 0)
                {
                    await FollowupAsync("No expansions found for Classic.");
                    return;
                }

                // Get zones from all expansions
                var allZones = new List<WclV2ZoneDetail>();
                foreach (var expansion in expansions)
                {
                    var zones = await _wclLogsV2Api.GetZonesAsync(expansion.Id, WowGameVersion.Classic);
                    if (zones != null)
                    {
                        allZones.AddRange(zones);
                    }
                }

                if (allZones.Count == 0)
                {
                    await FollowupAsync("No zones found for Classic.");
                    return;
                }

                WclV2ZoneDetail targetZone = null;

                if (string.IsNullOrEmpty(zoneName))
                {
                    // Show all zones
                    sb.AppendLine("**Classic Zones:**");
                    foreach (var zone in allZones.OrderBy(z => z.Id))
                    {
                        sb.AppendLine($"[{zone.Id}] {zone.Name} - {zone.Encounters?.Count ?? 0} encounters");
                    }
                }
                else
                {
                    // Handle shortcuts
                    var searchName = zoneName.ToLower() switch
                    {
                        "bwl" => "blackwing lair",
                        "mc" => "molten core",
                        "aq40" => "temple of ahn'qiraj",
                        "aq20" => "ruins of ahn'qiraj",
                        "naxx" => "naxxramas",
                        "ony" => "onyxia's lair",
                        "icc" => "icecrown citadel",
                        "uld" => "ulduar",
                        "toc" => "trial of the crusader",
                        _ => zoneName.ToLower()
                    };

                    targetZone = allZones.FirstOrDefault(z => z.Name.ToLower().Contains(searchName));

                    if (targetZone != null && targetZone.Encounters != null)
                    {
                        sb.AppendLine($"**Encounters for [{targetZone.Name}]:**");
                        foreach (var encounter in targetZone.Encounters)
                        {
                            sb.AppendLine($"  [{encounter.Id}] {encounter.Name}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"Zone '{zoneName}' not found. Use command without args to list all zones.");
                    }
                }

                // Discord has a 2000 char limit
                var response = sb.ToString();
                if (response.Length > 1900)
                {
                    response = response.Substring(0, 1900) + "\n... (truncated)";
                }
                await FollowupAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching classic zones");
                await FollowupAsync($"Error: {ex.Message}");
            }
        }
    }
}
