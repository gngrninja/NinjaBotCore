using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using NinjaBotCore.Models.OxfordDictionary;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace NinjaBotCore.Modules.Fun
{
    public class OxfordApi
    {
        private string _baseUrl;
        private string _apiKey;
        private string _appId;      
        private readonly IConfigurationRoot _config;

        public OxfordApi(IConfigurationRoot config)
        {
            _config = config;
            _baseUrl = "https://od-api.oxforddictionaries.com/api/v1";
            _apiKey = _config["OxfordDictionaryApi"].Split(',')[0];
            _appId = _config["OxfordDictionaryApi"].Split(',')[1];
        }

        private string GetAPIRequest(string url, string sourceLang = "en")
        {
            string response;            
            url = $"{_baseUrl}{url}";
            //https://od-api.oxforddictionaries.com:443/api/v1

            Console.WriteLine($"Oxford Dictionary Api Request to: {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpClient.DefaultRequestHeaders.Add("app_id", _appId);                
                httpClient.DefaultRequestHeaders.Add("app_key", _apiKey);                                                
                
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;             
                response = httpClient.GetStringAsync(url).Result;
            }
            return response;
        }

        public OxfordResponses.OxfordSearch SearchOxford(string searchFor, string sourceLang = "en")
        {
            string url = string.Empty;
            OxfordResponses.OxfordSearch searchResults = null;
            try
            {
                url = $"/search/{sourceLang}?q={searchFor}&prefix=false&limit=5";
                searchResults = JsonConvert.DeserializeObject<OxfordResponses.OxfordSearch>(GetAPIRequest(url));
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return searchResults;
        }

        public OxfordResponses.OxfordDefinition DefineOxford(string wordId, string sourceLang = "en")
        {
            string url = string.Empty;
            OxfordResponses.OxfordDefinition defineResults = null;
            try
            {
                url = $"/entries/{sourceLang}/{wordId}";
                defineResults = JsonConvert.DeserializeObject<OxfordResponses.OxfordDefinition>(GetAPIRequest(url));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return defineResults;
        }
    }
}