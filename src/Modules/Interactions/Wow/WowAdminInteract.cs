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
    // Interaction modules must be public and inherit from an IInteractionModuleBase
    public class WowAdminInteract : InteractionModuleBase<ShardedInteractionContext>
    {
        private ChannelCheck _cc;
        private WarcraftLogs _logsApi;
        private WowApi _wowApi;
        private DiscordShardedClient _client;
        private RaiderIOApi _rioApi;
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private readonly ILogger _logger;
        private WowUtilities _wowUtils;
        
        public WowAdminInteract(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WowAdminInteract>>();
            _wowUtils = services.GetRequiredService<WowUtilities>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _logsApi = services.GetRequiredService<WarcraftLogs>();
            _wowApi = services.GetRequiredService<WowApi>();
            _rioApi = services.GetRequiredService<RaiderIOApi>();
            _client = services.GetRequiredService<DiscordShardedClient>(); 
            _config = services.GetRequiredService<IConfigurationRoot>();            
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
                    using (var db = new NinjaBotEntities())
                    {
                        guildList = db.WowGuildAssociations.ToList();
                        logWatchList = db.LogMonitoring.ToList();
                    }
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
                                        DateTime startTime = _wowApi.UnixTimeStampToDateTime(latestLog.start);
                                        {
                                            using (var db = new NinjaBotEntities())
                                            {
                                                var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                                latestForGuild.LatestLogRetail = startTime;
                                                latestForGuild.RetailReportId = latestLog.id;
                                                await db.SaveChangesAsync();
                                            }
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
            using (var db = new NinjaBotEntities())
            {
                var foundCheeve = db.FindWowCheeves.Where(c => c.AchId == id).FirstOrDefault();
                if (foundCheeve != null)
                {
                    db.Remove(foundCheeve);
                    await db.SaveChangesAsync();
                    await RespondAsync($"Removed achievement id {id} from the database!");
                }
                else
                {
                    await RespondAsync($"Sorry, unable to find achievement ID {id} in the database!");
                }
            }
        }

        [SlashCommand("addachievement", "add achivement")]
        [Discord.Interactions.RequireOwner]
        public async Task AddAchieve(long id, int cat)
        {
            using (var db = new NinjaBotEntities())
            {
                var foundCheeve = db.FindWowCheeves.Where(c => c.AchId == id);
                if (foundCheeve != null)
                {
                    var category = db.AchCategories.Where(c => c.CatId == cat).FirstOrDefault();
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
                            await RespondAsync($"Added achievement ID {id} with category {category.CatName} to the database!");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"{ex.Message}");
                        }
                    }
                    else
                    {
                        await RespondAsync($"Unable to find category with ID {cat} in the database!");
                    }

                }
                else
                {
                    await RespondAsync($"Sorry, achievement {id} already exists in the database!");
                }
            }
        }

        [SlashCommand("listachievements", "list achivements")]
        [Discord.Interactions.RequireOwner]
        public async Task ListCheeves()
        {
            StringBuilder sb = new StringBuilder();
            List<FindWowCheeve> cheeves = new List<FindWowCheeve>();
            using (var db = new NinjaBotEntities())
            {
                cheeves = db.FindWowCheeves.ToList();
            }
            if (cheeves.Count > 0)
            {
                foreach (var cheeve in cheeves)
                {
                    sb.AppendLine($"{cheeve.AchId}");
                }
            }
            await RespondAsync(sb.ToString());
        }

        [SlashCommand("tu", "start wcl timer")]
        [Discord.Interactions.RequireOwner]
        public async Task StartTimer()
        {
            await _logsApi.StartTimer();
        }

        [SlashCommand("td", "stop wcl timer")]
        [Discord.Interactions.RequireOwner]
        public async Task StopTimer()
        {
            await _logsApi.StopTimer();
        }
    
        [SlashCommand("get-latest-zone", "get latest zone")]
        [Discord.Interactions.RequireOwner]
        public async Task GetLatestZone()
        {
            var zone = WarcraftLogs.Zones[WarcraftLogs.Zones.Count - 1];
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
                zone = WarcraftLogs.Zones[WarcraftLogs.Zones.Count - 1]; 
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
                    using (var db = new NinjaBotEntities())
                    {
                        var curTier = db.CurrentRaidTier.FirstOrDefault();
                        curTier.Partition = partition;
                        await db.SaveChangesAsync();
                    }
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
    }
}
