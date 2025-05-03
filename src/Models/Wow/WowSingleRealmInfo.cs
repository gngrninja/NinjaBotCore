namespace NinjaBotCore.Models.Wow
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    public partial class WowSingleRealmInfo
    {
        [JsonProperty("_links")]
        public Links Links { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("region")]
        public RealmRegion Region { get; set; }

        [JsonProperty("connected_realm")]
        public ConnectedRealm ConnectedRealm { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("locale")]
        public string Locale { get; set; }

        [JsonProperty("timezone")]
        public string Timezone { get; set; }

        [JsonProperty("type")]
        public TypeClass Type { get; set; }

        [JsonProperty("is_tournament")]
        public bool IsTournament { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }
    }

    public partial class ConnectedRealm
    {
        [JsonProperty("href")]
        public Uri Href { get; set; }
    }

    public class SelfLinks
    {
        [JsonProperty("self")]
        public ConnectedRealm Self { get; set; }
    }

    public class RealmRegion
    {
        [JsonProperty("key")]
        public ConnectedRealm RegionKey { get; set; }

        [JsonProperty("name")]
        public string RegionName { get; set; }

        [JsonProperty("id")]
        public long RegionId { get; set; }
    }

    public partial class TypeClass
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
