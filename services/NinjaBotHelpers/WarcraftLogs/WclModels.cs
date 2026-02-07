using Newtonsoft.Json;

namespace NinjaBotHelpers.WarcraftLogs;

/// <summary>
/// OAuth Token Response from WarcraftLogs
/// </summary>
public class WclV2TokenResponse
{
    [JsonProperty("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonProperty("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonProperty("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonIgnore]
    public DateTime ExpiresAt { get; set; }

    [JsonIgnore]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}

/// <summary>
/// GraphQL request wrapper
/// </summary>
public class GraphQLRequest
{
    [JsonProperty("query")]
    public string Query { get; set; } = string.Empty;

    [JsonProperty("variables")]
    public object? Variables { get; set; }
}

/// <summary>
/// GraphQL response wrapper
/// </summary>
public class GraphQLResponse<T>
{
    [JsonProperty("data")]
    public T? Data { get; set; }

    [JsonProperty("errors")]
    public List<GraphQLError>? Errors { get; set; }
}

/// <summary>
/// GraphQL error
/// </summary>
public class GraphQLError
{
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("path")]
    public List<object>? Path { get; set; }
}

/// <summary>
/// WarcraftLogs report data
/// </summary>
public class WclV2Report
{
    [JsonProperty("code")]
    public string Code { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("owner")]
    public WclV2User? Owner { get; set; }

    [JsonProperty("startTime")]
    public long StartTime { get; set; }

    [JsonProperty("endTime")]
    public long EndTime { get; set; }

    [JsonProperty("zone")]
    public WclV2Zone? Zone { get; set; }

    // Computed properties for compatibility
    [JsonIgnore]
    public string Id => Code;

    [JsonIgnore]
    public string OwnerName => Owner?.Name ?? "Unknown";

    [JsonIgnore]
    public string ZoneName => Zone?.Name ?? "Unknown";

    [JsonIgnore]
    public string ReportURL => $"https://www.warcraftlogs.com/reports/{Code}";

    [JsonIgnore]
    public DateTime StartTimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(StartTime).UtcDateTime;
}

/// <summary>
/// WarcraftLogs user reference
/// </summary>
public class WclV2User
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// WarcraftLogs zone reference
/// </summary>
public class WclV2Zone
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Result of batch guild reports query
/// </summary>
public class WclV2BatchResult
{
    /// <summary>
    /// Successfully retrieved reports (guild key -> report)
    /// </summary>
    public Dictionary<string, WclV2Report> Reports { get; set; } = new();

    /// <summary>
    /// Guild keys for guilds that don't exist on WarcraftLogs
    /// </summary>
    public HashSet<string> NonExistentGuilds { get; set; } = new();

    /// <summary>
    /// Guild keys for guilds that exist but have no reports
    /// </summary>
    public HashSet<string> GuildsWithNoReports { get; set; } = new();
}

/// <summary>
/// Rate limit data from WarcraftLogs API
/// </summary>
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

/// <summary>
/// Response wrapper for rate limit query
/// </summary>
public class WclV2RateLimitResponse
{
    [JsonProperty("rateLimitData")]
    public WclV2RateLimitData? RateLimitData { get; set; }
}

/// <summary>
/// Thrown when the WarcraftLogs API rate limit is at critical threshold
/// and cannot proceed even after waiting for reset.
/// </summary>
public class WclRateLimitException : Exception
{
    public WclV2RateLimitData RateLimitData { get; }

    public WclRateLimitException(WclV2RateLimitData rateLimitData)
        : base($"WCL rate limit at {rateLimitData.UsagePercent:F1}% after waiting for reset")
    {
        RateLimitData = rateLimitData;
    }

    public WclRateLimitException(string message, WclV2RateLimitData rateLimitData)
        : base(message)
    {
        RateLimitData = rateLimitData;
    }
}

/// <summary>
/// WoW game version for API endpoint selection
/// </summary>
public enum WowGameVersion
{
    Retail,
    Classic,
    ClassicFresh,
    Vanilla
}
