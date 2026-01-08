using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class RoomIndexResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class RoomIndexResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public RoomIndexResponseLinksSelf Self { get; set; }
    }

    public class RoomIndexResponseRoomsItemKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class RoomIndexResponseRoomsItem
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public RoomIndexResponseRoomsItemKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class RoomIndexResponse
    {
        [Newtonsoft.Json.JsonProperty("_links")]
        public RoomIndexResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("rooms")]
        public List<RoomIndexResponseRoomsItem> Rooms { get; set; }
    }
}
