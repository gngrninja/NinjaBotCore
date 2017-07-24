using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.RocketLeague
{
    public class Platforms
    {
        public int id { get; set; }
        public string name { get; set; }
    }

    public class Seasons
    {
        public int seasonId { get; set; }
        public int? endedOn { get; set; }
        public int startedOn { get; set; }
    }

    public class UserStats
    {
        public string uniqueId { get; set; }
        public string displayName { get; set; }
        public Platforms platform { get; set; }
        public string avatar { get; set; }
        public string profileUrl { get; set; }
        public string signatureUrl { get; set; }
        public Stats stats { get; set; }
        public Rankedseasons rankedSeasons { get; set; }
        public int lastRequested { get; set; }
        public int createdAt { get; set; }
        public int updatedAt { get; set; }
        public int nextUpdateAt { get; set; }
    }

    public class Stats
    {
        public int wins { get; set; }
        public int goals { get; set; }
        public int mvps { get; set; }
        public int saves { get; set; }
        public int shots { get; set; }
        public int assists { get; set; }
    }

    public class Rankedseasons
    {
        [JsonProperty(PropertyName = "1")]
        public _1 _1 { get; set; }
        [JsonProperty(PropertyName = "2")]
        public _2 _2 { get; set; }
        [JsonProperty(PropertyName = "3")]
        public _3 _3 { get; set; }
        [JsonProperty(PropertyName = "4")]
        public _4 _4 { get; set; }
        [JsonProperty(PropertyName = "5")]
        public _5 _5 { get; set; }
    }

    public class _1
    {
        [JsonProperty(PropertyName = "10")]
        public RankPoints _10 { get; set; }
        [JsonProperty(PropertyName = "11")]
        public RankPoints _11 { get; set; }
        [JsonProperty(PropertyName = "12")]
        public RankPoints _12 { get; set; }
        [JsonProperty(PropertyName = "13")]
        public RankPoints _13 { get; set; }
    }

    public class RankPoints
    {
        public int rankPoints { get; set; }
    }

    public class _2
    {
        [JsonProperty(PropertyName = "10")]
        public SeasonStats _10 { get; set; }
        [JsonProperty(PropertyName = "11")]
        public SeasonStats _11 { get; set; }
        [JsonProperty(PropertyName = "12")]
        public SeasonStats _12 { get; set; }
        [JsonProperty(PropertyName = "13")]
        public SeasonStats _13 { get; set; }
    }

    public class SeasonStats
    {
        public int rankPoints { get; set; }
        public int matchesPlayed { get; set; }
        public int tier { get; set; }
        public int division { get; set; }
    }

    public class _3
    {
        [JsonProperty(PropertyName = "10")]
        public SeasonStats _10 { get; set; }
        [JsonProperty(PropertyName = "11")]
        public SeasonStats _11 { get; set; }
        [JsonProperty(PropertyName = "12")]
        public SeasonStats _12 { get; set; }
        [JsonProperty(PropertyName = "13")]
        public SeasonStats _13 { get; set; }
    }

    public class _4
    {
        [JsonProperty(PropertyName = "10")]
        public SeasonStats _10 { get; set; }
        [JsonProperty(PropertyName = "11")]
        public SeasonStats _11 { get; set; }
        [JsonProperty(PropertyName = "12")]
        public SeasonStats _12 { get; set; }
        [JsonProperty(PropertyName = "13")]
        public SeasonStats _13 { get; set; }
    }

    public class _5
    {
        [JsonProperty(PropertyName = "10")]
        public SeasonStats _onevone { get; set; }
        [JsonProperty(PropertyName = "11")]
        public SeasonStats _twovtwo { get; set; }
        [JsonProperty(PropertyName = "12")]
        public SeasonStats _solostandard { get; set; }
        [JsonProperty(PropertyName = "13")]
        public SeasonStats _standard { get; set; }
    }
}