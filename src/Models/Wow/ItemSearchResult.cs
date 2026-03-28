using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ItemSearchResponse
    {
        [JsonProperty("results")]
        public List<ItemSearchResultEntry> Results { get; set; }
    }

    public class ItemSearchResultEntry
    {
        [JsonProperty("data")]
        public ItemSearchResultData Data { get; set; }
    }

    public class ItemSearchResultData
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public ItemSearchLocalizedName Name { get; set; }
    }

    public class ItemSearchLocalizedName
    {
        [JsonProperty("en_US")]
        public string EnUS { get; set; }
    }
}
