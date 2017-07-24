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

        public RlStatsApi()
        {
            Platforms = GetCurrentPlatforms();
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
                httpClient.DefaultRequestHeaders.Add("Authorization",$"Bearer {rlStatsKey}");
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
    }
}