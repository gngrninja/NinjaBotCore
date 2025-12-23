using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Modules.Wow
{
    public class WarcraftLogsV2Client
    {
        private readonly ILogger _logger;
        private readonly IConfigurationRoot _config;
        private readonly HttpClient _httpClient;
        private WclV2TokenResponse _currentToken;
        private readonly string _clientId;
        private readonly string _clientSecret;

        // Rate limit tracking
        private WclV2RateLimitData _lastRateLimitData;
        private DateTime _lastRateLimitCheck = DateTime.MinValue;
        private int _requestCounter = 0;

        private const string TokenUrl = "https://www.warcraftlogs.com/oauth/token";
        private const string ApiUrl = "https://www.warcraftlogs.com/api/v2/client";
        private const int RateLimitCheckInterval = 10; // Check every 10 requests
        private const double WarningThreshold = 80.0; // Warn at 80% usage
        private const double CriticalThreshold = 95.0; // Stop at 95% usage

        public WarcraftLogsV2Client(IConfigurationRoot config, IHttpClientFactory httpClientFactory, ILogger<WarcraftLogsV2Client> logger)
        {
            _config = config;
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient();
            _clientId = _config["WclClientId"];
            _clientSecret = _config["WclClientSecret"];

            if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
            {
                _logger.LogWarning("WarcraftLogs v2 API credentials not configured. Set WclClientId and WclClientSecret.");
            }
        }

        /// <summary>
        /// Gets a valid OAuth access token, refreshing if necessary
        /// </summary>
        private async Task<string> GetAccessTokenAsync()
        {
            // Return cached token if still valid
            if (_currentToken != null && !_currentToken.IsExpired)
            {
                return _currentToken.AccessToken;
            }

            // Request new token using client credentials flow
            var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);

            // OAuth requires Basic authentication with client_id:client_secret
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var formData = new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" }
            };
            request.Content = new FormUrlEncodedContent(formData);

            try
            {
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                _currentToken = JsonConvert.DeserializeObject<WclV2TokenResponse>(content);
                _currentToken.ExpiresAt = DateTime.UtcNow.AddSeconds(_currentToken.ExpiresIn - 60); // 60s buffer

                _logger.LogInformation($"WarcraftLogs v2 OAuth token acquired, expires in {_currentToken.ExpiresIn}s");
                return _currentToken.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get WarcraftLogs v2 OAuth token: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Fetches current rate limit data from the API
        /// </summary>
        private async Task<WclV2RateLimitData> GetRateLimitDataAsync()
        {
            var query = @"
                query {
                    rateLimitData {
                        limitPerHour
                        pointsSpentThisHour
                        pointsResetIn
                    }
                }
            ";

            try
            {
                var result = await ExecuteGraphQLInternalAsync<WclV2RateLimitResponse>(query);
                return result.Data?.RateLimitData;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to fetch rate limit data: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks and logs rate limit status if needed
        /// </summary>
        private async Task CheckRateLimitAsync()
        {
            _requestCounter++;

            // Check every N requests or if we haven't checked in 5 minutes
            bool shouldCheck = _requestCounter % RateLimitCheckInterval == 0 ||
                              (DateTime.UtcNow - _lastRateLimitCheck).TotalMinutes >= 5;

            if (!shouldCheck)
                return;

            var rateLimitData = await GetRateLimitDataAsync();
            if (rateLimitData == null)
                return;

            _lastRateLimitData = rateLimitData;
            _lastRateLimitCheck = DateTime.UtcNow;

            // Log based on usage level
            if (rateLimitData.UsagePercent >= CriticalThreshold)
            {
                _logger.LogError($"[WCL v2] CRITICAL: Rate limit at {rateLimitData.UsagePercent:F1}% ({rateLimitData.PointsSpentThisHour:F1}/{rateLimitData.LimitPerHour:F0}) - {rateLimitData.PointsRemaining:F1} points remaining, resets in {rateLimitData.PointsResetIn}s");
            }
            else if (rateLimitData.UsagePercent >= WarningThreshold)
            {
                _logger.LogWarning($"[WCL v2] Rate limit at {rateLimitData.UsagePercent:F1}% ({rateLimitData.PointsSpentThisHour:F1}/{rateLimitData.LimitPerHour:F0}) - {rateLimitData.PointsRemaining:F1} points remaining");
            }
            else
            {
                _logger.LogInformation($"[WCL v2] Rate limit: {rateLimitData.UsagePercent:F1}% used ({rateLimitData.PointsSpentThisHour:F1}/{rateLimitData.LimitPerHour:F0})");
            }
        }

        /// <summary>
        /// Internal GraphQL execution without rate limit checking (to avoid recursion)
        /// </summary>
        private async Task<GraphQLResponse<T>> ExecuteGraphQLInternalAsync<T>(string query, object variables = null)
        {
            var token = await GetAccessTokenAsync();

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var graphqlRequest = new GraphQLRequest
            {
                Query = query,
                Variables = variables
            };

            var json = JsonConvert.SerializeObject(graphqlRequest);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"GraphQL request failed: {response.StatusCode} - {content}");
                throw new HttpRequestException($"GraphQL request failed: {response.StatusCode}");
            }

            var result = JsonConvert.DeserializeObject<GraphQLResponse<T>>(content);

            if (result.Errors != null && result.Errors.Count > 0)
            {
                _logger.LogWarning($"GraphQL returned errors: {JsonConvert.SerializeObject(result.Errors)}");
            }

            return result;
        }

        /// <summary>
        /// Executes a GraphQL query against the WarcraftLogs v2 API with rate limit monitoring
        /// </summary>
        private async Task<GraphQLResponse<T>> ExecuteGraphQLAsync<T>(string query, object variables = null)
        {
            // Check if we're approaching rate limits
            if (_lastRateLimitData != null && _lastRateLimitData.UsagePercent >= CriticalThreshold)
            {
                _logger.LogError($"[WCL v2] Blocking request - rate limit at {_lastRateLimitData.UsagePercent:F1}%. Wait {_lastRateLimitData.PointsResetIn}s for reset.");
                throw new InvalidOperationException($"Rate limit exceeded: {_lastRateLimitData.UsagePercent:F1}% used. Resets in {_lastRateLimitData.PointsResetIn}s.");
            }

            try
            {
                var result = await ExecuteGraphQLInternalAsync<T>(query, variables);

                // Check rate limits after successful requests (not on every request to avoid spam)
                await CheckRateLimitAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"GraphQL execution failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets latest reports for multiple guilds in a single batched GraphQL query
        /// </summary>
        public async Task<Dictionary<string, WclV2Report>> GetBatchGuildReportsAsync(List<(string guildName, string serverSlug, string serverRegion, string guildKey)> guilds)
        {
            if (guilds == null || guilds.Count == 0)
                return new Dictionary<string, WclV2Report>();

            // Validate and sanitize input
            var validatedGuilds = new List<(string guildName, string serverSlug, string serverRegion, string guildKey, int index)>();
            for (int i = 0; i < guilds.Count; i++)
            {
                var (guildName, serverSlug, serverRegion, guildKey) = guilds[i];

                if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(serverSlug) || string.IsNullOrWhiteSpace(serverRegion))
                {
                    _logger.LogWarning($"Skipping invalid guild entry at index {i}: guildName='{guildName}', serverSlug='{serverSlug}', serverRegion='{serverRegion}'");
                    continue;
                }

                validatedGuilds.Add((guildName, serverSlug, serverRegion, guildKey, i));
            }

            if (validatedGuilds.Count == 0)
            {
                _logger.LogWarning("No valid guilds to query after validation");
                return new Dictionary<string, WclV2Report>();
            }

            // Build batched query with GraphQL fragment for reusability
            var queryBuilder = new StringBuilder();

            // Define reusable fragment
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

            // Build dynamic aliases for each guild
            foreach (var (guildName, serverSlug, serverRegion, _, index) in validatedGuilds)
            {
                // Escape quotes in parameters to prevent GraphQL injection
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
            _logger.LogDebug($"[v2 Batch] Querying {validatedGuilds.Count} guilds");

            try
            {
                var result = await ExecuteGraphQLAsync<Newtonsoft.Json.Linq.JObject>(query);

                if (result.Data == null)
                {
                    _logger.LogWarning("[v2 Batch] Query returned no data");
                    return new Dictionary<string, WclV2Report>();
                }

                var batchResults = new Dictionary<string, WclV2Report>();
                var parseErrors = new List<string>();

                // Parse each guild's results with type-safe validation
                foreach (var (guildName, serverSlug, serverRegion, guildKey, index) in validatedGuilds)
                {
                    var aliasKey = $"guild_{index}";
                    var guildIdentifier = $"{guildName}-{serverSlug} ({serverRegion})";

                    try
                    {
                        var guildData = result.Data[aliasKey];
                        if (guildData == null || guildData.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            _logger.LogWarning($"[v2 Batch] No data returned for {guildIdentifier}");
                            continue;
                        }

                        var reportsContainer = guildData["reports"];
                        if (reportsContainer == null)
                        {
                            _logger.LogWarning($"[v2 Batch] Missing 'reports' field for {guildIdentifier}");
                            continue;
                        }

                        var reportsData = reportsContainer["data"];
                        if (reportsData == null)
                        {
                            _logger.LogWarning($"[v2 Batch] Missing 'data' field for {guildIdentifier}");
                            continue;
                        }

                        // Type-safe array validation
                        if (!(reportsData is Newtonsoft.Json.Linq.JArray reportsArray))
                        {
                            _logger.LogWarning($"[v2 Batch] 'data' is not an array for {guildIdentifier}, got {reportsData.Type}");
                            continue;
                        }

                        if (reportsArray.Count == 0)
                        {
                            _logger.LogDebug($"[v2 Batch] No reports found for {guildIdentifier}");
                            continue;
                        }

                        var firstReport = reportsArray[0];
                        if (firstReport == null || firstReport.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            _logger.LogWarning($"[v2 Batch] First report is null for {guildIdentifier}");
                            continue;
                        }

                        var report = firstReport.ToObject<WclV2Report>();
                        if (report == null)
                        {
                            _logger.LogWarning($"[v2 Batch] Failed to deserialize report for {guildIdentifier}");
                            continue;
                        }

                        // Validate required fields
                        if (string.IsNullOrEmpty(report.Code))
                        {
                            _logger.LogWarning($"[v2 Batch] Report missing code for {guildIdentifier}");
                            continue;
                        }

                        batchResults[guildKey] = report;
                        _logger.LogDebug($"[v2 Batch] Successfully parsed report {report.Code} for {guildIdentifier}");
                    }
                    catch (JsonException jsonEx)
                    {
                        var error = $"{guildIdentifier}: JSON parsing failed - {jsonEx.Message}";
                        parseErrors.Add(error);
                        _logger.LogError($"[v2 Batch] {error}");
                    }
                    catch (Exception ex)
                    {
                        var error = $"{guildIdentifier}: {ex.GetType().Name} - {ex.Message}";
                        parseErrors.Add(error);
                        _logger.LogError($"[v2 Batch] Failed to parse result for {error}");
                    }
                }

                // Summary logging
                var successRate = validatedGuilds.Count > 0 ? (batchResults.Count * 100.0 / validatedGuilds.Count) : 0;
                _logger.LogInformation($"[v2 Batch] Retrieved reports for {batchResults.Count}/{validatedGuilds.Count} guilds ({successRate:F1}% success rate)");

                if (parseErrors.Count > 0)
                {
                    _logger.LogWarning($"[v2 Batch] Encountered {parseErrors.Count} parsing errors");
                }

                return batchResults;
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError($"[v2 Batch] HTTP request failed: {httpEx.Message}");
                throw;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError($"[v2 Batch] Response parsing failed: {jsonEx.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[v2 Batch] Unexpected error: {ex.GetType().Name} - {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Escapes special characters in GraphQL string literals to prevent injection
        /// </summary>
        private string EscapeGraphQLString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return input
                .Replace("\\", "\\\\")  // Backslash must be first
                .Replace("\"", "\\\"")  // Escape quotes
                .Replace("\n", "\\n")   // Escape newlines
                .Replace("\r", "\\r")   // Escape carriage returns
                .Replace("\t", "\\t");  // Escape tabs
        }

        /// <summary>
        /// Gets reports for a guild using the v2 GraphQL API
        /// </summary>
        public async Task<List<WclV2Report>> GetGuildReportsAsync(string guildName, string serverSlug, string serverRegion, int limit = 5)
        {
            var query = @"
                query($guildName: String!, $serverSlug: String!, $serverRegion: String!, $limit: Int!) {
                    reportData {
                        reports(
                            guildName: $guildName,
                            guildServerSlug: $serverSlug,
                            guildServerRegion: $serverRegion,
                            limit: $limit
                        ) {
                            data {
                                code
                                title
                                owner {
                                    name
                                }
                                startTime
                                endTime
                                zone {
                                    id
                                    name
                                }
                            }
                        }
                    }
                }
            ";

            var variables = new
            {
                guildName,
                serverSlug,
                serverRegion,
                limit
            };

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2GuildReportsResponse>(query, variables);

                if (result.Data?.ReportData?.Reports?.Data != null)
                {
                    _logger.LogInformation($"Retrieved {result.Data.ReportData.Reports.Data.Count} reports for {guildName}-{serverSlug}");
                    return result.Data.ReportData.Reports.Data;
                }

                _logger.LogWarning($"No reports found for {guildName}-{serverSlug}");
                return new List<WclV2Report>();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get guild reports: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Test method to compare v2 API response with v1
        /// </summary>
        public async Task<string> TestGuildReportsAsync(string guildName, string serverSlug, string serverRegion)
        {
            try
            {
                var reports = await GetGuildReportsAsync(guildName, serverSlug, serverRegion, 3);

                var sb = new StringBuilder();
                sb.AppendLine($"=== WarcraftLogs v2 API Test ===");
                sb.AppendLine($"Guild: {guildName}-{serverSlug} ({serverRegion.ToUpper()})");
                sb.AppendLine($"Reports found: {reports.Count}");
                sb.AppendLine();

                foreach (var report in reports)
                {
                    sb.AppendLine($"Report ID: {report.Code}");
                    sb.AppendLine($"  Title: {report.Title}");
                    sb.AppendLine($"  Owner: {report.OwnerName}");
                    sb.AppendLine($"  Zone: {report.ZoneName}");
                    sb.AppendLine($"  Start: {DateTimeOffset.FromUnixTimeMilliseconds(report.StartTime).UtcDateTime}");
                    sb.AppendLine($"  URL: {report.ReportURL}");
                    sb.AppendLine();
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Test failed: {ex.Message}\n{ex.StackTrace}";
            }
        }
    }
}
