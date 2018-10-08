using System;
using NinjaBotCore.Models.Wow;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public partial class WowStats
    {
        [JsonProperty("lastModified")]
        public long LastModified { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("realm")]
        public string Realm { get; set; }

        [JsonProperty("battlegroup")]
        public string Battlegroup { get; set; }

        [JsonProperty("class")]
        public long Class { get; set; }

        [JsonProperty("race")]
        public long Race { get; set; }

        [JsonProperty("gender")]
        public long Gender { get; set; }

        [JsonProperty("level")]
        public long Level { get; set; }

        [JsonProperty("achievementPoints")]
        public long AchievementPoints { get; set; }

        [JsonProperty("thumbnail")]
        public string Thumbnail { get; set; }

        [JsonProperty("calcClass")]
        public string CalcClass { get; set; }

        [JsonProperty("faction")]
        public long Faction { get; set; }

        [JsonProperty("statistics")]
        public Statistics Statistics { get; set; }

        [JsonProperty("totalHonorableKills")]
        public long TotalHonorableKills { get; set; }
    }

    public partial class Statistics
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("subCategories")]
        public SubCategory[] SubCategories { get; set; }
    }

    public partial class SubCategory
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("statistics")]
        public Statistic[] Statistics { get; set; }

        [JsonProperty("subCategories", NullValueHandling = NullValueHandling.Ignore)]
        public SubCategory[] SubCategories { get; set; }
    }

    public partial class Statistic
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("quantity")]
        public long Quantity { get; set; }

        [JsonProperty("lastUpdated")]
        public long LastUpdated { get; set; }

        [JsonProperty("money")]
        public bool Money { get; set; }

        [JsonProperty("highest", NullValueHandling = NullValueHandling.Ignore)]
        public string Highest { get; set; }
    }
}