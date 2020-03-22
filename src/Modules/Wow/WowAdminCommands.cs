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
using NinjaBotCore.Common;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaBotCore.Modules.Wow
{
    public class WowAdminCommands : ModuleBase
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
        
        public WowAdminCommands(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WowAdminCommands>>();
            _wowUtils = services.GetRequiredService<WowUtilities>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _logsApi = services.GetRequiredService<WarcraftLogs>();
            _wowApi = services.GetRequiredService<WowApi>();
            _rioApi = services.GetRequiredService<RaiderIOApi>();
            _client = services.GetRequiredService<DiscordShardedClient>(); 
            _config = services.GetRequiredService<IConfigurationRoot>();
            _prefix = _config["prefix"];
        }

        [Command("Populate-Logs",RunMode = RunMode.Async)]
        [RequireOwner]
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

        [Command("noggen", RunMode = RunMode.Async)]
        [RequireOwner]
        public async Task GetNoggen([Remainder] string args = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            string title = string.Empty;
            GuildMembers members = null;
            string thumbUrl = string.Empty;
            var guildInfo = Context.Guild;
            string discordGuildName = string.Empty;

            if (guildInfo == null)
            {
                discordGuildName = Context.User.Username;
                thumbUrl = Context.User.GetAvatarUrl();
            }
            else
            {
                discordGuildName = Context.Guild.Name;
                thumbUrl = Context.Guild.IconUrl;
            }

            var guildObject = await _wowUtils.GetGuildName(Context);

            title = $"Top Noggenfogger Consumers in **{guildObject.guildName}**";
            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;

            if (guildObject.guildName != null || members != null)
            {
                try
                {
                    if (args == "clear")
                    {
                        List<CharStats> statsFromDb = new List<CharStats>();

                        using (var db = new NinjaBotEntities())
                        {
                            statsFromDb = db.CharStats.Where(c => c.GuildName == guildObject.guildName).ToList();                                                                        
                        }
                        if (statsFromDb != null)
                        {
                            using (var db = new NinjaBotEntities())
                            {
                                var people = db.CharStats.Where(c => c.GuildName == guildObject.guildName).ToList();
                                foreach (var person in people)
                                {
                                    db.CharStats.Remove(person);
                                }                                
                                await db.SaveChangesAsync();
                            }                            
                        }
                    }
                    else 
                    {
                    if (!string.IsNullOrEmpty(guildObject.locale))
                    {
                        members = _wowApi.GetGuildMembers(guildObject.realmName, guildObject.guildName, locale: guildObject.locale, regionName: guildObject.regionName);
                    }
                    else
                    {
                        members = _wowApi.GetGuildMembers(guildObject.realmName, guildObject.guildName, regionName: guildObject.regionName);
                    }     

                    var maxLevelChars = members.members.Where(m => m.character.level == 120);               
                    List<CharStats> statsFromDb = new List<CharStats>();

                    using (var db = new NinjaBotEntities())
                    {
                        statsFromDb = db.CharStats.Where(c => c.GuildName == guildObject.guildName).ToList();                                                                        
                    }

                    foreach (var member in maxLevelChars)
                    {
                        var curMemberStats = new WowStats();
                        var match = statsFromDb.Where(s => s.CharName == member.character.name).FirstOrDefault(); 
                        var matchTime = DateTime.Now;

                        if (match != null)
                        {
                            matchTime = match.LastModified;  
                        }                                         
                        if (match == null || matchTime.Day != DateTime.Now.Day) 
                        {
                            curMemberStats = _wowApi.GetCharStats(member.character.name, member.character.realm.slug, guildObject.locale);
                            var elixer = curMemberStats.Statistics.SubCategories[0].SubCategories[0].Statistics.Where(s => s.Name == "Elixir consumed most").FirstOrDefault();
                            using (var db = new NinjaBotEntities())
                            {
                                db.CharStats.Add(new CharStats{
                                    CharName = member.character.name,
                                    RealmName = member.character.realm.slug,
                                    GuildName = guildObject.guildName,
                                    ElixerConsumed = elixer.Highest,
                                    Quantity = elixer.Quantity,
                                    LastModified = DateTime.Now                                    
                                });
                                await db.SaveChangesAsync();                                
                            }
                        }
                        else
                        {

                        }
                    }
                    if (statsFromDb.Count() == 0)
                    {
                        using (var db = new NinjaBotEntities())
                        {
                            statsFromDb = db.CharStats.Where(c => c.GuildName == guildObject.guildName).ToList();                                                                        
                        }
                    }                  
                    if (statsFromDb.Count() > 0)
                    {
                        var candidates = statsFromDb.OrderByDescending(o => o.Quantity).Take(10).ToList();
                        if (candidates != null && candidates.Count() > 0)
                        {
                            foreach (var canditate in candidates)
                            {
                                sb.AppendLine($":black_medium_small_square: {canditate.CharName} -> *{canditate.Quantity}*");
                            }                            
                        }
                    }
                    embed.Description = sb.ToString();
                    await _cc.Reply(Context, embed);
                    }                   
                }                
                catch (Exception ex)
                {
                    _logger.LogError($"Get-Guild Error getting guild info: [{ex.Message}]");
                }
            }
        }

        [Command("remove-achievement", RunMode = RunMode.Async)]
        [Alias("ra")]
        [RequireOwner]
        public async Task RemoveAchieve(long id)
        {
            using (var db = new NinjaBotEntities())
            {
                var foundCheeve = db.FindWowCheeves.Where(c => c.AchId == id).FirstOrDefault();
                if (foundCheeve != null)
                {
                    db.Remove(foundCheeve);
                    await db.SaveChangesAsync();
                    await _cc.Reply(Context, $"Removed achievement id {id} from the database!");
                }
                else
                {
                    await _cc.Reply(Context, $"Sorry, unable to find achievement ID {id} in the database!");
                }
            }
        }

        [Command("add-achievement", RunMode = RunMode.Async)]
        [Alias("adda")]
        [RequireOwner]
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
                            await _cc.Reply(Context, $"Added achievement ID {id} with category {category.CatName} to the database!");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"{ex.Message}");
                        }
                    }
                    else
                    {
                        await _cc.Reply(Context, $"Unable to find category with ID {cat} in the database!");
                    }

                }
                else
                {
                    await _cc.Reply(Context, $"Sorry, achievement {id} already exists in the database!");
                }
            }
        }

        [Command("list-achievements", RunMode = RunMode.Async)]
        [Alias("la")]
        [RequireOwner]
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
            await _cc.Reply(Context, sb.ToString());
        }

        [Command("tu", RunMode = RunMode.Async)]
        [RequireOwner]
        public async Task StartTimer()
        {
            await _logsApi.StartTimer();
        }

        [Command("td", RunMode = RunMode.Async)]
        [RequireOwner]
        public async Task StopTimer()
        {
            await _logsApi.StopTimer();
        }
    
        [Command("get-latest-zone", RunMode = RunMode.Async)]
        [Alias("glz")]
        [RequireOwner]
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
            
            await _cc.Reply(Context, embed);
        }

        [Command("set-zone", RunMode = RunMode.Async)]
        [Alias("slz")]
        [RequireOwner]
        public async Task SetLatestZone([Remainder] string args = null)
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
            await _cc.Reply(Context, embed);
        }

        [Command("set-partition",RunMode = RunMode.Async)]
        [Alias("sp")]
        [RequireOwner]
        public async Task SetPartition([Remainder] string args = null)
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
            await _cc.Reply(Context, embed);
            WarcraftLogs.CurrentRaidTier.Partition = partition;
        }
    }
}