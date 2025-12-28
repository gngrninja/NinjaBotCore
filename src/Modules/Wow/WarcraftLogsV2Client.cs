using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>
    /// Specifies which WoW game version API to query
    /// </summary>
    public enum WowGameVersion
    {
        /// <summary>Retail/Live WoW (default)</summary>
        Retail,
        /// <summary>Classic WoW (current iteration)</summary>
        Classic,
        /// <summary>Classic Fresh</summary>
        ClassicFresh,
        /// <summary>Vanilla Classic</summary>
        Vanilla
    }

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
        private const string ApiUrlRetail = "https://www.warcraftlogs.com/api/v2/client";
        private const string ApiUrlClassic = "https://classic.warcraftlogs.com/api/v2/client";
        private const string ApiUrlClassicFresh = "https://fresh.warcraftlogs.com/api/v2/client";
        private const string ApiUrlVanilla = "https://vanilla.warcraftlogs.com/api/v2/client";
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
        /// Gets the appropriate API endpoint URL based on game version
        /// </summary>
        private static string GetApiUrl(WowGameVersion gameVersion)
        {
            return gameVersion switch
            {
                WowGameVersion.Retail => ApiUrlRetail,
                WowGameVersion.Classic => ApiUrlClassic,
                WowGameVersion.ClassicFresh => ApiUrlClassicFresh,
                WowGameVersion.Vanilla => ApiUrlVanilla,
                _ => ApiUrlRetail
            };
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
        private async Task<WclV2RateLimitData> GetRateLimitDataAsync(WowGameVersion gameVersion = WowGameVersion.Retail)
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
                var result = await ExecuteGraphQLInternalAsync<WclV2RateLimitResponse>(query, null, gameVersion);
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
        private async Task<GraphQLResponse<T>> ExecuteGraphQLInternalAsync<T>(string query, object variables = null, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var token = await GetAccessTokenAsync();
            var apiUrl = GetApiUrl(gameVersion);

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
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
                // Group errors by message for cleaner logging
                var errorGroups = result.Errors
                    .GroupBy(e => e.Message)
                    .Select(g => new { Message = g.Key, Count = g.Count() })
                    .ToList();

                foreach (var group in errorGroups)
                {
                    _logger.LogWarning($"GraphQL error: {group.Message} (occurred {group.Count} time{(group.Count > 1 ? "s" : "")})");
                }
            }

            return result;
        }

        /// <summary>
        /// Executes a GraphQL query against the WarcraftLogs v2 API with rate limit monitoring
        /// </summary>
        private async Task<GraphQLResponse<T>> ExecuteGraphQLAsync<T>(string query, object variables = null, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            // Check if we're approaching rate limits
            if (_lastRateLimitData != null && _lastRateLimitData.UsagePercent >= CriticalThreshold)
            {
                _logger.LogError($"[WCL v2] Blocking request - rate limit at {_lastRateLimitData.UsagePercent:F1}%. Wait {_lastRateLimitData.PointsResetIn}s for reset.");
                throw new InvalidOperationException($"Rate limit exceeded: {_lastRateLimitData.UsagePercent:F1}% used. Resets in {_lastRateLimitData.PointsResetIn}s.");
            }

            try
            {
                var result = await ExecuteGraphQLInternalAsync<T>(query, variables, gameVersion);

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
        /// Returns detailed information including which guilds don't exist vs have no reports
        /// </summary>
        public async Task<WclV2BatchResult> GetBatchGuildReportsAsync(List<(string guildName, string serverSlug, string serverRegion, string guildKey)> guilds, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            if (guilds == null || guilds.Count == 0)
                return new WclV2BatchResult();

            // Validate and sanitize input
            var validatedGuilds = new List<(string guildName, string serverSlug, string serverRegion, string guildKey, int index)>();
            var seenKeys = new HashSet<string>();
            var duplicateKeys = new List<(string guildKey, string guild1, string guild2)>();

            for (int i = 0; i < guilds.Count; i++)
            {
                var (guildName, serverSlug, serverRegion, guildKey) = guilds[i];

                if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(serverSlug) || string.IsNullOrWhiteSpace(serverRegion))
                {
                    _logger.LogWarning($"Skipping invalid guild entry at index {i}: guildName='{guildName}', serverSlug='{serverSlug}', serverRegion='{serverRegion}'");
                    continue;
                }

                // Check for duplicate guild keys
                if (!seenKeys.Add(guildKey))
                {
                    var existingGuild = validatedGuilds.First(g => g.guildKey == guildKey);
                    var currentGuild = $"{guildName}-{serverSlug} ({serverRegion})";
                    var existing = $"{existingGuild.guildName}-{existingGuild.serverSlug} ({existingGuild.serverRegion})";
                    duplicateKeys.Add((guildKey, existing, currentGuild));
                    _logger.LogWarning($"[v2 Batch] DUPLICATE GUILD KEY '{guildKey}': '{existing}' and '{currentGuild}' - skipping duplicate!");
                    continue; // Skip the duplicate entry
                }

                validatedGuilds.Add((guildName, serverSlug, serverRegion, guildKey, i));
            }

            if (duplicateKeys.Count > 0)
            {
                _logger.LogWarning($"[v2 Batch] Found and skipped {duplicateKeys.Count} duplicate guild key(s) in batch. Run /wow-cleanup-duplicates to fix the database.");
            }

            if (validatedGuilds.Count == 0)
            {
                _logger.LogWarning("No valid guilds to query after validation");
                return new WclV2BatchResult();
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
                var result = await ExecuteGraphQLAsync<Newtonsoft.Json.Linq.JObject>(query, null, gameVersion);

                if (result.Data == null)
                {
                    _logger.LogWarning("[v2 Batch] Query returned no data");
                    return new WclV2BatchResult();
                }

                var batchResult = new WclV2BatchResult();
                var parseErrors = new List<string>();
                var uncategorizedGuilds = new List<(string guildKey, string guildName, string reason)>();

                // Parse GraphQL errors to identify non-existent guilds
                var nonExistentAliases = new HashSet<string>();
                if (result.Errors != null && result.Errors.Count > 0)
                {
                    foreach (var error in result.Errors)
                    {
                        // Check if this is a "guild doesn't exist" error
                        if (error.Message != null && error.Message.Contains("No guild exists for this name/server/region"))
                        {
                            // Extract the guild alias from the error path (e.g., ["guild_0", "reports"])
                            if (error.Path != null && error.Path.Count > 0)
                            {
                                var aliasFromPath = error.Path[0]?.ToString();
                                if (!string.IsNullOrEmpty(aliasFromPath))
                                {
                                    nonExistentAliases.Add(aliasFromPath);
                                    _logger.LogDebug($"[v2 Batch] Identified non-existent guild at {aliasFromPath}");
                                }
                            }
                        }
                    }
                }

                // Parse each guild's results with type-safe validation
                foreach (var (guildName, serverSlug, serverRegion, guildKey, index) in validatedGuilds)
                {
                    var aliasKey = $"guild_{index}";
                    var guildIdentifier = $"{guildName}-{serverSlug} ({serverRegion})";
                    var wasCategorized = false;

                    _logger.LogDebug($"[v2 Batch] Processing guild {guildIdentifier} (Key: {guildKey}, Alias: {aliasKey})");

                    try
                    {
                        // Check if this guild was identified as non-existent from GraphQL errors
                        if (nonExistentAliases.Contains(aliasKey))
                        {
                            batchResult.NonExistentGuilds.Add(guildKey);
                            _logger.LogDebug($"[v2 Batch] {guildIdentifier} does not exist on WarcraftLogs");
                            wasCategorized = true;
                            continue;
                        }

                        var guildData = result.Data[aliasKey];
                        if (guildData == null || guildData.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            batchResult.GuildsWithNoReports.Add(guildKey);
                            wasCategorized = true;
                            _logger.LogDebug($"[v2 Batch] No data returned for {guildIdentifier}");
                            continue;
                        }

                        var reportsContainer = guildData["reports"];
                        if (reportsContainer == null)
                        {
                            batchResult.GuildsWithNoReports.Add(guildKey);
                            wasCategorized = true;
                            _logger.LogDebug($"[v2 Batch] Missing 'reports' field for {guildIdentifier}");
                            continue;
                        }

                        // Type-safe object validation before accessing child properties
                        if (!(reportsContainer is Newtonsoft.Json.Linq.JObject reportsObject))
                        {
                            batchResult.GuildsWithNoReports.Add(guildKey);
                            wasCategorized = true;
                            _logger.LogDebug($"[v2 Batch] Reports field is not an object for {guildIdentifier}, got {reportsContainer.Type}");
                            continue;
                        }

                        var reportsData = reportsObject["data"];
                        if (reportsData == null)
                        {
                            batchResult.GuildsWithNoReports.Add(guildKey);
                            wasCategorized = true;
                            _logger.LogDebug($"[v2 Batch] Missing 'data' field for {guildIdentifier}");
                            continue;
                        }

                        // Type-safe array validation
                        if (!(reportsData is Newtonsoft.Json.Linq.JArray reportsArray))
                        {
                            batchResult.GuildsWithNoReports.Add(guildKey);
                            wasCategorized = true;
                            _logger.LogDebug($"[v2 Batch] 'data' is not an array for {guildIdentifier}, got {reportsData.Type}");
                            continue;
                        }

                        if (reportsArray.Count == 0)
                        {
                            batchResult.GuildsWithNoReports.Add(guildKey);
                            wasCategorized = true;
                            _logger.LogDebug($"[v2 Batch] No reports found for {guildIdentifier}");
                            continue;
                        }

                        var firstReport = reportsArray[0];
                        if (firstReport == null || firstReport.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            _logger.LogWarning($"[v2 Batch] First report is null for {guildIdentifier}");
                            uncategorizedGuilds.Add((guildKey, guildIdentifier, "First report is null"));
                            wasCategorized = true;
                            continue;
                        }

                        var report = firstReport.ToObject<WclV2Report>();
                        if (report == null)
                        {
                            _logger.LogWarning($"[v2 Batch] Failed to deserialize report for {guildIdentifier}");
                            uncategorizedGuilds.Add((guildKey, guildIdentifier, "Deserialization failed"));
                            wasCategorized = true;
                            continue;
                        }

                        // Validate required fields
                        if (string.IsNullOrEmpty(report.Code))
                        {
                            _logger.LogWarning($"[v2 Batch] Report missing code for {guildIdentifier}");
                            uncategorizedGuilds.Add((guildKey, guildIdentifier, "Missing report code"));
                            wasCategorized = true;
                            continue;
                        }

                        batchResult.Reports[guildKey] = report;
                        wasCategorized = true;
                        _logger.LogDebug($"[v2 Batch] Successfully parsed report {report.Code} for {guildIdentifier}");
                    }
                    catch (JsonException jsonEx)
                    {
                        var error = $"{guildIdentifier}: JSON parsing failed - {jsonEx.Message}";
                        parseErrors.Add(error);
                        uncategorizedGuilds.Add((guildKey, guildIdentifier, $"JSON exception: {jsonEx.Message}"));
                        wasCategorized = true;  // Categorized as exception
                        _logger.LogError($"[v2 Batch] {error}");
                    }
                    catch (Exception ex)
                    {
                        var error = $"{guildIdentifier}: {ex.GetType().Name} - {ex.Message}";
                        parseErrors.Add(error);
                        uncategorizedGuilds.Add((guildKey, guildIdentifier, $"Exception: {ex.GetType().Name}"));
                        wasCategorized = true;  // Categorized as exception
                        _logger.LogError($"[v2 Batch] Failed to parse result for {error}");
                    }

                    // Track guilds that didn't get categorized anywhere
                    if (!wasCategorized)
                    {
                        uncategorizedGuilds.Add((guildKey, guildIdentifier, "UNKNOWN - no categorization path taken"));
                        _logger.LogWarning($"[v2 Batch] Guild {guildIdentifier} (Key: {guildKey}) was not categorized by any code path!");
                    }

                    // Log final categorization for this guild
                    var category = "UNKNOWN";
                    if (batchResult.Reports.ContainsKey(guildKey)) category = "Reports";
                    else if (batchResult.NonExistentGuilds.Contains(guildKey)) category = "NonExistent";
                    else if (batchResult.GuildsWithNoReports.Contains(guildKey)) category = "NoReports";
                    else if (uncategorizedGuilds.Any(u => u.guildKey == guildKey)) category = "Uncategorized";

                    _logger.LogDebug($"[v2 Batch] Guild {guildIdentifier} final category: {category}");
                }

                // Summary logging
                _logger.LogDebug($"[v2 Batch] Pre-summary counts: Reports={batchResult.Reports.Count}, NonExistent={batchResult.NonExistentGuilds.Count}, NoReports={batchResult.GuildsWithNoReports.Count}, Uncategorized={uncategorizedGuilds.Count}, ValidatedTotal={validatedGuilds.Count}");

                var successRate = validatedGuilds.Count > 0 ? (batchResult.Reports.Count * 100.0 / validatedGuilds.Count) : 0;
                _logger.LogInformation($"[v2 Batch] Retrieved reports for {batchResult.Reports.Count}/{validatedGuilds.Count} guilds ({successRate:F1}% success rate)");

                if (parseErrors.Count > 0)
                {
                    _logger.LogWarning($"[v2 Batch] Encountered {parseErrors.Count} parsing errors");
                }

                if (batchResult.NonExistentGuilds.Count > 0)
                {
                    _logger.LogInformation($"[v2 Batch] {batchResult.NonExistentGuilds.Count} guilds don't exist on WarcraftLogs");
                }

                if (batchResult.GuildsWithNoReports.Count > 0)
                {
                    _logger.LogInformation($"[v2 Batch] {batchResult.GuildsWithNoReports.Count} guilds exist but have no reports");
                }

                if (uncategorizedGuilds.Count > 0)
                {
                    _logger.LogInformation($"[v2 Batch] {uncategorizedGuilds.Count} uncategorized guilds (validation failures):");
                    foreach (var (guildKey, guildName, reason) in uncategorizedGuilds)
                    {
                        _logger.LogInformation($"[v2 Batch]   - {guildName} (Key: {guildKey}): {reason}");
                    }
                }

                // Sanity check: detect guilds that aren't in any category
                var categorizedCount = batchResult.Reports.Count + batchResult.NonExistentGuilds.Count + batchResult.GuildsWithNoReports.Count;
                if (categorizedCount < validatedGuilds.Count)
                {
                    var missingCount = validatedGuilds.Count - categorizedCount;
                    _logger.LogWarning($"[v2 Batch] COUNT MISMATCH: {missingCount} guilds not in any category (Reports={batchResult.Reports.Count}, NonExistent={batchResult.NonExistentGuilds.Count}, NoReports={batchResult.GuildsWithNoReports.Count}, Validated={validatedGuilds.Count})");

                    // Find and log the missing guilds
                    var foundMissing = 0;
                    foreach (var (guildName, serverSlug, serverRegion, guildKey, index) in validatedGuilds)
                    {
                        var inReports = batchResult.Reports.ContainsKey(guildKey);
                        var inNonExistent = batchResult.NonExistentGuilds.Contains(guildKey);
                        var inNoReports = batchResult.GuildsWithNoReports.Contains(guildKey);

                        if (!inReports && !inNonExistent && !inNoReports)
                        {
                            foundMissing++;
                            _logger.LogWarning($"[v2 Batch] MISSING GUILD #{foundMissing}: {guildName}-{serverSlug} ({serverRegion}) - Key: '{guildKey}'");
                        }
                    }

                    if (foundMissing == 0)
                    {
                        _logger.LogWarning($"[v2 Batch] BUG: Expected to find {missingCount} missing guilds, but loop found 0!");
                        _logger.LogWarning($"[v2 Batch] Reports keys sample: {string.Join(", ", batchResult.Reports.Keys.Take(5))}");
                        _logger.LogWarning($"[v2 Batch] ValidatedGuilds keys sample: {string.Join(", ", validatedGuilds.Take(5).Select(g => g.guildKey))}");
                    }
                    else if (foundMissing != missingCount)
                    {
                        _logger.LogWarning($"[v2 Batch] BUG: Expected {missingCount} missing guilds, but found {foundMissing}!");
                    }
                }

                return batchResult;
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
        public async Task<List<WclV2Report>> GetGuildReportsAsync(string guildName, string serverSlug, string serverRegion, int limit = 5, WowGameVersion gameVersion = WowGameVersion.Retail)
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
                var result = await ExecuteGraphQLAsync<WclV2GuildReportsResponse>(query, variables, gameVersion);

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
        public async Task<string> TestGuildReportsAsync(string guildName, string serverSlug, string serverRegion, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            try
            {
                var reports = await GetGuildReportsAsync(guildName, serverSlug, serverRegion, 3, gameVersion);

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
