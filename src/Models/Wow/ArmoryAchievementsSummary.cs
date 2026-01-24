using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ArmoryAchievementsSummary
    {
        [JsonProperty("character")]
        public ArmoryCharacterRef Character { get; set; }

        [JsonProperty("total_quantity")]
        public int TotalQuantity { get; set; }

        [JsonProperty("total_points")]
        public int TotalPoints { get; set; }

        [JsonProperty("achievements")]
        public List<ArmoryCompletedAchievement> Achievements { get; set; }

        [JsonProperty("category_progress")]
        public List<ArmoryCategoryProgress> CategoryProgress { get; set; }

        [JsonProperty("recent_events")]
        public List<ArmoryRecentAchievement> RecentEvents { get; set; }

        [JsonProperty("statistics")]
        public ArmoryLink Statistics { get; set; }
    }

    public class ArmoryCompletedAchievement
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("achievement")]
        public ArmoryAchievementRef Achievement { get; set; }

        [JsonProperty("criteria")]
        public ArmoryCriteria Criteria { get; set; }

        [JsonProperty("completed_timestamp")]
        public long? CompletedTimestamp { get; set; }
    }

    public class ArmoryAchievementRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryCriteria
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("is_completed")]
        public bool IsCompleted { get; set; }

        [JsonProperty("child_criteria")]
        public List<ArmoryCriteria> ChildCriteria { get; set; }

        [JsonProperty("amount")]
        public long? Amount { get; set; }
    }

    public class ArmoryCategoryProgress
    {
        [JsonProperty("category")]
        public ArmoryCategoryRef Category { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("points")]
        public int Points { get; set; }
    }

    public class ArmoryCategoryRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryRecentAchievement
    {
        [JsonProperty("achievement")]
        public ArmoryAchievementRef Achievement { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }
    }
}
