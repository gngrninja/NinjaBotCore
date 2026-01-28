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
    using NinjaBotCore.Common;

    public class WarcraftLogs : IWarcraftLogs
    {
        private static Lazy<List<Zones>> _zones;
        private static Lazy<List<Zones>> _classicZones;
        private static Lazy<List<CharClasses>> _charClasses;
        private static bool _lazyInitialized = false;
        private static readonly object _initLock = new object();
        private readonly IConfigurationRoot _config;
        private readonly WclApiRequestor _api;
        private readonly WclApiRequestor _apiCmd;
        private readonly WclApiRequestor _apiClassicCmd;
        private readonly ILogger _logger;
        private static CurrentRaidTier _currentRaidTier;
        private readonly WclApiRequestor _apiClassic;
        private readonly WclApiRequestor _apiVanilla;
        private readonly WclApiRequestor _apiVanillaCmd;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly WarcraftLogsV2Client _v2Client;

        public WarcraftLogs(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WarcraftLogs>>();
            _config = services.GetRequiredService<IConfigurationRoot>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _v2Client = services.GetRequiredService<WarcraftLogsV2Client>();

            try
            {
                _api = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://www.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://www.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiClassic = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://classic.warcraftlogs.com:443/v1/" , services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiClassicCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://classic.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiVanilla = new ApiRequestorThrottle(_config["WarcraftLogsApi"], baseUrl: "https://vanilla.warcraftlogs.com:443/v1/" , services.GetRequiredService<IHttpClientFactory>().CreateClient());
                _apiVanillaCmd = new ApiRequestorThrottle(_config["WarcraftLogsApiCmd"], baseUrl: "https://vanilla.warcraftlogs.com:443/v1/", services.GetRequiredService<IHttpClientFactory>().CreateClient());

                // Initialize lazy loaders for WCL static data (thread-safe, loads on first access)
                InitializeLazyLoaders();

                // Load current raid tier from database first (fast)
                _currentRaidTier = this.SetCurrentTier();

                // Then refresh from V2 API in background (updates cache + database)
                _ = RefreshCurrentRaidTierAsync();

                // Note: Log monitoring timer moved to NinjaBotHelpers service (LogMonitoringWorker)
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
                return _zones?.Value;
            }
        }

        public static List<Zones> ClassicZones
        {
            get
            {
                return _classicZones?.Value;
            }
        }

        public static List<CharClasses> CharClasses
        {
            get
            {
                return _charClasses?.Value;
            }
        }

        /// <summary>
        /// Initializes the lazy loaders for WCL static data.
        /// The actual API calls are deferred until first property access.
        /// This prevents blocking during DI/constructor execution.
        /// </summary>
        private void InitializeLazyLoaders()
        {
            lock (_initLock)
            {
                if (_lazyInitialized) return;

                // Capture instance fields for use in lazy factories
                var api = _api;
                var apiClassic = _apiClassic;
                var logger = _logger;

                _charClasses = new Lazy<List<CharClasses>>(() =>
                {
                    try
                    {
                        logger.LogInformation("Loading WCL character classes (lazy initialization)...");
                        return api.Get<List<CharClasses>>("classes?").GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to load WCL character classes");
                        return new List<CharClasses>();
                    }
                }, LazyThreadSafetyMode.ExecutionAndPublication);

                _zones = new Lazy<List<Zones>>(() =>
                {
                    try
                    {
                        logger.LogInformation("Loading WCL zones (lazy initialization)...");
                        return api.Get<List<Zones>>("zones?").GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to load WCL zones");
                        return new List<Zones>();
                    }
                }, LazyThreadSafetyMode.ExecutionAndPublication);

                _classicZones = new Lazy<List<Zones>>(() =>
                {
                    try
                    {
                        logger.LogInformation("Loading WCL classic zones (lazy initialization)...");
                        return apiClassic.Get<List<Zones>>("zones?").GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to load WCL classic zones");
                        return new List<Zones>();
                    }
                }, LazyThreadSafetyMode.ExecutionAndPublication);

                _lazyInitialized = true;
                logger.LogInformation("WCL lazy loaders initialized (data will load on first access)");
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
            string realmSlug = GetRealmSlugSafe(realm, region.ToLower());
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
            string regionForLookup = locale switch
            {
                "en_US" => "us",
                "ru_RU" => "ru",
                "en_GB" => "eu",
                _ => "us"
            };
            string realmSlug = GetRealmSlugSafe(realm, regionForLookup);
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
            string realmSlug = GetRealmSlugSafe(realmName, regionName);
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
            string realmSlug = GetRealmSlugSafe(realmName, regionName);
            string url = $"rankings/encounter/{encounterID}?guild={guildName}&server={realmSlug}&region={regionName}&metric={metric}&difficulty={difficulty}&page={page}&partition={partition}&";
            return await _apiCmd.Get<WarcraftlogRankings.RankingObject>(url);
        }

        public async Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuild(int encounterID, string realmName, string guildName, string page = "1",string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            guildName = guildName.Replace(" ", "%20");
            string realmSlug = GetRealmSlugSafe(realmName, regionName);
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
            string realmSlug = GetRealmSlugSafe(realmName, regionName);
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
        /// Safely gets a realm slug with null checks and fallback.
        /// Returns a generated slug from the realm name if WowApi data is not available.
        /// </summary>
        private string GetRealmSlugSafe(string realmName, string region)
        {
            if (string.IsNullOrEmpty(realmName))
                return string.Empty;

            string realmSlug = null;
            var realmLower = realmName.Replace("'", "").ToLower();

            try
            {
                WowRealm.Realm[] realms = region.ToLower() switch
                {
                    "us" => WowApi.RealmInfo?.realms,
                    "eu" => WowApi.RealmInfoEu?.realms,
                    "ru" => WowApi.RealmInfoRu?.realms,
                    _ => WowApi.RealmInfo?.realms
                };

                if (realms != null)
                {
                    realmSlug = realms
                        .Where(r => r.name.Replace("'", "").ToLower().Contains(realmLower))
                        .Select(s => s.slug)
                        .FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error looking up realm slug for {Realm} in {Region}", realmName, region);
            }

            // Fallback: generate slug from realm name
            if (string.IsNullOrEmpty(realmSlug))
            {
                realmSlug = realmName.ToLower().Replace(" ", "-").Replace("'", "");
                _logger.LogDebug("Using generated realm slug for {Realm}: {Slug}", realmName, realmSlug);
            }

            return realmSlug;
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

        // Note: Background log monitoring has been moved to NinjaBotHelpers (LogMonitoringWorker)

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

        /// <summary>
        /// Refreshes the current raid tier from the WCL V2 API.
        /// </summary>
        public async Task<CurrentRaidTier> RefreshCurrentRaidTierAsync(int expansionId = 0)
        {
            try
            {
                _logger.LogInformation("Refreshing current raid tier from WCL V2 API (Expansion: {ExpansionId})...", expansionId);

                var zoneTier = await _v2Client.GetCurrentRaidTierAsync(expansionId);

                if (zoneTier == null)
                {
                    _logger.LogWarning("Could not detect current raid tier from API, keeping existing: {ExistingTier}",
                        _currentRaidTier?.RaidName ?? "none");
                    return _currentRaidTier;
                }

                var defaultPartition = zoneTier.Partitions?.FirstOrDefault(p => p.IsDefault == true)
                    ?? zoneTier.Partitions?.LastOrDefault();

                var newTier = new CurrentRaidTier
                {
                    WclZoneId = zoneTier.Id,
                    RaidName = zoneTier.Name,
                    Partition = defaultPartition?.Id
                };

                _currentRaidTier = newTier;

                using var dbScope = _scopeFactory.CreateScope();
                var db2 = dbScope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var existingTier = db2.CurrentRaidTier.FirstOrDefault();
                if (existingTier != null)
                {
                    existingTier.WclZoneId = newTier.WclZoneId;
                    existingTier.RaidName = newTier.RaidName;
                    existingTier.Partition = newTier.Partition;
                }
                else
                {
                    db2.CurrentRaidTier.Add(newTier);
                }

                await db2.SaveChangesAsync();

                _logger.LogInformation("Current raid tier updated: {RaidName} (Zone ID: {ZoneId}, Partition: {Partition})",
                    newTier.RaidName, newTier.WclZoneId, newTier.Partition);

                return newTier;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh current raid tier from API");
                return _currentRaidTier;
            }
        }
    }
}
