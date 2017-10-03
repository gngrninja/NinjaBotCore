using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using Newtonsoft.Json;
using NinjaBotCore.Models.RocketLeague;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.RocketLeague
{
    public class RlStatsApi
    {
        private static List<Platforms> _platforms = null;
        private static List<Tier> _tiers = null;

        public RlStatsApi()
        {
            Platforms = GetCurrentPlatforms();
            Tiers = GetTiers();
        }

        public static List<Platforms> Platforms
        {
            get
            {
                return _platforms;
            }
            set
            {
                _platforms = value;
            }
        }

        public static List<Tier> Tiers 
        {
            get 
            {
                return _tiers;
            }   
            set 
            {
                _tiers = value;
            } 
        }

        public string RlStatsApiRequest(string url)
        {
            string response = string.Empty;
            string rlStatsKey = $"{Config.RlStatsApi}";
            string baseUrl = "https://api.rocketleaguestats.com/v1";

            url = $"{baseUrl}{url}";
            Console.WriteLine($"Calling RlStats API with URL: {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {rlStatsKey}");
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;             
                response = httpClient.GetStringAsync(url).Result;
            }
            return response;
        }

        public List<Platforms> GetCurrentPlatforms()
        {
            List<Platforms> p = null;
            string url = "/data/platforms";
            p = JsonConvert.DeserializeObject<List<Platforms>>(RlStatsApiRequest(url));
            return p;
        }

        public async Task<UserStats> GetUserStats(long uniqueId, int platformId = 1)
        {
            UserStats s = null;
            string url = $"/player?unique_id={uniqueId}&platform_id={platformId}";
            s = JsonConvert.DeserializeObject<UserStats>(RlStatsApiRequest(url));
            return s;
        }

        public async Task<SearchResult> SearchForPlayer(string playerName)
        {
            SearchResult searchResults = null;
            string url = $"/search/players?display_name={playerName}";
            searchResults = JsonConvert.DeserializeObject<SearchResult>(RlStatsApiRequest(url));
            return searchResults;
        }

        public async Task<UserStats> SearchForPlayer(long? uniqueId, int platformId = 1)
        {
            UserStats userStats = null;
            string url = $"/player?unique_id={uniqueId.ToString()}&platform_id={platformId}";
            userStats = JsonConvert.DeserializeObject<UserStats>(RlStatsApiRequest(url));
            return userStats;
        }

        public List<Tier> GetTiers()
        {
            List<Tier> tiers = new List<Tier>();
            string url = "/data/tiers";
            tiers = JsonConvert.DeserializeObject<List<Tier>>(RlStatsApiRequest(url));
            return tiers;
        }

        public DateTime UnixTimeStampToDateTimeSeconds(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddMilliseconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }
    }
}