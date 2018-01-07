using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using NinjaBotCore.Models.Google;
using NinjaBotCore.Models.DarkSky;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace NinjaBotCore.Modules.Weather
{
    public class WeatherApi
    {
        private readonly string _dsApiKey;
        private readonly string _gMapsApiKey;
        private readonly IConfigurationRoot _config;

        public WeatherApi(IConfigurationRoot config)
        {
            _config = config;
            _dsApiKey = _config["DarkSkyApi"];
            _gMapsApiKey = _config["GoogleMapsApi"];
        }

        public string getAPIRequest(string url)
        {
            string response;
            string prefix;

            prefix = $"https://api.darksky.net/forecast/{_dsApiKey}";
            url = $"{prefix}/{url}";

            Console.WriteLine($"DS Weather API Request to: [{url}]");

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

        public string GetGoogleAPIRequest(string url)
        {
            string response;
            string prefix;

            prefix = $"https://maps.googleapis.com/maps/api/geocode/json?";
            url = $"{prefix}{url}&key={_gMapsApiKey}";

            Console.WriteLine($"Geocode request to: [{url}]");

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

        public ForecastResponse GetForecast(string coords)
        {
            var r = JsonConvert.DeserializeObject<ForecastResponse>(getAPIRequest(coords));
            return r;
        }

        public GeocodeResponse GetGeocode(string location)
        {
            string coords = string.Empty;
            location = location.Replace(" ", "+");
            string url = $"address={location}";
            var geocode = JsonConvert.DeserializeObject<GeocodeResponse>(GetGoogleAPIRequest(url));
            return geocode;
        }

    }
}