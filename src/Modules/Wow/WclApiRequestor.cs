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
using NinjaBotCore.Common;

namespace NinjaBotCore.Modules.Wow
{
    public class WclApiRequestor : IWclApiRequestor, IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _apiKey;
        private string apiKey;
        private string _baseUrl;

        public WclApiRequestor(string apiKey, string baseUrl, HttpClient client)
        {          
            _baseUrl = baseUrl;  
            _client = client;
            _client.BaseAddress = new Uri(_baseUrl);
            _client.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _apiKey = apiKey;
        }

        public async Task<T> Get<T>(string relativeUrl)
        {            
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"{relativeUrl}api_key={_apiKey}"))
            using (var response = await SendAsync(request))
            {
                var result = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject<T>(result);
            }
        }

        protected virtual async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            var response = await _client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            try
            {
                var errorMessage = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(errorMessage))
                {
                    throw new Exception("No message!");
                } 
                else 
                {
                    throw new Exception($"{errorMessage}");
                }

            }
            catch (JsonException e)
            {
                throw new Exception($"{e.Message}");
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}