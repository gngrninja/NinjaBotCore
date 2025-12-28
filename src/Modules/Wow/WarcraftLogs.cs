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
        private readonly WclApiRequestor _apiVanilla;
        private readonly WclApiRequestor _apiVanillaCmd;
        private readonly WowApi _wowApi;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly WarcraftLogsV2Client _v2Client;
        private readonly StartupService _startupService;

        // Tier tracking - when each tier was last checked
        private DateTime _tier1LastCheck = DateTime.MinValue;
        private DateTime _tier2LastCheck = DateTime.MinValue;
        private DateTime _tier3LastCheck = DateTime.MinValue;

        // Tier thresholds (days since last report)
        private readonly int _tier1ThresholdDays;  // Active threshold
        private readonly int _tier2ThresholdDays;  // Semi-active threshold

        // Tier intervals (how often to check each tier)
        private readonly TimeSpan _tier1Interval;  // Active check interval
        private readonly TimeSpan _tier2Interval;  // Semi-active check interval
        private readonly TimeSpan _tier3Interval;  // Inactive check interval

        public WarcraftLogs(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WarcraftLogs>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _config = services.GetRequiredService<IConfigurationRoot>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _wowApi = services.GetRequiredService<WowApi>();
            _v2Client = services.GetRequiredService<WarcraftLogsV2Client>();
            _startupService = services.GetRequiredService<StartupService>();

            // Load tier configuration from config with fallback defaults
            _tier1ThresholdDays = int.TryParse(_config["WCL:Tier1ThresholdDays"], out var t1) ? t1 : 14;
            _tier2ThresholdDays = int.TryParse(_config["WCL:Tier2ThresholdDays"], out var t2) ? t2 : 30;

            _tier1Interval = TimeSpan.FromMinutes(int.TryParse(_config["WCL:Tier1IntervalMinutes"], out var i1) ? i1 : 20);
            _tier2Interval = TimeSpan.FromHours(int.TryParse(_config["WCL:Tier2IntervalHours"], out var i2) ? i2 : 3);
            _tier3Interval = TimeSpan.FromHours(int.TryParse(_config["WCL:Tier3IntervalHours"], out var i3) ? i3 : 24);

            _logger.LogInformation("WCL Tier Config: T1={Tier1Days}d/{Tier1Minutes}m, T2={Tier2Days}d/{Tier2Hours}h, T3={Tier3Hours}h",
                _tier1ThresholdDays, _tier1Interval.TotalMinutes, _tier2ThresholdDays, _tier2Interval.TotalHours, _tier3Interval.TotalHours);

            try
            {
                _api = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://www.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://www.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiClassic = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://classic.warcraftlogs.com:443/v1/" , services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiClassicCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://classic.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiVanilla = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://vanilla.warcraftlogs.com:443/v1/" , services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiVanillaCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://vanilla.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());                
                CharClasses = this.GetCharClasses().Result;
                Zones = this.GetZones().Result;
                ClassicZones = this.GetClassicZones().Result;
                
                _currentRaidTier = this.SetCurrentTier();
                //this.MigrateOldReports();
                _ = StartTimer(); // Fire-and-forget background timer
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

        public async Task<List<Zones>> GetVanillaZones()
        {
            string url = string.Empty;
            url = $"zones?";
            return await _apiVanilla.Get<List<Zones>>(url);
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

        public async Task<List<Reports>> GetReportsFromGuildVanilla(string guildName, string realm, string region, bool isList = false, bool flip = false)
        {
            string url = string.Empty;           
            url = $"reports/guild/{guildName.Replace(" ", "%20")}/{realm}/{region.ToLower()}?";
            if (flip) 
            {
                return await _apiVanilla.Get<List<Reports>>(url);
            }
            else
            {
                return await _apiVanillaCmd.Get<List<Reports>>(url);
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

        // UnixTimeStampToDateTime method moved to NinjaExtensions.cs as extension method
        // Use: timestamp.UnixTimeStampToDateTime()

        /// <summary>
        /// Converts v2 API report model to v1 format for compatibility
        /// v2 API returns timestamps in milliseconds, v1 uses seconds
        /// </summary>
        private Reports ConvertV2ToV1Report(WclV2Report v2Report)
        {
            return new Reports
            {
                id = v2Report.Code,
                title = v2Report.Title,
                owner = v2Report.OwnerName,
                start = v2Report.StartTime / 1000,  // Convert ms to seconds for v1 compatibility
                end = v2Report.EndTime / 1000,      // Convert ms to seconds for v1 compatibility
                zone = v2Report.Zone?.Id ?? 0,
                zoneName = v2Report.Zone?.Name  // Use zone name from v2 API directly (avoids lookup)
            };
        }

        /// <summary>
        /// Guild activity tiers for optimized checking (configurable via WCL:Tier*ThresholdDays and WCL:Tier*Interval*)
        /// </summary>
        private enum GuildActivityTier
        {
            Tier1_Active,      // Recent reports - checked frequently
            Tier2_SemiActive,  // Moderate reports - checked periodically
            Tier3_Inactive     // Old/no reports - checked infrequently
        }

        /// <summary>
        /// Determines guild tier based on last report date
        /// </summary>
        private GuildActivityTier DetermineGuildTier(DateTime? lastReportDate)
        {
            if (lastReportDate == null)
                return GuildActivityTier.Tier3_Inactive;

            var daysSinceReport = (DateTime.UtcNow - lastReportDate.Value).TotalDays;

            if (daysSinceReport <= _tier1ThresholdDays)
                return GuildActivityTier.Tier1_Active;
            else if (daysSinceReport <= _tier2ThresholdDays)
                return GuildActivityTier.Tier2_SemiActive;
            else
                return GuildActivityTier.Tier3_Inactive;
        }

        /// <summary>
        /// Checks if a tier should be checked based on its interval
        /// </summary>
        private bool ShouldCheckTier(GuildActivityTier tier)
        {
            var now = DateTime.UtcNow;

            switch (tier)
            {
                case GuildActivityTier.Tier1_Active:
                    return (now - _tier1LastCheck) >= _tier1Interval;
                case GuildActivityTier.Tier2_SemiActive:
                    return (now - _tier2LastCheck) >= _tier2Interval;
                case GuildActivityTier.Tier3_Inactive:
                    return (now - _tier3LastCheck) >= _tier3Interval;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Updates the last check time for a tier
        /// </summary>
        private void UpdateTierCheckTime(GuildActivityTier tier)
        {
            var now = DateTime.UtcNow;

            switch (tier)
            {
                case GuildActivityTier.Tier1_Active:
                    _tier1LastCheck = now;
                    break;
                case GuildActivityTier.Tier2_SemiActive:
                    _tier2LastCheck = now;
                    break;
                case GuildActivityTier.Tier3_Inactive:
                    _tier3LastCheck = now;
                    break;
            }
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
                    // Check every 10 minutes - tier logic will determine which guilds to actually check
                    await Task.Delay(TimeSpan.FromMinutes(10), token);
                }
            }
            catch (TaskCanceledException)
            {
            }
        }

        public async Task StartTimer()
        {
            // Wait for all shards to be ready before starting the timer (with timeout)
            _logger.LogInformation("[WarcraftLogs] Waiting for all shards to be ready...");

            var timeout = Task.Delay(TimeSpan.FromSeconds(90));
            var completedTask = await Task.WhenAny(_startupService.AllShardsReady, timeout);

            if (completedTask == timeout)
            {
                _logger.LogWarning("[WarcraftLogs] Timeout waiting for all shards (90s) - starting timer anyway. Some guilds may not be accessible yet.");
            }
            else
            {
                _logger.LogInformation("[WarcraftLogs] All shards ready - starting timer");
            }

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
                    List<WowVanillaGuild> vGuildList = null;
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        guildList = db.WowGuildAssociations.ToList();
                        logWatchList = db.LogMonitoring.ToList();
                        cGuildList = db.WowClassicGuild.ToList();
                        vGuildList = db.WowVanillaGuild.ToList();

                        // Auto-initialize LatestLogRetail for existing guilds with monitoring enabled
                        // This ensures guilds aren't stuck in Tier 3 due to null LatestLogRetail
                        var uninitializedGuilds = logWatchList
                            .Where(l => l.MonitorLogs && l.LatestLogRetail == null && l.LatestLog.HasValue)
                            .ToList();

                        if (uninitializedGuilds.Count > 0)
                        {
                            _logger.LogInformation("Initializing LatestLogRetail for {Count} existing guilds with monitoring enabled", uninitializedGuilds.Count);
                            foreach (var guild in uninitializedGuilds)
                            {
                                guild.LatestLogRetail = guild.LatestLog;
                                _logger.LogDebug("  Initialized {ServerName}: LatestLog={LatestLog:yyyy-MM-dd} → LatestLogRetail", guild.ServerName, guild.LatestLog);
                            }
                            db.SaveChanges();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation($"Error getting guild/logwatch list -> [{ex.Message}]");
                    }
                    if (guildList != null)
                    {
                        _logger.LogInformation("Starting WCL Auto Posting...");

                        // Check which tiers are due for checking
                        var tier1Due = ShouldCheckTier(GuildActivityTier.Tier1_Active);
                        var tier2Due = ShouldCheckTier(GuildActivityTier.Tier2_SemiActive);
                        var tier3Due = ShouldCheckTier(GuildActivityTier.Tier3_Inactive);

                        if (!tier1Due && !tier2Due && !tier3Due)
                        {
                            _logger.LogInformation("No tiers due for checking at this time");
                        }
                        else
                        {
                            _logger.LogInformation($"Tiers due: Tier1={tier1Due}, Tier2={tier2Due}, Tier3={tier3Due}");

                            // Process each tier that's due
                            if (tier1Due)
                            {
                                _logger.LogInformation("[Tier 1] Checking active guilds...");
                                await PerformBatchedLogCheck(logWatchList, guildList, GuildActivityTier.Tier1_Active).ConfigureAwait(false);
                                UpdateTierCheckTime(GuildActivityTier.Tier1_Active);
                            }

                            if (tier2Due)
                            {
                                _logger.LogInformation("[Tier 2] Checking semi-active guilds...");
                                await PerformBatchedLogCheck(logWatchList, guildList, GuildActivityTier.Tier2_SemiActive).ConfigureAwait(false);
                                UpdateTierCheckTime(GuildActivityTier.Tier2_SemiActive);
                            }

                            if (tier3Due)
                            {
                                _logger.LogInformation("[Tier 3] Checking inactive guilds...");
                                await PerformBatchedLogCheck(logWatchList, guildList, GuildActivityTier.Tier3_Inactive).ConfigureAwait(false);
                                UpdateTierCheckTime(GuildActivityTier.Tier3_Inactive);
                            }
                        }

                        // Classic guilds - using v2 API with batched requests
                        if (cGuildList != null && cGuildList.Count > 0)
                        {
                            _logger.LogInformation($"[v2 Batch Classic] Processing {cGuildList.Count} Classic guilds");
                            await PerformBatchedLogCheckClassic(logWatchList, cGuildList).ConfigureAwait(false);
                        }

                        // Vanilla guilds - using v2 API with batched requests
                        if (vGuildList != null && vGuildList.Count > 0)
                        {
                            _logger.LogInformation($"[v2 Batch Vanilla] Processing {vGuildList.Count} Vanilla guilds");
                            await PerformBatchedLogCheckVanilla(logWatchList, vGuildList).ConfigureAwait(false);
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

        private async Task PerformLogCheck(List<LogMonitoring> logWatchList, WowGuildAssociations guild)
        {
            try
            {
                var watchGuild = logWatchList.Where(w => w.ServerId == guild.ServerId).FirstOrDefault();
                if (watchGuild != null)
                {
                    if (watchGuild.MonitorLogs)
                    {
                        List<Reports> logs = null;

                        // Try v2 API first
                        try
                        {
                            string realmSlug = guild.LocalRealmSlug ?? guild.WowRealm.ToLower().Replace(" ", "-").Replace("'", "");
                            var v2Reports = await _v2Client.GetGuildReportsAsync(guild.WowGuild, realmSlug, guild.WowRegion, limit: 1);

                            if (v2Reports != null && v2Reports.Count > 0)
                            {
                                logs = v2Reports.Select(r => ConvertV2ToV1Report(r)).ToList();
                                _logger.LogInformation($"[v2] Retrieved {logs.Count} reports for {guild.WowGuild}-{realmSlug}");
                            }
                        }
                        catch (Exception v2Ex)
                        {
                            _logger.LogWarning($"[v2] Failed for {guild.WowGuild}, falling back to v1: {v2Ex.Message}");

                            // Fallback to v1 API
                            if (!string.IsNullOrEmpty(guild.LocalRealmSlug))
                            {
                                logs = await GetReportsFromGuild(guildName: guild.WowGuild, locale: guild.Locale, realm: guild.WowRealm.Replace("'", ""), realmSlug: guild.LocalRealmSlug, region: guild.WowRegion, isList: true, flip: false);
                            }
                            else if (!string.IsNullOrEmpty(guild.Locale))
                            {
                                logs = await GetReportsFromGuild(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion, isList: true, locale: guild.Locale, flip: false);
                            }
                            else
                            {
                                logs = await GetReportsFromGuild(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion, isList: true, flip: false);
                            }
                            _logger.LogInformation($"[v1] Retrieved {logs?.Count ?? 0} reports for {guild.WowGuild}");
                        }

                        if (logs != null && logs.Count > 0)
                        {
                            var latestLog = logs[0];
                            DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();
                            //System.Console.WriteLine($"local id [{watchGuild.RetailReportId}] -> remote id [{latestLog.id}] for [{guild.WowGuild}] on [{guild.WowRealm}].");
                            if (latestLog.id != watchGuild.RetailReportId)
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                                var checkId = db.WclPosted.Where(p => p.ServerId == guild.ServerId && p.ReportId == latestLog.id).FirstOrDefault();
                                if (checkId != null)
                                {
                                    _logger.LogInformation($"latest report id {latestLog.id} found in database, cancelling post for {guild.ServerName}!");
                                    return;
                                }
                                var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                latestForGuild.LatestLogRetail = startTime;
                                latestForGuild.RetailReportId = latestLog.id;
                                db.WclPosted.Add(new WclPosted
                                {
                                    ServerId = (long)guild.ServerId,
                                    ChannelId = latestForGuild.ChannelId,
                                    ChannelName = latestForGuild.ChannelName,
                                    ServerName = latestForGuild.ServerName,
                                    ReportId = latestLog.id
                                });
                                await db.SaveChangesAsync();

                                // Get guild from all shards (not just local cache)
                                var discordGuild = _client.Guilds.FirstOrDefault(g => g.Id == (ulong)guild.ServerId);
                                if (discordGuild != null)
                                {
                                    var channel = discordGuild.GetTextChannel((ulong)watchGuild.ChannelId);
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
            }
            catch (Exception)
            {
                //_logger.LogError($"Error checking for logs [{guild.WowGuild}]:[{guild.WowRealm}]:[{guild.WowRealm}]! -> [{ex.Message}]");
            }
        }

        private async Task PerformBatchedLogCheck(List<LogMonitoring> logWatchList, List<WowGuildAssociations> guildList, GuildActivityTier tier)
        {
            try
            {
                // Filter guilds that need monitoring AND match the specified tier
                var guildsToMonitor = guildList
                    .Where(g => logWatchList.Any(w => w.ServerId == g.ServerId && w.MonitorLogs))
                    .Select(g => new
                    {
                        Guild = g,
                        Monitoring = logWatchList.First(w => w.ServerId == g.ServerId),
                        Tier = DetermineGuildTier(logWatchList.First(w => w.ServerId == g.ServerId).LatestLogRetail)
                    })
                    .Where(x => x.Tier == tier)
                    .Select(x => x.Guild)
                    .ToList();

                if (guildsToMonitor.Count == 0)
                {
                    _logger.LogInformation($"[v2 Batch] [Tier {(int)tier + 1}] No guilds to monitor");
                    return;
                }

                var tierName = tier switch
                {
                    GuildActivityTier.Tier1_Active => $"Active (<{_tier1ThresholdDays} days)",
                    GuildActivityTier.Tier2_SemiActive => $"Semi-Active ({_tier1ThresholdDays}-{_tier2ThresholdDays} days)",
                    GuildActivityTier.Tier3_Inactive => $"Inactive ({_tier2ThresholdDays}+ days)",
                    _ => "Unknown"
                };

                _logger.LogInformation($"[v2 Batch] [Tier {(int)tier + 1}] Processing {guildsToMonitor.Count} {tierName} guilds");

                // Build batch request list
                var batchRequest = guildsToMonitor.Select(g => (
                    guildName: g.WowGuild,
                    serverSlug: g.LocalRealmSlug ?? g.WowRealm.ToLower().Replace(" ", "-").Replace("'", ""),
                    serverRegion: g.WowRegion,
                    guildKey: $"{g.ServerId}"
                )).ToList();

                _logger.LogInformation($"[v2 Batch] Querying {batchRequest.Count} retail guilds in single request");

                WclV2BatchResult batchResult = null;

                // Try v2 batched API first
                try
                {
                    batchResult = await _v2Client.GetBatchGuildReportsAsync(batchRequest);
                }
                catch (Exception v2Ex)
                {
                    _logger.LogError($"[v2 Batch] API call failed, falling back to individual v1 requests: {v2Ex.Message}");

                    // Fallback to individual requests using the existing method
                    foreach (var guild in guildsToMonitor)
                    {
                        await PerformLogCheck(logWatchList, guild).ConfigureAwait(false);
                    }
                    return;
                }

                if (batchResult == null)
                {
                    _logger.LogError("[v2 Batch] Received null batch result, cannot process guilds");
                    return;
                }

                // Log batch result categorization
                _logger.LogInformation($"[v2 Batch] Result breakdown: {batchResult.Reports.Count} with reports, {batchResult.NonExistentGuilds.Count} not found, {batchResult.GuildsWithNoReports.Count} no reports, {guildsToMonitor.Count - (batchResult.Reports.Count + batchResult.NonExistentGuilds.Count + batchResult.GuildsWithNoReports.Count)} uncategorized");

                // Track statistics
                int processedCount = 0;
                int postedCount = 0;
                int duplicateCount = 0;
                int missingCount = 0;
                var failedGuilds = new List<string>();

                // Process results
                foreach (var guild in guildsToMonitor)
                {
                    try
                    {
                        var watchGuild = logWatchList.FirstOrDefault(w => w.ServerId == guild.ServerId);
                        if (watchGuild == null)
                        {
                            _logger.LogWarning($"[v2 Batch] No monitoring config found for guild {guild.WowGuild} (ServerId: {guild.ServerId})");
                            continue;
                        }

                        var guildKey = $"{guild.ServerId}";
                        var guildIdentifier = $"{guild.WowGuild}-{guild.WowRealm} ({guild.WowRegion})";

                        if (!batchResult.Reports.TryGetValue(guildKey, out var v2Report))
                        {
                            // Determine why this guild has no report
                            var reason = "unknown";
                            if (batchResult.NonExistentGuilds.Contains(guildKey))
                                reason = "guild doesn't exist on WCL";
                            else if (batchResult.GuildsWithNoReports.Contains(guildKey))
                                reason = "guild has no reports";

                            _logger.LogDebug($"[v2 Batch] No report found for {guildIdentifier} - {reason}");
                            missingCount++;
                            continue;
                        }

                        var latestLog = ConvertV2ToV1Report(v2Report);

                        // Validate conversion produced valid data
                        if (string.IsNullOrEmpty(latestLog.id))
                        {
                            _logger.LogWarning($"[v2 Batch] Conversion produced invalid report ID for {guildIdentifier}");
                            failedGuilds.Add(guildIdentifier);
                            continue;
                        }

                        // Note: zoneName is now set directly from v2 API, no lookup needed

                        DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();
                        processedCount++;

                        // Check if this is a new report
                        if (latestLog.id == watchGuild.RetailReportId)
                        {
                            _logger.LogDebug($"[v2 Batch] Report {latestLog.id} already tracked for {guildIdentifier}");
                            continue;
                        }

                        // Check if already posted and handle database operations
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var checkId = db.WclPosted.FirstOrDefault(p => p.ServerId == guild.ServerId && p.ReportId == latestLog.id);
                        if (checkId != null)
                        {
                            _logger.LogInformation($"[v2 Batch] Report {latestLog.id} already posted for {guild.ServerName}, updating tracking");
                            duplicateCount++;

                            // Update tracking even if already posted
                            var latestForGuild = db.LogMonitoring.FirstOrDefault(l => l.ServerId == guild.ServerId);
                            if (latestForGuild != null)
                            {
                                latestForGuild.LatestLogRetail = startTime;
                                latestForGuild.RetailReportId = latestLog.id;
                                await db.SaveChangesAsync();
                            }
                            continue;
                        }

                        // New report - post it
                        var latestForGuild2 = db.LogMonitoring.FirstOrDefault(l => l.ServerId == guild.ServerId);
                        if (latestForGuild2 == null)
                        {
                            _logger.LogWarning($"[v2 Batch] Cannot update monitoring record for {guildIdentifier} - record not found");
                            continue;
                        }

                        latestForGuild2.LatestLogRetail = startTime;
                        latestForGuild2.RetailReportId = latestLog.id;
                        db.WclPosted.Add(new WclPosted
                        {
                            ServerId = (long)guild.ServerId,
                            ChannelId = latestForGuild2.ChannelId,
                            ChannelName = latestForGuild2.ChannelName,
                            ServerName = latestForGuild2.ServerName,
                            ReportId = latestLog.id
                        });
                        await db.SaveChangesAsync();

                        // Get guild from all shards (not just local cache)
                        var discordGuild = _client.Guilds.FirstOrDefault(g => g.Id == (ulong)guild.ServerId);
                        if (discordGuild == null)
                        {
                            _logger.LogWarning("[v2 Batch] Could not find Discord guild {ServerId} ({ServerName})", guild.ServerId, guild.ServerName);
                            continue;
                        }

                        var channel = discordGuild.GetTextChannel((ulong)watchGuild.ChannelId);
                        if (channel == null)
                        {
                            _logger.LogWarning("[v2 Batch] Could not find Discord channel {ChannelId} in guild {ServerName} - channel may be deleted or bot lacks access", watchGuild.ChannelId, guild.ServerName);
                            continue;
                        }

                        var tz = GetLocalTz(guild);
                        DateTime logStart = GetLocalTime(latestLog, tz);

                        _logger.LogInformation($"[v2 Batch] Posting new log {latestLog.id} for {guildIdentifier} to {guild.ServerName}");

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
                        postedCount++;
                    }
                    catch (Exception guildEx)
                    {
                        var guildIdent = $"{guild.WowGuild}-{guild.WowRealm}";
                        _logger.LogError($"[v2 Batch] Error processing {guildIdent}: {guildEx.GetType().Name} - {guildEx.Message}");
                        failedGuilds.Add(guildIdent);
                    }
                }

                // Summary logging
                _logger.LogDebug($"[v2 Batch] [Tier {(int)tier + 1}] Counter values: processedCount={processedCount}, missingCount={missingCount}, postedCount={postedCount}, duplicateCount={duplicateCount}");
                _logger.LogInformation($"[v2 Batch] [Tier {(int)tier + 1}] Batch complete: {batchRequest.Count} queried, {processedCount} processed, {postedCount} posted, {duplicateCount} duplicates, {missingCount} no reports");

                if (failedGuilds.Count > 0)
                {
                    _logger.LogWarning($"[v2 Batch] Failed to process {failedGuilds.Count} guilds: {string.Join(", ", failedGuilds)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[v2 Batch] Critical failure in batched log check: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task PerformBatchedLogCheckClassic(List<LogMonitoring> logWatchList, List<WowClassicGuild> classicGuildList)
        {
            try
            {
                // Filter guilds that need monitoring
                var guildsToMonitor = classicGuildList
                    .Where(g => logWatchList.Any(w => w.ServerId == g.ServerId && w.MonitorLogs))
                    .ToList();

                if (guildsToMonitor.Count == 0)
                {
                    _logger.LogInformation("[v2 Batch Classic] No guilds to monitor");
                    return;
                }

                _logger.LogInformation("[v2 Batch Classic] Processing {Count} Classic guilds", guildsToMonitor.Count);

                // Build batch request list (no realm slug manipulation - use as-is)
                var batchRequest = guildsToMonitor.Select(g => (
                    guildName: g.WowGuild,
                    serverSlug: g.WowRealm.ToLower().Replace(" ", "-").Replace("'", ""),
                    serverRegion: g.WowRegion,
                    guildKey: $"{g.ServerId}"
                )).ToList();

                WclV2BatchResult batchResult = null;

                try
                {
                    batchResult = await _v2Client.GetBatchGuildReportsAsync(batchRequest, WowGameVersion.Classic);
                }
                catch (Exception v2Ex)
                {
                    _logger.LogError("[v2 Batch Classic] API call failed, falling back to individual v1 requests: {Message}", v2Ex.Message);

                    // Fallback to individual v1 requests
                    bool flip = true;
                    foreach (var guild in guildsToMonitor)
                    {
                        await this.PerformLogCheck(logWatchList, flip, guild).ConfigureAwait(false);
                        flip = !flip;
                    }
                    return;
                }

                if (batchResult == null)
                {
                    _logger.LogError("[v2 Batch Classic] Received null batch result");
                    return;
                }

                _logger.LogInformation("[v2 Batch Classic] Result: {Reports} with reports, {NotFound} not found, {NoReports} no reports",
                    batchResult.Reports.Count, batchResult.NonExistentGuilds.Count, batchResult.GuildsWithNoReports.Count);

                int postedCount = 0;
                int duplicateCount = 0;

                // Process results
                foreach (var guild in guildsToMonitor)
                {
                    try
                    {
                        var watchGuild = logWatchList.FirstOrDefault(w => w.ServerId == guild.ServerId);
                        if (watchGuild == null) continue;

                        var guildKey = $"{guild.ServerId}";

                        if (!batchResult.Reports.TryGetValue(guildKey, out var v2Report))
                        {
                            continue;
                        }

                        var latestLog = ConvertV2ToV1Report(v2Report);
                        if (string.IsNullOrEmpty(latestLog.id)) continue;

                        DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();

                        if (latestLog.id == watchGuild.ClassicReportId)
                        {
                            continue;
                        }

                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var checkId = db.WclPosted.FirstOrDefault(p => p.ServerId == guild.ServerId && p.ReportId == latestLog.id);
                        if (checkId != null)
                        {
                            duplicateCount++;
                            var latestForGuild = db.LogMonitoring.FirstOrDefault(l => l.ServerId == guild.ServerId);
                            if (latestForGuild != null)
                            {
                                latestForGuild.LatestLogClassic = startTime;
                                latestForGuild.ClassicReportId = latestLog.id;
                                await db.SaveChangesAsync();
                            }
                            continue;
                        }

                        var latestForGuild2 = db.LogMonitoring.FirstOrDefault(l => l.ServerId == guild.ServerId);
                        if (latestForGuild2 == null) continue;

                        latestForGuild2.LatestLogClassic = startTime;
                        latestForGuild2.ClassicReportId = latestLog.id;
                        db.WclPosted.Add(new WclPosted
                        {
                            ServerId = (long)guild.ServerId,
                            ChannelId = latestForGuild2.ChannelId,
                            ChannelName = latestForGuild2.ChannelName,
                            ServerName = latestForGuild2.ServerName,
                            ReportId = latestLog.id
                        });
                        await db.SaveChangesAsync();

                        // Get guild from all shards (not just local cache)
                        var discordGuild = _client.Guilds.FirstOrDefault(g => g.Id == (ulong)guild.ServerId);
                        if (discordGuild == null) continue;

                        var channel = discordGuild.GetTextChannel((ulong)watchGuild.ChannelId);
                        if (channel == null) continue;

                        _logger.LogInformation("[v2 Batch Classic] Posting log {ReportId} for {Guild}-{Realm}", latestLog.id, guild.WowGuild, guild.WowRealm);

                        var embed = new EmbedBuilder();
                        embed.Title = $"New log found for [{guild.WowGuild}]!";
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine($"[__**{latestLog.title}** **/** **{latestLog.zoneName}**__]({latestLog.reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{latestLog.start.UnixTimeStampToDateTimeSeconds().ToLocalTime()}**");
                        sb.AppendLine($"\t:pencil2: Created by [**{latestLog.owner}**]");
                        sb.AppendLine();
                        embed.Description = sb.ToString();
                        embed.WithColor(new Color(0, 0, 255));

                        await channel.SendMessageAsync("", false, embed.Build());
                        postedCount++;
                    }
                    catch (Exception guildEx)
                    {
                        _logger.LogError("[v2 Batch Classic] Error processing {Guild}: {Error}", $"{guild.WowGuild}-{guild.WowRealm}", guildEx.Message);
                    }
                }

                _logger.LogInformation("[v2 Batch Classic] Complete: {Posted} posted, {Duplicates} duplicates", postedCount, duplicateCount);
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2 Batch Classic] Critical failure: {Type} - {Message}", ex.GetType().Name, ex.Message);
            }
        }

        private async Task PerformBatchedLogCheckVanilla(List<LogMonitoring> logWatchList, List<WowVanillaGuild> vanillaGuildList)
        {
            try
            {
                // Filter guilds that need monitoring
                var guildsToMonitor = vanillaGuildList
                    .Where(g => logWatchList.Any(w => w.ServerId == g.ServerId && w.MonitorLogs))
                    .ToList();

                if (guildsToMonitor.Count == 0)
                {
                    _logger.LogInformation("[v2 Batch Vanilla] No guilds to monitor");
                    return;
                }

                _logger.LogInformation("[v2 Batch Vanilla] Processing {Count} Vanilla guilds", guildsToMonitor.Count);

                // Build batch request list (no realm slug manipulation - use as-is)
                var batchRequest = guildsToMonitor.Select(g => (
                    guildName: g.WowGuild,
                    serverSlug: g.WowRealm.ToLower().Replace(" ", "-").Replace("'", ""),
                    serverRegion: g.WowRegion,
                    guildKey: $"{g.ServerId}"
                )).ToList();

                WclV2BatchResult batchResult = null;

                try
                {
                    batchResult = await _v2Client.GetBatchGuildReportsAsync(batchRequest, WowGameVersion.Vanilla);
                }
                catch (Exception v2Ex)
                {
                    _logger.LogError("[v2 Batch Vanilla] API call failed, falling back to individual v1 requests: {Message}", v2Ex.Message);

                    // Fallback to individual v1 requests
                    bool flip = true;
                    foreach (var guild in guildsToMonitor)
                    {
                        await this.PerformLogCheck(logWatchList, flip, guild).ConfigureAwait(false);
                        flip = !flip;
                    }
                    return;
                }

                if (batchResult == null)
                {
                    _logger.LogError("[v2 Batch Vanilla] Received null batch result");
                    return;
                }

                _logger.LogInformation("[v2 Batch Vanilla] Result: {Reports} with reports, {NotFound} not found, {NoReports} no reports",
                    batchResult.Reports.Count, batchResult.NonExistentGuilds.Count, batchResult.GuildsWithNoReports.Count);

                int postedCount = 0;
                int duplicateCount = 0;

                // Process results
                foreach (var guild in guildsToMonitor)
                {
                    try
                    {
                        var watchGuild = logWatchList.FirstOrDefault(w => w.ServerId == guild.ServerId);
                        if (watchGuild == null) continue;

                        var guildKey = $"{guild.ServerId}";

                        if (!batchResult.Reports.TryGetValue(guildKey, out var v2Report))
                        {
                            continue;
                        }

                        var latestLog = ConvertV2ToV1Report(v2Report);
                        if (string.IsNullOrEmpty(latestLog.id)) continue;

                        DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();

                        if (latestLog.id == watchGuild.VanillaReportId)
                        {
                            continue;
                        }

                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                        var checkId = db.WclPosted.FirstOrDefault(p => p.ServerId == guild.ServerId && p.ReportId == latestLog.id);
                        if (checkId != null)
                        {
                            duplicateCount++;
                            var latestForGuild = db.LogMonitoring.FirstOrDefault(l => l.ServerId == guild.ServerId);
                            if (latestForGuild != null)
                            {
                                latestForGuild.LatestLogVanilla = startTime;
                                latestForGuild.VanillaReportId = latestLog.id;
                                await db.SaveChangesAsync();
                            }
                            continue;
                        }

                        var latestForGuild2 = db.LogMonitoring.FirstOrDefault(l => l.ServerId == guild.ServerId);
                        if (latestForGuild2 == null) continue;

                        latestForGuild2.LatestLogVanilla = startTime;
                        latestForGuild2.VanillaReportId = latestLog.id;
                        db.WclPosted.Add(new WclPosted
                        {
                            ServerId = (long)guild.ServerId,
                            ChannelId = latestForGuild2.ChannelId,
                            ChannelName = latestForGuild2.ChannelName,
                            ServerName = latestForGuild2.ServerName,
                            ReportId = latestLog.id
                        });
                        await db.SaveChangesAsync();

                        // Get guild from all shards (not just local cache)
                        var discordGuild = _client.Guilds.FirstOrDefault(g => g.Id == (ulong)guild.ServerId);
                        if (discordGuild == null) continue;

                        var channel = discordGuild.GetTextChannel((ulong)watchGuild.ChannelId);
                        if (channel == null) continue;

                        _logger.LogInformation("[v2 Batch Vanilla] Posting log {ReportId} for {Guild}-{Realm}", latestLog.id, guild.WowGuild, guild.WowRealm);

                        var embed = new EmbedBuilder();
                        embed.Title = $"New log found for [{guild.WowGuild}]!";
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine($"[__**{latestLog.title}** **/** **{latestLog.zoneName}**__]({latestLog.reportURL})");
                        sb.AppendLine($"\t:timer: Start time: **{latestLog.start.UnixTimeStampToDateTimeSeconds().ToLocalTime()}**");
                        sb.AppendLine($"\t:pencil2: Created by [**{latestLog.owner}**]");
                        sb.AppendLine();
                        embed.Description = sb.ToString();
                        embed.WithColor(new Color(0, 0, 255));

                        await channel.SendMessageAsync("", false, embed.Build());
                        postedCount++;
                    }
                    catch (Exception guildEx)
                    {
                        _logger.LogError("[v2 Batch Vanilla] Error processing {Guild}: {Error}", $"{guild.WowGuild}-{guild.WowRealm}", guildEx.Message);
                    }
                }

                _logger.LogInformation("[v2 Batch Vanilla] Complete: {Posted} posted, {Duplicates} duplicates", postedCount, duplicateCount);
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2 Batch Vanilla] Critical failure: {Type} - {Message}", ex.GetType().Name, ex.Message);
            }
        }

        private DateTime GetLocalTime(Reports latestLog, string tz = null)
        {
            DateTime logStart = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(tz))
            {
                logStart = ConvTimeToLocalTimezone(latestLog.start.UnixTimeStampToDateTimeSeconds(), tz);

            }
            else
            {
                logStart = ConvTimeToLocalTimezone(latestLog.start.UnixTimeStampToDateTimeSeconds());
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

        private async Task PerformLogCheck(List<LogMonitoring> logWatchList, bool flip, WowVanillaGuild guild)
        {
           try
            {
                var watchGuild = logWatchList.Where(w => w.ServerId == guild.ServerId).FirstOrDefault();
                if (watchGuild != null)
                {
                    if (watchGuild.MonitorLogs)
                    {
                        List<Reports> logs = null;                        

                        logs = await GetReportsFromGuildVanilla(guildName: guild.WowGuild, realm: guild.WowRealm.Replace("'", ""), region: guild.WowRegion, isList: true, flip: flip);
                        
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
                            DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();
                            //System.Console.WriteLine($"local id [{watchGuild.VanillaReportId}] -> remote id [{latestLog.id}] for [{guild.WowGuild}] on [{guild.WowRealm}].");
                            if (latestLog.id != watchGuild.VanillaReportId)
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                                var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                latestForGuild.LatestLogVanilla = startTime;
                                latestForGuild.VanillaReportId = latestLog.id;
                                await db.SaveChangesAsync();

                                // Get guild from all shards (not just local cache)
                                var discordGuild = _client.Guilds.FirstOrDefault(g => g.Id == (ulong)guild.ServerId);
                                if (discordGuild != null)
                                {
                                    var channel = discordGuild.GetTextChannel((ulong)watchGuild.ChannelId);
                                    if (channel != null)
                                    {
                                        _logger.LogInformation($"Posting log for [{guild.WowGuild}] on [{guild.WowRealm}] for server [{guild.ServerName}]");
                                    var embed = new EmbedBuilder();
                                    embed.Title = $"New log found for [{guild.WowGuild}]!";
                                    StringBuilder sb = new StringBuilder();
                                    sb.AppendLine($"[__**{latestLog.title}** **/** **{latestLog.zoneName}**__]({latestLog.reportURL})");
                                    sb.AppendLine($"\t:timer: Start time: **{latestLog.start.UnixTimeStampToDateTimeSeconds().ToLocalTime()}**");
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
            }
            catch (Exception)
            {
                //_logger.LogError($"Error checking for logs [{guild.WowGuild}]:[{guild.WowRealm}]:[{guild.WowRealm}]! -> [{ex.Message}]");
            }
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
                            DateTime startTime = latestLog.start.UnixTimeStampToDateTimeSeconds();
                            //System.Console.WriteLine($"local id [{watchGuild.ClassicReportId}] -> remote id [{latestLog.id}] for [{guild.WowGuild}] on [{guild.WowRealm}].");
                            if (latestLog.id != watchGuild.ClassicReportId)
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                                var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                latestForGuild.LatestLogClassic = startTime;
                                latestForGuild.ClassicReportId = latestLog.id;
                                await db.SaveChangesAsync();

                                // Get guild from all shards (not just local cache)
                                var discordGuild = _client.Guilds.FirstOrDefault(g => g.Id == (ulong)guild.ServerId);
                                if (discordGuild != null)
                                {
                                    var channel = discordGuild.GetTextChannel((ulong)watchGuild.ChannelId);
                                    if (channel != null)
                                    {
                                        _logger.LogInformation($"Posting log for [{guild.WowGuild}] on [{guild.WowRealm}] for server [{guild.ServerName}]");
                                    var embed = new EmbedBuilder();
                                    embed.Title = $"New log found for [{guild.WowGuild}]!";
                                    StringBuilder sb = new StringBuilder();
                                    sb.AppendLine($"[__**{latestLog.title}** **/** **{latestLog.zoneName}**__]({latestLog.reportURL})");
                                    sb.AppendLine($"\t:timer: Start time: **{latestLog.start.UnixTimeStampToDateTimeSeconds().ToLocalTime()}**");
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
            }
            catch (Exception)
            {
                //_logger.LogError($"Error checking for logs [{guild.WowGuild}]:[{guild.WowRealm}]:[{guild.WowRealm}]! -> [{ex.Message}]");
            }
        }

        private CurrentRaidTier SetCurrentTier()
        {
            var currentTier = new CurrentRaidTier();

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var tierFromDb = db.CurrentRaidTier.FirstOrDefault();
            if (tierFromDb != null)
            {
                currentTier = tierFromDb;
            }
            return currentTier;
        }

        private void MigrateOldReports()
        {
            List<LogMonitoring> logWatchList = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

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
            catch (Exception)
            {
                //_logger.LogError($"Error getting log watch list -> [{ex.Message}]");
            }
        }
    }
}
