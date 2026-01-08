using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class DecorDetailResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class DecorDetailResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public DecorDetailResponseLinksSelf Self { get; set; }
    }

    public class DecorDetailResponseItemsKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class DecorDetailResponseItems
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public DecorDetailResponseItemsKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }
    }

    public class DecorDetailResponse
    {
        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("_links")]
        public DecorDetailResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("items")]
        public DecorDetailResponseItems Items { get; set; }
    }
}
