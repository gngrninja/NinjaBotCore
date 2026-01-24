using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ArmoryConnectedRealmsIndex
    {
        [JsonProperty("connected_realms")]
        public List<ArmoryConnectedRealmLink> ConnectedRealms { get; set; }
    }

    public class ArmoryConnectedRealmLink
    {
        [JsonProperty("href")]
        public string Href { get; set; }
    }

    public class ArmoryConnectedRealmStatus
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("has_queue")]
        public bool HasQueue { get; set; }

        [JsonProperty("status")]
        public ArmoryRealmStatusType Status { get; set; }

        [JsonProperty("population")]
        public ArmoryPopulationType Population { get; set; }

        [JsonProperty("realms")]
        public List<ArmoryRealmDetail> Realms { get; set; }

        [JsonProperty("mythic_leaderboards")]
        public ArmoryLink MythicLeaderboards { get; set; }

        [JsonProperty("auctions")]
        public ArmoryLink Auctions { get; set; }
    }

    public class ArmoryRealmStatusType
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class ArmoryPopulationType
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class ArmoryRealmDetail
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("region")]
        public ArmoryRegionRef Region { get; set; }

        [JsonProperty("connected_realm")]
        public ArmoryLink ConnectedRealm { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("locale")]
        public string Locale { get; set; }

        [JsonProperty("timezone")]
        public string Timezone { get; set; }

        [JsonProperty("type")]
        public ArmoryRealmTypeInfo Type { get; set; }

        [JsonProperty("is_tournament")]
        public bool IsTournament { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }
    }

    public class ArmoryRegionRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryRealmTypeInfo
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
