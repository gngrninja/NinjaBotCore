namespace NinjaBotCore.Models.Wow
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    public class WowConnectedRealm
    {
        [JsonProperty("_links")]
        public Links Links { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("has_queue")]
        public bool HasQueue { get; set; }

        [JsonProperty("status")]
        public Population Status { get; set; }

        [JsonProperty("population")]
        public Population Population { get; set; }

        [JsonProperty("realms")]
        public Realm[] Realms { get; set; }

        [JsonProperty("mythic_leaderboards")]
        public Auctions MythicLeaderboards { get; set; }

        [JsonProperty("auctions")]
        public Auctions Auctions { get; set; }
    }

    public partial class Auctions
    {
        [JsonProperty("href")]
        public Uri Href { get; set; }
    }

    public class Links
    {
        [JsonProperty("self")]
        public Auctions Self { get; set; }
    }

    public partial class Population
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public partial class Realm
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("region")]
        public Region Region { get; set; }

        [JsonProperty("connected_realm")]
        public Auctions ConnectedRealm { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("locale")]
        public string Locale { get; set; }

        [JsonProperty("timezone")]
        public string Timezone { get; set; }

        [JsonProperty("type")]
        public Population Type { get; set; }

        [JsonProperty("is_tournament")]
        public bool IsTournament { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }
    }

    public class Region
    {
        [JsonProperty("key")]
        public Auctions AuctionsKey { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }
}
