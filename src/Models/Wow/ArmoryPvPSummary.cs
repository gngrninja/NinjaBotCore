using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ArmoryPvPSummary
    {
        [JsonProperty("character")]
        public ArmoryCharacterRef Character { get; set; }

        [JsonProperty("brackets")]
        public List<ArmoryPvPBracketLink> Brackets { get; set; }

        [JsonProperty("honor_level")]
        public int HonorLevel { get; set; }

        [JsonProperty("pvp_map_statistics")]
        public List<ArmoryPvPMapStat> PvPMapStatistics { get; set; }

        [JsonProperty("honorable_kills")]
        public int HonorableKills { get; set; }
    }

    public class ArmoryPvPBracketLink
    {
        [JsonProperty("href")]
        public string Href { get; set; }
    }

    public class ArmoryPvPMapStat
    {
        [JsonProperty("world_map")]
        public ArmoryMapRef WorldMap { get; set; }

        [JsonProperty("match_statistics")]
        public ArmoryMatchStats MatchStatistics { get; set; }
    }

    public class ArmoryMapRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class ArmoryMatchStats
    {
        [JsonProperty("played")]
        public int Played { get; set; }

        [JsonProperty("won")]
        public int Won { get; set; }

        [JsonProperty("lost")]
        public int Lost { get; set; }
    }

    public class ArmoryPvPBracket
    {
        [JsonProperty("character")]
        public ArmoryCharacterRef Character { get; set; }

        [JsonProperty("faction")]
        public ArmoryType Faction { get; set; }

        [JsonProperty("bracket")]
        public ArmoryBracketType Bracket { get; set; }

        [JsonProperty("rating")]
        public int Rating { get; set; }

        [JsonProperty("season")]
        public ArmorySeasonRef Season { get; set; }

        [JsonProperty("tier")]
        public ArmoryTier Tier { get; set; }

        [JsonProperty("season_match_statistics")]
        public ArmoryMatchStats SeasonMatchStatistics { get; set; }

        [JsonProperty("weekly_match_statistics")]
        public ArmoryMatchStats WeeklyMatchStatistics { get; set; }
    }

    public class ArmoryBracketType
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class ArmorySeasonRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryTier
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }
}
