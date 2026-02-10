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
using Microsoft.EntityFrameworkCore;
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
    public class WowVanillaInteract : NinjaBotBaseModule
    {
        private readonly ILogger<WowVanillaInteract> _logger;
        private readonly List<String> _wclRegions = new List<String>{"US", "EU", "KR", "TW", "CN"};
        private readonly WarcraftLogsV2Client _wclLogsV2Api;

        public WowVanillaInteract(
            IServiceScopeFactory scopeFactory,
            ILogger<WowVanillaInteract> logger,
            WarcraftLogsV2Client wclLogsV2Api)
            : base(scopeFactory)
        {
            _logger = logger;
            _wclLogsV2Api = wclLogsV2Api;
        }

        [SlashCommand("getvanillaguild", "get vanilla guild info")]
        public async Task GetvanillaGuild()
        {
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();

            embed.Title = $"[{Context.Guild.Name}] WoW Vanilla Guild Association";
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            var wowvanillaGuild = await WithDbAsync(async db =>
            {
                return await db.WowVanillaGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefaultAsync();
            });

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
        [DefaultMemberPermissions(GuildPermission.KickMembers)]
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
            wowvanillaGuild.TimeSet = DateTime.UtcNow;
            wowvanillaGuild.ServerName = Context.Guild.Name;

            try
            {
                await WithDbAsync(async db =>
                {
                    var currentGuild = await db.WowVanillaGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefaultAsync();
                    if (currentGuild != null)
                    {
                        db.Remove(currentGuild);
                    }
                    db.WowVanillaGuild.Add(wowvanillaGuild);
                    await db.SaveChangesAsync();
                });

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
            int maxReturn = 3;

            var wowvanillaGuild = await WithDbAsync(async db =>
            {
                return await db.WowVanillaGuild.Where(g => g.ServerId == (long)Context.Guild.Id).FirstOrDefaultAsync();
            });

            if (wowvanillaGuild == null)
            {
                embed.Title = "No Guild Associated";
                embed.Description = "Use `/setvanillaguild` to associate a Vanilla guild with this server.";
                embed.WithColor(255, 165, 0);
                await RespondAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            try
            {
                var guildLogs = await _wclLogsV2Api.GetGuildReportsAsync(
                    wowvanillaGuild.WowGuild,
                    wowvanillaGuild.WowRealm.ToLower().Replace(" ", "-").Replace("'", ""),
                    wowvanillaGuild.WowRegion.ToLower(),
                    limit: maxReturn,
                    gameVersion: WowGameVersion.Vanilla);

                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    foreach (var log in guildLogs)
                    {
                        sb.AppendLine($"[__**{log.Title}** **/** **{log.ZoneName}**__]({log.ReportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{log.StartTime.UnixTimeStampToDateTime().ToLocalTime()}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{log.EndTime.UnixTimeStampToDateTime().ToLocalTime()}**");
                        sb.AppendLine($"\t:pencil2: Created by [**{log.OwnerName}**]");
                        sb.AppendLine();
                    }
                    _logger.LogInformation("Sending vanilla logs to {Channel}, requested by {User}", Context.Channel.Name, Context.User.Username);
                    embed.Title = $":1234: __Logs for **{wowvanillaGuild.WowGuild}** on **{wowvanillaGuild.WowRealm}**__:1234: ";
                    embed.Description = sb.ToString();
                    embed.WithColor(0, 255, 100);
                }
                else
                {
                    embed.Title = "No Logs Found";
                    embed.Description = $"No recent logs found for **{wowvanillaGuild.WowGuild}** on **{wowvanillaGuild.WowRealm}**.";
                    embed.WithColor(255, 165, 0);
                }
                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching vanilla logs for {Guild}", wowvanillaGuild.WowGuild);
                embed.Title = "Error";
                embed.Description = "Failed to fetch logs from WarcraftLogs. Please try again later.";
                embed.WithColor(255, 0, 0);
                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("listvanillafights", "list vanilla fights")]
        [Discord.Interactions.RequireOwner]
        public async Task GetvanillaZones(string zoneName = null)
        {
            await DeferAsync();
            var sb = new StringBuilder();

            try
            {
                // Get all expansions for Vanilla
                var expansions = await _wclLogsV2Api.GetExpansionsAsync(WowGameVersion.Vanilla);

                if (expansions == null || expansions.Count == 0)
                {
                    await FollowupAsync("No expansions found for Vanilla.");
                    return;
                }

                // Get zones from the first/main expansion
                var allZones = new List<WclV2ZoneDetail>();
                foreach (var expansion in expansions)
                {
                    var zones = await _wclLogsV2Api.GetZonesAsync(expansion.Id, WowGameVersion.Vanilla);
                    if (zones != null)
                    {
                        allZones.AddRange(zones);
                    }
                }

                if (allZones.Count == 0)
                {
                    await FollowupAsync("No zones found for Vanilla.");
                    return;
                }

                WclV2ZoneDetail targetZone = null;

                if (string.IsNullOrEmpty(zoneName))
                {
                    // Show all zones
                    sb.AppendLine("**Vanilla Zones:**");
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
                _logger.LogError(ex, "Error fetching vanilla zones");
                await FollowupAsync($"Error: {ex.Message}");
            }
        }
    }
}
