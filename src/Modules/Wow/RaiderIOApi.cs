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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaBotCore.Modules.Wow
{
    public class RaiderIOApi
    {

        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;

        public RaiderIOApi(IServiceProvider services)
        {
            try
            {
                _logger = services.GetRequiredService<ILogger<RaiderIOApi>>();
                _config = services.GetRequiredService<IConfigurationRoot>();
          
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error creating RaiderIO class -> [{ex.Message}]");
            }
        }


        public string GetApiRequest(string url, string region = "us", string locale = "en")
        {
            string response;            
            string prefix;
            
            prefix = $"https://raider.io/api/v1";
            url = $"{prefix}{url}?region={region}&locale={locale}";

            _logger.LogInformation($"RaiderIO API request to {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;                             
                response = httpClient.GetStringAsync(url).Result;
            }

            return response;
        }

        public string GetApiRequest(string url, string region = "us")
        {
            string response;            
            string prefix;
            
            prefix = $"https://raider.io/api/v1";
            url = $"{prefix}{url}?region={region}";

            _logger.LogInformation($"RaiderIO API request to {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;                             
                response = httpClient.GetStringAsync(url).Result;
            }

            return response;
        }

        public string GetApiRequest(string url)
        {
            string response;            
            string prefix;
            
            prefix = $"https://raider.io/api/v1";
            url = $"{prefix}{url}";

            _logger.LogInformation($"RaiderIO API request to {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;                             
                response = httpClient.GetStringAsync(url).Result;
            }

            return response;
        }
        
        public RaiderIOModels.Affix GetCurrentAffix(string region = "us", string locale = "en")
        {
            RaiderIOModels.Affix affixes = null;
            string url = $"/mythic-plus/affixes";
            affixes = JsonConvert.DeserializeObject<RaiderIOModels.Affix>(GetApiRequest(url: url, region: region, locale: locale));
            return affixes;
        }

        public RaiderIOModels.RioGuildInfo GetRioGuildInfo(string guildName, string realmName, string region)
        {
            RaiderIOModels.RioGuildInfo guildInfo = null;
            guildName = guildName.Replace(" ", "%20");
            realmName = realmName.Replace(" ", "%20");
            string url = $"/guilds/profile?region={region}&realm={realmName}&name={guildName}&fields=raid_progression%2Craid_rankings";
            guildInfo = JsonConvert.DeserializeObject<RaiderIOModels.RioGuildInfo>(GetApiRequest(url: url));
            return guildInfo;
        }    

        public RaiderIOModels.RioMythicPlusChar GetCharMythicPlusInfo(string charName, string realmName, string region = "us")
        {
            RaiderIOModels.RioMythicPlusChar mythicCharInfo = null;            
            string url = $"/characters/profile?region={region}&realm={realmName}&name={charName}&fields=mythic_plus_scores_by_season:current%2Cmythic_plus_ranks%2Cmythic_plus_scores%2Cmythic_plus_highest_level_runs%2Cmythic_plus_recent_runs%2Cmythic_plus_best_runs%2Craid_progression";
            mythicCharInfo = JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(GetApiRequest(url: url));
            return mythicCharInfo;
        }
    }
}