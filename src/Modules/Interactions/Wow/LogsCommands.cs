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
        private readonly WarcraftLogsV2Client _logsApiV2;
        private readonly WowApi _wowApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public LogsCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<LogsCommands> logger,
            WarcraftLogsV2Client logsApiV2,
            WowApi wowApi,
            WowUtilities wowUtils,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
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
                    string realmSlug = guildObject.realmSlug ?? realmName.ToLower().Replace(" ", "-").Replace("'", "");

                    // Check cache first
                    var cachedReports = _wowCache.GetCachedGuildReports(guildName, realmSlug, guildRegion);
                    List<WclV2Report> v2Reports;

                    if (cachedReports != null)
                    {
                        v2Reports = cachedReports;
                        _logger.LogDebug("[logs] Using cached reports for {GuildName}", guildName);
                    }
                    else
                    {
                        v2Reports = await _logsApiV2.GetGuildReportsAsync(guildName, realmSlug, guildRegion, limit: 3);

                        // Cache the result
                        if (v2Reports != null)
                        {
                            _wowCache.SetCachedGuildReports(guildName, realmSlug, guildRegion, v2Reports);
                        }
                    }

                    if (v2Reports != null && v2Reports.Count > 0)
                    {
                        guildLogs = v2Reports.Select(r => new Reports
                        {
                            id = r.Code,
                            title = r.Title,
                            owner = r.OwnerName,
                            start = r.StartTime,
                            end = r.EndTime,
                            zone = r.Zone?.Id ?? 0,
                            zoneName = r.ZoneName
                        }).ToList();
                        _logger.LogInformation("[v2] Retrieved {Count} reports for {GuildName}", guildLogs.Count, guildName);
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
                        // Convert from milliseconds timestamp to local time
                        var startTime = guildLogs[arrayCount].start.UnixTimeStampToDateTime().ToLocalTime();
                        var endTime = guildLogs[arrayCount].end.UnixTimeStampToDateTime().ToLocalTime();

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

        [SlashCommand("top10", "View top 10 DPS/HPS rankings for raid encounters")]
        public async Task GetTop10(
            [Summary("encounter", "Boss encounter to view rankings for")]
            [Autocomplete(typeof(EncounterAutocomplete))]
            string encounter = null,

            [Summary("metric", "Ranking metric (DPS or HPS)")]
            [Choice("DPS (Damage)", "dps")]
            [Choice("HPS (Healing)", "hps")]
            string metric = "dps",

            [Summary("difficulty", "Raid difficulty")]
            [Choice("LFR", "lfr")]
            [Choice("Normal", "normal")]
            [Choice("Heroic", "heroic")]
            [Choice("Mythic", "mythic")]
            string difficulty = "heroic",

            [Summary("scope", "Server-wide or guild-only rankings")]
            [Choice("Server Rankings", "server")]
            [Choice("Guild Only", "guild")]
            string scope = "server")
        {
            await DeferAsync(ephemeral: true);

            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();

            // Get guild association for realm info
            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);
            string realmName = guildObject.realmName?.Replace("'", string.Empty) ?? string.Empty;
            string realmSlug = guildObject.realmSlug ?? realmName.ToLower().Replace(" ", "-");
            string guildName = guildObject.guildName;
            string region = guildObject.regionName ?? "us";

            // Get current raid tier
            var currentTier = await _logsApiV2.GetCurrentRaidTierAsync();
            var fightList = currentTier?.Encounters?.ToArray();

            if (fightList == null || fightList.Length == 0)
            {
                await FollowupAsync("No encounter data available. Please try again later.", ephemeral: true);
                return;
            }

            // Parse encounter ID
            int encounterID = 0;
            string encounterName = null;

            if (string.IsNullOrEmpty(encounter))
            {
                // Default to first boss if no encounter specified
                encounterID = fightList[0].Id;
                encounterName = fightList[0].Name;
            }
            else if (int.TryParse(encounter, out int parsedId))
            {
                // User selected from autocomplete (encounter ID)
                encounterID = parsedId;
                encounterName = fightList.FirstOrDefault(f => f.Id == parsedId)?.Name;
            }
            else
            {
                // Fallback: try to match by name (shouldn't happen with autocomplete)
                var matchingEncounter = fightList.FirstOrDefault(f =>
                    f.Name.Contains(encounter, StringComparison.OrdinalIgnoreCase));
                if (matchingEncounter != null)
                {
                    encounterID = matchingEncounter.Id;
                    encounterName = matchingEncounter.Name;
                }
            }

            if (encounterID == 0)
            {
                await FollowupAsync($"Could not find encounter: {encounter}", ephemeral: true);
                return;
            }

            // Get encounter name if not already set
            if (string.IsNullOrEmpty(encounterName))
            {
                try
                {
                    var enc = await _logsApiV2.GetEncounterAsync(encounterID);
                    encounterName = enc?.Name ?? $"Encounter {encounterID}";
                }
                catch
                {
                    encounterName = $"Encounter {encounterID}";
                }
            }

            // Map difficulty to ID
            int difficultyID = difficulty.ToLower() switch
            {
                "lfr" => 1,
                "normal" => 3,
                "heroic" => 4,
                "mythic" => 5,
                _ => 4
            };

            string difficultyName = difficultyID switch
            {
                1 => "LFR",
                3 => "Normal",
                4 => "Heroic",
                5 => "Mythic",
                _ => "Heroic"
            };

            // Set embed color and emoji based on metric
            string metricEmoji;
            if (metric.ToLower() == "hps")
            {
                embed.WithColor(new Color(0, 200, 0));
                metricEmoji = ":green_heart:";
            }
            else
            {
                embed.WithColor(new Color(200, 50, 50));
                metricEmoji = ":crossed_swords:";
                metric = "dps";
            }

            // Check cache first
            List<WclV2CharacterRanking> top10Rankings = _wowCache.GetCachedTop10Rankings(
                scope, realmSlug, region, encounterID, metric, difficulty, guildName);

            if (top10Rankings == null)
            {
                // Cache miss - fetch from API
                try
                {
                    if (scope == "guild")
                    {
                        if (string.IsNullOrEmpty(guildName))
                        {
                            await FollowupAsync("No guild associated with this server. Use `/setguild` first, or select 'Server Rankings'.", ephemeral: true);
                            return;
                        }

                        _logger.LogInformation("[top10] Fetching guild rankings for {Guild} on {Realm}-{Region}, encounter {Encounter}",
                            guildName, realmSlug, region, encounterID);

                        var allGuildRankings = await _logsApiV2.GetAllGuildRankingsForEncounterAsync(
                            encounterId: encounterID,
                            serverSlug: realmSlug,
                            serverRegion: region,
                            guildName: guildName,
                            metric: metric,
                            difficulty: difficultyID,
                            maxPages: 3);

                        // Dedupe by player name - keep only each player's best parse
                        top10Rankings = allGuildRankings
                            .GroupBy(r => r.Name.ToLower())
                            .Select(g => g.OrderByDescending(r => r.Amount).First())
                            .OrderByDescending(r => r.Amount)
                            .Take(10)
                            .ToList();
                    }
                    else
                    {
                        _logger.LogInformation("[top10] Fetching server rankings on {Realm}-{Region}, encounter {Encounter}",
                            realmSlug, region, encounterID);

                        var rankingsPage = await _logsApiV2.GetEncounterRankingsAsync(
                            encounterId: encounterID,
                            serverSlug: realmSlug,
                            serverRegion: region,
                            metric: metric,
                            difficulty: difficultyID,
                            page: 1);

                        top10Rankings = rankingsPage.Rankings?
                            .OrderByDescending(r => r.Amount)
                            .Take(10)
                            .ToList() ?? new List<WclV2CharacterRanking>();
                    }

                    // Cache the results
                    _wowCache.SetCachedTop10Rankings(scope, realmSlug, region, encounterID, metric, difficulty, top10Rankings, guildName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[top10] Error fetching rankings");
                    await FollowupAsync($"Error fetching rankings from Warcraft Logs: {ex.Message}", ephemeral: true);
                    return;
                }
            }

            // Build embed title
            string scopeText = scope == "guild" ? $"Guild: {guildName}" : $"Server: {realmName}";
            int rankingCount = top10Rankings?.Count ?? 0;
            string topLabel = rankingCount > 0 && rankingCount < 10 ? $"Top {rankingCount}" : "Top 10";
            embed.Title = $"{metricEmoji} {topLabel} {metric.ToUpper()} - {encounterName} ({difficultyName})";
            embed.WithFooter($"{scopeText} ({region.ToUpper()}) | Data from warcraftlogs.com");

            // Set thumbnail
            if (Context.Channel is IGuildChannel)
            {
                embed.ThumbnailUrl = Context.Guild.IconUrl;
            }
            else
            {
                embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            }

            if (top10Rankings != null && top10Rankings.Count > 0)
            {
                int rank = 1;
                foreach (var r in top10Rankings)
                {
                    // Rank display - medals for top 3, numbers for all
                    string rankDisplay = rank switch
                    {
                        1 => "1. :first_place:",
                        2 => "2. :second_place:",
                        3 => "3. :third_place:",
                        _ => $"**{rank}.**"
                    };

                    // Class name comes directly from API now (e.g., "DeathKnight", "Mage")
                    string className = r.Class ?? "Unknown";
                    string playerGuild = !string.IsNullOrEmpty(r.GuildName) ? $" <{r.GuildName}>" : "";

                    // Format the amount nicely
                    string amountFormatted = r.Amount >= 1000000
                        ? $"{r.Amount / 1000000.0:F2}M"
                        : r.Amount >= 1000
                            ? $"{r.Amount / 1000.0:F1}K"
                            : $"{r.Amount:N0}";

                    // Link to WarcraftLogs report (preferred) or character page
                    // Note: characterRankings API doesn't return fightID, so we link to report overview
                    string wclLink = r.Report?.Code != null
                        ? $"https://www.warcraftlogs.com/reports/{r.Report.Code}"
                        : $"https://www.warcraftlogs.com/character/{region}/{realmSlug}/{r.Name.ToLower()}";

                    sb.AppendLine($"{rankDisplay} [{r.Name}]({wclLink}) - **{amountFormatted}** {metric.ToLower()}");
                    sb.AppendLine($"> {className} · ilvl {r.ItemLevel}{playerGuild}");

                    rank++;
                }

                embed.Description = sb.ToString();
            }
            else
            {
                sb.AppendLine($"No rankings found for **{encounterName}** on **{realmName}** ({difficultyName}).");
                if (scope == "guild")
                {
                    sb.AppendLine($"\nGuild **{guildName}** may not have logged this encounter yet.");
                }
                embed.Description = sb.ToString();
            }

            // Build components for quick filtering
            var components = BuildTop10Components(encounterID, metric, difficulty, scope, currentTier, fightList);

            await FollowupAsync(embed: embed.Build(), components: components, ephemeral: true);
        }

        /// <summary>
        /// Builds interactive button components for the /top10 command
        /// </summary>
        private MessageComponent BuildTop10Components(int currentEncounterId, string currentMetric, string currentDifficulty, string currentScope, WclV2ZoneDetail raidTier, WclV2EncounterBasic[] encounters)
        {
            var builder = new ComponentBuilder();

            // Row 1: Metric toggle + Encounter navigation
            var currentIndex = Array.FindIndex(encounters, e => e.Id == currentEncounterId);

            // DPS/HPS buttons
            builder.WithButton(
                label: "DPS",
                customId: $"top10_metric~{currentEncounterId}~dps~{currentDifficulty}~{currentScope}",
                style: currentMetric == "dps" ? ButtonStyle.Primary : ButtonStyle.Secondary,
                emote: new Emoji("\u2694\ufe0f"), // crossed swords
                row: 0);

            builder.WithButton(
                label: "HPS",
                customId: $"top10_metric~{currentEncounterId}~hps~{currentDifficulty}~{currentScope}",
                style: currentMetric == "hps" ? ButtonStyle.Success : ButtonStyle.Secondary,
                emote: new Emoji("\U0001F49A"), // green heart
                row: 0);

            // Prev/Next encounter buttons
            bool hasPrev = currentIndex > 0;
            bool hasNext = currentIndex < encounters.Length - 1;

            builder.WithButton(
                label: "Prev Boss",
                customId: hasPrev ? $"top10_enc~{encounters[currentIndex - 1].Id}~{currentMetric}~{currentDifficulty}~{currentScope}" : "top10_disabled",
                style: ButtonStyle.Secondary,
                emote: new Emoji("\u25C0"), // left arrow
                disabled: !hasPrev,
                row: 0);

            builder.WithButton(
                label: "Next Boss",
                customId: hasNext ? $"top10_enc~{encounters[currentIndex + 1].Id}~{currentMetric}~{currentDifficulty}~{currentScope}" : "top10_disabled",
                style: ButtonStyle.Secondary,
                emote: new Emoji("\u25B6"), // right arrow
                disabled: !hasNext,
                row: 0);

            // Row 2: Difficulty buttons
            var difficulties = new[] { ("Normal", "normal"), ("Heroic", "heroic"), ("Mythic", "mythic") };
            foreach (var (label, value) in difficulties)
            {
                builder.WithButton(
                    label: label,
                    customId: $"top10_diff~{currentEncounterId}~{currentMetric}~{value}~{currentScope}",
                    style: currentDifficulty == value ? ButtonStyle.Primary : ButtonStyle.Secondary,
                    row: 1);
            }

            // Scope toggle
            builder.WithButton(
                label: currentScope == "guild" ? "Guild" : "Server",
                customId: $"top10_scope~{currentEncounterId}~{currentMetric}~{currentDifficulty}~{(currentScope == "guild" ? "server" : "guild")}",
                style: ButtonStyle.Secondary,
                emote: currentScope == "guild" ? new Emoji("\U0001F3E0") : new Emoji("\U0001F310"), // house or globe
                row: 1);

            // Row 2: Encounter select menu
            var selectMenu = new SelectMenuBuilder()
                .WithCustomId($"top10_select~{currentMetric}~{currentDifficulty}~{currentScope}")
                .WithPlaceholder("Jump to boss...")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var encounter in encounters)
            {
                var isSelected = encounter.Id == currentEncounterId;
                selectMenu.AddOption(
                    label: encounter.Name,
                    value: encounter.Id.ToString(),
                    isDefault: isSelected);
            }

            builder.WithSelectMenu(selectMenu, row: 2);

            return builder.Build();
        }

        /// <summary>
        /// Handles all /top10 button interactions
        /// </summary>
        [ComponentInteraction("top10_*~*~*~*~*")]
        public async Task HandleTop10Button(string action, string encounterId, string metric, string difficulty, string scope)
        {
            await DeferAsync();

            // Parse encounter ID
            if (!int.TryParse(encounterId, out int encounterID))
            {
                await ModifyOriginalResponseAsync(m => m.Content = "Invalid encounter ID");
                return;
            }

            // Re-run the top10 logic with new parameters
            await ExecuteTop10Async(encounterID, metric, difficulty, scope);
        }

        /// <summary>
        /// Handles encounter select menu for /top10
        /// </summary>
        [ComponentInteraction("top10_select~*~*~*")]
        public async Task HandleTop10Select(string metric, string difficulty, string scope, string[] selectedValues)
        {
            await DeferAsync();

            if (selectedValues == null || selectedValues.Length == 0)
            {
                await ModifyOriginalResponseAsync(m => m.Content = "No encounter selected");
                return;
            }

            // Parse selected encounter ID
            if (!int.TryParse(selectedValues[0], out int encounterID))
            {
                await ModifyOriginalResponseAsync(m => m.Content = "Invalid encounter ID");
                return;
            }

            // Re-run the top10 logic with selected encounter
            await ExecuteTop10Async(encounterID, metric, difficulty, scope);
        }

        /// <summary>
        /// Core logic for fetching and displaying top 10 rankings
        /// </summary>
        private async Task ExecuteTop10Async(int encounterID, string metric, string difficulty, string scope)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();

            // Get guild association for realm info
            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);
            string realmName = guildObject.realmName?.Replace("'", string.Empty) ?? string.Empty;
            string realmSlug = guildObject.realmSlug ?? realmName.ToLower().Replace(" ", "-");
            string guildName = guildObject.guildName;
            string region = guildObject.regionName ?? "us";

            // Get current raid tier for encounter info
            var currentTier = await _logsApiV2.GetCurrentRaidTierAsync();
            var fightList = currentTier?.Encounters?.ToArray() ?? Array.Empty<WclV2EncounterBasic>();

            // Get encounter name
            var encounterInfo = fightList.FirstOrDefault(f => f.Id == encounterID);
            string encounterName = encounterInfo?.Name ?? $"Encounter {encounterID}";

            // Map difficulty to ID
            int difficultyID = difficulty.ToLower() switch
            {
                "lfr" => 1,
                "normal" => 3,
                "heroic" => 4,
                "mythic" => 5,
                _ => 4
            };

            string difficultyName = difficultyID switch
            {
                1 => "LFR",
                3 => "Normal",
                4 => "Heroic",
                5 => "Mythic",
                _ => "Heroic"
            };

            // Set embed color and emoji based on metric
            string metricEmoji;
            if (metric.ToLower() == "hps")
            {
                embed.WithColor(new Color(0, 200, 0));
                metricEmoji = ":green_heart:";
            }
            else
            {
                embed.WithColor(new Color(200, 50, 50));
                metricEmoji = ":crossed_swords:";
                metric = "dps";
            }

            // Check cache first
            var cachedRankings = _wowCache.GetCachedTop10Rankings(scope, realmSlug, region, encounterID, metric, difficulty, guildName);
            List<WclV2CharacterRanking> top10Rankings = cachedRankings;

            if (top10Rankings == null)
            {
                try
                {
                    if (scope == "guild")
                    {
                        if (string.IsNullOrEmpty(guildName))
                        {
                            await ModifyOriginalResponseAsync(m =>
                            {
                                m.Content = "No guild associated with this server. Use `/setguild` first.";
                                m.Embed = null;
                                m.Components = null;
                            });
                            return;
                        }

                        var allGuildRankings = await _logsApiV2.GetAllGuildRankingsForEncounterAsync(
                            encounterId: encounterID,
                            serverSlug: realmSlug,
                            serverRegion: region,
                            guildName: guildName,
                            metric: metric,
                            difficulty: difficultyID,
                            maxPages: 3);

                        // Deduplicate by player name - take best parse per player
                        top10Rankings = allGuildRankings
                            .GroupBy(r => r.Name.ToLower())
                            .Select(g => g.OrderByDescending(r => r.Amount).First())
                            .OrderByDescending(r => r.Amount)
                            .Take(10)
                            .ToList();
                    }
                    else
                    {
                        var rankingsPage = await _logsApiV2.GetEncounterRankingsAsync(
                            encounterId: encounterID,
                            serverSlug: realmSlug,
                            serverRegion: region,
                            metric: metric,
                            difficulty: difficultyID,
                            page: 1);

                        top10Rankings = rankingsPage.Rankings?
                            .OrderByDescending(r => r.Amount)
                            .Take(10)
                            .ToList() ?? new List<WclV2CharacterRanking>();
                    }

                    // Cache the results
                    _wowCache.SetCachedTop10Rankings(scope, realmSlug, region, encounterID, metric, difficulty, top10Rankings, guildName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[top10] Error fetching rankings");
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = $"Error fetching rankings: {ex.Message}";
                        m.Embed = null;
                        m.Components = null;
                    });
                    return;
                }
            }

            // Build embed
            string scopeText = scope == "guild" ? $"Guild: {guildName}" : $"Server: {realmName}";
            int rankingCount = top10Rankings?.Count ?? 0;
            string topLabel = rankingCount > 0 && rankingCount < 10 ? $"Top {rankingCount}" : "Top 10";
            embed.Title = $"{metricEmoji} {topLabel} {metric.ToUpper()} - {encounterName} ({difficultyName})";
            embed.WithFooter($"{scopeText} ({region.ToUpper()}) | Data from warcraftlogs.com");

            if (Context.Channel is IGuildChannel)
            {
                embed.ThumbnailUrl = Context.Guild.IconUrl;
            }

            if (top10Rankings != null && top10Rankings.Count > 0)
            {
                int rank = 1;
                foreach (var r in top10Rankings)
                {
                    // Rank display - medals for top 3, numbers for all
                    string rankDisplay = rank switch
                    {
                        1 => "1. :first_place:",
                        2 => "2. :second_place:",
                        3 => "3. :third_place:",
                        _ => $"**{rank}.**"
                    };

                    string className = r.Class ?? "Unknown";
                    string playerGuild = !string.IsNullOrEmpty(r.GuildName) ? $" <{r.GuildName}>" : "";

                    string amountFormatted = r.Amount >= 1000000
                        ? $"{r.Amount / 1000000.0:F2}M"
                        : r.Amount >= 1000
                            ? $"{r.Amount / 1000.0:F1}K"
                            : $"{r.Amount:N0}";

                    // Link to WarcraftLogs report (preferred) or character page
                    // Note: characterRankings API doesn't return fightID, so we link to report overview
                    string wclLink = r.Report?.Code != null
                        ? $"https://www.warcraftlogs.com/reports/{r.Report.Code}"
                        : $"https://www.warcraftlogs.com/character/{region}/{realmSlug}/{r.Name.ToLower()}";

                    sb.AppendLine($"{rankDisplay} [{r.Name}]({wclLink}) - **{amountFormatted}** {metric.ToLower()}");
                    sb.AppendLine($"> {className} · ilvl {r.ItemLevel}{playerGuild}");

                    rank++;
                }

                embed.Description = sb.ToString();
            }
            else
            {
                sb.AppendLine($"No rankings found for **{encounterName}** on **{realmName}** ({difficultyName}).");
                if (scope == "guild")
                {
                    sb.AppendLine($"\nGuild **{guildName}** may not have logged this encounter yet.");
                }
                embed.Description = sb.ToString();
            }

            // Build components
            var components = BuildTop10Components(encounterID, metric, difficulty, scope, currentTier, fightList);

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed.Build();
                m.Components = components;
                m.Content = null;
            });
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

            // Get current raid tier from v2 API
            var currentTier = await _logsApiV2.GetCurrentRaidTierAsync();
            embed.Title = $"Raid Videos for {currentTier?.Name ?? "Current Raid"}";

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
