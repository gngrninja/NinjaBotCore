using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class RoomResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class RoomResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public RoomResponseLinksSelf Self { get; set; }
    }

    public class RoomResponse
    {
        [Newtonsoft.Json.JsonProperty("_links")]
        public RoomResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }
    }
}
