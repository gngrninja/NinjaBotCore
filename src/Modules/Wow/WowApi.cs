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
        private readonly TaskCompletionSource<bool> _initializationComplete = new();
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
                var innerLogger = services.GetRequiredService<ILogger<WowApi>>();
                _logger = new SanitizingLogger<WowApi>(innerLogger);

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

                // Start background token refresh - GetWowData will be called after first token is acquired
                InitializeTokenRefresh();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating WowApi class");
            }
        }

        /// <summary>
        /// Async version of GetWowData for non-blocking initialization
        /// </summary>
        public async Task GetWowDataAsync(CancellationToken cancellationToken = default)
        {
            Races = await GetRacesAsync(cancellationToken);
            Classes = await GetWowClassesAsync(cancellationToken);
            var cheeves = await GetWoWAchievementsAsync(cancellationToken);
            Achievements = cheeves.achievements.ToList();
            RealmSearch = await GetRealmSearchAsync("us", cancellationToken);
            RealmInfo = await GetRealmStatusAsync("en_US", "us", cancellationToken);
            RealmSearchEu = await GetRealmSearchAsync("eu", cancellationToken);
            RealmSearchRu = await GetRealmSearchAsync("ru_RU", "eu", cancellationToken);
            RealmInfoEu = await GetRealmStatusAsync("en_GB", "eu", cancellationToken);
            RealmInfoRu = await GetRealmStatusAsync("ru_RU", "eu", cancellationToken);
        }

        /// <summary>
        /// Wait for the WoW API to complete initialization (token acquisition and data preload)
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if initialization succeeded, false if it failed</returns>
        public async Task<bool> WaitForInitializationAsync(CancellationToken cancellationToken = default)
        {
            var completedTask = await Task.WhenAny(_initializationComplete.Task, Task.Delay(Timeout.Infinite, cancellationToken));

            if (completedTask == _initializationComplete.Task)
            {
                return await _initializationComplete.Task;
            }

            // Cancellation was requested
            return false;
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

        public async Task<string> GetAPIRequestAsync(string url, string region = "us", CancellationToken cancellationToken = default)
        {
            var normalizedRegion = region.ToLowerInvariant();
            var requestUrl = $"https://{normalizedRegion}.api.blizzard.com{url}";
            _logger.LogInformation("WoW API request to {RequestUrl}", requestUrl);
            return await SendAuthorizedGetAsync(requestUrl, cancellationToken);
        }

        public async Task<string> GetAPIRequestAsync(string url, bool fullUrl, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("WoW API request to {RequestUrl}", url);
            return await SendAuthorizedGetAsync(url, cancellationToken);
        }

        public async Task<string> GetAPIRequestAsync(string url, string locale, string region = "us", CancellationToken cancellationToken = default)
        {
            var normalizedRegion = region.ToLowerInvariant();
            var prefix = $"https://{normalizedRegion}.api.blizzard.com";
            var localeParameter = url.Contains('=') ? $"&locale={locale}" : $"locale={locale}";
            var requestUrl = $"{prefix}{url}{localeParameter}";
            _logger.LogInformation("WoW API request to {RequestUrl}", requestUrl);
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
                var result =  await _client.PostAsync("https://us.battle.net/oauth/token", content);
                var contentString = await result.Content.ReadAsStringAsync();
                ApiResponse response = JsonConvert.DeserializeObject<ApiResponse>(contentString);
                token = response.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while getting WoW API token");
            }
            _logger.LogInformation("Received new WoW API auth token.");
            return token;
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

        private async Task<string> SendAuthorizedGetAsync(string url, CancellationToken cancellationToken = default)
        {
            var token = GetCurrentToken();
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Blizzard API access token has not been initialized.");
            }

            // Use request factory to create new request for each retry attempt
            return await SendRequestAsync(url, token, cancellationToken);
        }

        private async Task<string> SendRequestAsync(string url, string token, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpResiliencePipeline.ExecuteAsync(
                    async ct =>
                    {
                        // Create new request for each retry attempt
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    },
                    cancellationToken);

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error executing WoW API request to {RequestUrl} after retries", url);
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "WoW API request timed out for {RequestUrl}", url);
                throw;
            }
        }

        // UnixTimeStampToDateTime methods moved to NinjaExtensions.cs as extension methods
        // Use: timestamp.UnixTimeStampToDateTime() or timestamp.UnixTimeStampToDateTimeSeconds()

        private void InitializeTokenRefresh()
        {
            _tokenRefreshCancellation = new CancellationTokenSource();
            // Start background token refresh loop - first refresh happens immediately in the loop
            _tokenRefreshTask = RunTokenRefreshLoopAsync(_tokenRefreshCancellation.Token);
        }

        private async Task RunTokenRefreshLoopAsync(CancellationToken token)
        {
            // Perform initial token fetch immediately
            await RenewTokenAsync(token);

            // After first successful token fetch, preload WoW data
            if (!string.IsNullOrEmpty(GetCurrentToken()))
            {
                try
                {
                    _logger.LogInformation("Token acquired successfully, preloading WoW data...");
                    await GetWowDataAsync(token);
                    _logger.LogInformation("WoW data preloaded successfully");
                    _initializationComplete.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to preload WoW data, will be loaded on-demand");
                    _initializationComplete.TrySetResult(false);
                }
            }
            else
            {
                _logger.LogWarning("Unable to preload WoW data because the API token could not be acquired. Data will be loaded on-demand.");
                _initializationComplete.TrySetResult(false);
            }

            // Then continue with periodic refresh
            using var timer = new PeriodicTimer(_tokenRefreshInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    await RenewTokenAsync(token);
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
                var newToken = await GetWowToken(_config["WoWClient"], _config["WoWSecret"]);
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

        #region Async Versions for Slash Commands

        /// <summary>
        /// Async version of GetCharInfo for use in slash commands
        /// </summary>
        public async Task<Character> GetCharInfoAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}?namespace=profile-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<Character>(response);
        }

        public async Task<ArmorySummary> GetArmorySummaryAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}?namespace=profile-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmorySummary>(response);
        }

        public async Task<ArmoryEquipment> GetArmoryEquipmentAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/equipment?namespace=profile-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryEquipment>(response);
        }

        public async Task<ArmoryMedia> GetArmoryMediaAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/character-media?namespace=profile-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryMedia>(response);
        }

        public async Task<ArmoryItemMedia> GetItemMediaAsync(int itemId, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/data/wow/media/item/{itemId}?namespace=static-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryItemMedia>(response);
        }

        public async Task<ArmoryItemMedia> GetCreatureDisplayMediaAsync(long displayId, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/data/wow/media/creature-display/{displayId}?namespace=static-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryItemMedia>(response);
        }

        public async Task<MountCollectionResponse> GetCharacterMountsAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/collections/mounts?namespace=profile-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<MountCollectionResponse>(response);
        }

        public async Task<Models.Wow.Housing.DecorCollectionResponse> GetCharacterDecorAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/collections/decor?namespace=profile-{regionSegment}";

            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<Models.Wow.Housing.DecorCollectionResponse>(response);
        }

        /// <summary>
        /// Async version of GetConnectedRealmInfo for use in slash commands
        /// </summary>
        public async Task<WowConnectedRealm> GetConnectedRealmInfoAsync(int realmId, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string locale = GetRegionFromString(regionName);
            string url = $"/data/wow/connected-realm/{realmId}?namespace=dynamic-{regionName}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<WowConnectedRealm>(response);
        }

        /// <summary>
        /// Async version of GetConnectedRealmInfo (href overload) for use in slash commands
        /// </summary>
        public async Task<WowConnectedRealm> GetConnectedRealmInfoAsync(string href, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string localeName = GetRegionFromString(regionName);
            string url = $"{href}&locale={localeName}";
            var response = await GetAPIRequestAsync(url, true, cancellationToken);
            return JsonConvert.DeserializeObject<WowConnectedRealm>(response);
        }

        /// <summary>
        /// Async version of GetSingleRealmInfo for use in slash commands
        /// </summary>
        public async Task<WowSingleRealmInfo> GetSingleRealmInfoAsync(string realmSlug, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string locale = GetRegionFromString(regionName);
            string url = $"/data/wow/realm/{realmSlug}?namespace=dynamic-{regionName}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<WowSingleRealmInfo>(response);
        }

        /// <summary>
        /// Async version of GetRealmStatus for use in slash commands
        /// </summary>
        public async Task<WowRealm> GetRealmStatusAsync(string locale, string region, CancellationToken cancellationToken = default)
        {
            string localeName = string.Empty;

            if (locale.Length == 5)
            {
                localeName = locale;
            }
            else
            {
                localeName = GetRegionFromString(locale);
            }

            string url = $"/data/wow/realm/index?namespace=dynamic-{region}";
            var response = await GetAPIRequestAsync(url, localeName, region, cancellationToken);
            return JsonConvert.DeserializeObject<WowRealm>(response);
        }

        /// <summary>
        /// Async version of GetRaces for use in initialization
        /// </summary>
        public async Task<Race> GetRacesAsync(CancellationToken cancellationToken = default)
        {
            string url = "/data/wow/playable-race/index?namespace=static-us&locale=en_US";
            var response = await GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
            return JsonConvert.DeserializeObject<Race>(response);
        }

        /// <summary>
        /// Async version of GetWowClasses for use in initialization
        /// </summary>
        public async Task<WowClasses> GetWowClassesAsync(CancellationToken cancellationToken = default)
        {
            string url = "/data/wow/playable-class/index?namespace=static-us&locale=en_US";
            var response = await GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
            return JsonConvert.DeserializeObject<WowClasses>(response);
        }

        /// <summary>
        /// Async version of GetWoWAchievements for use in initialization
        /// </summary>
        public async Task<Achievements> GetWoWAchievementsAsync(CancellationToken cancellationToken = default)
        {
            string url = "/data/wow/achievement/index?namespace=static-us&locale=en_US";
            var response = await GetAPIRequestAsync(url, "en_US", "us", cancellationToken);
            return JsonConvert.DeserializeObject<Achievements>(response);
        }

        /// <summary>
        /// Async version of GetRealmSearch for use in initialization
        /// </summary>
        public async Task<WowRealmSearch.Root> GetRealmSearchAsync(string locale = "us", CancellationToken cancellationToken = default)
        {
            string localeName = GetRegionFromString(locale);
            string url = $"/data/wow/search/realm?namespace=dynamic-{locale}&orderby=id&_pageSize=1000";
            var response = await GetAPIRequestAsync(url, localeName, locale, cancellationToken);
            return JsonConvert.DeserializeObject<WowRealmSearch.Root>(response);
        }

        /// <summary>
        /// Async version of GetRealmSearch with region for use in initialization
        /// </summary>
        public async Task<WowRealmSearch.Root> GetRealmSearchAsync(string locale, string regionName, CancellationToken cancellationToken = default)
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
            string url = $"/data/wow/search/realm?namespace=dynamic-{regionName}&orderby=id&_pageSize=1000";
            var response = await GetAPIRequestAsync(url, localeName, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<WowRealmSearch.Root>(response);
        }

        /// <summary>
        /// Async version of GetGuildMembers for use in slash commands
        /// </summary>
        public async Task<GuildMembers> GetGuildMembersAsync(string realm, string guildName, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string locale = GetRegionFromString(regionName);
            string url = $"/data/wow/guild/{realm}/{guildName.Replace(" ","-").Replace("%20","-").ToLower()}/roster?namespace=profile-{regionName}";

            string response;
            if (locale != "en_US")
            {
                response = await GetAPIRequestAsync(url, locale, "eu", cancellationToken);
            }
            else
            {
                url = $"{url}&locale=en_US";
                response = await GetAPIRequestAsync(url, regionName, cancellationToken);
            }

            return JsonConvert.DeserializeObject<GuildMembers>(response);
        }

        /// <summary>
        /// Async version of GetGuildMembersBySlug for use in slash commands
        /// </summary>
        public async Task<GuildMembers> GetGuildMembersBySlugAsync(string slug, string guildName, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string url = $"/data/wow/guild/{slug}/{guildName.ToLower().Replace(" ","-")}/roster?namespace=profile-{regionName}";
            url = $"{url}&locale=en_US";

            var response = await GetAPIRequestAsync(url, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<GuildMembers>(response);
        }

        /// <summary>
        /// Async version of GetGuildMembersBySlug (with locale) for use in slash commands
        /// </summary>
        public async Task<GuildMembers> GetGuildMembersBySlugAsync(string slug, string guildName, string locale, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string url = $"/data/wow/guild/{slug}/{guildName.ToLower().Replace(" ","-")}/roster?namespace=profile-{regionName}";

            string response;
            if (locale != "en_US")
            {
                response = await GetAPIRequestAsync(url, locale, "eu", cancellationToken);
            }
            else
            {
                url = $"{url}&locale=en_US";
                response = await GetAPIRequestAsync(url, regionName, cancellationToken);
            }

            return JsonConvert.DeserializeObject<GuildMembers>(response);
        }

        /// <summary>
        /// Async version of GetCharFromGuild for use in slash commands
        /// </summary>
        public async Task<GuildChar> GetCharFromGuildAsync(string findName, string realmName, string guildName, string regionName = "us", CancellationToken cancellationToken = default)
        {
            GuildMembers members;
            string matchedName = string.Empty;
            GuildChar guildInfo = new GuildChar();
            Regex myRegex = new Regex($@"{findName.ToLower()}");
            guildName = guildName.Replace(" ", "%20");

            try
            {
                members = await GetGuildMembersAsync(realmName, guildName, regionName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting guild members for {Guild} on {Realm}", guildName, realmName);
                return guildInfo;
            }

            var match = members.members.FirstOrDefault(m => myRegex.IsMatch(m.character.name.ToLower()));

            if (match != null)
            {
                matchedName = match.character.name;
                guildInfo.charName = matchedName;
                guildInfo.realmName = realmName;
                guildInfo.regionName = regionName;
            }

            return guildInfo;
        }

        /// <summary>
        /// Async version of SearchArmory for use in slash commands
        /// </summary>
        public async Task<List<FoundChar>> SearchArmoryAsync(string searchFor, CancellationToken cancellationToken = default)
        {
            string url = $"https://worldofwarcraft.com/en-us/search?q={searchFor}";
            string url_string;
            HtmlDocument document = new HtmlDocument();

            using (var httpclient = _client)
            {
                url_string = await httpclient.GetStringAsync(url, cancellationToken);
            }

            document.LoadHtml(url_string);
            List<FoundChar> chars = new List<FoundChar>();

            var CharDivs = document.DocumentNode.Descendants("div").Where(d => d.Attributes.Contains("class") && d.Attributes["class"].Value.Contains("Character-link"));

            foreach (var div in CharDivs)
            {
                var charName = div.Descendants("div").FirstOrDefault(d => d.Attributes.Contains("class") && d.Attributes["class"].Value.Contains("Character-name"));
                var charLevel = div.Descendants("div").FirstOrDefault(d => d.Attributes.Contains("class") && d.Attributes["class"].Value.Contains("Character-level"));
                var charRealm = div.Descendants("span").FirstOrDefault(d => d.Attributes.Contains("class") && d.Attributes["class"].Value.Contains("Character-realm"));

                if (charName != null && charRealm != null)
                {
                    FoundChar foundChar = new FoundChar
                    {
                        charName = charName.InnerText,
                        realmName = charRealm.InnerText,
                        level = charLevel?.InnerText ?? "?"
                    };
                    chars.Add(foundChar);
                }
            }

            return chars;
        }

        /// <summary>
        /// Get journal encounter details by ID
        /// </summary>
        public async Task<JournalEncounterResponse> GetJournalEncounterAsync(long encounterId, string region = "us", CancellationToken cancellationToken = default)
        {
            var url = $"/data/wow/journal-encounter/{encounterId}?namespace=static-{region}";
            var response = await GetAPIRequestAsync(url, "en_US", region, cancellationToken);
            return JsonConvert.DeserializeObject<JournalEncounterResponse>(response);
        }

        /// <summary>
        /// Get journal instance details by ID
        /// </summary>
        public async Task<JournalInstanceResponse> GetJournalInstanceAsync(long instanceId, string region = "us", CancellationToken cancellationToken = default)
        {
            var url = $"/data/wow/journal-instance/{instanceId}?namespace=static-{region}";
            var response = await GetAPIRequestAsync(url, "en_US", region, cancellationToken);
            return JsonConvert.DeserializeObject<JournalInstanceResponse>(response);
        }

        /// <summary>
        /// Get journal encounter index (for caching)
        /// </summary>
        public async Task<JournalEncounterIndexResponse> GetJournalEncounterIndexAsync(string region = "us", CancellationToken cancellationToken = default)
        {
            var url = $"/data/wow/journal-encounter/index?namespace=static-{region}";
            var response = await GetAPIRequestAsync(url, "en_US", region, cancellationToken);
            return JsonConvert.DeserializeObject<JournalEncounterIndexResponse>(response);
        }

        /// <summary>
        /// Get journal instance index (for caching)
        /// </summary>
        public async Task<JournalInstanceIndexResponse> GetJournalInstanceIndexAsync(string region = "us", CancellationToken cancellationToken = default)
        {
            var url = $"/data/wow/journal-instance/index?namespace=static-{region}";
            var response = await GetAPIRequestAsync(url, "en_US", region, cancellationToken);
            return JsonConvert.DeserializeObject<JournalInstanceIndexResponse>(response);
        }

        /// <summary>
        /// Get guild roster with character details
        /// </summary>
        public async Task<ArmoryGuildRoster> GetGuildRosterAsync(string guildName, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            guildName = guildName.Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/data/wow/guild/{realm}/{guildName}/roster?namespace=profile-{regionSegment}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryGuildRoster>(response);
        }

        /// <summary>
        /// Get character's Mythic Keystone profile with M+ rating
        /// </summary>
        public async Task<ArmoryMythicKeystoneProfile> GetMythicKeystoneProfileAsync(string name, string realm, string regionName = "us", int? seasonId = null, CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var seasonPath = seasonId.HasValue ? $"/season/{seasonId}" : "";
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/mythic-keystone-profile{seasonPath}?namespace=profile-{regionSegment}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryMythicKeystoneProfile>(response);
        }

        /// <summary>
        /// Get character's PvP summary with arena and RBG ratings
        /// </summary>
        public async Task<ArmoryPvPSummary> GetPvPSummaryAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/pvp-summary?namespace=profile-{regionSegment}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryPvPSummary>(response);
        }

        /// <summary>
        /// Get character's achievements summary with recent achievements
        /// </summary>
        public async Task<ArmoryAchievementsSummary> GetAchievementsSummaryAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            realm = realm.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/profile/wow/character/{realm}/{name.ToLowerInvariant()}/achievements?namespace=profile-{regionSegment}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryAchievementsSummary>(response);
        }

        /// <summary>
        /// Get connected realms index for a region (for batch status checking)
        /// </summary>
        public async Task<ArmoryConnectedRealmsIndex> GetConnectedRealmsIndexAsync(string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/data/wow/connected-realm/index?namespace=dynamic-{regionSegment}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryConnectedRealmsIndex>(response);
        }

        /// <summary>
        /// Get connected realm status with queue info
        /// </summary>
        public async Task<ArmoryConnectedRealmStatus> GetConnectedRealmStatusAsync(long connectedRealmId, string regionName = "us", CancellationToken cancellationToken = default)
        {
            var locale = GetRegionFromString(regionName);
            var regionSegment = regionName.ToLowerInvariant();
            var url = $"/data/wow/connected-realm/{connectedRealmId}?namespace=dynamic-{regionSegment}";
            var response = await GetAPIRequestAsync(url, locale, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ArmoryConnectedRealmStatus>(response);
        }

        #endregion
    }
}
