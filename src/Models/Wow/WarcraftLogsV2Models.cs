using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    // OAuth Token Response
    public class WclV2TokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonIgnore]
        public DateTime ExpiresAt { get; set; }

        [JsonIgnore]
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    }

    // GraphQL Request/Response
    public class GraphQLRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("variables")]
        public object Variables { get; set; }
    }

    public class GraphQLResponse<T>
    {
        [JsonProperty("data")]
        public T Data { get; set; }

        [JsonProperty("errors")]
        public List<GraphQLError> Errors { get; set; }
    }

    public class GraphQLError
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("locations")]
        public List<GraphQLErrorLocation> Locations { get; set; }

        [JsonProperty("path")]
        public List<object> Path { get; set; }
    }

    public class GraphQLErrorLocation
    {
        [JsonProperty("line")]
        public int Line { get; set; }

        [JsonProperty("column")]
        public int Column { get; set; }
    }

    // Guild Reports Response
    public class WclV2GuildReportsResponse
    {
        [JsonProperty("reportData")]
        public WclV2ReportData ReportData { get; set; }
    }

    public class WclV2ReportData
    {
        [JsonProperty("reports")]
        public WclV2ReportPagination Reports { get; set; }
    }

    public class WclV2ReportPagination
    {
        [JsonProperty("data")]
        public List<WclV2Report> Data { get; set; }

        [JsonProperty("has_more_pages")]
        public bool HasMorePages { get; set; }

        [JsonProperty("current_page")]
        public int CurrentPage { get; set; }

        [JsonProperty("from")]
        public int From { get; set; }

        [JsonProperty("to")]
        public int To { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }
    }

    public class WclV2Report
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("owner")]
        public WclV2User Owner { get; set; }

        [JsonProperty("startTime")]
        public long StartTime { get; set; }

        [JsonProperty("endTime")]
        public long EndTime { get; set; }

        [JsonProperty("zone")]
        public WclV2Zone Zone { get; set; }

        // Computed properties for compatibility with v1
        [JsonIgnore]
        public string Id => Code;

        [JsonIgnore]
        public string OwnerName => Owner?.Name;

        [JsonIgnore]
        public string ZoneName => Zone?.Name;

        [JsonIgnore]
        public string ReportURL => $"https://www.warcraftlogs.com/reports/{Code}";
    }

    public class WclV2User
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class WclV2Zone
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    // Batch Query Result with Error Information
    public class WclV2BatchResult
    {
        /// <summary>
        /// Successfully retrieved reports (guild key -> report)
        /// </summary>
        public Dictionary<string, WclV2Report> Reports { get; set; } = new Dictionary<string, WclV2Report>();

        /// <summary>
        /// Guild keys for guilds that don't exist on WarcraftLogs (returned GraphQL error)
        /// </summary>
        public HashSet<string> NonExistentGuilds { get; set; } = new HashSet<string>();

        /// <summary>
        /// Guild keys for guilds that exist but have no reports (empty data array)
        /// </summary>
        public HashSet<string> GuildsWithNoReports { get; set; } = new HashSet<string>();
    }

    // ===== Encounter Rankings (for /top10 command) =====

    /// <summary>
    /// Response wrapper for worldData encounter rankings query
    /// </summary>
    public class WclV2EncounterRankingsResponse
    {
        [JsonProperty("worldData")]
        public WclV2WorldData WorldData { get; set; }
    }

    public class WclV2WorldData
    {
        [JsonProperty("encounter")]
        public WclV2Encounter Encounter { get; set; }

        [JsonProperty("expansion")]
        public WclV2Expansion Expansion { get; set; }
    }

    public class WclV2Encounter
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("characterRankings")]
        public WclV2CharacterRankingsPage CharacterRankings { get; set; }
    }

    public class WclV2CharacterRankingsPage
    {
        [JsonProperty("rankings")]
        public List<WclV2CharacterRanking> Rankings { get; set; }

        [JsonProperty("page")]
        public int Page { get; set; }

        [JsonProperty("hasMorePages")]
        public bool HasMorePages { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    public class WclV2CharacterRanking
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("class")]
        public string Class { get; set; }

        [JsonProperty("spec")]
        public string Spec { get; set; }

        [JsonProperty("amount")]
        public double Amount { get; set; }

        [JsonProperty("hardModeLevel")]
        public int HardModeLevel { get; set; }

        [JsonProperty("duration")]
        public long Duration { get; set; }

        [JsonProperty("startTime")]
        public long StartTime { get; set; }

        [JsonProperty("report")]
        public WclV2ReportReference Report { get; set; }

        [JsonProperty("guild")]
        public WclV2GuildReference Guild { get; set; }

        [JsonProperty("server")]
        public WclV2ServerReference Server { get; set; }

        [JsonProperty("faction")]
        public int Faction { get; set; }

        [JsonProperty("bracketData")]
        public int BracketData { get; set; }

        [JsonProperty("rank")]
        public int? Rank { get; set; }

        [JsonProperty("best")]
        public bool? Best { get; set; }

        // Compatibility with v1 field names
        [JsonIgnore]
        public double Total => Amount;

        [JsonIgnore]
        public int ItemLevel => BracketData;

        [JsonIgnore]
        public string GuildName => Guild?.Name ?? "No Guild";

        [JsonIgnore]
        public string ServerName => Server?.Slug ?? "";
    }

    public class WclV2ReportReference
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    public class WclV2GuildReference
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public int? Id { get; set; }
    }

    public class WclV2ServerReference
    {
        [JsonProperty("slug")]
        public string Slug { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// Region - can be string ("US") or object depending on query
        /// </summary>
        [JsonProperty("region")]
        public string Region { get; set; }
    }

    // ===== Zones/Encounters (for encounter list) =====

    /// <summary>
    /// Response wrapper for worldData zones query
    /// </summary>
    public class WclV2ZonesResponse
    {
        [JsonProperty("worldData")]
        public WclV2WorldData WorldData { get; set; }
    }

    /// <summary>
    /// Response wrapper for worldData expansions query
    /// </summary>
    public class WclV2ExpansionsResponse
    {
        [JsonProperty("worldData")]
        public WclV2WorldDataExpansions WorldData { get; set; }
    }

    public class WclV2WorldDataExpansions
    {
        [JsonProperty("expansions")]
        public List<WclV2Expansion> Expansions { get; set; }
    }

    public class WclV2Expansion
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("zones")]
        public List<WclV2ZoneDetail> Zones { get; set; }
    }

    public class WclV2ZoneDetail
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("frozen")]
        public bool? Frozen { get; set; }

        [JsonProperty("encounters")]
        public List<WclV2EncounterBasic> Encounters { get; set; }

        [JsonProperty("brackets")]
        public WclV2Brackets Brackets { get; set; }

        [JsonProperty("partitions")]
        public List<WclV2Partition> Partitions { get; set; }

        [JsonProperty("difficulties")]
        public List<WclV2Difficulty> Difficulties { get; set; }
    }

    public class WclV2EncounterBasic
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class WclV2Brackets
    {
        [JsonProperty("min")]
        public int? Min { get; set; }

        [JsonProperty("max")]
        public int? Max { get; set; }

        [JsonProperty("bucket")]
        public double? Bucket { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class WclV2Partition
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("compactName")]
        public string CompactName { get; set; }

        [JsonProperty("default")]
        public bool? IsDefault { get; set; }
    }

    public class WclV2Difficulty
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    // ===== Character Classes =====

    public class WclV2GameDataResponse
    {
        [JsonProperty("gameData")]
        public WclV2GameData GameData { get; set; }
    }

    public class WclV2GameData
    {
        [JsonProperty("classes")]
        public List<WclV2Class> Classes { get; set; }
    }

    public class WclV2Class
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }
    }

    // Rate Limit Data
    public class WclV2RateLimitResponse
    {
        [JsonProperty("rateLimitData")]
        public WclV2RateLimitData RateLimitData { get; set; }
    }

    public class WclV2RateLimitData
    {
        [JsonProperty("limitPerHour")]
        public double LimitPerHour { get; set; }

        [JsonProperty("pointsSpentThisHour")]
        public double PointsSpentThisHour { get; set; }

        [JsonProperty("pointsResetIn")]
        public int PointsResetIn { get; set; }

        [JsonIgnore]
        public double UsagePercent => LimitPerHour > 0 ? PointsSpentThisHour / LimitPerHour * 100 : 0;

        [JsonIgnore]
        public double PointsRemaining => LimitPerHour - PointsSpentThisHour;
    }

    // ===== Character Zone Rankings (for /char logs view) =====

    /// <summary>
    /// Response wrapper for characterData query
    /// </summary>
    public class WclV2CharacterDataResponse
    {
        [JsonProperty("characterData")]
        public WclV2CharacterDataWrapper CharacterData { get; set; }
    }

    public class WclV2CharacterDataWrapper
    {
        [JsonProperty("character")]
        public WclV2CharacterInfo Character { get; set; }
    }

    /// <summary>
    /// Character info with zone rankings from characterData query
    /// </summary>
    public class WclV2CharacterInfo
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("classID")]
        public int ClassId { get; set; }

        /// <summary>
        /// zoneRankings returns parsed JSON with ranking data
        /// </summary>
        [JsonProperty("zoneRankings")]
        public WclV2ZoneRankingsData ZoneRankings { get; set; }
    }

    /// <summary>
    /// Zone rankings data for a character (current raid tier)
    /// </summary>
    public class WclV2ZoneRankingsData
    {
        [JsonProperty("bestPerformanceAverage")]
        public double? BestPerformanceAverage { get; set; }

        [JsonProperty("medianPerformanceAverage")]
        public double? MedianPerformanceAverage { get; set; }

        [JsonProperty("difficulty")]
        public int? Difficulty { get; set; }

        [JsonProperty("metric")]
        public string Metric { get; set; }

        [JsonProperty("partition")]
        public int? Partition { get; set; }

        [JsonProperty("zone")]
        public int? ZoneId { get; set; }

        [JsonProperty("allStars")]
        public List<WclV2AllStarRanking> AllStars { get; set; }

        [JsonProperty("rankings")]
        public List<WclV2BossRanking> Rankings { get; set; }
    }

    /// <summary>
    /// All-star ranking data for a character
    /// </summary>
    public class WclV2AllStarRanking
    {
        [JsonProperty("partition")]
        public int Partition { get; set; }

        [JsonProperty("spec")]
        public string Spec { get; set; }

        [JsonProperty("points")]
        [JsonConverter(typeof(NullableDoubleOrDashConverter))]
        public double? Points { get; set; }

        [JsonProperty("possiblePoints")]
        [JsonConverter(typeof(NullableDoubleOrDashConverter))]
        public double? PossiblePoints { get; set; }

        [JsonProperty("rank")]
        [JsonConverter(typeof(NullableIntOrDashConverter))]
        public int? Rank { get; set; }

        [JsonProperty("regionRank")]
        [JsonConverter(typeof(NullableIntOrDashConverter))]
        public int? RegionRank { get; set; }

        [JsonProperty("serverRank")]
        [JsonConverter(typeof(NullableIntOrDashConverter))]
        public int? ServerRank { get; set; }

        [JsonProperty("rankPercent")]
        [JsonConverter(typeof(NullableDoubleOrDashConverter))]
        public double? RankPercent { get; set; }
    }

    /// <summary>
    /// Per-boss ranking data for a character
    /// </summary>
    public class WclV2BossRanking
    {
        [JsonProperty("encounter")]
        public WclV2EncounterBasic Encounter { get; set; }

        [JsonProperty("rankPercent")]
        public double? RankPercent { get; set; }

        [JsonProperty("medianPercent")]
        public double? MedianPercent { get; set; }

        [JsonProperty("totalKills")]
        public int TotalKills { get; set; }

        [JsonProperty("fastestKill")]
        public long? FastestKill { get; set; }

        [JsonProperty("allStars")]
        public WclV2AllStarPoints AllStars { get; set; }

        [JsonProperty("spec")]
        public string Spec { get; set; }

        [JsonProperty("bestSpec")]
        public string BestSpec { get; set; }

        [JsonProperty("bestAmount")]
        public double? BestAmount { get; set; }
    }

    /// <summary>
    /// All-star points for a specific boss
    /// </summary>
    public class WclV2AllStarPoints
    {
        [JsonProperty("points")]
        [JsonConverter(typeof(NullableDoubleOrDashConverter))]
        public double? Points { get; set; }

        [JsonProperty("possiblePoints")]
        [JsonConverter(typeof(NullableDoubleOrDashConverter))]
        public double? PossiblePoints { get; set; }

        /// <summary>
        /// Rank can be "-" when not applicable, so we use a custom converter
        /// </summary>
        [JsonProperty("rank")]
        [JsonConverter(typeof(NullableIntOrDashConverter))]
        public int? Rank { get; set; }

        [JsonProperty("rankPercent")]
        [JsonConverter(typeof(NullableDoubleOrDashConverter))]
        public double? RankPercent { get; set; }
    }

    /// <summary>
    /// Converts "-" string to null for integer fields in WarcraftLogs API responses
    /// </summary>
    public class NullableIntOrDashConverter : JsonConverter<int?>
    {
        public override int? ReadJson(JsonReader reader, Type objectType, int? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            if (reader.TokenType == JsonToken.String)
            {
                var str = reader.Value?.ToString();
                if (string.IsNullOrEmpty(str) || str == "-")
                    return null;
                if (int.TryParse(str, out var result))
                    return result;
                return null;
            }

            if (reader.TokenType == JsonToken.Integer)
                return Convert.ToInt32(reader.Value);

            return null;
        }

        public override void WriteJson(JsonWriter writer, int? value, JsonSerializer serializer)
        {
            if (value.HasValue)
                writer.WriteValue(value.Value);
            else
                writer.WriteNull();
        }
    }

    /// <summary>
    /// Converts "-" string to null for double fields in WarcraftLogs API responses
    /// </summary>
    public class NullableDoubleOrDashConverter : JsonConverter<double?>
    {
        public override double? ReadJson(JsonReader reader, Type objectType, double? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            if (reader.TokenType == JsonToken.String)
            {
                var str = reader.Value?.ToString();
                if (string.IsNullOrEmpty(str) || str == "-")
                    return null;
                if (double.TryParse(str, out var result))
                    return result;
                return null;
            }

            if (reader.TokenType == JsonToken.Float || reader.TokenType == JsonToken.Integer)
                return Convert.ToDouble(reader.Value);

            return null;
        }

        public override void WriteJson(JsonWriter writer, double? value, JsonSerializer serializer)
        {
            if (value.HasValue)
                writer.WriteValue(value.Value);
            else
                writer.WriteNull();
        }
    }
}
