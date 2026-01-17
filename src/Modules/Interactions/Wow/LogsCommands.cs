using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Warcraft Logs-related commands for viewing raid logs and rankings.
    /// Includes: /watchlogs, /logs, /top10, /raidvids
    /// </summary>
    public class LogsCommands : NinjaBotBaseModule
    {
        private readonly ILogger<LogsCommands> _logger;
        private readonly WarcraftLogs _logsApi;
        private readonly WarcraftLogsV2Client _logsApiV2;
        private readonly WowApi _wowApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public LogsCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<LogsCommands> logger,
            WarcraftLogs logsApi,
            WarcraftLogsV2Client logsApiV2,
            WowApi wowApi,
            WowUtilities wowUtils,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _logsApi = logsApi;
            _logsApiV2 = logsApiV2;
            _wowApi = wowApi;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
        }

        [SlashCommand("watchlogs", "watch logs for guild")]
        public async Task ToggleLogs()
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();

            var enable = await WithDbAsync(async db =>
            {
                List<LogMonitoring> logMonitorList = db.LogMonitoring.ToList();
                bool shouldEnable = false;

                if (logMonitorList != null)
                {
                    var getGuild = logMonitorList.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                    if (getGuild != null)
                    {
                        if (!getGuild.MonitorLogs)
                        {
                            shouldEnable = true;
                        }
                    }
                    else
                    {
                        shouldEnable = true;
                    }
                }

                var updateGuild = db.LogMonitoring.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (updateGuild != null)
                {
                    updateGuild.ChannelId = (long)Context.Channel.Id;
                    updateGuild.ChannelName = Context.Channel.Name;
                    updateGuild.MonitorLogs = shouldEnable;

                    if (shouldEnable)
                    {
                        updateGuild.LatestLogRetail = DateTime.UtcNow;
                    }
                }
                else
                {
                    db.LogMonitoring.Add(new LogMonitoring
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        ChannelId = (long)Context.Channel.Id,
                        ChannelName = Context.Channel.Name,
                        MonitorLogs = shouldEnable,
                        LatestLog = DateTime.UtcNow,
                        LatestLogRetail = shouldEnable ? DateTime.UtcNow : null
                    });
                }

                await db.SaveChangesAsync();
                return shouldEnable;
            });

            if (enable)
            {
                embed.Title = $"Enabling log watching for {Context.Guild.Name}!";
                sb.AppendLine($"When a new log is posted, you'll get a notification in this channel: **{Context.Channel.Name}**");
                sb.AppendLine($"If you'd like to have them posted in a different channel, use this command to disable the auto posting, and then again to enable them from the channel you'd like them posted in");
            }
            else
            {
                embed.Title = $"Disabling log watching for {Context.Guild.Name}!";
                sb.AppendLine($"Use the command again to enable log watching!");
            }

            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("logs", "Gets logs from Warcraftlogs")]
        public async Task GetLogs(string args = "")
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            string guildRegion = string.Empty;
            string locale = string.Empty;
            StringBuilder sb = new StringBuilder();
            List<Reports> guildLogs = new List<Reports>();
            int maxReturn = 3;
            int arrayCount = 0;
            string discordGuildName = string.Empty;
            var guildInfo = Context.Guild;
            var embed = new EmbedBuilder();

            guildObject = await _wowUtils.GetGuildName(Context);
            guildName = guildObject.guildName ?? string.Empty;
            realmName = guildObject.realmName?.Replace("'", string.Empty) ?? string.Empty;
            guildRegion = guildObject.regionName ?? string.Empty;
            locale = guildObject.locale ?? string.Empty;
            var realmInfo = new WowRealm.Realm();
            if (!string.IsNullOrEmpty(locale))
            {
                try
                {
                    WowRealm.Realm[] realms = locale switch
                    {
                        "en_US" => WowApi.RealmInfo?.realms,
                        "en_GB" => WowApi.RealmInfoEu?.realms,
                        "ru_RU" => WowApi.RealmInfoRu?.realms,
                        _ => null
                    };

                    if (realms != null)
                    {
                        realmInfo = realms.FirstOrDefault(r => r.name == guildObject.realmName) ?? new WowRealm.Realm();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error looking up realm info for {Realm} in locale {Locale}", guildObject.realmName, locale);
                }
            }
            if (!string.IsNullOrEmpty(guildObject.locale))
            {
                locale = guildObject.locale;
            }
            if (string.IsNullOrEmpty(guildRegion))
            {
                guildRegion = "US";
            }
            if (Context.Channel is IDMChannel)
            {
                discordGuildName = Context.Channel.Name;
            }
            else if (Context.Channel is IGuildChannel)
            {
                discordGuildName = Context.Guild.Name;
            }
            if (args != null && args.Split(' ')[0].ToLower() == "name")
            {
                try
                {
                    guildLogs = await _logsApi.GetReportsFromUser(args.Split(' ')[1]);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs from **{args.Split(' ')[1]}**");
                    _logger.LogError($"Erorr getting logs from user -> [{ex.Message}]");
                    await RespondAsync(sb.ToString());
                    return;
                }
                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i < guildLogs.Count && i < maxReturn; i++)
                    {
                        var startTime = guildLogs[arrayCount].start.UnixTimeStampToDateTime();
                        var endTime = guildLogs[arrayCount].end.UnixTimeStampToDateTime();
                        var wfUrl = $"https://www.wipefest.net/report/{guildLogs[arrayCount].id}";
                        var wowAnUrl = $"https://wowanalyzer.com/report/{guildLogs[arrayCount].id}";

                        sb.AppendLine($"[__**{guildLogs[arrayCount].title}** **/** **{guildLogs[arrayCount].zoneName}**__]({guildLogs[arrayCount].reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{startTime}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{endTime}**");
                        sb.AppendLine($"\t:mag: [WoWAnalyzer]({wowAnUrl}) | :sob: [WipeFest]({wfUrl})");

                        sb.AppendLine();
                        arrayCount++;
                    }
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");

                    embed.Title = $":1234:__Logs from **{args.Split(' ')[1]}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
                else if (guildLogs.Count == 1)
                {
                    sb.AppendLine($"[__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__]({guildLogs[0].reportURL})");
                    sb.AppendLine($"\t:timer: Start time: **{guildLogs[0].start.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{guildLogs[0].end.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[0].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");
                    sb.AppendLine();
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
            }
            else
            {
                if (args.Split(',').Count() > 1)
                {
                    if (args.Contains(',') && !string.IsNullOrEmpty(args))
                    {
                        switch (args.Split(',').Count())
                        {
                            case 2:
                                {
                                    realmName = args.Split(',')[0].ToString().Trim();
                                    guildName = args.Split(',')[1].ToString().Trim();
                                    break;
                                }
                            case 3:
                                {
                                    realmName = args.Split(',')[0].ToString().Trim();
                                    guildName = args.Split(',')[1].ToString().Trim();
                                    guildRegion = args.Split(',')[2].ToString().Trim();
                                    break;
                                }
                        }
                    }
                    else
                    {
                        sb.AppendLine("Please specify a guild and realm name!");
                        sb.AppendLine($"Example: /logs Thunderlord, UR KEY UR CARRY");
                        await RespondAsync(sb.ToString());
                        return;
                    }
                }
                if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
                {
                    sb.AppendLine("Please specify a guild and realm name!");
                    sb.AppendLine($"Example: /logs Thunderlord, UR KEY UR CARRY");
                    await RespondAsync(sb.ToString());
                    return;
                }
                try
                {
                    try
                    {
                        string realmSlug = guildObject.realmSlug ?? realmName.ToLower().Replace(" ", "-").Replace("'", "");
                        var v2Reports = await _logsApiV2.GetGuildReportsAsync(guildName, realmSlug, guildRegion, limit: 3);

                        if (v2Reports != null && v2Reports.Count > 0)
                        {
                            guildLogs = v2Reports.Select(r => new Reports
                            {
                                id = r.Code,
                                title = r.Title,
                                owner = r.OwnerName,
                                start = r.StartTime,
                                end = r.EndTime,
                                zone = r.Zone?.Id ?? 0
                            }).ToList();
                            _logger.LogInformation($"[v2] Retrieved {guildLogs.Count} reports for {guildName}");
                        }
                    }
                    catch (Exception v2Ex)
                    {
                        _logger.LogWarning($"[v2] Failed for {guildName}, falling back to v1: {v2Ex.Message}");

                        if (string.IsNullOrEmpty(locale))
                        {
                            guildLogs = await _logsApi.GetReportsFromGuild(guildName: guildName, realm: realmName, region: guildRegion);
                        }
                        else
                        {
                            guildLogs = await _logsApi.GetReportsFromGuild(guildName: guildName, realm: realmName, region: guildRegion, locale: locale, realmSlug: guildObject.realmSlug);
                        }
                        _logger.LogInformation($"[v1] Retrieved {guildLogs?.Count ?? 0} reports for {guildName}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs for **{guildName}** on **{realmName}**");
                    _logger.LogError($"{ex.Message}");
                    await RespondAsync(sb.ToString(), ephemeral: true);
                    return;
                }
                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i < guildLogs.Count && i < maxReturn; i++)
                    {
                        DateTime startTime = DateTime.UtcNow;
                        DateTime endTime = DateTime.UtcNow;

                        if (realmInfo != null && !string.IsNullOrEmpty(realmInfo.timezone))
                        {
                            startTime = _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].start.UnixTimeStampToDateTime(), realmInfo.timezone);
                            endTime = _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].end.UnixTimeStampToDateTime(), realmInfo.timezone);
                        }
                        else
                        {
                            startTime = _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].start.UnixTimeStampToDateTime());
                            endTime = _logsApi.ConvTimeToLocalTimezone(guildLogs[arrayCount].end.UnixTimeStampToDateTime());
                        }

                        sb.AppendLine($"[__**{guildLogs[arrayCount].title}** **/** **{guildLogs[arrayCount].zoneName}**__]({guildLogs[arrayCount].reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{startTime}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{endTime}**");
                        sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[arrayCount].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");

                        sb.AppendLine();
                        arrayCount++;
                    }
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234:__Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
                else if (guildLogs.Count == 1)
                {
                    sb.AppendLine($"[__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__]({guildLogs[0].reportURL})");
                    sb.AppendLine($"\t:timer: Start time: **{guildLogs[0].start.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{guildLogs[0].end.UnixTimeStampToDateTime()}**");
                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[0].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");
                    sb.AppendLine($"\t");
                    sb.AppendLine();
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
                else
                {
                    embed.Title = $"Unable to find logs for {guildName} on {realmName} ({guildRegion})";
                    embed.Description = $"**{Context.User.Username}**, ensure you've uploaded the logs as attached to **{guildName}** on http://www.warcraftlogs.com \n";
                    embed.Description += $"More information: http://www.wowhead.com/guides/raiding/warcraft-logs";
                    await RespondAsync(embed: embed.Build(), ephemeral: true);
                }
            }
        }

        [SlashCommand("top10", "Get the top 10 dps or hps for the latest raid in World of Warcraft (via warcraftlogs.com)")]
        public async Task GetTop10(string args = null)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            string fightName = string.Empty;
            string guildOnly = string.Empty;
            string difficulty = string.Empty;
            string metric = string.Empty;
            string raidName = string.Empty;
            string thumbUrl = string.Empty;
            var guildInfo = Context.Guild;
            string discordGuildName = string.Empty;
            int encounterID = 0;
            string region = "us";

            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);
            string realmName = guildObject.realmName.Replace("'", string.Empty);
            string guildName = guildObject.guildName;
            region = guildObject.regionName;

            var fightList = WarcraftLogs.Zones.Where(z => z.id == WarcraftLogs.CurrentRaidTier.WclZoneId)
                                                .Select(z => z.encounters)
                                                .FirstOrDefault();

            raidName = WarcraftLogs.CurrentRaidTier.RaidName;

            if (Context.Channel is IDMChannel)
            {
                discordGuildName = Context.User.Username;
                thumbUrl = Context.User.GetAvatarUrl();
            }
            else if (Context.Channel is IGuildChannel)
            {
                discordGuildName = Context.Guild.Name;
                thumbUrl = Context.Guild.IconUrl;
            }

            if (args == null || args.Split(',')[0] == "help")
            {
                sb.AppendLine($"**/top10** fightName(or ID from /top10 list) guild(type guild to get guild only results, all for all guilds) metric(dps(default), or hps) difficulty(lfr, flex, normal, heroic(default), or mythic) ");
                sb.AppendLine();
                sb.AppendLine($"**/top10** list");
                sb.AppendLine($"Get a list of all encounters and shortcut IDs");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1");
                sb.AppendLine($"The above command would get all top 10 **dps** results for **Garothi Worldbreaker** on **{realmName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, guild");
                sb.AppendLine($"The above command would get the top 10 **dps** results for **Garothi Worldbreaker** on **{realmName}** for **{guildName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, guild, hps");
                sb.AppendLine($"The above command would get the top 10 **hps** results for **Garothi Worldbreaker** on **{realmName}** for **{guildName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, all, hps");
                sb.AppendLine($"The above command would get all top 10 **hps** results for **Garothi Worldbreaker** on **{realmName}**.");
                sb.AppendLine();
                sb.AppendLine($"**/top10** 1, guild, dps, mythic");
                sb.AppendLine($"The above command would get the top 10 **dps** results for **Garothi Worldbreaker** on **{realmName}** for **{guildName}** on **mythic** difficulty.");
                embed.Title = $"{Context.User.Username}, here are some examples for **/top10**";
                embed.Description = sb.ToString();
                await RespondAsync(embed: embed.Build(), ephemeral: true);
                return;
            }
            else
            {
                if (args.Split(' ')[0].ToLower() == "list")
                {
                    if (fightList != null)
                    {
                        embed.Title = $"__Fight names for **{raidName}**__";
                        int j = 1;
                        foreach (var fight in fightList)
                        {
                            sb.AppendLine($"[**{j}**] {fight.name}");
                            j++;
                        }
                        embed.Description = sb.ToString();
                        await RespondAsync(embed: embed.Build(), ephemeral: true);
                    }
                    return;
                }

                await DeferAsync(ephemeral: true);

                difficulty = "heroic";

                int argCount = args.Split(',').Count();
                string[] splitArgs = args.Split(',');
                switch (argCount)
                {
                    case 1:
                        fightName = splitArgs[0].Trim();
                        break;
                    case 2:
                        fightName = splitArgs[0].Trim();
                        guildOnly = splitArgs[1].Trim();
                        break;
                    case 3:
                        fightName = splitArgs[0].Trim();
                        guildOnly = splitArgs[1].Trim();
                        metric = splitArgs[2].Trim();
                        break;
                    case 4:
                        fightName = splitArgs[0].Trim();
                        guildOnly = splitArgs[1].Trim();
                        metric = splitArgs[2].Trim();
                        difficulty = splitArgs[3].Trim();
                        break;
                }

                int difficultyID = difficulty.ToLower() switch
                {
                    "lfr" => 1,
                    "flex" => 2,
                    "normal" => 3,
                    "heroic" => 4,
                    "mythic" => 5,
                    _ => 4
                };

                WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
                if (fightName.Length <= 2)
                {
                    if (int.TryParse(fightName, out int fightIndex) && fightIndex >= 1 && fightIndex <= fightList.Length)
                    {
                        encounterID = fightList[fightIndex - 1].id;
                    }
                }
                else
                {
                    encounterID = _wowUtils.GetEncounterID(fightName);
                }

                string metricEmoji = string.Empty;
                if (string.IsNullOrEmpty(metric))
                {
                    metric = "dps";
                }
                switch (metric.ToLower())
                {
                    case "hps":
                        embed.WithColor(new Color(0, 255, 0));
                        metricEmoji = ":green_heart:";
                        break;
                    case "dps":
                        embed.WithColor(new Color(255, 0, 0));
                        metricEmoji = ":dagger: ";
                        break;
                    default:
                        embed.WithColor(new Color(255, 0, 0));
                        metricEmoji = ":dagger: ";
                        metric = "dps";
                        break;
                }

                if (string.IsNullOrEmpty(fightName))
                {
                    sb.AppendLine($"{Context.User.Username}, please specify a fight name/number!");
                    sb.AppendLine($"**Example:** /top10 1");
                    sb.AppendLine($"**Encounter Lists:** /top10 list");
                    await FollowupAsync(sb.ToString(), ephemeral: true);
                    return;
                }

                IEnumerable<WarcraftlogRankings.Ranking> top10 = null;
                var guildOnlyList = new List<WarcraftlogRankings.RankingObject>();

                if (!(string.IsNullOrEmpty(guildOnly) || guildOnly.ToLower() != "guild"))
                {
                    bool proceed = true;
                    int page = 1;
                    while (proceed)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(guildObject.realmSlug))
                            {
                                l = await _logsApi.GetRankingsByEncounterGuildSlug(
                                        encounterID: encounterID,
                                        realmSlug: guildObject.realmSlug,
                                        guildName: guildObject.guildName,
                                        page: page.ToString(),
                                        metric: metric,
                                        difficulty: difficultyID,
                                        regionName: region);
                            }
                            else
                            {
                                l = await _logsApi.GetRankingsByEncounterGuild(
                                        encounterID: encounterID,
                                        realmName: guildObject.realmName,
                                        guildName: guildObject.guildName,
                                        page: page.ToString(),
                                        metric: metric,
                                        difficulty: difficultyID,
                                        regionName: region);
                            }
                            _logger.LogInformation($"Adding page {page}!");

                            if (l != null)
                            {
                                guildOnlyList.Add(l);
                                page++;
                            }
                            else
                            {
                                proceed = false;
                            }
                            if (!l.hasMorePages || page >= 25)
                            {
                                proceed = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error getting top 10 data -> [{ex.Message}]");
                            proceed = false;
                        }

                        top10 = guildOnlyList.SelectMany(p => p.rankings).Where(r => r.guildName == guildObject.guildName).OrderByDescending(o => o.total).Take(10);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(guildObject.realmSlug))
                    {
                        l = await _logsApi.GetRankingsByEncounterSlug(
                                encounterID: encounterID,
                                realmSlug: guildObject.realmSlug,
                                metric: metric,
                                difficulty: difficultyID,
                                regionName: region);
                    }
                    else
                    {
                        l = await _logsApi.GetRankingsByEncounter(
                                encounterID: encounterID,
                                realmName: realmName,
                                metric: metric,
                                difficulty: difficultyID,
                                regionName: region);
                    }

                    top10 = l.rankings.OrderByDescending(a => a.total).Take(10);
                }

                string difficultyName = difficultyID switch
                {
                    1 => "LFR",
                    2 => "Flex",
                    3 => "Normal",
                    4 => "Heroic",
                    5 => "Mythic",
                    _ => "Heroic"
                };

                string fightNameFromEncounterID = fightList.Where(f => f.id == encounterID).Select(f => f.name).FirstOrDefault();

                embed.Title = $"__Top 10 for fight [**{fightNameFromEncounterID}** (Metric [**{metric.ToUpper()}**] Difficulty [**{difficultyName}**]) Realm [**{guildObject.realmName}**]]__";

                int i = 1;
                if (top10 != null)
                {
                    foreach (var rank in top10)
                    {
                        var classInfo = WarcraftLogs.CharClasses.Where(c => c.id == rank._class).FirstOrDefault();
                        sb.AppendLine($"**{i}** [{rank.name}](http://{region}.battle.net/wow/en/character/{rank.serverName.Replace(" ", "-")}/{rank.name}/advanced) ilvl **{rank.itemLevel}** {classInfo.name} from *[{rank.guildName}]*");
                        sb.AppendLine($"\t{metricEmoji}[**{rank.total.ToString("###,###")}** {metric.ToLower()}]");
                        i++;
                    }
                    sb.AppendLine($"Data gathered from **https://www.warcraftlogs.com**");
                    embed.Description = sb.ToString();
                    embed.ThumbnailUrl = thumbUrl;
                }
                else
                {
                    sb.AppendLine($"Error getting top 10 for {guildObject.guildName}!");
                    _logger.LogError($"Variable top10 was null for {guildObject.guildName} on {guildObject.realmSlug} [{guildObject.regionName}]");
                }
                try
                {
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
        }

        [SlashCommand("raidvids", "Get list of current raid videos")]
        public async Task GetRaidVids()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.WithColor(0, 255, 0);
            embed.WithFooter(new EmbedFooterBuilder
            {
                Text = $"Good luck and have fun!"
            });
            embed.ThumbnailUrl = "https://vignette.wikia.nocookie.net/wowwiki/images/1/17/Jainaunit.JPG/revision/latest?cb=20080826081813";
            var fightList = WarcraftLogs.Zones.Where(z => z.id == WarcraftLogs.CurrentRaidTier.WclZoneId)
                .Select(z => z.encounters)
                .FirstOrDefault();
            embed.Title = $"Raid Videos for {WarcraftLogs.CurrentRaidTier.RaidName}";

            var vids = await _wowCache.GetWowResourcesAsync("raidvid");

            if (vids != null && vids.Any())
            {
                foreach (var vid in vids)
                {
                    embed.AddField(new EmbedFieldBuilder
                    {
                        Name = $"{vid.ClassName}",
                        Value = $"{vid.Resource}",
                        IsInline = true
                    });
                }
            }

            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }
    }
}
