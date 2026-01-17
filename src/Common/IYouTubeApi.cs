using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Apis.YouTube.v3.Data;

namespace NinjaBotCore.Common
{
    public interface IYouTubeApi
    {
        string getLatestVideoByID(string id, int numVideos = 1);
        string getRandomVideoByID(string id, int numVideos = 50);
        Task<List<SearchResult>> SearchChannelsAsync(string keyword = "space", int maxResults = 5);
    }
}
