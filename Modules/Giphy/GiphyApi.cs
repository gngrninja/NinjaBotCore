using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using NinjaBotCore.Models.Giphy;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NinjaBotCore.Modules.Giphy
{
    class GiphyApi
    {
        public string ApiRequest(string url, string appendUrl = null)
        {
            string response = string.Empty; ;
            string apiKey = $"{NinjaBot.GiphyApi}";
            if (string.IsNullOrEmpty(appendUrl))
            {
                url = $"http://api.giphy.com/v1{url}?api_key={apiKey}";
            }
            else
            {
                url = $"http://api.giphy.com/v1{url}?api_key={apiKey}{appendUrl}";
            }
            Console.WriteLine($"Giphy request -> {url}");
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

        public GiphyReponse GetRandomImage(string tags)
        {
            GiphyReponse r = new GiphyReponse();
            string url = string.Empty;
            string appendUrl = string.Empty;

            if (string.IsNullOrEmpty(tags))
            {
                url = $"/gifs/random";
            }
            else
            {
                tags = tags.Replace(" ", "+");
                url = $"/gifs/random";
                appendUrl = $"&tag={tags}";
            }
            r = JsonConvert.DeserializeObject<GiphyReponse>(ApiRequest(url, appendUrl));
            return r;
        }
    }
}
