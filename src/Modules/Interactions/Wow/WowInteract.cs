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
    public class WowInteract : InteractionModuleBase<ShardedInteractionContext>
    {
        // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
        public InteractionService Commands { get; set; }
        private InteractionHandler _handler;
        private ChannelCheck _cc;
        private WarcraftLogs _logsApi;
        private WowApi _wowApi;
        private DiscordShardedClient _client;
        private RaiderIOApi _rioApi;
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private readonly ILogger _logger;
        private WowUtilities _wowUtils;
        // Constructor injection is also a valid way to access the dependencies
        public WowInteract(IServiceProvider services)
        {
            _handler = services.GetRequiredService<InteractionHandler>();
            _logger = services.GetRequiredService<ILogger<WowInteract>>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _logsApi = services.GetRequiredService<WarcraftLogs>();            
            _wowApi = services.GetRequiredService<WowApi>();
            _rioApi = services.GetRequiredService<RaiderIOApi>();
            _client = services.GetRequiredService<DiscordShardedClient>();            
            _config = services.GetRequiredService<IConfigurationRoot>();
            _wowUtils = services.GetRequiredService<WowUtilities>();
        }

        // You can use a number of parameter types in you Slash Command handlers (string, int, double, bool, IUser, IChannel, IMentionable, IRole, Enums) by default. Optionally,
        // you can implement your own TypeConverters to support a wider range of parameter types. For more information, refer to the library documentation.
        // Optional method parameters(parameters with a default value) also will be displayed as optional on Discord.

        // [Summary] lets you customize the name and the description of a parameter
        [SlashCommand("rio", "Get character's raider IO profile")]
        public async Task GetRioProfile(string args, [Summary(description: "get RIO info")] bool mention = false)
        {
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            GuildChar charInfo = null;
            if (string.IsNullOrEmpty(args))
            {
                embed.Title = $"M+ Character Info Help";
                sb.AppendLine($"Usage examples:");
                sb.AppendLine($":black_small_square: **/rpi** charactername");
                sb.AppendLine($"\t:black_small_square: Search raider.io for *charactername* (first in guild, then in the rest of WoW(US))");
                sb.AppendLine($":black_small_square: **/rpi** charactername realmname");
                sb.AppendLine($"\t:black_small_square: Search raider.io for *charactername* on *realmname* WoW (US)");
                sb.AppendLine($":black_small_square: **/rpi** charactername realmname region(us or eu)");
                sb.AppendLine($"\t:black_small_square: Search raider.io for *charactername* on *realmname* WoW (US or EU as specified)");
                embed.Description = sb.ToString();
                await ReplyAsync(embed: embed.Build());
                return;
            }
            else
            {
                RaiderIOModels.RioMythicPlusChar mPlusInfo = null;
                charInfo = await _wowUtils.GetCharFromArgs(args, Context);
                Character armoryInfo = null;
                string realmSlug = string.Empty;
                string region = string.Empty;
                if (!(string.IsNullOrEmpty(charInfo.regionName)))
                {
                    mPlusInfo = _rioApi.GetCharMythicPlusInfo(charName: charInfo.charName, realmName: charInfo.realmName.Replace(" ","%20"), region: charInfo.regionName.ToLower());
                    //armoryInfo = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName, charInfo.regionName);
                }
                else
                {
                    mPlusInfo = _rioApi.GetCharMythicPlusInfo(charName: charInfo.charName, realmName: charInfo.realmName.Replace(" ","%20"));
                    //armoryInfo = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName);
                }      
                var locale = string.Empty;
                if (!string.IsNullOrEmpty(charInfo.locale))
                {
                    locale = charInfo.locale.Substring(3).ToLower();
                }          
                else if (!string.IsNullOrEmpty(charInfo.regionName))
                {
                    locale = charInfo.regionName;
                }
                switch (locale.ToLower())
                {
                    case "us":
                        {
                            region = "us";
                            realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(charInfo.realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                            break;
                        }
                    case "ru":
                        {                 
                            region = "ru";   
                            realmSlug = WowApi.RealmInfoRu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(charInfo.realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                            break;
                        }
                    case "gb":
                        {
                            region = "eu";
                            realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(charInfo.realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                            break;
                        }
                    default:
                        {
                            realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(charInfo.realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                            break;
                        }
                }   

                string normalKilled = _wowUtils.GetNumberEmojiFromString((int)mPlusInfo.RaidProgression.Vault.NormalBossesKilled);
                string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)mPlusInfo.RaidProgression.Vault.HeroicBossesKilled);
                string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)mPlusInfo.RaidProgression.Vault.MythicBossesKilled);
                string totalBosses  = _wowUtils.GetNumberEmojiFromString((int)mPlusInfo.RaidProgression.Vault.TotalBosses); 
                           
                sb.AppendLine($"**__Raid Progression__**");
                sb.AppendLine();
                sb.AppendLine($"Sanctum of Domination");                               
                sb.AppendLine($"\t **normal** [{normalKilled} / {totalBosses}] **heroic** [{heroicKilled} / {totalBosses}] **mythic** [{mythicKilled} / {totalBosses}]");
                sb.AppendLine();               
                sb.AppendLine($"**__Best Runs__**");                
                foreach (var run in mPlusInfo.MythicPlusBestRuns)
                {
                    sb.AppendLine($"\t [:white_square_button: [{run.ShortName}(**{run.MythicLevel}**)] {run.ClearTimeMs / 60000} minutes]({run.Url.AbsoluteUri})");
                } 
                sb.AppendLine();                                
                sb.AppendLine($"**__M+ Rankings For Active Spec ({mPlusInfo.ActiveSpecRole})__**");
                switch (mPlusInfo.ActiveSpecRole.ToLower())
                {
                    case "dps":
                    {
                        sb.AppendLine($"\t Realm [{mPlusInfo.MythicPlusRanks.Dps.Realm}] Region [{mPlusInfo.MythicPlusRanks.Dps.Region}] World [{mPlusInfo.MythicPlusRanks.Dps.World}]");
                        break;
                    }
                    case "healer":
                    {
                        sb.AppendLine($"\t Realm [{mPlusInfo.MythicPlusRanks.Healer.Realm}] Region [{mPlusInfo.MythicPlusRanks.Healer.Region}] World [{mPlusInfo.MythicPlusRanks.Healer.World}]");
                        break;
                    }
                    case "tank":
                    {
                        sb.AppendLine($"\t Realm [{mPlusInfo.MythicPlusRanks.Tank.Realm}] Region [{mPlusInfo.MythicPlusRanks.Tank.Region}] World [{mPlusInfo.MythicPlusRanks.Tank.World}]");
                        break;
                    }
                }
                sb.AppendLine();          
                embed.Title = $"Mythic+ Information For {mPlusInfo.Name} on {mPlusInfo.Realm}";
                embed.AddField("Raider.IO",$"[{mPlusInfo.Name}]({mPlusInfo.ProfileUrl.AbsoluteUri})", true);
                //embed.AddField("WoW Armory",$"[{mPlusInfo.Name}]({armoryInfo.armoryURL})", true);
                embed.AddField("Warcraftlogs",$"[{mPlusInfo.Name}](https://www.warcraftlogs.com/character/{region}/{realmSlug}/{mPlusInfo.Name})", true);
                embed.ThumbnailUrl = $"{mPlusInfo.ThumbnailUrl.AbsoluteUri}";
                embed.Description = sb.ToString();
                embed.WithColor(new Color(0, 200, 150));
                embed.Footer = new EmbedFooterBuilder{
                    Text = $"Raider.IO Score {mPlusInfo.MythicPlusScores.All}"
                };                            
                await RespondAsync(embed: embed.Build());
            }
        }

        [SlashCommand("ginfo", "Get guild information")]
        public async Task GetRioGuildStats()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            var guildInfo = Context.Guild;
            
            string title = string.Empty;            
            string discordGuildName = string.Empty;
            string thumbUrl = string.Empty;
            string region = string.Empty;

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
            var guildStats = _rioApi.GetRioGuildInfo(guildName: guildObject.guildName, realmName: guildObject.realmSlug, region: guildObject.regionName);
                        
            string normalKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.Vault.NormalBossesKilled);
            string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.Vault.HeroicBossesKilled);
            string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.Vault.MythicBossesKilled);
            string totalBosses  = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.Vault.TotalBosses);
            
            title = $"{guildObject.guildName} on {guildObject.realmName}'s Raider.IO Stats";

            sb.AppendLine("**__Raid Progression:__**");
            sb.AppendLine($"\t **normal** [{normalKilled} / {totalBosses}]");
            sb.AppendLine($"\t **heroic** [{heroicKilled} / {totalBosses}]");
            sb.AppendLine($"\t **mythic** [{mythicKilled} / {totalBosses}]");
            sb.AppendLine();
            sb.AppendLine("**__Raid Rankings:__**");
            sb.AppendLine($"\t **normal** [ realm [**{guildStats.RaidRankings.Vault.Normal.Realm}**] world [**{guildStats.RaidRankings.Vault.Normal.World}**] region [**{guildStats.RaidRankings.Vault.Normal.Region}**] ]");            
            sb.AppendLine($"\t **heroic** [ realm [**{guildStats.RaidRankings.Vault.Heroic.Realm}**] world [**{guildStats.RaidRankings.Vault.Heroic.World}**] region [**{guildStats.RaidRankings.Vault.Heroic.Region}**] ]");
            sb.AppendLine($"\t **mythic** [ realm [**{guildStats.RaidRankings.Vault.Mythic.Realm}**] world [**{guildStats.RaidRankings.Vault.Mythic.World}**] region [**{guildStats.RaidRankings.Vault.Mythic.Region}**] ]");
            sb.AppendLine();
            sb.AppendLine($"[{guildObject.guildName} Profile]({guildStats.ProfileUrl.AbsoluteUri})");

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            embed.WithColor(new Color(0, 0, 255));
            embed.Description = sb.ToString();

            await RespondAsync(embed: embed.Build());                        
        }

        [SlashCommand("affixes", "Get current m+ affixes")]
        public async Task GetAffixes()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            var guildInfo = Context.Guild;
            
            string title = string.Empty;            
            string discordGuildName = string.Empty;
            string thumbUrl = string.Empty;
            string region = string.Empty;

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
            RaiderIOModels.Affix affixes = null;
            
            switch (guildObject.regionName.ToLower())
            {
                case "us":
                {
                    region = "us";
                    break;
                }
                case "eu":
                {
                    region = "eu";
                    break;
                }
                default:
                {
                    region = "us";
                    break;
                }
            }

            affixes = _rioApi.GetCurrentAffix(region: region);

            title = $"Current M+ Affixes ({region})";
           
            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            embed.WithColor(new Color(0, 255, 0));
            string affixLevel = string.Empty;
            foreach (var detail in affixes.AffixDetails)
            {
                sb.AppendLine($"[{detail.Name}]({detail.WowheadUrl})");
                sb.AppendLine($"\t*{detail.Description}*");
                sb.AppendLine();
            }

            sb.AppendLine($"[Leaderboard]({affixes.LeaderboardUrl.AbsoluteUri})");            
            embed.Description = sb.ToString();

            await RespondAsync(embed: embed.Build());                       
        }

        [SlashCommand("watchlogs", "watch logs for guild")]
        public async Task ToggleLogs()
        {
            bool enable = false;
            var embed = new EmbedBuilder();
            List<LogMonitoring> logMonitorList = null;
            StringBuilder sb = new StringBuilder();
            using (var db = new NinjaBotEntities())
            {
                logMonitorList = db.LogMonitoring.ToList();
            }
            if (logMonitorList != null)
            {
                var getGuild = logMonitorList.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (getGuild != null)
                {
                    if (!getGuild.MonitorLogs)
                    {
                        enable = true;
                    }
                }
                else
                {
                    enable = true;
                }
            }
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
            using (var db = new NinjaBotEntities())
            {
                var getGuild = db.LogMonitoring.Where(l => l.ServerId == (long)Context.Guild.Id).FirstOrDefault();
                if (getGuild != null)
                {
                    getGuild.ChannelId = (long)Context.Channel.Id;
                    getGuild.ChannelName = Context.Channel.Name;
                    getGuild.MonitorLogs = enable;
                }
                else
                {
                    db.LogMonitoring.Add(new LogMonitoring
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        ChannelId = (long)Context.Channel.Id,
                        ChannelName = Context.Channel.Name,
                        MonitorLogs = enable,
                        LatestLog = DateTime.Now
                    });
                }
                await db.SaveChangesAsync();
            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build());
        }
        [SlashCommand("wowdiscord", "list class discord servers")]
        public async Task ListWowDiscordServers()
        {
            try
            {
                List<WowResources> resourceList = null;
                using (var db = new NinjaBotEntities())
                {
                    resourceList = db.WowResources.Where(r => r.ResourceDescription == "Discord").ToList();
                }
                if (resourceList != null)
                {
                    var embed = new EmbedBuilder();
                    embed.Title = $"WoW Class Discord List";
                    foreach (var resource in resourceList)
                    {
                        embed.AddField(new EmbedFieldBuilder
                        {
                            Name = $"{resource.ClassName}",
                            Value = $"{resource.Resource}",
                            IsInline = true
                        });
                    }
                    embed.WithColor(new Color(0, 255, 0));
                    await RespondAsync(embed: embed.Build());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error listing channels: [{ex.Message}]");
                await RespondAsync($"Sorry, {Context.User.Username}, something went wrong :(");
            }
        }

        [SlashCommand("ksm", "Check a character for the Keystone Master achievement")]
        public async Task CheckKsm(string args = null)
        {
            var charInfo = await _wowUtils.GetCharFromArgs(args, Context);
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            bool ksm = false;
            embed.Title = "Keystone Master Achievement Check";
            if (!string.IsNullOrEmpty(charInfo.charName))
            {
                Character charAchievements = null;
                if (!string.IsNullOrEmpty(charInfo.regionName))
                {
                    charAchievements = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName, charInfo.regionName);
                }
                else
                {
                    charAchievements = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName);
                }
                if (charAchievements != null)
                {
                    foreach (var cheeve in charAchievements.achievements.achievementsCompleted)
                    {
                        if (cheeve == 11162)
                        {
                            ksm = true;
                        }
                    }
                }
                if (!ksm)
                {
                    sb.AppendLine($"**{charAchievements.name}** from **{charAchievements.realm}** does not have the Keystone Master achievement! :(");
                    embed.WithColor(new Color(255, 0, 0));
                }
                else
                {
                    sb.AppendLine($"**{charAchievements.name}** from **{charAchievements.realm}** has the Keystone Master achievement! :)");
                    embed.WithColor(new Color(0, 255, 0));
                }
                embed.ThumbnailUrl = charAchievements.thumbnailURL;
            }
            else
            {
                sb.AppendLine($"Sorry, unable to find that character!");
            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build());
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
            int maxReturn = 2;
            int arrayCount = 0;
            string discordGuildName = string.Empty;
            var guildInfo = Context.Guild;
            var embed = new EmbedBuilder();            

            guildObject = await _wowUtils.GetGuildName(Context);
            guildName = guildObject.guildName;
            realmName = guildObject.realmName.Replace("'", string.Empty);
            guildRegion = guildObject.regionName;
            locale = guildObject.locale;
            var realmInfo = new WowRealm.Realm();            
            if (!string.IsNullOrEmpty(locale))
            {
                switch (locale)
                {
                    case "en_US":
                    {
                        realmInfo = WowApi.RealmInfo.realms.FirstOrDefault(r => r.name == guildObject.realmName);                        
                        break;
                    }
                    case "en_GB":
                    {
                        realmInfo = WowApi.RealmInfoEu.realms.FirstOrDefault(r => r.name == guildObject.realmName);   
                        break;
                    }
                    case "ru_RU":
                    {
                        realmInfo = WowApi.RealmInfoRu.realms.FirstOrDefault(r => r.name == guildObject.realmName);   
                        break;
                    }
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
                   // arrayCount = guildLogs.Count - 1;
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
                    for (int i = 0; i <= (guildLogs.Count - 1) && i <= maxReturn; i++)
                    {
                        var startTime = _logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start);
                        var endTime   =  _logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end);
                        var wfUrl     = $"https://www.wipefest.net/report/{guildLogs[arrayCount].id}";
                        var wowAnUrl  = $"https://wowanalyzer.com/report/{guildLogs[arrayCount].id}";

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
                    await RespondAsync(embed: embed.Build());
                    return;
                }
                else if (guildLogs.Count == 1)
                {
                    sb.AppendLine($"[__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__]({guildLogs[0].reportURL})");
                    sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].start)}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].end)}**");
                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[0].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");
                    sb.AppendLine();
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build());
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
                    if (string.IsNullOrEmpty(locale))
                    {
                        guildLogs = await _logsApi.GetReportsFromGuild(guildName: guildName, realm: realmName, region: guildRegion);
                    }
                    else
                    {
                        guildLogs = await _logsApi.GetReportsFromGuild(guildName: guildName, realm: realmName, region: guildRegion, locale: locale, realmSlug: guildObject.realmSlug);
                    }
                    //arrayCount = guildLogs.Count - 1;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs for **{guildName}** on **{realmName}**");
                    _logger.LogError($"{ex.Message}");
                    await RespondAsync(sb.ToString());
                    return;
                }
                if (guildLogs.Count > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i <= (guildLogs.Count - 1) && i <= maxReturn; i++)
                    {
                        DateTime startTime = DateTime.UtcNow;
                        DateTime endTime = DateTime.UtcNow;

                        if (realmInfo != null && !string.IsNullOrEmpty(realmInfo.timezone))
                        {                            
                            startTime = _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start), realmInfo.timezone);
                            endTime =  _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end), realmInfo.timezone);
                        }
                        else 
                        {
                            startTime = _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start));
                            endTime =  _logsApi.ConvTimeToLocalTimezone(_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end));
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
                    await RespondAsync(embed: embed.Build());
                }
                else if (guildLogs.Count == 1)
                {
                    sb.AppendLine($"[__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__]({guildLogs[0].reportURL})");
                    sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].start)}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].end)}**");
                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{guildLogs[0].id}) | :sob: [WipeFest](https://www.wipefest.net/report/{guildLogs[arrayCount].id})");
                    sb.AppendLine($"\t");
                    sb.AppendLine();
                    _logger.LogInformation($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await RespondAsync(embed: embed.Build());
                }
                else
                {
                    embed.Title = $"Unable to find logs for {guildName} on {realmName} ({guildRegion})";
                    embed.Description = $"**{Context.User.Username}**, ensure you've uploaded the logs as attached to **{guildName}** on http://www.warcraftlogs.com \n";
                    embed.Description += $"More information: http://www.wowhead.com/guides/raiding/warcraft-logs";
                    await RespondAsync(embed: embed.Build());
                }
            }
        }

        [SlashCommand("setguild", "Sets a realm/guild association for a Discord server")]
        public async Task SetGuild(string args = "")
        {
            string realmName = string.Empty;
            string guildName = string.Empty;
            string region = string.Empty;
            string locale = string.Empty;

            if (args.Contains(',') && !string.IsNullOrEmpty(args))
            {
                switch (args.Split(',').Count())
                {
                    case 2:
                        {
                            realmName = args.Split(',')[0].ToString().Trim();
                            guildName = args.Split(',')[1].ToString().Trim();
                            region = "us";                            
                            break;
                        }
                    case 3:
                        {
                            realmName = args.Split(',')[0].ToString().Trim();
                            guildName = args.Split(',')[1].ToString().Trim();
                            region = args.Split(',')[2].ToString().Trim().ToLower();
                            break;
                        }
                }
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Please specify realm, guild, and locale (supported locales: us (default)/eu/ru)!");
                sb.AppendLine($"Example: /set-guild Destromath, NinjaBread Men");
                sb.AppendLine($"Example: /set-guild Silvermoon, Rome in a Day, eu");
                sb.AppendLine($"Example: /set-guild Ревущий фьорд, Порейдим месяц, ru");
                await RespondAsync(sb.ToString());
                return;
            }
            locale = _wowUtils.GetLocaleFromRegion(ref region);
            string discordGuildName = string.Empty;
            try
            {
                if (Context.Channel is IDMChannel)
                {
                    discordGuildName = Context.Channel.Name;
                }
                else if (Context.Channel is IGuildChannel)
                {
                    discordGuildName = Context.Guild.Name;
                    if (!((IGuildUser)Context.User).GuildPermissions.KickMembers) return;
                }
                GuildMembers members = null;
                try
                {
                    await DeferAsync();
                    members = _wowApi.GetGuildMembers(realmName, guildName, locale: locale, regionName: region);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error getting guild info -> [{ex.Message}]");
                }

                if (members != null)
                {
                    guildName = members.guild.name;
                    realmName = members.guild.realm.slug;
                    await FollowupAsync("Looking up realm information, hang tight!");
                    await _wowUtils.SetGuildAssociation(guildName, realmName, locale: locale, regionName: region, context: Context);
                    await FollowupAsync("Guild info set! Try /getguild");
                    //await GetGuild();
                }
                else
                {
                    await RespondAsync($"Unable to associate guild/realm (**{guildName}**/**{realmName}**) (region {region}) to **{discordGuildName}** (typo?)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Set-Guild error: {ex.Message}");
            }
        }

        [SlashCommand("getguild", "Report Discord Server -> Guild Association")]
        public async Task GetGuild()
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
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

            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);

            title = $"Guild association for **{discordGuildName}**";

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            if (guildObject.guildName != null || members != null)
            {
                try
                {
                    await DeferAsync();
                    if (!string.IsNullOrEmpty(guildObject.locale))
                    {
                        members = _wowApi.GetGuildMembersBySlug(guildObject.realmName, guildObject.guildName, locale: guildObject.locale, regionName: guildObject.regionName);
                    }
                    else
                    {
                        members = _wowApi.GetGuildMembersBySlug(guildObject.realmName, guildObject.guildName, regionName: guildObject.regionName);
                    }                                        
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Get-Guild Error getting guild info: [{ex.Message}]");
                }
                string guildName = string.Empty;
                string guildRealm = string.Empty;
                string guildRegion = string.Empty;
                string faction = string.Empty;
                //string battlegroup = members.battlegroup;
                //int achievementPoints = members.achievementPoints;
                switch (members.guild.faction._type)
                {
                    case "ALLIANCE":
                        {
                            faction = "Alliance";
                            embed.WithColor(new Color(0, 0, 255));
                            break;
                        }
                    case "HORDE":
                        {
                            faction = "Horde";
                            embed.WithColor(new Color(255, 0, 0));
                            break;
                        }
                }
                guildName = members.guild.name;
                guildRealm = members.guild.realm.slug;
                guildRegion = guildObject.regionName;
                sb.AppendLine($"Guild Name: **{guildName}**");
                sb.AppendLine($"Realm Name: **{guildRealm}**");
                sb.AppendLine($"Members: **{members.members.Count().ToString()}**");
                //sb.AppendLine($"Battlegroup: **{battlegroup}**");
                sb.AppendLine($"Faction: **{faction}**");
                sb.AppendLine($"Region: **{guildRegion}**");
                //sb.AppendLine($"Achievement Points: **{achievementPoints.ToString()}**");
            }
            else
            {
                sb.AppendLine($"No guild association found for **{discordGuildName}**!");
                sb.AppendLine($"Please use /set-guild realmName, guild name, region (optional, defaults to US, valid values are eu or us) to associate a guild with **{discordGuildName}**");
            }
            embed.Description = sb.ToString();
            await FollowupAsync(embed: embed.Build());
        }

        [SlashCommand("wow", "Use this combined with rankings (gets guild rank from WoWProgress")]
        public async Task GetRanking(string args = null)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            string regionName = "us";
            await DeferAsync();
            if (string.IsNullOrEmpty(args))
            {
                guildObject = await _wowUtils.GetGuildName(Context);
                guildName = guildObject.guildName;
                realmName = guildObject.realmName;
                regionName = guildObject.regionName;
            }
            else
            {
                if (args.Contains(','))
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
                                regionName = args.Split(',')[2].ToString().Trim();
                                break;
                            }
                    }
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    var embed = new EmbedBuilder();
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Title = $"Unable to find a guild/realm association!\nTry /wow rankings Realm Name, Guild Name";
                    sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                    sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                    embed.Description = sb.ToString();
                    await FollowupAsync(embed: embed.Build());
                    return;
                }
            }
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
            {
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $"Unable to find a guild/realm association!\nTry /wow rankings Realm Name, Guild Name";
                sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await FollowupAsync(embed: embed.Build());
                return;
            }
            try
            {
                var guildMembers = _wowApi.GetGuildMembers(realmName, guildName, regionName);
                int memberCount = 0;
                if (guildMembers != null)
                {
                    guildName = guildMembers.guild.name;
                    realmName = guildMembers.guild.realm.slug;
                    memberCount = guildMembers.members.Count();
                }
                var wowProgressApi = new WowProgress();
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 255, 0));
                var ranking = wowProgressApi.GetGuildRank(guildName, realmName, regionName);
                var realmObject = wowProgressApi.GetRealmObject(realmName, wowProgressApi._links, regionName);
                var topGuilds = realmObject.OrderBy(r => r.realm_rank).Take(3);
                var guild = realmObject.Where(r => r.name.ToLower() == guildName.ToLower()).FirstOrDefault();
                int guildRank = guild.realm_rank;
                var surroundingGuilds = realmObject.Where(r => r.realm_rank > (guild.realm_rank - 2) && r.realm_rank < (guild.realm_rank + 2));

                embed.Title = $"__:straight_ruler:Guild ranking for **{guildName}** [**{memberCount}** members] (Score: **{ranking.score}**):straight_ruler:__";
                sb.AppendLine($"Realm rank: **{ranking.realm_rank}** **|** World rank: **{ranking.world_rank}** **|** Area rank: **{ranking.area_rank}**");
                sb.AppendLine();
                sb.AppendLine($"__Where **{guildName}** fits in on **{realmName}**__");
                foreach (var singleGuild in surroundingGuilds)
                {
                    sb.AppendLine($"\t(**{singleGuild.realm_rank}**) **{singleGuild.name}** **|** World rank: **{singleGuild.world_rank}**");
                }
                sb.AppendLine();
                sb.AppendLine($"__:top:Top 3 guilds on **{realmName}**:top:__");
                foreach (var topGuild in topGuilds)
                {
                    sb.AppendLine($"\t(**{topGuild.realm_rank}**) **{topGuild.name}** **|** World Rank: **{topGuild.world_rank}**");
                }
                sb.AppendLine();
                sb.AppendLine("Ranking data gathered via **WoWProgress.com**");
                embed.WithUrl($"{guild.url}");
                embed.Description = sb.ToString();

                await FollowupAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex.Message} {ex.InnerException} {ex.Data}{ex.Source}{ex.StackTrace}");
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $":frowning: Sorry, {Context.User.Username}, something went wrong! Perhaps check the guild's home realm.:frowning: ";
                sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await FollowupAsync(embed: embed.Build());
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

            //Attempt to get guild info
            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);
            string realmName = guildObject.realmName.Replace("'", string.Empty);
            string guildName = guildObject.guildName;
            region = guildObject.regionName;

            var fightList = WarcraftLogs.Zones.Where(z => z.id == WarcraftLogs.CurrentRaidTier.WclZoneId)
                                                .Select(z => z.encounters)
                                                .FirstOrDefault();

            raidName = WarcraftLogs.CurrentRaidTier.RaidName;

            //Get Guild Information for Discord Server (or channel for DM)
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
            //Argument logic
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
                await RespondAsync(embed: embed.Build());
                return;
            }
            else
            {
                if (args.Split(' ')[0].ToLower() == "list")
                {
                    //list fights here
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
                        await RespondAsync(embed: embed.Build());
                    }
                    return;
                }

                await DeferAsync();

                //set default difficulty
                difficulty = "heroic";

                //handle args
                int argCount = args.Split(',').Count();                
                string[] splitArgs = args.Split(',');
                switch (argCount)
                {
                    //Just name
                    case 1:
                        {
                            fightName = splitArgs[0].Trim();
                            break;
                        }
                    //Name + metric
                    case 2:
                        {
                            fightName = splitArgs[0].Trim();                            
                            guildOnly = splitArgs[1].Trim();
                            break;
                        }
                    //Name + metric + guild/all
                    case 3:
                        {
                            fightName = splitArgs[0].Trim();
                            guildOnly = splitArgs[1].Trim();
                            metric = splitArgs[2].Trim();
                            break;
                        }
                    //Name + metric + guild/all + difficulty
                    case 4:
                        {
                            fightName = splitArgs[0].Trim();
                            guildOnly = splitArgs[1].Trim();
                            metric = splitArgs[2].Trim();
                            difficulty = splitArgs[3].Trim();
                            break;
                        }
                }
                //Difficulty logic
                int difficultyID = 4;
                switch (difficulty.ToLower())
                {
                    case "lfr":
                        {
                            difficultyID = 1;
                            break;
                        }
                    case "flex":
                        {
                            difficultyID = 2;
                            break;
                        }
                    case "normal":
                        {
                            difficultyID = 3;
                            break;
                        }
                    case "heroic":
                        {
                            difficultyID = 4;
                            break;
                        }
                    case "mythic":
                        {
                            difficultyID = 5;
                            break;
                        }
                }
                //End difficulty logic
                //Get the list of fights that pertain to the specific zone id. 11 == Nighthold (True)
                //Begin fight logic                
                WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
                if (fightName.Length <= 2)
                {
                    switch (fightName)
                    {
                        case "1":
                            {
                                encounterID = fightList[0].id;
                                break;
                            }
                        case "2":
                            {
                                encounterID = fightList[1].id;
                                break;
                            }
                        case "3":
                            {
                                encounterID = fightList[2].id;
                                break;
                            }
                        case "4":
                            {
                                encounterID = fightList[3].id;
                                break;
                            }
                        case "5":
                            {
                                encounterID = fightList[4].id;
                                break;
                            }
                        case "6":
                            {
                                encounterID = fightList[5].id;
                                break;
                            }
                        case "7":
                            {
                                encounterID = fightList[6].id;
                                break;
                            }
                        case "8":
                            {
                                encounterID = fightList[7].id;
                                break;
                            }
                        case "9":
                            {
                                encounterID = fightList[8].id;
                                break;
                            }
                        case "10":
                            {
                                encounterID = fightList[9].id;
                                break;
                            }
                        case "11":
                            {
                                encounterID = fightList[10].id;
                                break;
                            }
                    }
                }
                else
                {
                    encounterID = _wowUtils.GetEncounterID(fightName);
                }
                //End fight logic               
                //Begin metric set
                string metricEmoji = string.Empty;
                if (string.IsNullOrEmpty(metric))
                {
                    metric = "dps";
                }
                switch (metric.ToLower())
                {
                    case "hps":
                        {
                            embed.WithColor(new Color(0, 255, 0));
                            metricEmoji = ":green_heart:";
                            break;
                        }
                    case "dps":
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            metricEmoji = ":dagger: ";
                            break;
                        }
                    default:
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            metricEmoji = ":dagger: ";
                            metric = "dps";
                            break;
                        }
                }
                //End metric set

                if (string.IsNullOrEmpty(fightName))
                {
                    sb.AppendLine($"{Context.User.Username}, please specify a fight name/number!");
                    sb.AppendLine($"**Example:** /top10 1");
                    sb.AppendLine($"**Encounter Lists:** /top10 list");
                    await FollowupAsync(sb.ToString());
                    return;
                }

                IEnumerable<WarcraftlogRankings.Ranking> top10 = null;
                var guildOnlyList = new List<WarcraftlogRankings.RankingObject>();

                //Guild logic
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
                                        realmSlug:   guildObject.realmSlug, 
                                        guildName:   guildObject.guildName,
                                        page:        page.ToString(),
                                        metric:      metric, 
                                        difficulty:  difficultyID, 
                                        regionName:  region
                                        //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()
                                    );                   
                            }
                            else
                            {
                                l = await _logsApi.GetRankingsByEncounterGuild(
                                        encounterID: encounterID, 
                                        realmName:   guildObject.realmName, 
                                        guildName:   guildObject.guildName, 
                                        page:        page.ToString(),
                                        metric:      metric, 
                                        difficulty:  difficultyID, 
                                        regionName:  region
                                        //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()                                   
                                    );                                          
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
                else //else for non-guild (all realm top 10)
                {
                    if (!string.IsNullOrEmpty(guildObject.realmSlug))
                    {
                        l = await _logsApi.GetRankingsByEncounterSlug(
                                encounterID: encounterID, 
                                realmSlug: guildObject.realmSlug, 
                                metric: metric,
                                difficulty: difficultyID, 
                                regionName: region
                                //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()
                            );
                    }
                    else
                    {
                        l = await _logsApi.GetRankingsByEncounter(
                                encounterID: encounterID, 
                                realmName: realmName, 
                                metric: metric, 
                                difficulty: difficultyID, 
                                regionName: region
                                //partition: WarcraftLogs.CurrentRaidTier.Partition.ToString()
                            );
                    }

                   top10 = l.rankings.OrderByDescending(a => a.total).Take(10);                        

                }

                //Setup the results for the embed
                string difficultyName = string.Empty;
                switch (difficultyID)
                {
                    case 1:
                        {
                            difficultyName = "LFR";
                            break;
                        }
                    case 2:
                        {
                            difficultyName = "Flex";
                            break;
                        }
                    case 3:
                        {
                            difficultyName = "Normal";
                            break;
                        }
                    case 4:
                        {
                            difficultyName = "Heroic";
                            break;
                        }
                    case 5:
                        {
                            difficultyName = "Mythic";
                            break;
                        }
                }

                string fightNameFromEncounterID = fightList.Where(f => f.id == encounterID).Select(f => f.name).FirstOrDefault();

                //Build embed
                embed.Title = $"__Top 10 for fight [**{fightNameFromEncounterID}** (Metric [**{metric.ToUpper()}**] Difficulty [**{difficultyName}**]) Realm [**{guildObject.realmName}**]]__";

                int i = 1;
                if (top10 != null)
                {
                    foreach (var rank in top10)
                    {
                        var classInfo = WarcraftLogs.CharClasses.Where(c => c.id == rank._class).FirstOrDefault();
                        sb.AppendLine($"**{i}** [{rank.name}](http://{region}.battle.net/wow/en/character/{rank.serverName.Replace(" ","-")}/{rank.name}/advanced) ilvl **{rank.itemLevel}** {classInfo.name} from *[{rank.guildName}]*");
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
                    await FollowupAsync(embed: embed.Build());
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
            var vids = new List<WowResources>();
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.WithColor(0,255,0);
            embed.WithFooter(new EmbedFooterBuilder{
                Text = $"Good luck and have fun!"
            });
            embed.ThumbnailUrl = "https://vignette.wikia.nocookie.net/wowwiki/images/1/17/Jainaunit.JPG/revision/latest?cb=20080826081813";
            var fightList = WarcraftLogs.Zones.Where(z => z.id == WarcraftLogs.CurrentRaidTier.WclZoneId)
                .Select(z => z.encounters)
                .FirstOrDefault();                
            embed.Title = $"Raid Videos for {WarcraftLogs.CurrentRaidTier.RaidName}";
            using (var db = new NinjaBotEntities())
            {
                vids = db.WowResources.Where(r => r.ResourceDescription == "raidvid").ToList();
            }
            if (vids != null)
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
            else
            {

            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build());            
        }

        [SlashCommand("realminfo", "Return WoW realm information")]
        public async Task GetRealmInfo(string args = "")
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            var guildInfo = await _wowUtils.GetGuildName(Context);
            string region = string.Empty;
            string findMe = string.Empty;
            findMe = args;
            await DeferAsync();
            
            if (!string.IsNullOrEmpty(guildInfo.regionName))
            {
                region = guildInfo.regionName;
            }
            else
            {
                region = "us";
            }

            if (!string.IsNullOrEmpty(guildInfo.realmName) && string.IsNullOrEmpty(findMe))
            {
                findMe = guildInfo.realmName;
            }
            var getRealmList = _wowApi.GetRealmStatus(region);
            var foundRealm = getRealmList.realms.Where(r => r.slug.ToLower().Contains(findMe.ToLower())).FirstOrDefault();
            if (foundRealm != null)
            {
                embed.Title = $"Realm Information for {foundRealm.name}!";
                sb.AppendLine($":black_small_square: Battlegroup: **{foundRealm.battlegroup}**");
                sb.AppendLine($":black_small_square: Type: **{foundRealm.type}**");
                sb.AppendLine($":black_small_square: Locale: **{foundRealm.locale}**");
                sb.AppendLine($":black_small_square: Population: **{foundRealm.population}**");
                sb.AppendLine($":black_small_square: Status: **{foundRealm.status}**");
                sb.AppendLine($":black_small_square: TimeZone: **{foundRealm.timezone}**");
                sb.AppendLine($":black_small_square: Queue: **{foundRealm.queue}**");
                sb.AppendLine($":black_small_square: Connected Realms:");
                foreach (var realm in foundRealm.connected_realms)
                {
                    sb.AppendLine($"\t :black_small_square: **{realm}**");
                }
            }
            if (foundRealm.status)
            {
                embed.WithColor(new Color(0, 255, 0));
            }
            else
            {
                embed.WithColor(new Color(255, 0, 0));
            }
            embed.Description = sb.ToString();
            await FollowupAsync(embed: embed.Build());
        }   

        [SlashCommand("yoink", "grab users from one voice channel and yoink them into another")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task Yoink(SocketVoiceChannel to, SocketVoiceChannel from)
        {
            var usersToMove = from.Users;
            var numUsers = from.Users.Count;
            foreach (var user in usersToMove)
            {
                await user.ModifyAsync(u =>
                {
                    u.Channel = to;
                });
                Thread.Sleep(750);
            }
            var message = $"Yoinked [{numUsers}] users from [{from.Name}] to [{to.Name}]!";
            await RespondAsync(message);
        }

        [SlashCommand("member", "give user the member role")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task AddMemberRole(IGuildUser user)
        {
            var serverRoles  = Context.Guild.Roles;
            var userRoles    = user.RoleIds;

            var memberRole   = serverRoles.Where(r => r.Name.ToLower() == "member").FirstOrDefault();
            var raiderRole   = serverRoles.Where(r => r.Name.ToLower() == "raider").FirstOrDefault();

            if (memberRole == null)
            {
                await RespondAsync($"Could not find the [**Member**] role, please add it if you'd like to use this command!");
                return;
            }

            var memberRoleId = memberRole.Id;
            var isMember     = userRoles.Where(u => u == memberRoleId).FirstOrDefault();
            var embed        = new EmbedBuilder();
            var sb           = new StringBuilder();

            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            embed.Title        = $"User role change for [{user.Username}]";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            if (isMember != 0)
            {
                if (raiderRole != null && userRoles.Where(r => r == raiderRole.Id).FirstOrDefault() != 0)
                {
                    await user.RemoveRoleAsync(raiderRole);
                }
                await user.RemoveRoleAsync(memberRole);        
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"Member role removed </3");                
                embed.WithColor(255, 0, 0);                      
            }
            else
            {
                await user.AddRoleAsync(memberRole);                
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"You should now be able to see more channels, welcome to [**{Context.Guild.Name}**]");                
                embed.WithColor(0, 255, 0);                         
            }  
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build());                           
        }

        [SlashCommand("raider", "give user the raider role")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task AddRaiderRole(IGuildUser user)
        {
            var serverRoles  = Context.Guild.Roles;
            var userRoles    = user.RoleIds;
            var guild        = (IGuild)Context.Guild;
            var channels     = await guild.GetTextChannelsAsync();
            var raidCat      = guild.GetCategoriesAsync().Result.Where(c => c.Name.ToLower() == "raiding").FirstOrDefault();            
            var raiderRole   = serverRoles.Where(r => r.Name.ToLower() == "raider").FirstOrDefault();
                        
            if (raiderRole == null)
            {
                await RespondAsync($"Could not find the [**Raider**] role, please add it if you'd like to use this command!");
                return;
            }

            ITextChannel signUpChannel = null;
            ITextChannel stratChannel  = null;
            ITextChannel addonChannel  = null;
            ITextChannel logsChannel   = null;

            if (raidCat != null)
            {                
                signUpChannel = channels.Where(c => c.Name.ToLower() == "sign-up" && c.CategoryId == raidCat.Id).FirstOrDefault();
                stratChannel  = channels.Where(c => c.Name.ToLower() == "strategy" && c.CategoryId == raidCat.Id).FirstOrDefault();
                addonChannel  = channels.Where(c => c.Name.ToLower() == "addons" && c.CategoryId == raidCat.Id).FirstOrDefault();
                logsChannel   = channels.Where(c => c.Name.ToLower() == "logs" && c.CategoryId == raidCat.Id).FirstOrDefault();
            }

            var raiderRoleId = raiderRole.Id;
            var isRaider     = userRoles.Where(u => u == raiderRoleId).FirstOrDefault();
            var embed        = new EmbedBuilder();
            var sb           = new StringBuilder();
            
            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            embed.Title        = $"User role change for [{user.Username}]";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            if (isRaider != 0)
            {
                await user.RemoveRoleAsync(raiderRole);        
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"Raider role removed </3");                
                embed.WithColor(255, 0, 0);
                      
            }
            else
            {
                await user.AddRoleAsync(raiderRole);                
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"You should now be able to see raiding channels, welcome to the [**back2back mirror fam**]");   
                sb.AppendLine();
                sb.AppendLine("<:b2bm:710554622452039731>"); 
                sb.AppendLine();
                if (signUpChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Raid sign-ups are announced in [{signUpChannel.Mention}]");
                }  
                if (addonChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Mandatory addons for raiding are located in [{addonChannel.Mention}]");
                }
                if (stratChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Strats are posted in [{stratChannel.Mention}]");                
                }         
                if (logsChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Logs and WoWAnalyzer/Wipefest links are located in [{logsChannel.Mention}]");                
                }                           
                embed.WithColor(0, 255, 0);                         
            }  
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build());                           
        }        
        
        [SlashCommand("listmythic", "list mythic raiders")]
        public async Task ListMythicRaiders()
        {
            var serverRoles      = Context.Guild.Roles;
            var mythicRole       = serverRoles.Where(r => r.Name.ToLower() == "mythic raider").FirstOrDefault();
            var mythicBackupRole = serverRoles.Where(r => r.Name.ToLower() == "mythic backup").FirstOrDefault();
            var guild            = (IGuild)Context.Guild;
            var guildMembers     = await guild.GetUsersAsync();
            var mythicRaiders    = guildMembers.Where(m => m.RoleIds.Contains(mythicRole.Id)).ToList();  
            var mythicBackups    = guildMembers.Where(m => m.RoleIds.Contains(mythicBackupRole.Id)).ToList();
            var sb               = new StringBuilder();

            foreach (var raider in mythicRaiders)
            {                
                if (!string.IsNullOrEmpty(raider.Nickname))
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**{raider.Nickname}**]");
                }
                else
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**none set**]");
                }                
            }

            sb.AppendLine("");
            sb.AppendLine($"Total [{mythicRaiders.Count}]");
            sb.AppendLine("");
            
            sb.AppendLine("__Backups__");
            foreach (var raider in mythicBackups)
            {                
                if (!string.IsNullOrEmpty(raider.Nickname))
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**{raider.Nickname}**]");
                }
                else
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**none set**]");
                }                
            }

            sb.AppendLine("");
            sb.AppendLine($"Total [{mythicBackups.Count}]");

            var embed = new EmbedBuilder();
            embed.Color = new Color(0, 255, 0);
            embed.Title = $"Mythic Raiders in [{Context.Guild.Name}]";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.Description = sb.ToString();            
            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            await RespondAsync(embed: embed.Build());
        }            
    }
}
