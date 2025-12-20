using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NinjaBotCore.Models.Wow;
using System.IO;
using NinjaBotCore.Database;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using NinjaBotCore.Services;
using Polly;
using Polly.Retry;

namespace NinjaBotCore.Modules.Wow
{
    public class WowApi : IWowApi
    {
        private static string _token;
        private static readonly object _tokenLock = new();
        private readonly TimeSpan _tokenRefreshInterval = TimeSpan.FromHours(12);
        private CancellationTokenSource _tokenRefreshCancellation;
        private Task _tokenRefreshTask;
        private static WowClasses _classes;
        private static Race _race;
        private static List<Achievement> _achievements;
        private static WowRealmSearch.Root _realmSearch;
        private static WowRealmSearch.Root _realmSearchEu;
        private static WowRealmSearch.Root _realmSearchRu;
        private static WowRealm _realmInfo;
        private static WowRealm _realmInfoEu;
        private static WowRealm _realmInfoRu;        
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private readonly HttpClient _client;
        private readonly ResiliencePipeline<HttpResponseMessage> _httpResiliencePipeline;

        public WowApi(IServiceProvider services)
        {
            try
            {
                _client = services.GetRequiredService<IHttpClientFactory>().CreateClient();
                _config = services.GetRequiredService<IConfigurationRoot>();
                _logger = services.GetRequiredService<ILogger<WowApi>>();

                // Configure resilience pipeline for API calls
                _httpResiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
                    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                    {
                        MaxRetryAttempts = 3,
                        Delay = TimeSpan.FromSeconds(1),
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                            .Handle<HttpRequestException>()
                            .Handle<TaskCanceledException>()
                            .HandleResult(response =>
                                response.StatusCode == HttpStatusCode.TooManyRequests || // 429 Rate limit
                                response.StatusCode == HttpStatusCode.RequestTimeout || // 408
                                response.StatusCode == HttpStatusCode.InternalServerError || // 500
                                response.StatusCode == HttpStatusCode.BadGateway || // 502
                                response.StatusCode == HttpStatusCode.ServiceUnavailable || // 503
                                response.StatusCode == HttpStatusCode.GatewayTimeout), // 504
                        OnRetry = args =>
                        {
                            var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                            _logger.LogWarning(
                                "Retry attempt {AttemptNumber} for WoW API request. Status: {StatusCode}, Delay: {Delay}",
                                args.AttemptNumber,
                                statusCode,
                                args.RetryDelay);
                            return ValueTask.CompletedTask;
                        }
                    })
                    .Build();

                InitializeTokenRefresh();
                if (!string.IsNullOrEmpty(GetCurrentToken()))
                {
                    GetWowData();
                }
                else
                {
                    _logger.LogWarning("Unable to preload WoW data because the API token could not be acquired.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating WowApi class");
            }
        }

        public void GetWowData()
        {
            Races = this.GetRaces();
            Classes = this.GetWowClasses();
            Achievements cheeves = this.GetWoWAchievements();
            Achievements = cheeves.achievements.ToList();
            RealmSearch = this.GetRealmSearch();
            RealmInfo = this.GetRealmStatus("us");  
            RealmSearchEu = this.GetRealmSearch("eu");
            RealmSearchRu = this.GetRealmSearch("ru_RU", "eu");   
            RealmInfoEu = this.GetRealmStatus("eu");         
            RealmInfoRu = this.GetRealmStatus("ru_RU", "eu");                           
        }

        public static WowRealmSearch.Root RealmSearch
        {
            get
            {
                return _realmSearch;
            }
            private set
            {
                _realmSearch = value;
            }
        }     

        public static WowRealmSearch.Root RealmSearchEu
        {
            get
            {
                return _realmSearchEu;
            }
            private set
            {
                _realmSearchEu = value;
            }
        }   

        public static WowRealmSearch.Root RealmSearchRu
        {
            get
            {
                return _realmSearchRu;
            }
            private set
            {
                _realmSearchRu = value;
            }
        }                     

        public static WowRealm RealmInfo
        {
            get
            {
                return _realmInfo;
            }
            private set
            {
                _realmInfo = value;
            }
        }

        public static WowRealm RealmInfoEu
        {
            get
            {
                return _realmInfoEu;
            }
            private set
            {
                _realmInfoEu = value;
            }
        }

        public static WowRealm RealmInfoRu
        {
            get
            {
                return _realmInfoRu;
            }
            private set
            {
                _realmInfoRu = value;
            }
        }
        
        public static List<Achievement> Achievements
        {
            get
            {
                return _achievements;
            }
            private set
            {
                _achievements = value;
            }
        }

        public static Race Races
        {
            get
            {
                return _race;
            }
            private set
            {
                _race = value;
            }
        }

        public static WowClasses Classes
        {
            get
            {
                return _classes;
            }
            private set
            {
                _classes = value;
            }
        }

        public WowClasses wowclasses;

        private static string GetCurrentToken()
        {
            lock (_tokenLock)
            {
                return _token;
            }
        }

        private static void SetCurrentToken(string token)
        {
            lock (_tokenLock)
            {
                _token = token;
            }
        }

        public string GetAPIRequest(string url, string region = "us")
        {
            var normalizedRegion = region.ToLowerInvariant();
            var requestUrl = $"https://{normalizedRegion}.api.blizzard.com{url}";
            _logger.LogInformation("Wow API request to {RequestUrl}", requestUrl);
            return SendAuthorizedGet(requestUrl);
        }

        public async Task<string> GetAPIRequestAsync(string url, string region = "us", CancellationToken cancellationToken = default)
        {
            var normalizedRegion = region.ToLowerInvariant();
            var requestUrl = $"https://{normalizedRegion}.api.blizzard.com{url}";
            _logger.LogInformation("Wow API request to {RequestUrl}", requestUrl);
            return await SendAuthorizedGetAsync(requestUrl, cancellationToken);
        }

        public string GetAPIRequest(string url, bool fullUrl)
        {
            _logger.LogInformation("Wow API request to {RequestUrl}", url);
            return SendAuthorizedGet(url);
        }

        public async Task<string> GetAPIRequestAsync(string url, bool fullUrl, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Wow API request to {RequestUrl}", url);
            return await SendAuthorizedGetAsync(url, cancellationToken);
        }

        public string GetAPIRequest(string url, string locale, string region = "us")
        {
            var normalizedRegion = region.ToLowerInvariant();
            var prefix = $"https://{normalizedRegion}.api.blizzard.com";
            var localeParameter = url.Contains('=') ? $"&locale={locale}" : $"locale={locale}";
            var requestUrl = $"{prefix}{url}{localeParameter}";
            _logger.LogInformation("Wow API request to {RequestUrl}", requestUrl);
            return SendAuthorizedGet(requestUrl);
        }

        public async Task<string> GetAPIRequestAsync(string url, string locale, string region = "us", CancellationToken cancellationToken = default)
        {
            var normalizedRegion = region.ToLowerInvariant();
            var prefix = $"https://{normalizedRegion}.api.blizzard.com";
            var localeParameter = url.Contains('=') ? $"&locale={locale}" : $"locale={locale}";
            var requestUrl = $"{prefix}{url}{localeParameter}";
            _logger.LogInformation("Wow API request to {RequestUrl}", requestUrl);
            return await SendAuthorizedGetAsync(requestUrl, cancellationToken);
        }

        public async Task<string> GetWowToken(string username, string password) 
        {            
            string token = string.Empty;                        
            try
            {                             
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", username),
                    new KeyValuePair<string, string>("client_secret", password)
                });                
                var result =  await _client.PostAsync("https://us.battle.net/oauth/token", content).ConfigureAwait(false);                                            
                var contentString = await result.Content.ReadAsStringAsync().ConfigureAwait(false);                
                ApiResponse response = JsonConvert.DeserializeObject<ApiResponse>(contentString);                
                token = response.AccessToken;                                                                                                                  
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Error while getting token: [{ex.Message}]!");
                System.Console.WriteLine($"[{ex.HelpLink}]");
                System.Console.WriteLine($"[{ex.Source}]");
                System.Console.WriteLine($"[{ex.StackTrace}]");
            }    
            _logger.LogInformation("Received new WoW API auth token.");
            return token;
        }

        public WowConnectedRealm GetConnectedRealmInfo(int realmId, string regionName = "us")
        {
            string url;
            WowConnectedRealm w = new WowConnectedRealm();
            string locale = GetRegionFromString(regionName);
            string realmSlug = string.Empty;             
            url = $"/data/wow/connected-realm/{realmId}?namespace=dynamic-{regionName}";
            w = JsonConvert.DeserializeObject<WowConnectedRealm>(GetAPIRequest(url, locale: locale, region: regionName));
            return w;
        }

        public WowConnectedRealm GetConnectedRealmInfo(string href, string regionName = "us")
        {            
            string localeName = GetRegionFromString(regionName);
            string url = $"{href}&locale={localeName}";
            WowConnectedRealm w = new WowConnectedRealm();
            w = JsonConvert.DeserializeObject<WowConnectedRealm>(GetAPIRequest(url, true));
            return w;
        }        

        public WowRealmSearch.Root GetRealmSearch(string locale = "us")
        {
            string localeName = GetRegionFromString(locale);
            
            WowRealmSearch.Root w = new WowRealmSearch.Root();            
            string url = $"/data/wow/search/realm?namespace=dynamic-{locale}&orderby=id&_pageSize=1000";
            w = JsonConvert.DeserializeObject<WowRealmSearch.Root>(GetAPIRequest(url, localeName, locale));
            return w;
        }

        public WowRealmSearch.Root GetRealmSearch(string locale, string regionName)
        {
            string localeName = string.Empty;            
            if (locale.Length == 5)
            {
                localeName = GetRegionFromString(locale.Substring(3).ToLower());
            }
            else if (locale.Length == 2)
            {
                localeName = GetRegionFromString(locale);
            }
            WowRealmSearch.Root w = new WowRealmSearch.Root();            
            string url = $"/data/wow/search/realm?namespace=dynamic-{regionName}&orderby=id&_pageSize=1000";
            w = JsonConvert.DeserializeObject<WowRealmSearch.Root>(GetAPIRequest(url, locale: localeName, region: regionName));
            return w;
        }

        public WowRealm GetRealmStatus(string locale = "us")
        {
            string localeName = GetRegionFromString(locale);
            WowRealm w = new WowRealm();
            string url = $"/data/wow/realm/index?namespace=dynamic-{locale}";
            w = JsonConvert.DeserializeObject<WowRealm>(GetAPIRequest(url, localeName, locale));
            return w;
        }

        public WowSingleRealmInfo GetSingleRealmInfo(string realmSlug, string regionName = "us")
        {
            string url;
            string locale = GetRegionFromString(regionName);
            WowSingleRealmInfo w = new WowSingleRealmInfo();
            url = $"/data/wow/realm/{realmSlug}?namespace=dynamic-{regionName}";
            w = JsonConvert.DeserializeObject<WowSingleRealmInfo>(GetAPIRequest(url, locale: locale, region: regionName));
            return w;
        }

        public WowRealm GetRealmStatus(string locale, string region)
        {
            string localeName = string.Empty;
            
            if (locale.Length == 5)
            {
                localeName = GetRegionFromString(locale.Substring(3).ToLower());
            }
            else if (locale.Length == 2)
            {
                localeName = GetRegionFromString(locale);
            }
            
            WowRealm w = new WowRealm();
            string url = $"/data/wow/realm/index?namespace=dynamic-{region}";
            w = JsonConvert.DeserializeObject<WowRealm>(GetAPIRequest(url, locale: localeName, region: region));
            return w;
        }
        

        public Character GetCharInfo(string name, string realm, string regionName = "us")
        {
            string url;
            string region = string.Empty;
            region = GetRegionFromString(regionName);
            Character c = new Character();
            realm = realm.Replace("'", string.Empty).Replace(" ", "-");
            url = $"/profile/wow/character/{realm}/{name}";            
            if (region != "en_US")
            {
                c = JsonConvert.DeserializeObject<Character>(GetAPIRequest(url, "eu"));
            }
            else
            {
                c = JsonConvert.DeserializeObject<Character>(GetAPIRequest(url));
            }
            string thumbUrl = $"http://render-{regionName}.worldofwarcraft.com/character/{c.thumbnail}";
            c.thumbnailURL = thumbUrl;
            string insetUrl = $"http://render-{regionName}.worldofwarcraft.com/character/{c.thumbnail.Replace("-avatar", "-inset")}";
            c.insetURL = insetUrl;
            string profilePicUrl = $"http://render-{regionName}.worldofwarcraft.com/character/{c.thumbnail.Replace("-avatar", "-profilemain")}";
            c.profilePicURL = profilePicUrl;
                        
            string armoryUrl = $"https://worldofwarcraft.com/{region}/character/{regionName}/{c.realm.slug.Replace(" ","-")}/{c.name}";
            c.armoryURL = armoryUrl;
            return c;
        }

        private static string GetRegionFromString(string regionName)
        {
            string region;
            switch (regionName.ToLower())
            {
                case "us":
                    {
                        region = "en_US";
                        break;
                    }
                case "uk":
                    {
                        region = "en_GB";
                        break;
                    }
                case "gb":
                    {
                        region = "en_GB";
                        break;
                    }
                case "eu":
                    {
                        region = "en_GB";
                        break;
                    }
                case "ru":
                    {
                        region = "ru_RU";
                        break;
                    }
                default:
                    {
                        region = "en_US";
                        break;
                    }
            }
            return region;
        }

        private string SendAuthorizedGet(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var token = GetCurrentToken();
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Blizzard API access token has not been initialized.");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return SendRequest(request);
        }

        private async Task<string> SendAuthorizedGetAsync(string url, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var token = GetCurrentToken();
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Blizzard API access token has not been initialized.");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await SendRequestAsync(request, cancellationToken);
        }

        private string SendRequest(HttpRequestMessage request)
        {
            try
            {
                using var response = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error executing WoW API request to {RequestUrl}", request.RequestUri);
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "WoW API request timed out for {RequestUrl}", request.RequestUri);
                throw;
            }
        }

        private async Task<string> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpResiliencePipeline.ExecuteAsync(
                    async ct => await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct),
                    cancellationToken);

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error executing WoW API request to {RequestUrl} after retries", request.RequestUri);
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "WoW API request timed out for {RequestUrl}", request.RequestUri);
                throw;
            }
        }

        public ItemInfo GetItemInfo(int itemID)
        {
            string url;
            ItemInfo i;

            url = $"/item/{itemID}?locale=en";

            i = JsonConvert.DeserializeObject<ItemInfo>(GetAPIRequest(url));

            return i;
        }

        public Race GetRaces()
        {
            string url;
            Race r;

            url = "/data/wow/playable-race/index?namespace=static-us&locale=en_US";
            r = JsonConvert.DeserializeObject<Race>(GetAPIRequest(url));

            return r;
        }

        public WowClasses GetWowClasses()
        {
            WowClasses c;
            string url;

            url = "/data/wow/playable-class/index?namespace=static-us&locale=en_US";
            c = JsonConvert.DeserializeObject<WowClasses>(GetAPIRequest(url));

            return c;
        }

        public TalentList GetWowTalents()
        {
            TalentList talents;
            string url;

            url = "/data/wow/talent/index?namespace=static-us&locale=en_US";

            talents = JsonConvert.DeserializeObject<TalentList>(GetAPIRequest(url));

            return talents;
        }

        public Achievements GetWoWAchievements()
        {
            Achievements a;
            string url;

            url = "/data/wow/achievement/index?namespace=static-us&locale=en_US";
            a = JsonConvert.DeserializeObject<Achievements>(GetAPIRequest(url));

            return a;
        }

        public CharAchievements GetCharAchievements(string charName, string realmName)
        {
            AchievementChar charAchievements;

            string url = $"/character/{realmName}/{charName}?fields=achievements&locale=en_US";

            charAchievements = JsonConvert.DeserializeObject<AchievementChar>(GetAPIRequest(url));

            CharAchievements c = charAchievements.achievements;

            return c;
        }

        public GuildMembers GetGuildMembers(string realm, string guildName, string regionName = "us")
        {
            string url;
            GuildMembers g;
            string locale = GetRegionFromString(regionName);
            url = $"/data/wow/guild/{realm}/{guildName.Replace(" ","-").Replace("%20","-").ToLower()}/roster?namespace=profile-{regionName}";
            if (locale != "en_US")
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url, region: "eu", locale: locale ));
            }
            else
            {
                url = $"{url}&locale=en_US";
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url));
            }
            return g;
        }
        
        public GuildMembers GetGuildMembers(string realm, string guildName, string locale, string regionName = "us")
        {
            string url;
            GuildMembers g;               
            string realmSlug = string.Empty;        
            switch (locale)
            {
                case "ru_RU":
                    {                                                        
                        realmSlug = RealmSearchRu.results.Where(r => r.data.name.ru_RU.Replace("'","").ToLower().Contains(realm.Replace("'","").ToLower())).Select(s => s.data.slug).FirstOrDefault();
                        break;
                    }
                case "en_GB":
                    {                            
                        realmSlug = RealmSearchEu.results.Where(r => r.data.name.en_GB.Replace("'","").ToLower().Contains(realm.Replace("'","").ToLower())).Select(s => s.data.slug).FirstOrDefault();
                        break;
                    }
                case "en_US":
                    {   
                        //realmSlug = slugs.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realm.Replace("'","").ToLower())).Select(s => s.slug).FirstOrDefault();      
                        realmSlug = RealmSearch.results.Where(r => r.data.name.en_US.Replace("'","").ToLower().Contains(realm.Replace("'","").ToLower())).Select(s => s.data.slug).FirstOrDefault();                                           
                        break;
                    }
                default: 
                    {                            
                        realmSlug = RealmSearch.results.Where(r => r.data.name.en_US.Replace("'","").ToLower().Contains(realm.Replace("'","").ToLower())).Select(s => s.data.slug).FirstOrDefault();
                        break;
                    }
            }   
            if (regionName != "us")
            {
                regionName = "eu";
            }         
            url = $"/data/wow/guild/{realmSlug}/{guildName.ToLower().Replace(" ","-")}/roster?namespace=profile-{regionName}";
            if (locale != "en_US")
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url, region: "eu", locale: locale));
            }
            else
            {
                url = $"{url}&locale=en_US";
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url));
            }
            return g;
        }

        public GuildMembers GetGuildMembersBySlug(string slug, string guildName, string locale, string regionName = "us")
        {
            string url;
            GuildMembers g;
            var slugs = GetRealmStatus(locale: locale, region: regionName);                
            string realmSlug = slug;                        
            url = $"/data/wow/guild/{realmSlug}/{guildName.ToLower().Replace(" ","-")}/roster?namespace=profile-{regionName}";
            if (locale != "en_US")
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url, region: "eu", locale: locale));
            }
            else
            {
                url = $"{url}&locale=en_US";
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url));
            }
            return g;
        }

        public GuildMembers GetGuildMembersBySlug(string slug, string guildName, string regionName = "us")
        {
            string url;
            GuildMembers g;             
            string realmSlug = slug;                        
            url = $"/data/wow/guild/{realmSlug}/{guildName.ToLower().Replace(" ","-")}/roster?namespace=profile-{regionName}"; 
            url = $"{url}&locale=en_US";       
            g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url));
            return g;
        }

        public WoWTalentMain GetCharTalents(string charName, string realmName)
        {
            WoWTalentMain charTalents;

            string url;

            url = $"/character/{realmName}/{charName}?fields=talents&locale=en_US";

            charTalents = JsonConvert.DeserializeObject<WoWTalentMain>(GetAPIRequest(url));

            return charTalents;
        }

        public WowStats GetCharStats(string charName, string realmName, string locale)
        {
            string region = string.Empty;
            WowStats g;
            string url;

            if (locale != "en_US")
            {
                region = "eu";
            }
            else
            {
                region = "us";
            }

            url = $"/character/{realmName}/{charName}?fields=statistics&locale={locale}";
            g = JsonConvert.DeserializeObject<WowStats>(GetAPIRequest(url,region: region));

            return g;
        }

        public DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddMilliseconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public DateTime UnixTimeStampToDateTimeSeconds(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public GuildChar GetCharFromGuild(string findName, string realmName, string guildName, string regionName = "us")
        {
            GuildMembers members = new GuildMembers();
            string matchedName = string.Empty;
            GuildChar guildInfo = new GuildChar();
            Regex myRegex = new Regex($@"{findName.ToLower()}");
            guildName = guildName.Replace(" ", "%20");
            try
            {
                members = GetGuildMembers(realmName, guildName, regionName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex.Message}");
            }
            bool done = false;
            while (!done)
            {            
                foreach (Member member in members.members.OrderByDescending(m => m.character.name))
                {

                    string curMember = string.Empty;
                    curMember = member.character.name;
                    MatchCollection m = myRegex.Matches(curMember.ToLower());

                    switch (m.Count)
                    {
                        case 1:
                            {
                                matchedName = curMember;
                                realmName = member.character.realm.slug;

                                guildInfo.charName = curMember;
                                guildInfo.realmName = realmName;
                                guildInfo.regionName = regionName;
                                done = true;
                                break;
                            }
                        default:
                            {
                                break;
                            }
                    }
                }    
            }

            return guildInfo;
        }

        public List<FoundChar> SearchArmory(string searchFor)
        {
            string url = $"https://worldofwarcraft.com/en-us/search?q={searchFor}";
            string url_string = string.Empty;
            HtmlDocument document = new HtmlDocument();
            using (var httpclient = new HttpClient())
            {
                url_string = httpclient.GetStringAsync(url).Result;
            }
            document.LoadHtml(url_string);
            List<FoundChar> chars = new List<FoundChar>();
            FoundChar found = new FoundChar();
            try
            {
                foreach (HtmlNode div in document.DocumentNode.SelectNodes("//div[contains(@class,'Character-')]"))
                {

                    if ((div.Attributes[0].Value) == "Character-name")
                    {
                        found.charName = div.InnerText;
                    }

                    if ((div.Attributes[0].Value) == "Character-level")
                    {
                        found.level = div.InnerText;
                    }

                    if ((div.Attributes[0].Value) == "Character-realm")
                    {
                        found.realmName = div.InnerText;
                        chars.Add(found);
                        found = new FoundChar();
                    }

                }

            }
            catch (Exception ex)
            {
                _logger.LogError($"WoW Char Search Error: {ex.Message}");
                chars = null;
            }
            return chars;
        }
        
        private void InitializeTokenRefresh()
        {
            _tokenRefreshCancellation = new CancellationTokenSource();
            RenewTokenAsync(_tokenRefreshCancellation.Token).GetAwaiter().GetResult();
            _tokenRefreshTask = RunTokenRefreshLoopAsync(_tokenRefreshCancellation.Token);
        }

        private async Task RunTokenRefreshLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(_tokenRefreshInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await RenewTokenAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when disposing the service.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while refreshing WoW API auth token.");
            }
        }

        private async Task RenewTokenAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                _logger.LogInformation("Refreshing WoW API auth token.");
                var newToken = await GetWowToken(_config["WoWClient"], _config["WoWSecret"]).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(newToken))
                {
                    SetCurrentToken(newToken);
                    _logger.LogInformation("WoW API auth token refreshed successfully.");
                }
                else
                {
                    _logger.LogWarning("Received empty WoW API auth token while refreshing credentials.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh WoW API auth token.");
            }
        }
    }
}
