using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using NinjaBotCore.Models.YouTube;
using Google;
using Google.Apis;
using Google.Apis.YouTube.v3;
using System.Net.Http;
using System.Net.Http.Headers;
using Google.Apis.YouTube.v3.Data;
using Google.Apis.Services;

namespace NinjaBotCore.Modules.YouTube
{
    public class YouTubeApi
    {
        private string key = Config.YouTubeApi;

        private string getYouTubeApiRequest(string url)
        {
            string reponse = string.Empty;
            string fullUrl = $"https://www.googleapis.com/youtube/v3/search?key={key}{url}";

            Console.WriteLine($"YouTube API Request {fullUrl}");

            string response = string.Empty;

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

        public string getLatestVideoByID(string id, int numVideos = 1)
        {
            string videoURL = string.Empty;
            string url = $"&channelId={id}&part=snippet,id&order=date&maxResults={numVideos}";
            YouTubeModel.Video videos = JsonConvert.DeserializeObject<YouTubeModel.Video>(getYouTubeApiRequest(url));
            videoURL = $"https://www.youtube.com/watch?v={videos.items[0].id.videoId}";
            return videoURL;
        }

        public string getRandomVideoByID(string id, int numVideos = 50)
        {
            string videoURL = string.Empty;
            string url = $"&channelId={id}&part=snippet,id&order=date&maxResults={numVideos}";
            YouTubeModel.Video videos = JsonConvert.DeserializeObject<YouTubeModel.Video>(getYouTubeApiRequest(url));
            Random getRandom = new Random();
            int random = getRandom.Next(0, numVideos);
            videoURL = $"https://www.youtube.com/watch?v={videos.items[random].id.videoId}";
            return videoURL;
        }

        public async Task<List<SearchResult>> SearchChannelsAsync(string keyword = "space", int maxResults = 5)
        {
            var youtubeService = new YouTubeService(new BaseClientService.Initializer()
            {
                ApiKey = Config.YouTubeApi,
                ApplicationName = this.GetType().ToString()

            });
            var searchListRequest = youtubeService.Search.List("snippet");
            searchListRequest.Q = keyword;
            searchListRequest.MaxResults = maxResults;
            var searchListResponse = await searchListRequest.ExecuteAsync();
            return searchListResponse.Items.ToList();
        }
    }
}
