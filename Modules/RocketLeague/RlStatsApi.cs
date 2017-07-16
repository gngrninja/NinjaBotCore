using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using Newtonsoft.Json;
using NinjaBotCore.Models.RocketLeague;

namespace NinjaBotCore.Modules.RocketLeague
{
    public class RlStatsApi
    {
        public string RlStatsApiRequest(string url)
        {
            string response = string.Empty;
            string rlStatsKey = $"{Config.RlStatsApi}";
            string baseUrl = "https://api.rocketleaguestats.com/v1/data";

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
            string url = "/platforms";
            p = JsonConvert.DeserializeObject<List<Platforms>>(RlStatsApiRequest(url));
            return p;
        }        
    }
}