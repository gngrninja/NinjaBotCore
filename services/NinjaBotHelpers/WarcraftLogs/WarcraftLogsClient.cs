using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotHelpers.Configuration;
using Polly;
using Polly.Retry;

namespace NinjaBotHelpers.WarcraftLogs;

/// <summary>
/// WarcraftLogs V2 API client for batch guild log monitoring.
/// Uses OAuth 2.0 client credentials flow and GraphQL API.
/// </summary>
public class WarcraftLogsClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WarcraftLogsClient> _logger;
    private readonly HelpersConfiguration _config;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;

    private WclV2TokenResponse? _currentToken;
    private WclV2RateLimitData? _lastRateLimitData;
    private DateTime _lastRateLimitCheck = DateTime.MinValue;
    private int _requestCounter = 0;

    private const string TokenUrl = "https://www.warcraftlogs.com/oauth/token";
    private const string ApiUrlRetail = "https://www.warcraftlogs.com/api/v2/client";
    private const string ApiUrlClassic = "https://classic.warcraftlogs.com/api/v2/client";
    private const string ApiUrlVanilla = "https://vanilla.warcraftlogs.com/api/v2/client";

    private const int RateLimitCheckInterval = 10;
    private const double WarningThreshold = 80.0;
    private const double CriticalThreshold = 95.0;
    private const int MinRateLimitWaitSeconds = 10;
    private const int MaxRateLimitWaitSeconds = 3600;

    /// <summary>
    /// Exposes last rate limit data for test seeding
    /// </summary>
    internal WclV2RateLimitData? LastRateLimitData
    {
        get => _lastRateLimitData;
        set => _lastRateLimitData = value;
    }

    public WarcraftLogsClient(HttpClient httpClient, ILogger<WarcraftLogsClient> logger, HelpersConfiguration config)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;

        // Configure resilience pipeline for GraphQL API calls
        _resiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(response =>
                        (int)response.StatusCode == 429 ||
                        (int)response.StatusCode >= 500),
                OnRetry = args =>
                {
                    var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                    _logger.LogWarning(
                        "[WCL] Retry attempt {AttemptNumber}. Status: {StatusCode}",
                        args.AttemptNumber, statusCode);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        if (string.IsNullOrEmpty(_config.WclClientId) || string.IsNullOrEmpty(_config.WclClientSecret))
        {
            _logger.LogWarning("WarcraftLogs API credentials not configured. Set NINJABOT_WclClientId and NINJABOT_WclClientSecret.");
        }
    }

    /// <summary>
    /// Check if the client is configured with valid credentials
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_config.WclClientId) && !string.IsNullOrEmpty(_config.WclClientSecret);

    /// <summary>
    /// Gets the appropriate API endpoint URL based on game version
    /// </summary>
    private static string GetApiUrl(WowGameVersion gameVersion)
    {
        return gameVersion switch
        {
            WowGameVersion.Retail => ApiUrlRetail,
            WowGameVersion.Classic => ApiUrlClassic,
            WowGameVersion.ClassicFresh => ApiUrlClassic,
            WowGameVersion.Vanilla => ApiUrlVanilla,
            _ => ApiUrlRetail
        };
    }

    /// <summary>
    /// Gets a valid OAuth access token, refreshing if necessary
    /// </summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Return cached token if still valid
        if (_currentToken != null && !_currentToken.IsExpired)
        {
            return _currentToken.AccessToken;
        }

        _logger.LogInformation("Requesting new WarcraftLogs OAuth token...");

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);

        // OAuth requires Basic authentication with client_id:client_secret
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.WclClientId}:{_config.WclClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var formData = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" }
        };
        request.Content = new FormUrlEncodedContent(formData);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        _currentToken = JsonConvert.DeserializeObject<WclV2TokenResponse>(content);

        if (_currentToken == null)
        {
            throw new InvalidOperationException("Failed to parse OAuth token response");
        }

        _currentToken.ExpiresAt = DateTime.UtcNow.AddSeconds(_currentToken.ExpiresIn - 60); // 60s buffer

        _logger.LogInformation("WarcraftLogs OAuth token acquired, expires in {ExpiresIn}s", _currentToken.ExpiresIn);
        return _currentToken.AccessToken;
    }

    /// <summary>
    /// Executes a GraphQL query against the WarcraftLogs API
    /// </summary>
    private async Task<GraphQLResponse<T>> ExecuteGraphQLAsync<T>(
        string query,
        object? variables = null,
        WowGameVersion gameVersion = WowGameVersion.Retail,
        CancellationToken cancellationToken = default)
    {
        // Check if we're approaching rate limits - wait for reset instead of hard stop
        if (_lastRateLimitData != null && _lastRateLimitData.UsagePercent >= CriticalThreshold)
        {
            var waitSeconds = Math.Clamp(_lastRateLimitData.PointsResetIn, MinRateLimitWaitSeconds, MaxRateLimitWaitSeconds);
            _logger.LogWarning("[WCL] Rate limit at {Percent:F1}%. Waiting {WaitSeconds}s for reset.",
                _lastRateLimitData.UsagePercent, waitSeconds);

            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);

            // Refresh rate limit data after waiting
            await CheckRateLimitAsync(gameVersion, cancellationToken);

            // If STILL over limit after waiting, throw typed exception
            if (_lastRateLimitData != null && _lastRateLimitData.UsagePercent >= CriticalThreshold)
            {
                throw new WclRateLimitException(_lastRateLimitData);
            }

            _logger.LogInformation("[WCL] Rate limit recovered to {Percent:F1}% after waiting. Resuming.",
                _lastRateLimitData?.UsagePercent ?? 0);
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var apiUrl = GetApiUrl(gameVersion);

        var graphqlRequest = new GraphQLRequest
        {
            Query = query,
            Variables = variables
        };
        var json = JsonConvert.SerializeObject(graphqlRequest);

        // Use Polly resilience pipeline - must recreate request for each retry (POST)
        using var response = await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await _httpClient.SendAsync(request, ct);
        }, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GraphQL request failed: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"GraphQL request failed: {response.StatusCode}");
        }

        var result = JsonConvert.DeserializeObject<GraphQLResponse<T>>(content);

        if (result == null)
        {
            throw new InvalidOperationException("Failed to deserialize GraphQL response");
        }

        if (result.Errors != null && result.Errors.Count > 0)
        {
            // Group errors by message for cleaner logging
            var errorGroups = result.Errors
                .GroupBy(e => e.Message)
                .Select(g => new { Message = g.Key, Count = g.Count() })
                .ToList();

            foreach (var group in errorGroups)
            {
                _logger.LogWarning("GraphQL error: {Message} (occurred {Count} time(s))", group.Message, group.Count);
            }
        }

        // Check rate limits proactively - at warning threshold or periodically
        _requestCounter++;
        var shouldCheck = _requestCounter >= RateLimitCheckInterval
            || DateTime.UtcNow - _lastRateLimitCheck > TimeSpan.FromMinutes(5)
            || (_lastRateLimitData != null && _lastRateLimitData.UsagePercent >= WarningThreshold);

        if (shouldCheck)
        {
            await CheckRateLimitAsync(gameVersion, cancellationToken);
            _requestCounter = 0;
        }

        return result;
    }

    /// <summary>
    /// Check current rate limit status
    /// </summary>
    internal async Task CheckRateLimitAsync(WowGameVersion gameVersion = WowGameVersion.Retail, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = @"query { rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn } }";
            var token = await GetAccessTokenAsync(cancellationToken);
            var apiUrl = GetApiUrl(gameVersion);

            var graphqlRequest = new GraphQLRequest { Query = query };
            var json = JsonConvert.SerializeObject(graphqlRequest);

            using var response = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return await _httpClient.SendAsync(request, ct);
            }, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonConvert.DeserializeObject<GraphQLResponse<WclV2RateLimitResponse>>(content);
                _lastRateLimitData = result?.Data?.RateLimitData;
                _lastRateLimitCheck = DateTime.UtcNow;

                if (_lastRateLimitData != null)
                {
                    var percent = _lastRateLimitData.UsagePercent;
                    _logger.LogInformation("[WCL] Rate limit: {Percent:F1}% used ({Spent:F1}/{Limit})",
                        percent, _lastRateLimitData.PointsSpentThisHour, _lastRateLimitData.LimitPerHour);

                    if (percent >= WarningThreshold)
                    {
                        _logger.LogWarning("[WCL] Rate limit warning: {Percent:F1}% used. Resets in {ResetIn}s",
                            percent, _lastRateLimitData.PointsResetIn);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch rate limit data");
        }
    }

    /// <summary>
    /// Gets latest reports for multiple guilds in a single batched GraphQL query.
    /// Returns detailed information including which guilds don't exist vs have no reports.
    /// </summary>
    public async Task<WclV2BatchResult> GetBatchGuildReportsAsync(
        List<(string guildName, string serverSlug, string serverRegion, string guildKey)> guilds,
        WowGameVersion gameVersion = WowGameVersion.Retail,
        CancellationToken cancellationToken = default)
    {
        if (guilds == null || guilds.Count == 0)
            return new WclV2BatchResult();

        if (!IsConfigured)
        {
            _logger.LogWarning("WarcraftLogs client not configured, skipping batch query");
            return new WclV2BatchResult();
        }

        // Validate and sanitize input
        var validatedGuilds = new List<(string guildName, string serverSlug, string serverRegion, string guildKey, int index)>();
        var seenKeys = new HashSet<string>();

        for (int i = 0; i < guilds.Count; i++)
        {
            var (guildName, serverSlug, serverRegion, guildKey) = guilds[i];

            if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(serverSlug) || string.IsNullOrWhiteSpace(serverRegion))
            {
                _logger.LogDebug("Skipping invalid guild entry: guildName='{GuildName}', serverSlug='{ServerSlug}'",
                    guildName, serverSlug);
                continue;
            }

            // Skip duplicate guild keys
            if (!seenKeys.Add(guildKey))
            {
                _logger.LogDebug("Skipping duplicate guild key: {GuildKey}", guildKey);
                continue;
            }

            validatedGuilds.Add((guildName, serverSlug, serverRegion, guildKey, i));
        }

        if (validatedGuilds.Count == 0)
        {
            _logger.LogWarning("No valid guilds to query after validation");
            return new WclV2BatchResult();
        }

        // Build batched query with GraphQL fragment
        var queryBuilder = new StringBuilder();

        queryBuilder.AppendLine(@"
            fragment ReportFields on Report {
                code
                title
                owner { name }
                startTime
                endTime
                zone { id name }
            }");

        queryBuilder.AppendLine("query {");

        foreach (var (guildName, serverSlug, serverRegion, _, index) in validatedGuilds)
        {
            var escapedGuildName = EscapeGraphQLString(guildName);
            var escapedServerSlug = EscapeGraphQLString(serverSlug);
            var escapedServerRegion = EscapeGraphQLString(serverRegion);

            queryBuilder.AppendLine($@"
                guild_{index}: reportData {{
                    reports(
                        guildName: ""{escapedGuildName}"",
                        guildServerSlug: ""{escapedServerSlug}"",
                        guildServerRegion: ""{escapedServerRegion}"",
                        limit: 1
                    ) {{
                        data {{
                            ...ReportFields
                        }}
                    }}
                }}");
        }

        queryBuilder.AppendLine("}");

        var query = queryBuilder.ToString();
        _logger.LogDebug("[WCL Batch] Querying {Count} guilds", validatedGuilds.Count);

        try
        {
            var result = await ExecuteGraphQLAsync<JObject>(query, null, gameVersion, cancellationToken);

            if (result.Data == null)
            {
                _logger.LogWarning("[WCL Batch] Query returned no data");
                return new WclV2BatchResult();
            }

            var batchResult = new WclV2BatchResult();

            // Parse GraphQL errors to identify non-existent guilds
            var nonExistentAliases = new HashSet<string>();
            if (result.Errors != null)
            {
                foreach (var error in result.Errors)
                {
                    if (error.Message?.Contains("No guild exists") == true && error.Path?.Count > 0)
                    {
                        var alias = error.Path[0]?.ToString();
                        if (!string.IsNullOrEmpty(alias))
                        {
                            nonExistentAliases.Add(alias);
                        }
                    }
                }
            }

            // Parse each guild's results
            foreach (var (guildName, serverSlug, serverRegion, guildKey, index) in validatedGuilds)
            {
                var aliasKey = $"guild_{index}";

                try
                {
                    // Check if guild was identified as non-existent
                    if (nonExistentAliases.Contains(aliasKey))
                    {
                        batchResult.NonExistentGuilds.Add(guildKey);
                        continue;
                    }

                    var guildData = result.Data[aliasKey];
                    if (guildData == null || guildData.Type == JTokenType.Null)
                    {
                        batchResult.GuildsWithNoReports.Add(guildKey);
                        continue;
                    }

                    var reportsContainer = guildData["reports"];
                    if (reportsContainer is not JObject reportsObject)
                    {
                        batchResult.GuildsWithNoReports.Add(guildKey);
                        continue;
                    }

                    var reportsData = reportsObject["data"];
                    if (reportsData is not JArray reportsArray || reportsArray.Count == 0)
                    {
                        batchResult.GuildsWithNoReports.Add(guildKey);
                        continue;
                    }

                    var firstReport = reportsArray[0];
                    if (firstReport == null || firstReport.Type == JTokenType.Null)
                    {
                        batchResult.GuildsWithNoReports.Add(guildKey);
                        continue;
                    }

                    var report = firstReport.ToObject<WclV2Report>();
                    if (report == null || string.IsNullOrEmpty(report.Code))
                    {
                        batchResult.GuildsWithNoReports.Add(guildKey);
                        continue;
                    }

                    batchResult.Reports[guildKey] = report;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WCL Batch] Failed to parse result for {GuildName}-{ServerSlug}", guildName, serverSlug);
                    batchResult.GuildsWithNoReports.Add(guildKey);
                }
            }

            _logger.LogInformation("[WCL Batch] Retrieved reports for {Count}/{Total} guilds",
                batchResult.Reports.Count, validatedGuilds.Count);

            if (batchResult.NonExistentGuilds.Count > 0)
            {
                _logger.LogDebug("[WCL Batch] {Count} guilds don't exist on WarcraftLogs", batchResult.NonExistentGuilds.Count);
            }

            return batchResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WCL Batch] Failed to execute batch query");
            throw;
        }
    }

    /// <summary>
    /// Escapes special characters in GraphQL string literals
    /// </summary>
    private static string EscapeGraphQLString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return input
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
