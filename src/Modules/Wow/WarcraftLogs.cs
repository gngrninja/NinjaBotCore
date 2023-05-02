using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using NinjaBotCore.Database;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NodaTime.TimeZones;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Wow
{
    public class WarcraftLogs
    {
        private static CancellationTokenSource _tokenSource;
        private static List<Zones> _zones;
        private static List<Zones> _classicZones;
        private static List<CharClasses> _charClasses;
        private readonly IConfigurationRoot _config;
        private DiscordShardedClient _client;
        private readonly WclApiRequestor _api;
        private readonly WclApiRequestor _apiCmd;        
        private readonly WclApiRequestor _apiClassicCmd;        
        private readonly ILogger _logger;
        private static CurrentRaidTier _currentRaidTier;
        private readonly WclApiRequestor _apiClassic;
        private readonly WowApi _wowApi;

        public WarcraftLogs(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WarcraftLogs>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _config = services.GetRequiredService<IConfigurationRoot>(); 
            _wowApi = services.GetRequiredService<WowApi>();
                        
            try 
            {   
                _api = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://www.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());             
                _apiCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://www.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient()); 
                _apiClassic = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://classic.warcraftlogs.com:443/v1/" , services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiClassicCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://classic.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                CharClasses = this.GetCharClasses().Result;
                Zones = this.GetZones().Result;
                ClassicZones = this.GetClassicZones().Result;
                
                _currentRaidTier = this.SetCurrentTier();
                //this.MigrateOldReports();
                this.StartTimer();                
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error performing class setup for WCL: {ex.Message}");
            }
        }

        public static List<Zones> Zones
        {
            get
            {
                return _zones;
            }
            private set
            {
                _zones = value;
            }
        }

        public static List<Zones> ClassicZones
        {
            get
            {
                return _classicZones;
            }
            private set
            {
                _classicZones = value;
            }
        }

        public static CancellationTokenSource TokenSource
        {
            get
            {
                return _tokenSource;
            }
            set
            {
                _tokenSource = value;
            }
        }

        public static List<CharClasses> CharClasses
        {
            get
            {
                return _charClasses;
            }
            private set
            {
                _charClasses = value;
            }
        }

        public static CurrentRaidTier CurrentRaidTier
        {
            get
            {
                return _currentRaidTier;
            } 
            set 
            {   
                _currentRaidTier = value;
            }
        }

        public async Task<List<CharClasses>> GetCharClasses()
        {
            return await _api.Get<List<CharClasses>>("classes?");
        }

        public async Task<List<Zones>> GetZones()
        {
            string url = string.Empty;
            url = $"zones?";
            return await _api.Get<List<Zones>>(url);
        }

        public async Task<List<Zones>> GetClassicZones()
        {
            string url = string.Empty;
            url = $"zones?";
            return await _apiClassic.Get<List<Zones>>(url);
        }

        public async Task<List<Reports>> GetReportsFromGuildClassic(string guildName, string realm, string region, bool isList = false, bool flip = false)
        {
            string url = string.Empty;           
            url = $"reports/guild/{guildName.Replace(" ", "%20")}/{realm}/{region.ToLower()}?";
            if (flip) 
            {
                return await _apiClassic.Get<List<Reports>>(url);
            }
            else
            {
                return await _apiClassicCmd.Get<List<Reports>>(url);
            } 
        }

        public async Task<List<Reports>> GetReportsFromGuild(string guildName, string realm, string region, bool isList = false, bool flip = false)
        {
            string url = string.Empty;
            string realmSlug = string.Empty;
            switch (region.ToLower())
            {
                case "us":
                    {
                        realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realm.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "eu":
                    {
                        realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realm.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
            }               
            url = $"reports/guild/{guildName.Replace(" ", "%20")}/{realmSlug}/{region}?";
            if (flip) 
            {
                return await _api.Get<List<Reports>>(url);
            }
            else
            {
                return await _apiCmd.Get<List<Reports>>(url);
            } 
        }

        public async Task<List<Reports>> GetReportsFromGuild(string guildName, string realm, string locale, string region, bool isList = false, bool flip = false)
        {
            string url = string.Empty;
            string realmSlug = string.Empty;
            switch (locale)
            {
                case "en_US":
                    {
                        realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realm.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "ru_RU":
                    {                    
                        realmSlug = WowApi.RealmInfoRu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realm.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "en_GB":
                    {
                        realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realm.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
            }            
            url = $"reports/guild/{guildName.Replace(" ", "%20")}/{realmSlug}/{region}?";
            if (flip) 
            {
                return await _api.Get<List<Reports>>(url);
            }
            else
            {
                return await _apiCmd.Get<List<Reports>>(url);
            } 
        }

        public async Task<List<Reports>> GetReportsFromGuild(string guildName, string realm, string locale, string region, string realmSlug , bool isList = false, bool flip = false)
        {
            string url = string.Empty;         
            url = $"reports/guild/{guildName.Replace(" ", "%20")}/{realmSlug}/{region}?";
            if (flip) 
            {
                return await _api.Get<List<Reports>>(url);
            }
            else
            {
                return await _apiCmd.Get<List<Reports>>(url);
            } 
        }

        public async Task<List<Reports>> GetReportsFromUser(string userName)
        {
            string url = string.Empty;
            url = $"reports/user/{userName.Replace(" ", "%20")}?";
            return await _api.Get<List<Reports>>(url);
        }

        public async Task<List<CharParses>> GetParsesFromCharacterName(string charName, string realm, string region = "us")
        {
            string url = string.Empty;
            url = $"parses/character/{charName}/{realm}/{region}?";
            return await _apiCmd.Get<List<CharParses>>(url);
        }

        public async Task<List<LogCharRankings>> GetRankingFromCharName(string charName, string realm, string region = "us")
        {
            string url = string.Empty;
            url = $"rankings/character/{charName}/{realm}/{region}?";
            return await _apiCmd.Get<List<LogCharRankings>>(url);
        }

        public async Task<List<LogCharRankings>> GetRankingFromCharName(string charName, string realm, string zone, string region = "us")
        {
            string url = string.Empty;
            int zoneID = 0;
            string findString = string.Empty;
            switch (zone)
            {
                case "en":
                    {
                        findString = "Emerald Nightmare";
                        break;
                    }
                case "tov":
                    {
                        findString = "Trial of Valor";
                        break;
                    }
                case "nh":
                    {
                        findString = "The Nighthold";
                        break;
                    }
                case "tos":
                    {
                        findString = "Tomb of Sargeras";
                        break;
                    }
            }
            zoneID = Zones.Where(z => z.name == findString).Select(z => z.id).FirstOrDefault();
            url = $"rankings/character/{charName}/{realm}/{region}?zone={zoneID}&";
            return await _apiCmd.Get<List<LogCharRankings>>(url);
        }

        public async Task<Fights> GetFights(string code)
        {
            string url = string.Empty;
            url = $"report/fights/{code}?";
            return await _apiCmd.Get<Fights>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounter(int encounterID, string realmName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            string realmSlug = string.Empty;
            switch (regionName.ToLower())
            {
                case "us":
                    {
                        realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "eu":
                    {
                        realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
            }                        
            string url = $"rankings/encounter/{encounterID}?metric={metric}&server={realmSlug}&region={regionName}&difficulty={difficulty}&page={page}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounter(int encounterID, string realmName, string partition, string realmSlug, string page = "1",string metric = "dps", int difficulty = 4, string regionName = "us")
        {               
            WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
            string url = $"rankings/encounter/{encounterID}?metric={metric}&server={realmSlug}&region={regionName}&difficulty={difficulty}&page={page}&partition={partition}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterSlug(int encounterID, string realmSlug, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();                   
            string url = $"rankings/encounter/{encounterID}?metric={metric}&server={realmSlug}&region={regionName}&difficulty={difficulty}&page={page}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
            
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterSlug(int encounterID, string realmSlug, string partition, string page = "1" ,string metric = "dps", int difficulty = 4, string regionName = "us")
        {                 
            string url = $"rankings/encounter/{encounterID}?metric={metric}&server={realmSlug}&region={regionName}&difficulty={difficulty}&page={page}&partition={partition}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuild(int encounterID, string realmName, string guildName, string partition, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            guildName = guildName.Replace(" ", "%20");
            string realmSlug = string.Empty;
            switch (regionName.ToLower())
            {
                case "us":
                    {
                        realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "eu":
                    {
                        realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
            }
            string url = $"rankings/encounter/{encounterID}?guild={guildName}&server={realmSlug}&region={regionName}&metric={metric}&difficulty={difficulty}&page={page}&partition={partition}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuild(int encounterID, string realmName, string guildName, string page = "1",string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            guildName = guildName.Replace(" ", "%20");
            string realmSlug = string.Empty;
            switch (regionName.ToLower())
            {
                case "us":
                    {
                        realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "eu":
                    {
                        realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
            }
            string url = $"rankings/encounter/{encounterID}?guild={guildName}&server={realmSlug}&region={regionName}&metric={metric}&difficulty={difficulty}&page={page}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuildSlug(int encounterID, string realmSlug, string guildName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            guildName = guildName.Replace(" ", "%20");
            string url = $"rankings/encounter/{encounterID}?guild={guildName}&server={realmSlug}&region={regionName}&metric={metric}&difficulty={difficulty}&page={page}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuildSlug(int encounterID, string realmSlug, string partition, string guildName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            guildName = guildName.Replace(" ", "%20");
            string url = $"rankings/encounter/{encounterID}?guild={guildName}&server={realmSlug}&region={regionName}&metric={metric}&difficulty={difficulty}&page={page}&partition={partition}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }
        
        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounter(int encounterID, string realmName, string partition, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            string realmSlug = string.Empty;
            switch (regionName.ToLower())
            {
                case "us":
                    {
                        realmSlug = WowApi.RealmInfo.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
                case "eu":
                    {
                        realmSlug = WowApi.RealmInfoEu.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.ToLower())).Select(s => s.slug).FirstOrDefault();
                        break;
                    }
            }            
            string url = $"rankings/encounter/{encounterID}?metric={metric}&server={realmSlug}&region={regionName}&difficulty={difficulty}&page={page}&partition={partition}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddMilliseconds(unixTimeStamp).ToUniversalTime();
            return dtDateTime;
        }

        public DateTime ConvTimeToLocalTimezone(DateTime time, string timezone = "America/Los_Angeles")
        {
            TimeZoneInfo tzInfo;
            DateTime date = new DateTime();
            
            try 
            {
                tzInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                date = TimeZoneInfo.ConvertTimeFromUtc(time, tzInfo);
            }
            catch (TimeZoneNotFoundException)
            {
                var map = TzdbDateTimeZoneSource.Default.WindowsMapping.MapZones.FirstOrDefault(x =>
                    x.TzdbIds.Any(z => z.Equals(timezone, StringComparison.OrdinalIgnoreCase)));
                date = TimeZoneInfo.ConvertTimeFromUtc(time, TimeZoneInfo.FindSystemTimeZoneById(map.WindowsId));                                    
            }
            finally 
            {
                if (date == DateTime.MinValue)
                {
                    date = TimeZoneInfo.ConvertTimeFromUtc(time, TimeZoneInfo.Local);
                }
            }
            return date;
        }

        public async Task WarcraftLogsTimer(Action action, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {                             
                    action();   
                    await Task.Delay(TimeSpan.FromSeconds(1800),token);                                     
                }
            }
            catch (TaskCanceledException ex)
            {                            
            }
        }

        public async Task StartTimer()
        {            
            TokenSource = new CancellationTokenSource();
            var timerAction = new Action(CheckForNewLogs);
            await WarcraftLogsTimer(timerAction, TokenSource.Token);
        }

        public async Task StopTimer()
        {
            TokenSource.Cancel();
        }

        async void CheckForNewLogs()
        {
            var _ = Task.Run(async () =>
            {
                try
                {
                    System.Console.WriteLine("Checking for logs...");
                    List<WowGuildAssociations> guildList = null;
                    List<LogMonitoring> logWatchList = null;
                    List<WowClassicGuild> cGuildList = null;            
                    bool flip = true;
                    try
                    {
                        using (var db = new NinjaBotEntities())
                        {
                            guildList = db.WowGuildAssociations.ToList();
                            logWatchList = db.LogMonitoring.ToList();
                            cGuildList = db.WowClassicGuild.ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation($"Error getting guild/logwatch list -> [{ex.Message}]");
                    }
                    if (guildList != null)
                    {
                        _logger.LogInformation("Starting WCL Auto Posting...");
                        foreach (var guild in guildList)
                        {
                            await PerformLogCheck(logWatchList, flip, guild).ConfigureAwait(false);
                        }
                        foreach (var guild in cGuildList)
                        {
                            await PerformLogCheck(logWatchList, flip, guild).ConfigureAwait(false);
                        }
                        _logger.LogInformation("Finished WCL Auto Posting...");
                    }
                }
                finally
                {
                    System.Console.WriteLine("done checking for logs");
                    await this.StopTimer();                    
                    Thread.Sleep(TimeSpan.FromSeconds(130));
                    await this.StartTimer();
                }
            });                       
        }

        private async Task PerformLogCheck(List<LogMonitoring> logWatchList, bool flip, WowGuildAssociations guild)
        {
            try
            {
                var watchGuild = logWatchList.Where(w => w.ServerId == guild.ServerId).FirstOrDefault();
                if (watchGuild != null)
                {
                    if (watchGuild.MonitorLogs)
                    {
                        List<Reports> logs = null;
                        if (!string.IsNullOrEmpty(guild.LocalRealmSlug))
                        {

                            logs = await GetReportsFromGuild(guildName: guild.WowGuild, locale: guild.Locale, realm: guild.WowRealm.Replace("'", ""), realmSlug: guild.LocalRealmSlug, region: guild.WowRegion, isList: true, flip: flip);

                        }
                        else if (!string.IsNullOrEmpty(guild.Locale))
                        {

                            logs = await GetReportsFromGuild(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion, isList: true, locale: guild.Locale, flip: flip);

                        }
                        else
                        {

                            logs = await GetReportsFromGuild(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion, isList: true, flip: flip);

                        }
                        if (flip)
                        {
                            flip = false;
                        }
                        else
                        {
                            flip = true;
                        }
                        if (logs != null && logs.Count > 0)
                        {
                            var latestLog = logs[0];
                            DateTime startTime = UnixTimeStampToDateTime(latestLog.start);
                            //System.Console.WriteLine($"local id [{watchGuild.RetailReportId}] -> remote id [{latestLog.id}] for [{guild.WowGuild}] on [{guild.WowRealm}].");
                            if (latestLog.id != watchGuild.RetailReportId)
                            {
                                using (var db = new NinjaBotEntities())
                                {
                                    var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                    latestForGuild.LatestLogRetail = startTime;
                                    latestForGuild.RetailReportId = latestLog.id;
                                    await db.SaveChangesAsync();
                                }
                                ISocketMessageChannel channel = _client.GetChannel((ulong)watchGuild.ChannelId) as ISocketMessageChannel;
                                if (channel != null)
                                {
                                    var tz = GetLocalTz(guild);
                                    DateTime logStart = GetLocalTime(latestLog, tz);

                                    _logger.LogInformation($"Posting log for [{guild.WowGuild}] on [{guild.WowRealm}] for server [{guild.ServerName}]");

                                    var embed = new EmbedBuilder();
                                    embed.Title = $"New log found for [{guild.WowGuild}]!";
                                    StringBuilder sb = new StringBuilder();
                                    sb.AppendLine($"[__**{latestLog.title}** **/** **{latestLog.zoneName}**__]({latestLog.reportURL})");
                                    sb.AppendLine($"\t:timer: Start time: **{logStart}**");
                                    sb.AppendLine($"\t:mag: [WoWAnalyzer](https://wowanalyzer.com/report/{latestLog.id}) | :sob: [WipeFest](https://www.wipefest.net/report/{latestLog.id}) ");
                                    sb.AppendLine($"\t:pencil2: Created by [**{latestLog.owner}**]");
                                    sb.AppendLine();
                                    embed.Description = sb.ToString();
                                    embed.WithColor(new Color(0, 0, 255));
                                    await channel.SendMessageAsync("", false, embed.Build());
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //_logger.LogError($"Error checking for logs [{guild.WowGuild}]:[{guild.WowRealm}]:[{guild.WowRealm}]! -> [{ex.Message}]");
            }            
        }

        private DateTime GetLocalTime(Reports latestLog, string tz = null)
        {
            DateTime logStart = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(tz))
            {
                logStart = ConvTimeToLocalTimezone(UnixTimeStampToDateTime(latestLog.start), tz);

            }
            else
            {
                logStart = ConvTimeToLocalTimezone(UnixTimeStampToDateTime(latestLog.start));
            }
            return logStart;
        }

        private string GetLocalTz(WowGuildAssociations guild)
        {
            var locale = guild.Locale;
            var realmInfo = new WowRealm.Realm();
            WowRealmSearch.Result tzRealmInfo = new WowRealmSearch.Result();
            var tz = string.Empty;
            if (!string.IsNullOrEmpty(locale))
            {
                switch (locale)
                {
                    case "en_US":
                        {                            
                            tzRealmInfo = WowApi.RealmSearch.results.Where(r => r.data.slug == guild.WowRealm).FirstOrDefault();
                            break;
                        }
                    case "en_GB":
                        {                            
                            tzRealmInfo = WowApi.RealmSearchEu.results.Where(r => r.data.slug == guild.WowRealm).FirstOrDefault();
                            break;
                        }
                    case "ru_RU":
                        {                            
                            tzRealmInfo = WowApi.RealmSearchRu.results.Where(r => r.data.slug == guild.WowRealm).FirstOrDefault();
                            break;
                        }
                }
            }
            
            if (tzRealmInfo != null && !string.IsNullOrEmpty(tzRealmInfo.data.timezone))
            {
                tz = tzRealmInfo.data.timezone;
            }
            return tz;
        }

        private async Task PerformLogCheck(List<LogMonitoring> logWatchList, bool flip, WowClassicGuild guild)
        {
            try
            {
                var watchGuild = logWatchList.Where(w => w.ServerId == guild.ServerId).FirstOrDefault();
                if (watchGuild != null)
                {
                    if (watchGuild.MonitorLogs)
                    {
                        List<Reports> logs = null;                        

                        logs = await GetReportsFromGuildClassic(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion, isList: true, flip: flip);
                        
                        if (flip)
                        {
                            flip = false;
                        }
                        else
                        {
                            flip = true;
                        }
                        if (logs != null)
                        {
                            var latestLog = logs[0];
                            DateTime startTime = UnixTimeStampToDateTime(latestLog.start);
                            //System.Console.WriteLine($"local id [{watchGuild.ClassicReportId}] -> remote id [{latestLog.id}] for [{guild.WowGuild}] on [{guild.WowRealm}].");
                            if (latestLog.id != watchGuild.ClassicReportId)
                            {
                                using (var db = new NinjaBotEntities())
                                {
                                    var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                    latestForGuild.LatestLogClassic = startTime;
                                    latestForGuild.ClassicReportId = latestLog.id;
                                    await db.SaveChangesAsync();
                                }
                                ISocketMessageChannel channel = _client.GetChannel((ulong)watchGuild.ChannelId) as ISocketMessageChannel;
                                if (channel != null)
                                {
                                    _logger.LogInformation($"Posting log for [{guild.WowGuild}] on [{guild.WowRealm}] for server [{guild.ServerName}]");
                                    var embed = new EmbedBuilder();
                                    embed.Title = $"New log found for [{guild.WowGuild}]!";
                                    StringBuilder sb = new StringBuilder();
                                    sb.AppendLine($"[__**{latestLog.title}** **/** **{latestLog.zoneName}**__]({latestLog.reportURL})");
                                    sb.AppendLine($"\t:timer: Start time: **{UnixTimeStampToDateTime(latestLog.start).ToLocalTime()}**");
                                    sb.AppendLine($"\t:pencil2: Created by [**{latestLog.owner}**]");                                                                        
                                    sb.AppendLine();
                                    embed.Description = sb.ToString();
                                    embed.WithColor(new Color(0, 0, 255));
                                    await channel.SendMessageAsync("", false, embed.Build());
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //_logger.LogError($"Error checking for logs [{guild.WowGuild}]:[{guild.WowRealm}]:[{guild.WowRealm}]! -> [{ex.Message}]");
            }            
        }

        private CurrentRaidTier SetCurrentTier()
        {
            var currentTier = new CurrentRaidTier();
            using (var db = new NinjaBotEntities())
            {
                var tierFromDb = db.CurrentRaidTier.FirstOrDefault();
                if (tierFromDb != null)
                {
                    currentTier = tierFromDb;
                }
            }
            return currentTier;
        }

        private void MigrateOldReports()
        {
            List<LogMonitoring> logWatchList = null;         
            try
            {
                using (var db = new NinjaBotEntities())
                {
                    logWatchList = db.LogMonitoring.ToList();
                    foreach (var entry in logWatchList.Where(r => !string.IsNullOrEmpty(r.ReportId)))
                    {
                        var oldReportId = entry.ReportId;
                        var oldLatestDate = entry.LatestLog;
                        entry.LatestLogRetail = oldLatestDate;
                        entry.RetailReportId = oldReportId;  
                        entry.ReportId = string.Empty;
                        System.Console.WriteLine($"Updating [{entry.ServerName}]...");    
                        db.SaveChanges();                                
                    }                                        
                }
            }
            catch (Exception ex)
            {
                //_logger.LogError($"Error getting log watch list -> [{ex.Message}]");
            }
        }        
    }
}
