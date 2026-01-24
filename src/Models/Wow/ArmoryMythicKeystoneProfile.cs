using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ArmoryMythicKeystoneProfile
    {
        [JsonProperty("current_period")]
        public ArmoryMythicPeriod CurrentPeriod { get; set; }

        [JsonProperty("seasons")]
        public List<ArmoryMythicSeasonRef> Seasons { get; set; }

        [JsonProperty("character")]
        public ArmoryCharacterRef Character { get; set; }

        [JsonProperty("current_mythic_rating")]
        public ArmoryMythicRating CurrentMythicRating { get; set; }
    }

    public class ArmoryMythicPeriod
    {
        [JsonProperty("period")]
        public ArmoryPeriodRef Period { get; set; }

        [JsonProperty("best_runs")]
        public List<ArmoryMythicRun> BestRuns { get; set; }
    }

    public class ArmoryPeriodRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryMythicSeasonRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryMythicRun
    {
        [JsonProperty("completed_timestamp")]
        public long CompletedTimestamp { get; set; }

        [JsonProperty("duration")]
        public long Duration { get; set; }

        [JsonProperty("keystone_level")]
        public int KeystoneLevel { get; set; }

        [JsonProperty("keystone_affixes")]
        public List<ArmoryKeystoneAffix> KeystoneAffixes { get; set; }

        [JsonProperty("dungeon")]
        public ArmoryDungeonRef Dungeon { get; set; }

        [JsonProperty("is_completed_within_time")]
        public bool IsCompletedWithinTime { get; set; }

        [JsonProperty("mythic_rating")]
        public ArmoryMythicRating MythicRating { get; set; }

        [JsonProperty("map_rating")]
        public ArmoryMythicRating MapRating { get; set; }
    }

    public class ArmoryKeystoneAffix
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryDungeonRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryMythicRating
    {
        [JsonProperty("color")]
        public ArmoryColor Color { get; set; }

        [JsonProperty("rating")]
        public double Rating { get; set; }
    }

    public class ArmoryColor
    {
        [JsonProperty("r")]
        public int R { get; set; }

        [JsonProperty("g")]
        public int G { get; set; }

        [JsonProperty("b")]
        public int B { get; set; }

        [JsonProperty("a")]
        public double A { get; set; }
    }

    public class ArmoryCharacterRef
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("realm")]
        public ArmoryRealm Realm { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }
}
