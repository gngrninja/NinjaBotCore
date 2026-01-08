using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class DecorIndexResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class DecorIndexResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public DecorIndexResponseLinksSelf Self { get; set; }
    }

    public class DecorIndexResponseDecorItemsItemKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class DecorIndexResponseDecorItemsItem
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public DecorIndexResponseDecorItemsItemKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class DecorIndexResponse
    {
        [Newtonsoft.Json.JsonProperty("_links")]
        public DecorIndexResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("decor_items")]
        public List<DecorIndexResponseDecorItemsItem> DecorItems { get; set; }
    }
}
