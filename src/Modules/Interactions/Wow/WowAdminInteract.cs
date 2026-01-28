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
    // Interaction modules must be public and inherit from an IInteractionModuleBase
    public class WowAdminInteract : NinjaBotBaseModule
    {
        private WarcraftLogs _logsApi;
        private WowApi _wowApi;
        private DiscordShardedClient _client;
        private RaiderIOApi _rioApi;
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private WowUtilities _wowUtils;
        private readonly WarcraftLogsV2Client _v2Client;
        private readonly WowStaticDataService _wowStaticData;

        // Pattern #3: Constructor injection instead of service locator
        public WowAdminInteract(
            IServiceScopeFactory scopeFactory,
            ILogger<WowAdminInteract> logger,
            WowUtilities wowUtils,
            WarcraftLogs logsApi,
            WowApi wowApi,
            RaiderIOApi rioApi,
            DiscordShardedClient client,
            IConfigurationRoot config,
            WarcraftLogsV2Client v2Client,
            WowStaticDataService wowStaticData)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowUtils = wowUtils;
            _logsApi = logsApi;
            _wowApi = wowApi;
            _rioApi = rioApi;
            _client = client;
            _config = config;
            _v2Client = v2Client;
            _wowStaticData = wowStaticData;
        }

        /// <summary>
        /// Finds problematic guilds (inactive, not-found, no-reports) based on cleanup type
        /// </summary>
        private async Task<List<(WowGuildAssociations Guild, LogMonitoring Monitoring, int? DaysSinceReport, string Reason)>>
            GetProblematicGuildsAsync(string cleanupType, int daysThreshold)
        {
            var type = cleanupType.ToLower();
            var problemGuilds = new List<(WowGuildAssociations Guild, LogMonitoring Monitoring, int? DaysSinceReport, string Reason)>();

            var (guildList, logWatchList) = await WithDbAsync(async db =>
            {
                var guilds = await db.WowGuildAssociations.ToListAsync();
                var logWatch = await db.LogMonitoring.Where(w => w.MonitorLogs).ToListAsync();
                return (guilds, logWatch);
            });

            // Find inactive guilds
            if (type == "inactive" || type == "all")
            {
                var thresholdDate = DateTime.UtcNow.AddDays(-daysThreshold);

                foreach (var monitoring in logWatchList)
                {
                    var guild = guildList.FirstOrDefault(g => g.ServerId == monitoring.ServerId);
                    if (guild == null) continue;

                    int? daysSinceReport = monitoring.LatestLogRetail.HasValue
                        ? (int)(DateTime.UtcNow - monitoring.LatestLogRetail.Value).TotalDays
                        : null;

                    if (!monitoring.LatestLogRetail.HasValue || monitoring.LatestLogRetail.Value < thresholdDate)
                    {
                        var reason = daysSinceReport.HasValue
                            ? $"Inactive: {daysSinceReport} days"
                            : "Inactive: never";
                        problemGuilds.Add((guild, monitoring, daysSinceReport, reason));
                    }
                }
            }

            // Find not-found guilds or no-reports guilds via WCL API
            if (type == "not-found" || type == "no-reports" || type == "all")
            {
                var guildsToCheck = guildList
                    .Where(g => logWatchList.Any(w => w.ServerId == g.ServerId && w.MonitorLogs))
                    .ToList();

                var batchRequest = guildsToCheck.Select(g => (
                    guildName: g.WowGuild,
                    serverSlug: g.LocalRealmSlug ?? g.WowRealm.ToLower().Replace(" ", "-").Replace("'", ""),
                    serverRegion: g.WowRegion,
                    guildKey: $"{g.ServerId}"
                )).ToList();

                var batchResult = await _v2Client.GetBatchGuildReportsAsync(batchRequest);

                foreach (var guild in guildsToCheck)
                {
                    var guildKey = $"{guild.ServerId}";
                    var monitoring = logWatchList.FirstOrDefault(w => w.ServerId == guild.ServerId);
                    if (monitoring == null) continue;

                    int? daysSinceReport = monitoring.LatestLogRetail.HasValue
                        ? (int)(DateTime.UtcNow - monitoring.LatestLogRetail.Value).TotalDays
                        : null;

                    // Check for not-found guilds
                    if ((type == "not-found" || type == "all") && batchResult.NonExistentGuilds.Contains(guildKey))
                    {
                        var existingIndex = problemGuilds.FindIndex(p => p.Guild.ServerId == guild.ServerId);
                        if (existingIndex < 0)
                        {
                            problemGuilds.Add((guild, monitoring, daysSinceReport, "Not found on WCL"));
                        }
                        else
                        {
                            var existing = problemGuilds[existingIndex];
                            problemGuilds[existingIndex] = (existing.Guild, existing.Monitoring, existing.DaysSinceReport, $"{existing.Reason} + Not found");
                        }
                    }

                    // Check for guilds with no reports
                    if ((type == "no-reports" || type == "all") && batchResult.GuildsWithNoReports.Contains(guildKey))
                    {
                        var existingIndex = problemGuilds.FindIndex(p => p.Guild.ServerId == guild.ServerId);
                        if (existingIndex < 0)
                        {
                            problemGuilds.Add((guild, monitoring, daysSinceReport, "No reports on WCL"));
                        }
                        else
                        {
                            var existing = problemGuilds[existingIndex];
                            problemGuilds[existingIndex] = (existing.Guild, existing.Monitoring, existing.DaysSinceReport, $"{existing.Reason} + No reports");
                        }
                    }
                }
            }

            return problemGuilds
                .OrderBy(x => x.Reason)
                .ThenByDescending(x => x.DaysSinceReport ?? int.MaxValue)
                .ToList();
        }

        [SlashCommand("populatelogs", "populate logs")]
        [Discord.Interactions.RequireOwner]
        public async Task PopulateLogs()
        {
            {
                List<WowGuildAssociations> guildList = null;
                List<LogMonitoring> logWatchList = null;
                try
                {
                    var result = await WithDbAsync(async db =>
                    {
                        var guilds = await db.WowGuildAssociations.ToListAsync();
                        var logWatch = await db.LogMonitoring.ToListAsync();
                        return (guilds, logWatch);
                    });
                    guildList = result.guilds;
                    logWatchList = result.logWatch;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error getting guild/logwatch list -> [{ex.Message}]");
                }
                if (guildList != null)
                {
                    foreach (var guild in guildList)
                    {
                        try
                        {
                            var watchGuild = logWatchList.Where(w => w.ServerId == guild.ServerId).FirstOrDefault();
                            if (watchGuild != null)
                            {
                                if (watchGuild.MonitorLogs)
                                {
                                    //System._logger.LogInformation($"YES! Watch logs on {guild.ServerName}!");
                                    var logs = await _logsApi.GetReportsFromGuild(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion);
                                    if (logs != null)
                                    {
                                        var latestLog = logs[logs.Count - 1];
                                        DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();
                                        {
                                            await WithDbAsync(async db =>
                                            {
                                                var latestForGuild = await db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefaultAsync();
                                                latestForGuild.LatestLogRetail = startTime;
                                                latestForGuild.RetailReportId = latestLog.id;
                                                await db.SaveChangesAsync();
                                            });
                                            //System._logger.LogInformation($"Updated [{watchGuild.ServerName}] -> [{latestLog.id}] [{latestLog.owner}]!");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error checking for logs! -> [{ex.Message}]");
                        }
                    }
                }
            }
        }

        [SlashCommand("removeachievement", "remove achivement")]
        [Discord.Interactions.RequireOwner]
        public async Task RemoveAchieve(long id)
        {
            var message = await WithDbAsync(async db =>
            {
                var foundCheeve = await db.FindWowCheeves.Where(c => c.AchId == id).FirstOrDefaultAsync();
                if (foundCheeve != null)
                {
                    db.Remove(foundCheeve);
                    await db.SaveChangesAsync();
                    return $"Removed achievement id {id} from the database!";
                }
                else
                {
                    return $"Sorry, unable to find achievement ID {id} in the database!";
                }
            });

            await RespondAsync(message);
        }

        [SlashCommand("addachievement", "add achivement")]
        [Discord.Interactions.RequireOwner]
        public async Task AddAchieve(long id, int cat)
        {
            var (success, message, categoryName) = await WithDbAsync(async db =>
            {
                var foundCheeve = await db.FindWowCheeves.Where(c => c.AchId == id).FirstOrDefaultAsync();
                if (foundCheeve == null)
                {
                    var category = await db.AchCategories.Where(c => c.CatId == cat).FirstOrDefaultAsync();
                    if (category != null)
                    {
                        try
                        {
                            db.FindWowCheeves.Add(new FindWowCheeve
                            {
                                AchId = id,
                                AchCategory = category
                            });
                            await db.SaveChangesAsync();
                            return (true, $"Added achievement ID {id} with category {category.CatName} to the database!", category.CatName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"{ex.Message}");
                            return (false, $"Error adding achievement: {ex.Message}", null);
                        }
                    }
                    else
                    {
                        return (false, $"Unable to find category with ID {cat} in the database!", null);
                    }

                }
                else
                {
                    return (false, $"Sorry, achievement {id} already exists in the database!", null);
                }
            });

            await RespondAsync(message);
        }

        [SlashCommand("listachievements", "list achivements")]
        [Discord.Interactions.RequireOwner]
        public async Task ListCheeves()
        {
            StringBuilder sb = new StringBuilder();
            var cheeves = await WithDbAsync(async db =>
            {
                return await db.FindWowCheeves.ToListAsync();
            });
            if (cheeves.Count > 0)
            {
                foreach (var cheeve in cheeves)
                {
                    sb.AppendLine($"{cheeve.AchId}");
                }
            }
            await RespondAsync(sb.ToString());
        }

        // Note: /tu and /td commands removed - log monitoring now handled by NinjaBotHelpers service

        [SlashCommand("get-latest-zone", "get latest zone")]
        [Discord.Interactions.RequireOwner]
        public async Task GetLatestZone()
        {
            var zone = WarcraftLogs.Zones.OrderByDescending(z => z.id).First();
            var encounters = zone.encounters.Select(s => s.name).ToList();

            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            
            foreach (var encounter in encounters)
            {
                sb.AppendLine($"*{encounter}*");
            }    

            embed.Title = $"{zone.name}";

            embed.AddField(new EmbedFieldBuilder
            {
                Name = "ID",
                Value = $"*{zone.id.ToString()}*",
                              
            });

            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Encounters",
                Value = sb.ToString()

            });
            
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("set-zone", "set zone")]
        [Discord.Interactions.RequireOwner]
        public async Task SetLatestZone(string args = null)
        {     
            Zones zone = null;
            int currentId = 0;
            string name = string.Empty;

            if (args == null)
            {
                zone = WarcraftLogs.Zones.OrderByDescending(z => z.id).First();
                currentId = zone.id;
                name = zone.name;
            }
            else
            {
                currentId = int.Parse(args);
                zone = WarcraftLogs.Zones.Where(z => z.id == currentId).FirstOrDefault();    
                name = zone.name;    
            }
            
            var embed = new EmbedBuilder();
            embed.Title = "Raid tier setter for NinjaBot";
            try
            {
                await _wowUtils.SetLatestRaid(zone);
                embed.Description = $"Raid tier set to [{zone.id}] -> [{zone.name}]";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting db to new raid -> [{ex.Message}]");
                embed.Description = $"Error setting raid tier!";
            }            
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("set-partition", "set partition")]
        [Discord.Interactions.RequireOwner]
        public async Task SetPartition(string args = null)
        {
            var embed = new EmbedBuilder();
            embed.Title = "Parition setter for NinjaBot";
            int? partition = int.Parse(args.Trim());
            try
            {
                if (partition != null)
                {
                    await WithDbAsync(async db =>
                    {
                        var curTier = await db.CurrentRaidTier.FirstOrDefaultAsync();
                        curTier.Partition = partition;
                        await db.SaveChangesAsync();
                    });
                }
                embed.Description = $"Parition set to {partition}";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting partition -> [{ex.Message}]");
                embed.Description = "Error setting parition!";
            }
            await RespondAsync(embed: embed.Build());
            WarcraftLogs.CurrentRaidTier.Partition = partition;
        }

        [SlashCommand("wcl-guild-cleanup", "List or remove problematic guild associations from WarcraftLogs monitoring")]
        [Discord.Interactions.RequireOwner]
        public async Task WclGuildCleanup(
            [Summary("type", "Type of guilds to find: inactive, not-found, no-reports, or all")] string cleanupType = "inactive",
            [Summary("days-threshold", "For inactive: days without reports to consider inactive (default: 90)")] int daysThreshold = 90,
            [Summary("action", "Action to perform: list or unflag")] string action = "list")
        {
            await DeferAsync(ephemeral: true);

            var type = cleanupType.ToLower();
            if (type != "inactive" && type != "not-found" && type != "no-reports" && type != "all")
            {
                await FollowupAsync($"Invalid cleanup type '{cleanupType}'. Use 'inactive', 'not-found', 'no-reports', or 'all'.", ephemeral: true);
                return;
            }

            try
            {
                // Show progress message for WCL API calls
                if (type == "not-found" || type == "no-reports" || type == "all")
                {
                    await FollowupAsync("Checking WarcraftLogs... This may take a moment.", ephemeral: true);
                }

                var problemGuilds = await GetProblematicGuildsAsync(type, daysThreshold);

                if (problemGuilds.Count == 0)
                {
                    var typeDesc = type == "all" ? "problematic" : type;
                    await FollowupAsync($"No {typeDesc} guilds found!", ephemeral: true);
                    return;
                }

                if (action.ToLower() == "list")
                {
                    // List mode - show with pagination
                    await ShowProblematicGuildsPage(problemGuilds, 0, daysThreshold, type);
                }
                else if (action.ToLower() == "unflag")
                {
                    // Unflag mode - actually disable monitoring
                    var unflagResult = await WithDbAsync(async db =>
                    {
                        int unflaggedCount = 0;
                        var unflaggedServers = new List<string>();

                        foreach (var (guild, monitoring, _, reason) in problemGuilds)
                        {
                            var dbMonitoring = await db.LogMonitoring.FirstOrDefaultAsync(m => m.ServerId == monitoring.ServerId);
                            if (dbMonitoring != null && dbMonitoring.MonitorLogs)
                            {
                                dbMonitoring.MonitorLogs = false;
                                unflaggedCount++;
                                unflaggedServers.Add($"{guild.WowGuild}-{guild.WowRealm}");
                            }
                        }

                        await db.SaveChangesAsync();
                        return (unflaggedCount, unflaggedServers);
                    });

                    int unflaggedCount = unflagResult.unflaggedCount;
                    var unflaggedServers = unflagResult.unflaggedServers;

                    var resultEmbed = new EmbedBuilder();
                    resultEmbed.WithTitle("Cleanup Complete");
                    resultEmbed.WithDescription($"Disabled monitoring for {unflaggedCount} inactive guilds");
                    resultEmbed.WithColor(Color.Green);

                    var resultSb = new StringBuilder();
                    foreach (var server in unflaggedServers.Take(20))
                    {
                        resultSb.AppendLine($"• {server}");
                    }
                    if (unflaggedServers.Count > 20)
                    {
                        resultSb.AppendLine($"*...and {unflaggedServers.Count - 20} more*");
                    }

                    resultEmbed.AddField("Unflagged Guilds", resultSb.ToString());

                    _logger.LogInformation("WCL Cleanup: Unflagged {Count} inactive guilds (threshold: {Days} days)", unflaggedCount, daysThreshold);

                    await FollowupAsync(embed: resultEmbed.Build(), ephemeral: true);
                }
                else
                {
                    await FollowupAsync($"Invalid action '{action}'. Use 'list' or 'unflag'.", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WCL cleanup command");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }

        private async Task ShowProblematicGuildsPage(
            List<(WowGuildAssociations Guild, LogMonitoring Monitoring, int? DaysSinceReport, string Reason)> problemGuilds,
            int page,
            int daysThreshold,
            string cleanupType)
        {
            const int itemsPerPage = 10;
            int totalPages = (int)Math.Ceiling(problemGuilds.Count / (double)itemsPerPage);
            page = Math.Max(0, Math.Min(page, totalPages - 1)); // Clamp page

            var embed = new EmbedBuilder();
            var typeDesc = cleanupType switch
            {
                "inactive" => "Inactive",
                "not-found" => "Not Found",
                "no-reports" => "No Reports",
                "all" => "Problematic",
                _ => "Problematic"
            };
            embed.WithTitle($"{typeDesc} Guilds ({problemGuilds.Count} found)");

            var description = cleanupType switch
            {
                "inactive" => $"Guilds with no reports in the last {daysThreshold} days",
                "not-found" => "Guilds that don't exist on WarcraftLogs",
                "no-reports" => "Guilds that exist on WarcraftLogs but have no reports",
                "all" => $"Guilds with issues (inactive, not found, or no reports)",
                _ => "Problematic guilds"
            };
            embed.WithDescription(description);
            embed.WithColor(Color.Orange);

            var sb = new StringBuilder();
            var pageItems = problemGuilds.Skip(page * itemsPerPage).Take(itemsPerPage);

            foreach (var (guild, monitoring, daysSince, reason) in pageItems)
            {
                // Build WarcraftLogs guild URL for spot checking
                var realmSlug = guild.LocalRealmSlug ?? guild.WowRealm.ToLower().Replace(" ", "-").Replace("'", "");
                var wclUrl = $"https://www.warcraftlogs.com/guild/{guild.WowRegion.ToLower()}/{realmSlug}/{Uri.EscapeDataString(guild.WowGuild)}";

                sb.AppendLine($"**[{guild.WowGuild}]({wclUrl})** ({guild.WowRealm}-{guild.WowRegion})");
                sb.AppendLine($"└─ Server: {guild.ServerName} | {reason}");
            }

            if (sb.Length > 0)
            {
                // Ensure field value doesn't exceed Discord's 1024 char limit
                var fieldValue = sb.ToString();
                if (fieldValue.Length > 1020)
                {
                    // Truncate at last complete line to avoid breaking markdown links
                    var lastNewline = fieldValue.LastIndexOf('\n', 1020);
                    if (lastNewline > 0)
                    {
                        fieldValue = fieldValue.Substring(0, lastNewline) + "\n...";
                    }
                    else
                    {
                        fieldValue = fieldValue.Substring(0, 1020) + "...";
                    }
                }
                embed.AddField($"Page {page + 1} of {totalPages}", fieldValue);
            }
            else
            {
                embed.AddField("Inactive Guilds", "*No guilds to display*");
            }

            embed.WithFooter($"Use action='unflag' to disable monitoring for these guilds");

            // Build component buttons
            var componentBuilder = new ComponentBuilder();

            var prevButton = new ButtonBuilder()
                .WithLabel("Previous")
                .WithCustomId($"wcl-cleanup-prev:{page}:{daysThreshold}:{cleanupType}")
                .WithStyle(ButtonStyle.Primary)
                .WithDisabled(page == 0);

            var nextButton = new ButtonBuilder()
                .WithLabel("Next")
                .WithCustomId($"wcl-cleanup-next:{page}:{daysThreshold}:{cleanupType}")
                .WithStyle(ButtonStyle.Primary)
                .WithDisabled(page >= totalPages - 1);

            componentBuilder.WithButton(prevButton);
            componentBuilder.WithButton(nextButton);

            await FollowupAsync(embed: embed.Build(), components: componentBuilder.Build(), ephemeral: true);
        }

        [ComponentInteraction("wcl-cleanup-prev:*:*:*")]
        [Discord.Interactions.RequireOwner]
        public async Task WclCleanupPrevPage(int currentPage, int daysThreshold, string cleanupType)
        {
            await DeferAsync(ephemeral: true);
            await NavigateCleanupPage(currentPage - 1, daysThreshold, cleanupType);
        }

        [ComponentInteraction("wcl-cleanup-next:*:*:*")]
        [Discord.Interactions.RequireOwner]
        public async Task WclCleanupNextPage(int currentPage, int daysThreshold, string cleanupType)
        {
            await DeferAsync(ephemeral: true);
            await NavigateCleanupPage(currentPage + 1, daysThreshold, cleanupType);
        }

        private async Task NavigateCleanupPage(int page, int daysThreshold, string cleanupType)
        {
            try
            {
                var problemGuilds = await GetProblematicGuildsAsync(cleanupType, daysThreshold);

                var embed = BuildProblematicGuildsEmbed(problemGuilds, page, daysThreshold, cleanupType);
                var components = BuildPaginationComponents(page, problemGuilds.Count, daysThreshold, cleanupType);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = components;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error navigating cleanup page");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }

        private Embed BuildProblematicGuildsEmbed(
            List<(WowGuildAssociations Guild, LogMonitoring Monitoring, int? DaysSinceReport, string Reason)> problemGuilds,
            int page,
            int daysThreshold,
            string cleanupType)
        {
            const int itemsPerPage = 10;
            int totalPages = (int)Math.Ceiling(problemGuilds.Count / (double)itemsPerPage);
            page = Math.Max(0, Math.Min(page, totalPages - 1));

            var embed = new EmbedBuilder();
            var typeDesc = cleanupType switch
            {
                "inactive" => "Inactive",
                "not-found" => "Not Found",
                "no-reports" => "No Reports",
                "all" => "Problematic",
                _ => "Problematic"
            };
            embed.WithTitle($"{typeDesc} Guilds ({problemGuilds.Count} found)");

            var description = cleanupType switch
            {
                "inactive" => $"Guilds with no reports in the last {daysThreshold} days",
                "not-found" => "Guilds that don't exist on WarcraftLogs",
                "no-reports" => "Guilds that exist on WarcraftLogs but have no reports",
                "all" => $"Guilds with issues (inactive, not found, or no reports)",
                _ => "Problematic guilds"
            };
            embed.WithDescription(description);
            embed.WithColor(Color.Orange);

            var sb = new StringBuilder();
            var pageItems = problemGuilds.Skip(page * itemsPerPage).Take(itemsPerPage);

            foreach (var (guild, monitoring, daysSince, reason) in pageItems)
            {
                // Build WarcraftLogs guild URL for spot checking
                var realmSlug = guild.LocalRealmSlug ?? guild.WowRealm.ToLower().Replace(" ", "-").Replace("'", "");
                var wclUrl = $"https://www.warcraftlogs.com/guild/{guild.WowRegion.ToLower()}/{realmSlug}/{Uri.EscapeDataString(guild.WowGuild)}";

                sb.AppendLine($"**[{guild.WowGuild}]({wclUrl})** ({guild.WowRealm}-{guild.WowRegion})");
                sb.AppendLine($"└─ Server: {guild.ServerName} | {reason}");
            }

            if (sb.Length > 0)
            {
                var fieldValue = sb.ToString();
                if (fieldValue.Length > 1020)
                {
                    // Truncate at last complete line to avoid breaking markdown links
                    var lastNewline = fieldValue.LastIndexOf('\n', 1020);
                    if (lastNewline > 0)
                    {
                        fieldValue = fieldValue.Substring(0, lastNewline) + "\n...";
                    }
                    else
                    {
                        fieldValue = fieldValue.Substring(0, 1020) + "...";
                    }
                }
                embed.AddField($"Page {page + 1} of {totalPages}", fieldValue);
            }
            else
            {
                embed.AddField("Inactive Guilds", "*No guilds to display*");
            }

            embed.WithFooter($"Use action='unflag' to disable monitoring for these guilds");

            return embed.Build();
        }

        [SlashCommand("wow-list-duplicates", "List WoW guild associations with duplicate ServerIds")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task ListDuplicateGuilds()
        {
            await DeferAsync();

            try
            {
                var allGuilds = await WithDbAsync(async db =>
                {
                    return await db.WowGuildAssociations.ToListAsync();
                });

                // Group by ServerId and find duplicates
                var duplicates = allGuilds
                    .GroupBy(g => g.ServerId)
                    .Where(group => group.Count() > 1)
                    .ToList();

                if (duplicates.Count == 0)
                {
                    await FollowupAsync("No duplicate guild associations found!");
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Duplicate WoW Guild Associations")
                    .WithDescription($"Found {duplicates.Count} server(s) with duplicate guild associations")
                    .WithColor(Color.Orange)
                    .WithCurrentTimestamp();

                foreach (var duplicateGroup in duplicates.Take(10)) // Limit to 10 to avoid embed size limits
                {
                    var serverId = duplicateGroup.Key;
                    var guilds = duplicateGroup.ToList();

                    var fieldValue = new StringBuilder();
                    fieldValue.AppendLine($"**ServerId:** {serverId}");

                    for (int i = 0; i < guilds.Count; i++)
                    {
                        var g = guilds[i];
                        var hasSlug = !string.IsNullOrWhiteSpace(g.LocalRealmSlug);
                        var slugInfo = hasSlug ? $"Slug: `{g.LocalRealmSlug}`" : "**Slug: [null]**";

                        fieldValue.AppendLine($"{i + 1}. {g.WowGuild}-{g.WowRealm} ({g.WowRegion})");
                        fieldValue.AppendLine($"   {slugInfo}, Set by: {g.SetBy ?? "Unknown"}");
                    }

                    embed.AddField($"Duplicate #{duplicates.IndexOf(duplicateGroup) + 1}", fieldValue.ToString());
                }

                if (duplicates.Count > 10)
                {
                    embed.WithFooter($"Showing 10 of {duplicates.Count} duplicates. Use /wow-cleanup-null-slugs to clean up entries with null realm slugs.");
                }
                else
                {
                    embed.WithFooter("Use /wow-cleanup-null-slugs to clean up entries with null realm slugs.");
                }

                await FollowupAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error listing duplicate guilds: {ex.Message}");
                await FollowupAsync($"Error listing duplicates: {ex.Message}");
            }
        }

        [SlashCommand("wow-cleanup-duplicates", "Remove duplicate guild associations, keeping only the latest entry per ServerId")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task CleanupDuplicates(
            [Summary("confirm", "Type 'DELETE' to confirm permanent deletion")] string confirm = "")
        {
            // Require explicit confirmation
            if (confirm?.ToUpper() != "DELETE")
            {
                var warningEmbed = new EmbedBuilder()
                    .WithTitle("⚠️ Confirmation Required")
                    .WithDescription("This command will **permanently delete** duplicate guild associations from the database.")
                    .WithColor(Color.Orange)
                    .AddField("What will happen:",
                        "• All duplicate ServerIds will be identified\n" +
                        "• Entries with LocalRealmSlug are preferred, then most recent\n" +
                        "• All other duplicates will be permanently deleted\n" +
                        "• This action **cannot be undone**")
                    .AddField("To proceed:", "Run the command again with `confirm:DELETE`")
                    .WithFooter("Use /wow-list-duplicates to preview what will be removed")
                    .Build();

                await RespondAsync(embed: warningEmbed);
                return;
            }

            await DeferAsync();

            try
            {
                var allGuilds = await WithDbAsync(async db =>
                {
                    return await db.WowGuildAssociations.ToListAsync();
                });

                // Group by ServerId and find duplicates
                var duplicateGroups = allGuilds
                    .GroupBy(g => g.ServerId)
                    .Where(group => group.Count() > 1)
                    .ToList();

                if (duplicateGroups.Count == 0)
                {
                    await FollowupAsync("No duplicate guild associations found!");
                    return;
                }

                var toRemove = new List<WowGuildAssociations>();
                var keptGuilds = new List<(WowGuildAssociations kept, int removedCount)>();

                foreach (var duplicateGroup in duplicateGroups)
                {
                    // Prefer entries with LocalRealmSlug, then by most recent TimeSet
                    var guilds = duplicateGroup
                        .OrderByDescending(g => !string.IsNullOrWhiteSpace(g.LocalRealmSlug)) // Prefer with slug
                        .ThenByDescending(g => g.TimeSet ?? DateTime.MinValue)
                        .ToList();
                    var keep = guilds.First(); // Keep the preferred entry (slug + most recent)
                    var remove = guilds.Skip(1).ToList(); // Remove all others

                    toRemove.AddRange(remove);
                    keptGuilds.Add((keep, remove.Count));
                }

                // Remove the duplicates
                int removedCount = await WithDbAsync(async db =>
                {
                    int count = 0;
                    foreach (var guild in toRemove)
                    {
                        var toDelete = await db.WowGuildAssociations.FirstOrDefaultAsync(g => g.Id == guild.Id);
                        if (toDelete != null)
                        {
                            db.WowGuildAssociations.Remove(toDelete);
                            count++;
                        }
                    }
                    await db.SaveChangesAsync();
                    return count;
                });

                var embed = new EmbedBuilder()
                    .WithTitle("Cleanup Duplicate Guild Associations")
                    .WithDescription($"Removed {removedCount} duplicate entries from {duplicateGroups.Count} server(s)")
                    .WithColor(Color.Green)
                    .WithCurrentTimestamp();

                if (keptGuilds.Count > 0)
                {
                    var keptList = new StringBuilder();
                    int itemsShown = 0;
                    const int maxFieldLength = 1020; // Leave some room for safety

                    foreach (var (kept, removed) in keptGuilds)
                    {
                        var timeSetInfo = kept.TimeSet.HasValue ? $"set {kept.TimeSet.Value:yyyy-MM-dd}" : "no date";
                        var slugInfo = !string.IsNullOrWhiteSpace(kept.LocalRealmSlug) ? $"slug: `{kept.LocalRealmSlug}`" : "**no slug**";

                        var entry = $"**{kept.WowGuild}-{kept.WowRealm}** ({kept.WowRegion})\n" +
                                   $"  ServerId: {kept.ServerId}, {slugInfo}, {timeSetInfo}\n" +
                                   $"  Removed {removed} older duplicate{(removed > 1 ? "s" : "")}\n\n";

                        // Check if adding this entry would exceed the limit
                        if (keptList.Length + entry.Length > maxFieldLength)
                        {
                            keptList.AppendLine($"... and {keptGuilds.Count - itemsShown} more (truncated to fit Discord limits)");
                            break;
                        }

                        keptList.Append(entry);
                        itemsShown++;
                    }

                    embed.AddField($"Kept Latest Entries ({keptGuilds.Count} servers)", keptList.ToString());
                }

                embed.WithFooter("Kept entries with LocalRealmSlug preferred, then most recent TimeSet. Run /wow-list-duplicates to verify.");

                await FollowupAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error cleaning up duplicates: {ex.Message}");
                await FollowupAsync($"Error during cleanup: {ex.Message}");
            }
        }

        [SlashCommand("wow-cleanup-null-slugs", "Remove WoW guild associations with null LocalRealmSlug")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task CleanupNullSlugs()
        {
            await DeferAsync();

            try
            {
                var (nullSlugGuilds, allGuilds) = await WithDbAsync(async db =>
                {
                    var nullSlugs = await db.WowGuildAssociations
                        .Where(g => string.IsNullOrWhiteSpace(g.LocalRealmSlug))
                        .ToListAsync();
                    var all = await db.WowGuildAssociations.ToListAsync();
                    return (nullSlugs, all);
                });

                if (nullSlugGuilds.Count == 0)
                {
                    await FollowupAsync("No guild associations with null LocalRealmSlug found!");
                    return;
                }

                var safeToRemove = new List<WowGuildAssociations>();
                var notSafeToRemove = new List<WowGuildAssociations>();

                foreach (var nullSlugGuild in nullSlugGuilds)
                {
                    var hasAlternative = allGuilds.Any(g =>
                        g.ServerId == nullSlugGuild.ServerId &&
                        !string.IsNullOrWhiteSpace(g.LocalRealmSlug));

                    if (hasAlternative)
                    {
                        safeToRemove.Add(nullSlugGuild);
                    }
                    else
                    {
                        notSafeToRemove.Add(nullSlugGuild);
                    }
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Cleanup Null LocalRealmSlug Entries")
                    .WithColor(Color.Blue)
                    .WithCurrentTimestamp();

                if (safeToRemove.Count > 0)
                {
                    // Remove the safe ones
                    await WithDbAsync(async db =>
                    {
                        foreach (var guild in safeToRemove)
                        {
                            var toRemove = await db.WowGuildAssociations
                                .FirstOrDefaultAsync(g => g.ServerId == guild.ServerId &&
                                                    string.IsNullOrWhiteSpace(g.LocalRealmSlug));
                            if (toRemove != null)
                            {
                                db.WowGuildAssociations.Remove(toRemove);
                            }
                        }
                        await db.SaveChangesAsync();
                    });

                    var removedList = string.Join("\n", safeToRemove.Take(20).Select(g =>
                        $"- {g.WowGuild}-{g.WowRealm} ({g.WowRegion}) [ServerId: {g.ServerId}]"));

                    if (safeToRemove.Count > 20)
                    {
                        removedList += $"\n... and {safeToRemove.Count - 20} more";
                    }

                    embed.AddField($"✅ Removed {safeToRemove.Count} duplicate(s) with null slugs", removedList);
                }
                else
                {
                    embed.AddField("No Safe Removals", "No duplicate entries with null slugs found that have valid alternatives.");
                }

                if (notSafeToRemove.Count > 0)
                {
                    var notSafeList = string.Join("\n", notSafeToRemove.Take(10).Select(g =>
                        $"- {g.WowGuild}-{g.WowRealm} ({g.WowRegion}) [ServerId: {g.ServerId}]"));

                    if (notSafeToRemove.Count > 10)
                    {
                        notSafeList += $"\n... and {notSafeToRemove.Count - 10} more";
                    }

                    embed.AddField($"⚠️ Skipped {notSafeToRemove.Count} entry/entries (no alternative found)",
                        notSafeList + "\n\nThese entries were not removed because no valid alternative exists. Consider setting the realm slug for these.");
                }

                await FollowupAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error cleaning up null slugs: {ex.Message}");
                await FollowupAsync($"Error during cleanup: {ex.Message}");
            }
        }

        private MessageComponent BuildPaginationComponents(int page, int totalItems, int daysThreshold, string cleanupType)
        {
            const int itemsPerPage = 10;
            int totalPages = (int)Math.Ceiling(totalItems / (double)itemsPerPage);

            var componentBuilder = new ComponentBuilder();

            var prevButton = new ButtonBuilder()
                .WithLabel("Previous")
                .WithCustomId($"wcl-cleanup-prev:{page}:{daysThreshold}:{cleanupType}")
                .WithStyle(ButtonStyle.Primary)
                .WithDisabled(page == 0);

            var nextButton = new ButtonBuilder()
                .WithLabel("Next")
                .WithCustomId($"wcl-cleanup-next:{page}:{daysThreshold}:{cleanupType}")
                .WithStyle(ButtonStyle.Primary)
                .WithDisabled(page >= totalPages - 1);

            componentBuilder.WithButton(prevButton);
            componentBuilder.WithButton(nextButton);

            return componentBuilder.Build();
        }

        [SlashCommand("import-mount", "Import a single mount by ID")]
        [Discord.Interactions.RequireOwner]
        public async Task ImportSingleMount(
            [Summary("mount-id", "The mount ID to import")]
            long mountId,
            [Summary("region", "Region to import from")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Importing mount {MountId} for region {Region} by user {User}", mountId, region, Context.User.Username);

                var mount = await _wowStaticData.ImportMountAsync(mountId, region);

                if (mount != null)
                {
                    var embed = new EmbedBuilder()
                        .WithTitle($"Mount Imported: {mount.Name}")
                        .WithColor(Color.Green)
                        .WithDescription($"**Source:** {mount.Source}\n**Instance:** {mount.InstanceName ?? "N/A"}\n**Encounter:** {mount.EncounterName ?? "N/A"}")
                        .WithFooter($"Mount ID: {mount.Id}")
                        .WithCurrentTimestamp();

                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
                else
                {
                    await FollowupAsync($"Mount {mountId} not found or failed to import.", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing mount {MountId}", mountId);
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("import-mounts", "Import all mounts from the WoW API")]
        [Discord.Interactions.RequireOwner]
        public async Task ImportMounts(
            [Summary("region", "Region to import from")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Starting mount import for region {Region} by user {User}", region, Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Mount Import Started")
                    .WithColor(Color.Blue)
                    .WithDescription($"Importing mounts from **{region.ToUpper()}** region...\n\nThis may take several minutes.")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Run import in background
                var cts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _wowStaticData.ImportAllMountsAsync(region, cts.Token);

                        var successEmbed = new EmbedBuilder()
                            .WithTitle("Mount Import Complete")
                            .WithColor(Color.Green)
                            .WithDescription($"Successfully imported mounts from **{region.ToUpper()}** region.")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during mount import");

                        var errorEmbed = new EmbedBuilder()
                            .WithTitle("Mount Import Failed")
                            .WithColor(Color.Red)
                            .WithDescription($"Error: {ex.Message}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating mount import");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Error")
                    .WithColor(Color.Red)
                    .WithDescription($"Failed to start mount import: {ex.Message}");
                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("merge-mount-sources", "Merge source data from mounts.json into database")]
        [Discord.Interactions.RequireOwner]
        public async Task MergeMountSources()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Starting mount source merge by user {User}", Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Mount Source Merge Started")
                    .WithColor(Color.Blue)
                    .WithDescription("Merging source data from **mounts.json** into database...\n\nThis will update drop locations, vendors, achievements, etc.")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Run merge in background
                var cts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await _wowStaticData.MergeScrapedMountDataAsync("mounts.json", cts.Token);

                        var successEmbed = new EmbedBuilder()
                            .WithTitle("Mount Source Merge Complete")
                            .WithColor(Color.Green)
                            .WithDescription($"{result}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during mount source merge");

                        var errorEmbed = new EmbedBuilder()
                            .WithTitle("Mount Source Merge Failed")
                            .WithColor(Color.Red)
                            .WithDescription($"Error: {ex.Message}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating mount source merge");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Error")
                    .WithColor(Color.Red)
                    .WithDescription($"Failed to start mount source merge: {ex.Message}");
                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("refresh-static-data", "Refresh static data (realms, classes, races) from WoW API")]
        [Discord.Interactions.RequireOwner]
        public async Task RefreshStaticData(
            [Summary("type", "What to refresh")]
            [Choice("All", "all")]
            [Choice("Realms", "realms")]
            [Choice("Classes", "classes")]
            [Choice("Races", "races")]
            string type = "all")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Refreshing static data ({Type}) by user {User}", type, Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Static Data Refresh Started")
                    .WithColor(Color.Blue)
                    .WithDescription($"Refreshing **{type}** from WoW API...\n\nThis may take a moment.")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                var cts = new CancellationTokenSource();
                var results = new List<string>();

                switch (type.ToLower())
                {
                    case "realms":
                        await _wowStaticData.ImportAllRealmsAsync(cts.Token);
                        results.Add("Realms: Success");
                        break;
                    case "classes":
                        await _wowStaticData.ImportAllClassesAsync(cts.Token);
                        results.Add("Classes: Success");
                        break;
                    case "races":
                        await _wowStaticData.ImportAllRacesAsync(cts.Token);
                        results.Add("Races: Success");
                        break;
                    case "all":
                    default:
                        var refreshResult = await _wowStaticData.RefreshAllStaticDataAsync(cts.Token);
                        results.Add(refreshResult);
                        break;
                }

                var successEmbed = new EmbedBuilder()
                    .WithTitle("Static Data Refresh Complete")
                    .WithColor(Color.Green)
                    .WithDescription(string.Join("\n", results))
                    .WithCurrentTimestamp();

                await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing static data");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Static Data Refresh Failed")
                    .WithColor(Color.Red)
                    .WithDescription($"Error: {ex.Message}");
                await Context.Interaction.FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("static-data-stats", "Show statistics about cached static data")]
        [Discord.Interactions.RequireOwner]
        public async Task StaticDataStats()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var realms = await _wowStaticData.GetAllRealmsAsync();
                var classes = await _wowStaticData.GetAllClassesAsync();
                var races = await _wowStaticData.GetAllRacesAsync();
                var mounts = await _wowStaticData.GetAllMountsAsync();
                var achievements = await _wowStaticData.GetAllAchievementsAsync();
                var pets = await _wowStaticData.GetAllPetsAsync();

                var embed = new EmbedBuilder()
                    .WithTitle("Static Data Statistics")
                    .WithColor(Color.Blue)
                    .WithCurrentTimestamp();

                // Realms by region
                var realmsByRegion = realms.GroupBy(r => r.Region).OrderBy(g => g.Key);
                var realmStats = string.Join("\n", realmsByRegion.Select(g => $"**{g.Key}:** {g.Count()} realms"));
                embed.AddField($"Realms ({realms.Count} total)", realmStats.Length > 0 ? realmStats : "No realms cached");

                // Classes
                embed.AddField($"Playable Classes ({classes.Count})",
                    classes.Count > 0 ? string.Join(", ", classes.OrderBy(c => c.Name).Select(c => c.Name)) : "No classes cached");

                // Races by faction
                var racesByFaction = races.GroupBy(r => r.Faction).OrderBy(g => g.Key);
                var raceStats = string.Join("\n", racesByFaction.Select(g => $"**{g.Key}:** {string.Join(", ", g.Select(r => r.Name))}"));
                embed.AddField($"Playable Races ({races.Count})", raceStats.Length > 0 ? raceStats : "No races cached");

                // Mounts
                embed.AddField("Mounts", $"{mounts.Count:N0} mounts cached");

                // Achievements
                var achievementsByCategory = achievements.GroupBy(a => a.ParentCategory ?? a.Category ?? "Uncategorized")
                    .OrderByDescending(g => g.Count())
                    .Take(5);
                var achievementStats = string.Join("\n", achievementsByCategory.Select(g => $"**{g.Key}:** {g.Count()}"));
                embed.AddField($"Achievements ({achievements.Count:N0} total)",
                    achievements.Count > 0 ? $"Top categories:\n{achievementStats}" : "No achievements cached");

                // Pets
                var petsByType = pets.GroupBy(p => p.PetType ?? "Unknown")
                    .OrderByDescending(g => g.Count());
                var petStats = string.Join(", ", petsByType.Select(g => $"{g.Key}: {g.Count()}"));
                embed.AddField($"Pets ({pets.Count:N0} total)",
                    pets.Count > 0 ? petStats : "No pets cached");

                // Last updated info
                var oldestRealm = realms.OrderBy(r => r.LastUpdated).FirstOrDefault();
                var oldestClass = classes.OrderBy(c => c.LastUpdated).FirstOrDefault();
                var oldestRace = races.OrderBy(r => r.LastUpdated).FirstOrDefault();
                var oldestAchievement = achievements.OrderBy(a => a.LastUpdated).FirstOrDefault();
                var oldestPet = pets.OrderBy(p => p.LastUpdated).FirstOrDefault();

                var lastUpdatedInfo = new List<string>();
                if (oldestRealm != null) lastUpdatedInfo.Add($"Realms: {oldestRealm.LastUpdated:g}");
                if (oldestClass != null) lastUpdatedInfo.Add($"Classes: {oldestClass.LastUpdated:g}");
                if (oldestRace != null) lastUpdatedInfo.Add($"Races: {oldestRace.LastUpdated:g}");
                if (oldestAchievement != null) lastUpdatedInfo.Add($"Achievements: {oldestAchievement.LastUpdated:g}");
                if (oldestPet != null) lastUpdatedInfo.Add($"Pets: {oldestPet.LastUpdated:g}");

                if (lastUpdatedInfo.Count > 0)
                {
                    embed.AddField("Oldest Update", string.Join("\n", lastUpdatedInfo));
                }

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting static data stats");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("import-items", "Import items from the WoW API by ID range")]
        [Discord.Interactions.RequireOwner]
        public async Task ImportItems(
            [Summary("start-id", "Starting item ID")]
            long startId = 1,

            [Summary("end-id", "Ending item ID (max 10000 at a time)")]
            long endId = 10000,

            [Summary("region", "Region to import from")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Validate range
                if (endId < startId)
                {
                    await FollowupAsync("❌ End ID must be greater than or equal to start ID.", ephemeral: true);
                    return;
                }

                if (endId - startId > 10000)
                {
                    await FollowupAsync("❌ Range too large. Maximum 10,000 items at a time.", ephemeral: true);
                    return;
                }

                _logger.LogInformation("Admin command: Starting item import {Start}-{End} for region {Region} by user {User}",
                    startId, endId, region, Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Item Import Started")
                    .WithColor(Color.Blue)
                    .WithDescription($"Importing items **{startId:N0}** to **{endId:N0}** from **{region.ToUpper()}** region...\n\nThis may take several minutes.")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Run import in background
                var cts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        int imported = 0;
                        int failed = 0;

                        for (long itemId = startId; itemId <= endId; itemId++)
                        {
                            if (cts.Token.IsCancellationRequested) break;

                            try
                            {
                                var item = await _wowStaticData.ImportItemAsync(itemId, region, cts.Token);
                                if (item != null)
                                {
                                    imported++;
                                }
                                else
                                {
                                    failed++;
                                }

                                // Progress update every 1000 items
                                if ((itemId - startId) % 1000 == 0 && itemId > startId)
                                {
                                    var progressEmbed = new EmbedBuilder()
                                        .WithTitle("Import Progress")
                                        .WithColor(Color.Orange)
                                        .WithDescription($"Processed: **{itemId - startId:N0}** / **{endId - startId:N0}**\nImported: **{imported:N0}** | Failed: **{failed:N0}**")
                                        .WithCurrentTimestamp();

                                    await Context.Interaction.FollowupAsync(embed: progressEmbed.Build(), ephemeral: true);
                                }

                                // Rate limiting
                                await Task.Delay(50, cts.Token);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to import item {ItemId}", itemId);
                                failed++;
                            }
                        }

                        var successEmbed = new EmbedBuilder()
                            .WithTitle("Item Import Complete")
                            .WithColor(Color.Green)
                            .WithDescription($"**Range:** {startId:N0} - {endId:N0}\n**Imported:** {imported:N0}\n**Failed:** {failed:N0}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during item import");

                        var errorEmbed = new EmbedBuilder()
                            .WithTitle("Item Import Failed")
                            .WithColor(Color.Red)
                            .WithDescription($"Error: {ex.Message}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating item import");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Error")
                    .WithColor(Color.Red)
                    .WithDescription($"Failed to start item import: {ex.Message}");
                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("import-achievements", "Import all achievements from the WoW API")]
        [Discord.Interactions.RequireOwner]
        public async Task ImportAchievements()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Starting achievement import by user {User}", Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Achievement Import Started")
                    .WithColor(Color.Blue)
                    .WithDescription("Importing all achievements from the WoW API...\n\n**Estimated time:** 15-20 minutes (~5,500 achievements)\n\nThis runs in the background. You'll be notified when complete.")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Run import in background
                var cts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _wowStaticData.ImportAllAchievementsAsync(cts.Token);

                        var achievements = await _wowStaticData.GetAllAchievementsAsync();

                        var successEmbed = new EmbedBuilder()
                            .WithTitle("Achievement Import Complete")
                            .WithColor(Color.Green)
                            .WithDescription($"**Total achievements:** {achievements.Count:N0}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during achievement import");

                        var errorEmbed = new EmbedBuilder()
                            .WithTitle("Achievement Import Failed")
                            .WithColor(Color.Red)
                            .WithDescription($"Error: {ex.Message}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating achievement import");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Error")
                    .WithColor(Color.Red)
                    .WithDescription($"Failed to start achievement import: {ex.Message}");
                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("recalc-mount-expansions", "Recalculate expansion tags for all mounts using smart detection")]
        [Discord.Interactions.RequireOwner]
        public async Task RecalculateMountExpansions()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Recalculating mount expansions by user {User}", Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Mount Expansion Recalculation Started")
                    .WithColor(Color.Blue)
                    .WithDescription("Recalculating expansion tags for all mounts...\n\nUsing smart detection: Description > Zone > ID-based fallback")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Run recalculation
                var result = await _wowStaticData.RecalculateMountExpansionsAsync();

                var successEmbed = new EmbedBuilder()
                    .WithTitle("Mount Expansion Recalculation Complete")
                    .WithColor(Color.Green)
                    .WithDescription(result)
                    .WithCurrentTimestamp();

                await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recalculating mount expansions");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Recalculation Failed")
                    .WithColor(Color.Red)
                    .WithDescription($"Error: {ex.Message}");
                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("import-pets", "Import all pets from the WoW API")]
        [Discord.Interactions.RequireOwner]
        public async Task ImportPets()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation("Admin command: Starting pet import by user {User}", Context.User.Username);

                var embed = new EmbedBuilder()
                    .WithTitle("Pet Import Started")
                    .WithColor(Color.Blue)
                    .WithDescription("Importing all pets from the WoW API...\n\n**Estimated time:** 5-7 minutes (~1,700 pets)\n\nThis runs in the background. You'll be notified when complete.")
                    .WithCurrentTimestamp();

                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Run import in background
                var cts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _wowStaticData.ImportAllPetsAsync(cts.Token);

                        var pets = await _wowStaticData.GetAllPetsAsync();

                        var successEmbed = new EmbedBuilder()
                            .WithTitle("Pet Import Complete")
                            .WithColor(Color.Green)
                            .WithDescription($"**Total pets:** {pets.Count:N0}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: successEmbed.Build(), ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during pet import");

                        var errorEmbed = new EmbedBuilder()
                            .WithTitle("Pet Import Failed")
                            .WithColor(Color.Red)
                            .WithDescription($"Error: {ex.Message}")
                            .WithCurrentTimestamp();

                        await Context.Interaction.FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating pet import");
                var errorEmbed = new EmbedBuilder()
                    .WithTitle("Error")
                    .WithColor(Color.Red)
                    .WithDescription($"Failed to start pet import: {ex.Message}");
                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }
    }
}
