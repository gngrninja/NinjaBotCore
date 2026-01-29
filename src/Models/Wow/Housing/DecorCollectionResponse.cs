using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{
    public class DecorCollectionResponse
    {
        [JsonProperty("_links")]
        public DecorCollectionLinks Links { get; set; }

        [JsonProperty("decor")]
        public List<DecorCollectionItem> Decor { get; set; }
    }

    public class DecorCollectionLinks
    {
        [JsonProperty("self")]
        public DecorCollectionSelf Self { get; set; }
    }

    public class DecorCollectionSelf
    {
        [JsonProperty("href")]
        public string Href { get; set; }
    }

    public class DecorCollectionItem
    {
        [JsonProperty("decor")]
        public DecorReference DecorRef { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }

    public class DecorReference
    {
        [JsonProperty("key")]
        public DecorKey Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class DecorKey
    {
        [JsonProperty("href")]
        public string Href { get; set; }
    }
}
