using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

    public partial class WarcraftLogsV2Client
    {
        private readonly ILogger _logger;
        private readonly IConfigurationRoot _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TimeProvider _clock;
        private WclV2TokenResponse _currentToken;
        private readonly string _clientId;
        private readonly string _clientSecret;

        // Quota observations and rechecks are serialized, including recovery after a reset.
        private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
        private WclV2RateLimitData _lastRateLimitData;
        private DateTimeOffset _lastRateLimitCheck = DateTimeOffset.MinValue;
        private DateTimeOffset _rateLimitResetAt = DateTimeOffset.MinValue;
        private DateTimeOffset _nextQuotaProbeAt = DateTimeOffset.MinValue;
        private long _retryAfterUtcTicks;
        private int _requestCounter = 0;
        private static readonly TimeSpan QuotaCheckTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan QuotaRetryDelay = TimeSpan.FromSeconds(30);
        private const int MaxResponseBytes = 4 * 1024 * 1024;

        // Current raid tier cache (7 day TTL - boss encounters are static data that only changes when new raids release)
        // Using ConcurrentDictionary for thread-safety (singleton service, static cache)
        private static readonly ConcurrentDictionary<WowGameVersion, (WclV2ZoneDetail Zone, DateTime CachedAt)> _raidTierCache = new();
        private static readonly TimeSpan RaidTierCacheTtl = TimeSpan.FromDays(7);

        /// <summary>
        /// Clears the raid tier cache, forcing a fresh fetch on next request.
        /// Call this when a new raid releases or after /refresh-raid-tier.
        /// </summary>
        public void ClearRaidTierCache()
        {
            _raidTierCache.Clear();
            _logger.LogInformation("Raid tier cache cleared");
        }

        private const string TokenUrl = "https://www.warcraftlogs.com/oauth/token";
        private const string ApiUrlRetail = "https://www.warcraftlogs.com/api/v2/client";
        private const string ApiUrlClassic = "https://classic.warcraftlogs.com/api/v2/client";
        private const string ApiUrlClassicFresh = "https://fresh.warcraftlogs.com/api/v2/client";
        private const string ApiUrlVanilla = "https://vanilla.warcraftlogs.com/api/v2/client";
        private const int RateLimitCheckInterval = 10; // Check every 10 requests
        private const double WarningThreshold = 80.0; // Warn at 80% usage
        private const double CriticalThreshold = 95.0; // Stop at 95% usage

        public WarcraftLogsV2Client(IConfigurationRoot config, IHttpClientFactory httpClientFactory, ILogger<WarcraftLogsV2Client> logger)
            : this(config, httpClientFactory, logger, TimeProvider.System) { }

        internal WarcraftLogsV2Client(IConfigurationRoot config, IHttpClientFactory httpClientFactory, ILogger<WarcraftLogsV2Client> logger, TimeProvider clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _config = config;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
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
        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Return cached token if still valid
            if (_currentToken != null && !_currentToken.IsExpired)
            {
                return _currentToken.AccessToken;
            }

            // Request new token using client credentials flow
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);

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
                using var client = _httpClientFactory.CreateClient();
                // ResponseHeadersRead stops HttpClient's timeout at the headers; retain it for the body.
                using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestBudget.CancelAfter(client.Timeout);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token);
                EnsureWclSuccess(response, "OAuth");

                var content = await ReadResponseAsync(response, requestBudget.Token);
                _currentToken = ParseResponse<WclV2TokenResponse>(content);
                if (string.IsNullOrWhiteSpace(_currentToken.AccessToken))
                    throw new InvalidOperationException("WCL OAuth response omitted the access token.");
                _currentToken.ExpiresAt = DateTime.UtcNow.AddSeconds(_currentToken.ExpiresIn - 60); // 60s buffer

                _logger.LogInformation($"WarcraftLogs v2 OAuth token acquired, expires in {_currentToken.ExpiresIn}s");
                return _currentToken.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to get WarcraftLogs v2 OAuth token ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Fetches current rate limit data from the API
        /// </summary>
        private async Task<WclV2RateLimitData> GetRateLimitDataAsync(CancellationToken cancellationToken)
        {
            const string query = "query { rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn } }";
            try
            {
                // Validate field presence/types before mapping to the legacy nonnullable DTO: defaults
                // such as zero limit/spend must never turn an unusable probe into a healthy budget.
                var result = await ExecuteGraphQLInternalAsync<JObject>(query, cancellationToken: cancellationToken);
                if (result.Errors?.Count > 0 || result.Data?["rateLimitData"] is not JObject data ||
                    !TryQuotaNumber(data["limitPerHour"], out var limit) || limit <= 0 ||
                    !TryQuotaNumber(data["pointsSpentThisHour"], out var spent) || spent < 0 ||
                    data["pointsResetIn"]?.Type != JTokenType.Integer)
                    throw new InvalidOperationException("Unusable WCL quota response.");
                var reset = data.Value<long>("pointsResetIn");
                if (reset < 0 || reset > int.MaxValue || !double.IsFinite(spent / limit * 100))
                    throw new InvalidOperationException("Unusable WCL quota response.");
                return new WclV2RateLimitData { LimitPerHour = limit, PointsSpentThisHour = spent, PointsResetIn = (int)reset };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch rate limit data ({ErrorType})", ex.GetType().Name);
                return null;
            }
        }

        private static bool TryQuotaNumber(JToken token, out double value)
        {
            value = 0;
            if (token?.Type is not (JTokenType.Integer or JTokenType.Float)) return false;
            value = token.Value<double>();
            return double.IsFinite(value);
        }

        // Caller holds _rateLimitGate. Failure/cancellation keeps the last known budget, never
        // clears an exhausted observation, and prevents waiting callers from hot-looping probes.
        private async Task RefreshQuotaAsync(bool recovery, CancellationToken cancellationToken)
        {
            _nextQuotaProbeAt = _clock.GetUtcNow() + QuotaRetryDelay;
            var data = await GetRateLimitDataAsync(cancellationToken);
            var now = _clock.GetUtcNow();
            if (data == null)
            {
                _nextQuotaProbeAt = now + QuotaRetryDelay;
                return;
            }
            _lastRateLimitData = data;
            _lastRateLimitCheck = now;
            _rateLimitResetAt = now.AddSeconds(data.PointsResetIn);
            _nextQuotaProbeAt = data.UsagePercent >= CriticalThreshold
                ? (recovery && _rateLimitResetAt < now + QuotaRetryDelay ? now + QuotaRetryDelay : _rateLimitResetAt)
                : now;
            var level = data.UsagePercent >= CriticalThreshold ? LogLevel.Error :
                data.UsagePercent >= WarningThreshold ? LogLevel.Warning : LogLevel.Information;
            _logger.Log(level, "[WCL v2] Rate limit: {Usage:F1}% used ({Spent:F1}/{Limit:F0}); reset in {Reset}s",
                data.UsagePercent, data.PointsSpentThisHour, data.LimitPerHour, data.PointsResetIn);
        }

        private DateTimeOffset RetryAfterUtc => new(Interlocked.Read(ref _retryAfterUtcTicks), TimeSpan.Zero);

        private InvalidOperationException QuotaBlocked()
        {
            var retry = RetryAfterUtc;
            if (_lastRateLimitData?.UsagePercent >= CriticalThreshold)
            {
                if (_rateLimitResetAt > retry) retry = _rateLimitResetAt;
                if (_nextQuotaProbeAt > retry) retry = _nextQuotaProbeAt;
            }
            var seconds = Math.Max(0, Math.Ceiling((retry - _clock.GetUtcNow()).TotalSeconds));
            _logger.LogWarning("[WCL v2] Blocking request; retry quota check in {Seconds}s", seconds);
            return new InvalidOperationException($"Warcraft Logs quota unavailable. Retry in {seconds:F0}s.");
        }

        private async Task EnsureQuotaAsync(CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(QuotaCheckTimeout);
            var acquired = false;
            try
            {
                await _rateLimitGate.WaitAsync(budget.Token);
                acquired = true;
                var now = _clock.GetUtcNow();
                if (now < RetryAfterUtc) throw QuotaBlocked();
                if (_lastRateLimitData?.UsagePercent >= CriticalThreshold)
                {
                    if (now >= _rateLimitResetAt && now >= _nextQuotaProbeAt)
                        await RefreshQuotaAsync(recovery: true, budget.Token);
                    if (_lastRateLimitData.UsagePercent >= CriticalThreshold) throw QuotaBlocked();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Warcraft Logs quota check timed out. Try again later.");
            }
            finally { if (acquired) _rateLimitGate.Release(); }
        }

        /// <summary>Best-effort periodic monitoring, separate from the fail-closed recovery gate.</summary>
        private async Task CheckRateLimitAsync(CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(QuotaCheckTimeout);
            var acquired = false;
            try
            {
                await _rateLimitGate.WaitAsync(budget.Token);
                acquired = true;
                _requestCounter++;
                var now = _clock.GetUtcNow();
                // Reports already in flight when exhaustion was observed cannot bypass its reset.
                if (_lastRateLimitData?.UsagePercent >= CriticalThreshold || now < _nextQuotaProbeAt || now < RetryAfterUtc)
                    return;
                if (_requestCounter % RateLimitCheckInterval != 0 && (now - _lastRateLimitCheck).TotalMinutes < 5)
                    return;
                await RefreshQuotaAsync(recovery: false, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[WCL v2] Periodic quota check timed out");
            }
            finally { if (acquired) _rateLimitGate.Release(); }
        }

        /// <summary>
        /// Internal GraphQL execution without rate limit checking (to avoid recursion)
        /// </summary>
        private async Task<GraphQLResponse<T>> ExecuteGraphQLInternalAsync<T>(string query, object variables = null, WowGameVersion gameVersion = WowGameVersion.Retail, CancellationToken cancellationToken = default)
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var apiUrl = GetApiUrl(gameVersion);

            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var json = JsonConvert.SerializeObject(new GraphQLRequest { Query = query, Variables = variables });
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var client = _httpClientFactory.CreateClient();
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestBudget.CancelAfter(client.Timeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token);
            EnsureWclSuccess(response, "GraphQL");
            var content = await ReadResponseAsync(response, requestBudget.Token);
            var result = ParseResponse<GraphQLResponse<T>>(content);
            if (result.Errors?.Count > 0)
                _logger.LogWarning("[WCL v2] GraphQL response contained {ErrorCount} errors", result.Errors.Count);
            return result;
        }

        /// <summary>
        /// Executes a GraphQL query against the WarcraftLogs v2 API with rate limit monitoring
        /// </summary>
        private async Task<GraphQLResponse<T>> ExecuteGraphQLAsync<T>(string query, object variables = null, WowGameVersion gameVersion = WowGameVersion.Retail, CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureQuotaAsync(cancellationToken);
                var result = await ExecuteGraphQLInternalAsync<T>(query, variables, gameVersion, cancellationToken);
                await CheckRateLimitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("GraphQL execution failed ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        private void EnsureWclSuccess(HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode) return;
            // No error body or reason phrase is needed: both can contain private report/token data.
            var now = _clock.GetUtcNow();
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - now) ??
                (response.StatusCode == HttpStatusCode.TooManyRequests ? QuotaRetryDelay : TimeSpan.Zero);
            if (delay > TimeSpan.Zero)
            {
                var until = now.UtcTicks + Math.Min(delay.Ticks, DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks);
                long previous;
                do
                {
                    previous = Interlocked.Read(ref _retryAfterUtcTicks);
                    if (previous >= until) break;
                } while (Interlocked.CompareExchange(ref _retryAfterUtcTicks, until, previous) != previous);
            }
            _logger.LogError("[WCL v2] {Operation} request failed with HTTP {Status}", operation, (int)response.StatusCode);
            throw new HttpRequestException($"WCL {operation} request failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        private T ParseResponse<T>(string content) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(content) ?? throw new InvalidOperationException("WCL returned a null response.");
            }
            catch (JsonException ex)
            {
                _logger.LogError("[WCL v2] Response parsing failed ({ErrorType})", ex.GetType().Name);
                // JsonException messages/paths and inner exceptions may themselves contain payloads.
                throw new InvalidOperationException("Failed to parse WCL response.");
            }
        }

        private T ParseNestedResponse<T>(JToken token)
        {
            try
            {
                return token.ToObject<T>();
            }
            catch (JsonException ex)
            {
                _logger.LogError("[WCL v2] Nested response parsing failed ({ErrorType})", ex.GetType().Name);
                // Callers may log the escaping exception; discard provider values, paths and inner exceptions.
                throw new InvalidOperationException("Failed to parse WCL response.");
            }
        }

        private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using var raw = await response.Content.ReadAsStreamAsync(cancellationToken);
            Stream decoded = raw;
            var wrappers = new List<Stream>();
            try
            {
                // Handler automatic decompression removes its encoding header. Decode remaining
                // encodings before counting bytes, so compressed/chunked payloads cannot evade the cap.
                foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
                {
                    decoded = encoding.ToLowerInvariant() switch
                    {
                        "gzip" => new GZipStream(decoded, CompressionMode.Decompress, leaveOpen: true),
                        "deflate" => new ZLibStream(decoded, CompressionMode.Decompress, leaveOpen: true),
                        "br" => new BrotliStream(decoded, CompressionMode.Decompress, leaveOpen: true),
                        "identity" => decoded,
                        _ => throw new InvalidOperationException("Unsupported WCL response encoding.")
                    };
                    if (!encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) wrappers.Add(decoded);
                }
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var remaining = MaxResponseBytes + 1 - (int)body.Length;
                    var read = await decoded.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (read == 0) break;
                    if (body.Length + read > MaxResponseBytes)
                        throw new InvalidOperationException("WCL response exceeds the 4 MiB limit.");
                    body.Write(buffer, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            }
            finally
            {
                for (var i = wrappers.Count - 1; i >= 0; i--) wrappers[i].Dispose();
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
                                    _logger.LogDebug("[v2 Batch] Classified a non-existent guild from a GraphQL error");
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
                        var error = $"JSON parsing failed ({jsonEx.GetType().Name})";
                        parseErrors.Add(error);
                        uncategorizedGuilds.Add((guildKey, guildIdentifier, $"JSON exception: {jsonEx.GetType().Name}"));
                        wasCategorized = true;  // Categorized as exception
                        _logger.LogError($"[v2 Batch] {error}");
                    }
                    catch (Exception ex)
                    {
                        var error = ex.GetType().Name;
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
                _logger.LogError("[v2 Batch] HTTP request failed ({ErrorType})", httpEx.GetType().Name);
                throw;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError("[v2 Batch] Response parsing failed ({ErrorType})", jsonEx.GetType().Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2 Batch] Unexpected error ({ErrorType})", ex.GetType().Name);
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
                _logger.LogError("Failed to get guild reports ({ErrorType})", ex.GetType().Name);
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

        // ===== Encounter Rankings Methods (for /top10 command) =====

        /// <summary>
        /// Gets encounter rankings for a server (realm-wide top performers)
        /// </summary>
        /// <param name="encounterId">WCL encounter ID</param>
        /// <param name="serverSlug">Realm slug (e.g., "illidan")</param>
        /// <param name="serverRegion">Region (us, eu, etc.)</param>
        /// <param name="metric">Ranking metric: dps or hps</param>
        /// <param name="difficulty">Difficulty ID: 1=LFR, 2=Flex, 3=Normal, 4=Heroic, 5=Mythic</param>
        /// <param name="page">Page number (1-based)</param>
        /// <param name="gameVersion">Game version (Retail, Classic, etc.)</param>
        public async Task<WclV2CharacterRankingsPage> GetEncounterRankingsAsync(
            int encounterId,
            string serverSlug,
            string serverRegion,
            string metric = "dps",
            int difficulty = 4,
            int page = 1,
            WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var query = @"
                query($encounterId: Int!, $serverSlug: String!, $serverRegion: String!, $metric: CharacterRankingMetricType!, $difficulty: Int!, $page: Int!) {
                    worldData {
                        encounter(id: $encounterId) {
                            id
                            name
                            characterRankings(
                                serverSlug: $serverSlug
                                serverRegion: $serverRegion
                                metric: $metric
                                difficulty: $difficulty
                                page: $page
                            )
                        }
                    }
                }
            ";

            var variables = new
            {
                encounterId,
                serverSlug,
                serverRegion,
                metric,
                difficulty,
                page
            };

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2EncounterRankingsResponse>(query, variables, gameVersion);

                if (result.Data?.WorldData?.Encounter?.CharacterRankings != null)
                {
                    var rankings = result.Data.WorldData.Encounter.CharacterRankings;
                    _logger.LogInformation($"[v2] Retrieved {rankings.Rankings?.Count ?? 0} rankings for encounter {encounterId} on {serverSlug}-{serverRegion}");
                    return rankings;
                }

                _logger.LogWarning($"[v2] No rankings found for encounter {encounterId} on {serverSlug}-{serverRegion}");
                return new WclV2CharacterRankingsPage { Rankings = new List<WclV2CharacterRanking>() };
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get encounter rankings ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets encounter rankings filtered by guild (guild-specific top performers)
        /// </summary>
        public async Task<WclV2CharacterRankingsPage> GetEncounterRankingsForGuildAsync(
            int encounterId,
            string serverSlug,
            string serverRegion,
            string guildName,
            string metric = "dps",
            int difficulty = 4,
            int page = 1,
            WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            // Note: The v2 API doesn't have a direct guild filter for characterRankings
            // We fetch server-wide rankings and filter client-side by guild name
            var query = @"
                query($encounterId: Int!, $serverSlug: String!, $serverRegion: String!, $metric: CharacterRankingMetricType!, $difficulty: Int!, $page: Int!) {
                    worldData {
                        encounter(id: $encounterId) {
                            id
                            name
                            characterRankings(
                                serverSlug: $serverSlug
                                serverRegion: $serverRegion
                                metric: $metric
                                difficulty: $difficulty
                                page: $page
                            )
                        }
                    }
                }
            ";

            var variables = new
            {
                encounterId,
                serverSlug,
                serverRegion,
                metric,
                difficulty,
                page
            };

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2EncounterRankingsResponse>(query, variables, gameVersion);

                if (result.Data?.WorldData?.Encounter?.CharacterRankings != null)
                {
                    var rankings = result.Data.WorldData.Encounter.CharacterRankings;

                    // Filter to only include rankings from the specified guild
                    var filteredRankings = rankings.Rankings?
                        .Where(r => r.Guild?.Name != null &&
                                    r.Guild.Name.Equals(guildName, StringComparison.OrdinalIgnoreCase))
                        .ToList() ?? new List<WclV2CharacterRanking>();

                    _logger.LogInformation($"[v2] Retrieved {filteredRankings.Count} guild rankings for {guildName} on encounter {encounterId} (filtered from {rankings.Rankings?.Count ?? 0} total)");

                    return new WclV2CharacterRankingsPage
                    {
                        Rankings = filteredRankings,
                        Page = rankings.Page,
                        HasMorePages = rankings.HasMorePages,
                        Count = filteredRankings.Count
                    };
                }

                _logger.LogWarning($"[v2] No guild rankings found for {guildName} on encounter {encounterId}");
                return new WclV2CharacterRankingsPage { Rankings = new List<WclV2CharacterRanking>() };
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get guild encounter rankings ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets all rankings for a guild by fetching multiple pages and filtering
        /// </summary>
        public async Task<List<WclV2CharacterRanking>> GetAllGuildRankingsForEncounterAsync(
            int encounterId,
            string serverSlug,
            string serverRegion,
            string guildName,
            string metric = "dps",
            int difficulty = 4,
            int maxPages = 3,
            WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var allRankings = new List<WclV2CharacterRanking>();
            var page = 1;
            var hasMore = true;
            var consecutiveEmptyPages = 0;

            // Optimization thresholds
            const int earlyExitThreshold = 10;      // Stop once we have enough for top 10
            const int maxConsecutiveEmpty = 3;      // Stop after 3 pages with no guild members

            while (hasMore && page <= maxPages)
            {
                try
                {
                    var result = await GetEncounterRankingsForGuildAsync(
                        encounterId, serverSlug, serverRegion, guildName,
                        metric, difficulty, page, gameVersion);

                    if (result.Rankings != null && result.Rankings.Count > 0)
                    {
                        // Filter to just the specified guild
                        var guildRankings = result.Rankings
                            .Where(r => r.GuildName?.Equals(guildName, StringComparison.OrdinalIgnoreCase) == true)
                            .ToList();

                        if (guildRankings.Count > 0)
                        {
                            allRankings.AddRange(guildRankings);
                            consecutiveEmptyPages = 0; // Reset counter
                            _logger.LogDebug($"[v2] Page {page}: Added {guildRankings.Count} rankings for {guildName} (total: {allRankings.Count})");

                            // Early exit if we have enough rankings
                            if (allRankings.Count >= earlyExitThreshold)
                            {
                                _logger.LogInformation($"[v2] Early exit: Found {allRankings.Count} guild rankings after {page} pages");
                                break;
                            }
                        }
                        else
                        {
                            consecutiveEmptyPages++;
                            _logger.LogDebug($"[v2] Page {page}: No guild members found ({consecutiveEmptyPages} consecutive empty)");

                            // Stop if too many consecutive pages without guild members
                            if (consecutiveEmptyPages >= maxConsecutiveEmpty)
                            {
                                _logger.LogInformation($"[v2] Stopping: {maxConsecutiveEmpty} consecutive pages without guild members");
                                break;
                            }
                        }
                    }
                    else
                    {
                        consecutiveEmptyPages++;
                    }

                    hasMore = result.HasMorePages;
                    page++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[v2] Error fetching guild rankings page {Page} ({ErrorType})", page, ex.GetType().Name);
                    break;
                }
            }

            _logger.LogInformation($"[v2] Retrieved total {allRankings.Count} rankings for {guildName} across {page - 1} pages");
            return allRankings;
        }

        // ===== Zone/Encounter Static Data Methods =====

        /// <summary>
        /// Gets zones and encounters for an expansion
        /// </summary>
        /// <param name="expansionId">Expansion ID (e.g., 5 for The War Within)</param>
        /// <param name="gameVersion">Game version</param>
        public async Task<List<WclV2ZoneDetail>> GetZonesAsync(int expansionId, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var query = @"
                query($expansionId: Int!) {
                    worldData {
                        expansion(id: $expansionId) {
                            id
                            name
                            zones {
                                id
                                name
                                frozen
                                encounters {
                                    id
                                    name
                                }
                                brackets {
                                    min
                                    max
                                    bucket
                                    type
                                }
                                partitions {
                                    id
                                    name
                                    compactName
                                    default
                                }
                                difficulties {
                                    id
                                    name
                                }
                            }
                        }
                    }
                }
            ";

            var variables = new { expansionId };

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2ZonesResponse>(query, variables, gameVersion);

                if (result.Data?.WorldData?.Expansion?.Zones != null)
                {
                    var zones = result.Data.WorldData.Expansion.Zones;
                    _logger.LogInformation($"[v2] Retrieved {zones.Count} zones for expansion {expansionId}");
                    return zones;
                }

                _logger.LogWarning($"[v2] No zones found for expansion {expansionId}");
                return new List<WclV2ZoneDetail>();
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get zones ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets character classes from the game data
        /// </summary>
        public async Task<List<WclV2Class>> GetCharacterClassesAsync(WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var query = @"
                query {
                    gameData {
                        classes {
                            id
                            name
                            slug
                        }
                    }
                }
            ";

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2GameDataResponse>(query, null, gameVersion);

                if (result.Data?.GameData?.Classes != null)
                {
                    var classes = result.Data.GameData.Classes;
                    _logger.LogInformation($"[v2] Retrieved {classes.Count} character classes");
                    return classes;
                }

                _logger.LogWarning($"[v2] No character classes found");
                return new List<WclV2Class>();
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get character classes ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets encounter info by ID
        /// </summary>
        public async Task<WclV2Encounter> GetEncounterAsync(int encounterId, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var query = @"
                query($encounterId: Int!) {
                    worldData {
                        encounter(id: $encounterId) {
                            id
                            name
                        }
                    }
                }
            ";

            var variables = new { encounterId };

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2EncounterRankingsResponse>(query, variables, gameVersion);
                return result.Data?.WorldData?.Encounter;
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get encounter {EncounterId} ({ErrorType})", encounterId, ex.GetType().Name);
                throw;
            }
        }

        // ===== Current Raid Tier Detection =====

        /// <summary>
        /// Gets all available expansions from the WarcraftLogs API.
        /// Useful for discovering correct expansion IDs.
        /// </summary>
        public async Task<List<WclV2Expansion>> GetExpansionsAsync(WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            var query = @"
                query {
                    worldData {
                        expansions {
                            id
                            name
                        }
                    }
                }
            ";

            try
            {
                var result = await ExecuteGraphQLAsync<WclV2ExpansionsResponse>(query, null, gameVersion);

                if (result.Data?.WorldData?.Expansions != null)
                {
                    var expansions = result.Data.WorldData.Expansions;
                    _logger.LogInformation("[v2] Retrieved {Count} expansions", expansions.Count);
                    foreach (var exp in expansions.OrderByDescending(e => e.Id))
                    {
                        _logger.LogInformation("[v2] Expansion: {Name} (ID: {Id})", exp.Name, exp.Id);
                    }
                    return expansions;
                }

                _logger.LogWarning("[v2] No expansions found");
                return new List<WclV2Expansion>();
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get expansions ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets the current raid tier by finding the latest non-frozen raid zone.
        /// Frozen zones are old content that WCL no longer considers "current".
        /// </summary>
        /// <param name="expansionId">Expansion ID. Use 0 to auto-detect by querying all expansions.</param>
        /// <param name="gameVersion">Game version</param>
        public async Task<WclV2ZoneDetail> GetCurrentRaidTierAsync(int expansionId = 0, WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            // Check cache first (only for auto-detect, not specific expansion)
            if (expansionId == 0 && _raidTierCache.TryGetValue(gameVersion, out var cached))
            {
                if (DateTime.UtcNow - cached.CachedAt < RaidTierCacheTtl)
                {
                    _logger.LogDebug("[v2] Using cached raid tier: {ZoneName} (cached {MinutesAgo:F0}m ago)",
                        cached.Zone.Name, (DateTime.UtcNow - cached.CachedAt).TotalMinutes);
                    return cached.Zone;
                }
            }

            try
            {
                List<int> expansionIds;

                if (expansionId > 0)
                {
                    // Use specific expansion
                    expansionIds = new List<int> { expansionId };
                }
                else
                {
                    // Query all expansions and check from newest to oldest
                    var allExpansions = await GetExpansionsAsync(gameVersion);
                    if (allExpansions == null || allExpansions.Count == 0)
                    {
                        _logger.LogWarning("[v2] No expansions found from API");
                        return null;
                    }

                    // Sort by ID descending (newest first)
                    expansionIds = allExpansions.OrderByDescending(e => e.Id).Select(e => e.Id).ToList();
                    _logger.LogInformation("[v2] Auto-detecting current tier from {Count} expansions: {ExpIds}",
                        expansionIds.Count, string.Join(", ", expansionIds.Take(5)));
                }

                foreach (var expId in expansionIds)
                {
                    _logger.LogInformation("[v2] Checking expansion {ExpansionId} for current raid tier...", expId);

                    var zones = await GetZonesAsync(expId, gameVersion);

                    if (zones == null || zones.Count == 0)
                    {
                        _logger.LogDebug("[v2] No zones found for expansion {ExpansionId}", expId);
                        continue;
                    }

                    // Log all zones for debugging
                    _logger.LogInformation("[v2] Found {Count} zones for expansion {ExpansionId}", zones.Count, expId);
                    foreach (var z in zones)
                    {
                        var difficultyNames = z.Difficulties != null
                            ? string.Join(",", z.Difficulties.Select(d => $"{d.Name}({d.Id})"))
                            : "none";
                        _logger.LogInformation("[v2] Zone: {Name} (ID: {Id}, Frozen: {Frozen}, Encounters: {EncCount}, Difficulties: {Diffs})",
                            z.Name, z.Id, z.Frozen, z.Encounters?.Count ?? 0, difficultyNames);
                    }

                    // Filter to actual raid zones only:
                    // 1. Must have Normal/Heroic/Mythic difficulties (IDs 3, 4, 5)
                    // 2. Must NOT be "Complete Raids" aggregate zones (these have only 1 encounter)
                    // 3. Should have multiple encounters (real raids have 8+ bosses)
                    var raidZones = zones
                        .Where(z => z.Difficulties != null &&
                                    z.Difficulties.Any(d => d.Id == 3 || d.Id == 4 || d.Id == 5) &&
                                    !z.Name.StartsWith("Complete Raids") &&
                                    z.Encounters != null && z.Encounters.Count > 1)
                        .ToList();

                    _logger.LogInformation("[v2] Filtered to {Count} raid zones (excluding Complete Raids aggregates)", raidZones.Count);

                    // Find the latest non-frozen raid zone (has encounters)
                    // Higher zone ID = newer raid tier
                    // Note: Frozen=null or false means current content, Frozen=true means old content
                    var currentTier = raidZones
                        .Where(z => z.Frozen != true &&
                                    z.Encounters != null && z.Encounters.Any())
                        .OrderByDescending(z => z.Id)
                        .FirstOrDefault();

                    if (currentTier != null)
                    {
                        _logger.LogInformation("[v2] Detected current raid tier: {ZoneName} (ID: {ZoneId}, Expansion: {ExpId})",
                            currentTier.Name, currentTier.Id, expId);

                        // Cache the result for future calls
                        _raidTierCache[gameVersion] = (currentTier, DateTime.UtcNow);
                        return currentTier;
                    }

                    // Fallback: just get the latest raid zone with encounters
                    currentTier = raidZones
                        .Where(z => z.Encounters != null && z.Encounters.Any())
                        .OrderByDescending(z => z.Id)
                        .FirstOrDefault();

                    if (currentTier != null)
                    {
                        _logger.LogInformation("[v2] Using latest zone from expansion {ExpId}: {ZoneName} (ID: {ZoneId})",
                            expId, currentTier.Name, currentTier.Id);

                        // Cache the result for future calls
                        _raidTierCache[gameVersion] = (currentTier, DateTime.UtcNow);
                        return currentTier;
                    }
                }

                _logger.LogWarning("[v2] No raid zones found in any checked expansion");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get current raid tier ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        // ===== Character Zone Rankings (for /char logs view) =====

        /// <summary>
        /// Gets character zone rankings for a specific zone (current raid tier).
        /// Returns per-boss ranking data including best percentile, kills, and all-stars.
        /// </summary>
        /// <param name="name">Character name</param>
        /// <param name="serverSlug">Realm slug (e.g., "illidan")</param>
        /// <param name="serverRegion">Region (us, eu, etc.)</param>
        /// <param name="zoneId">WCL zone ID for the raid tier</param>
        /// <param name="difficulty">Optional difficulty filter: 3=Normal, 4=Heroic, 5=Mythic, null=All</param>
        /// <param name="partition">Optional partition filter: 1=All, or specific partition ID. null=current partition</param>
        /// <param name="gameVersion">Game version (Retail, Classic, etc.)</param>
        public async Task<WclV2ZoneRankingsData> GetCharacterZoneRankingsAsync(
            string name,
            string serverSlug,
            string serverRegion,
            int zoneId,
            int? difficulty = null,
            int? partition = null,
            WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            // Build the zoneRankings field with parameters
            // Note: difficulty of 0 means "All" in WCL API, null also works
            // Partition 1 = "All" (combines all partitions), null = current/default partition
            var difficultyParam = difficulty.HasValue ? $", difficulty: {difficulty.Value}" : "";
            var partitionParam = partition.HasValue ? $", partition: {partition.Value}" : "";

            var query = $@"
                query($name: String!, $serverSlug: String!, $serverRegion: String!) {{
                    characterData {{
                        character(name: $name, serverSlug: $serverSlug, serverRegion: $serverRegion) {{
                            id
                            name
                            classID
                            zoneRankings(zoneID: {zoneId}{difficultyParam}{partitionParam})
                        }}
                    }}
                }}
            ";

            var variables = new
            {
                name,
                serverSlug,
                serverRegion
            };

            try
            {
                // First get raw response to debug
                var rawResult = await ExecuteGraphQLAsync<Newtonsoft.Json.Linq.JObject>(query, variables, gameVersion);

                _logger.LogDebug("[v2] Zone rankings response received");

                if (rawResult.Data?["characterData"]?["character"] != null)
                {
                    var characterJson = rawResult.Data["characterData"]["character"];
                    var charName = characterJson["name"]?.ToString();
                    var zoneRankingsJson = characterJson["zoneRankings"];

                    _logger.LogInformation("[v2] Retrieved zone rankings for {Name}-{Server} ({Region}), Zone: {ZoneId}, Difficulty: {Difficulty}",
                        name, serverSlug, serverRegion, zoneId, difficulty?.ToString() ?? "null");

                    if (zoneRankingsJson == null || zoneRankingsJson.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                    {
                        _logger.LogWarning("[v2] zoneRankings is null for {Name} - character may have no logs for zone {ZoneId}", name, zoneId);
                        return null;
                    }

                    _logger.LogDebug("[v2] Zone rankings payload type: {PayloadType}", zoneRankingsJson.Type);

                    // Parse the zoneRankings JSON
                    var zoneRankings = ParseNestedResponse<WclV2ZoneRankingsData>(zoneRankingsJson);

                    if (zoneRankings != null)
                    {
                        _logger.LogInformation("[v2] Parsed zone rankings: BestAvg={BestAvg}, Rankings={RankCount}",
                            zoneRankings.BestPerformanceAverage, zoneRankings.Rankings?.Count ?? 0);
                    }

                    return zoneRankings;
                }

                _logger.LogWarning("[v2] No character found for {Name}-{Server} ({Region})", name, serverSlug, serverRegion);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get character zone rankings ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets character encounter rankings (individual parses) for a specific boss.
        /// Returns detailed per-kill data including fight links, percentiles, and DPS/HPS.
        /// </summary>
        /// <param name="name">Character name</param>
        /// <param name="serverSlug">Realm slug (e.g., "illidan")</param>
        /// <param name="serverRegion">Region (us, eu, etc.)</param>
        /// <param name="encounterId">WCL encounter ID for the boss</param>
        /// <param name="difficulty">Optional difficulty filter: 3=Normal, 4=Heroic, 5=Mythic, null=All</param>
        /// <param name="partition">Optional partition filter: 1=All, or specific partition ID. null=current partition</param>
        /// <param name="gameVersion">Game version (Retail, Classic, etc.)</param>
        public async Task<WclV2EncounterRankingsData> GetCharacterEncounterRankingsAsync(
            string name,
            string serverSlug,
            string serverRegion,
            int encounterId,
            int? difficulty = null,
            int? partition = null,
            WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            // Build the encounterRankings field with parameters
            var difficultyParam = difficulty.HasValue ? $", difficulty: {difficulty.Value}" : "";
            var partitionParam = partition.HasValue ? $", partition: {partition.Value}" : "";

            var query = $@"
                query($name: String!, $serverSlug: String!, $serverRegion: String!) {{
                    characterData {{
                        character(name: $name, serverSlug: $serverSlug, serverRegion: $serverRegion) {{
                            id
                            name
                            classID
                            encounterRankings(encounterID: {encounterId}{difficultyParam}{partitionParam})
                        }}
                    }}
                }}
            ";

            var variables = new
            {
                name,
                serverSlug,
                serverRegion
            };

            try
            {
                // First get raw response to debug
                var rawResult = await ExecuteGraphQLAsync<Newtonsoft.Json.Linq.JObject>(query, variables, gameVersion);

                _logger.LogDebug("[v2] Encounter rankings response received");

                if (rawResult.Data?["characterData"]?["character"] != null)
                {
                    var characterJson = rawResult.Data["characterData"]["character"];
                    var charName = characterJson["name"]?.ToString();
                    var encounterRankingsJson = characterJson["encounterRankings"];

                    _logger.LogInformation("[v2] Retrieved encounter rankings for {Name}-{Server} ({Region}), Encounter: {EncounterId}, Difficulty: {Difficulty}",
                        name, serverSlug, serverRegion, encounterId, difficulty?.ToString() ?? "null");

                    if (encounterRankingsJson == null || encounterRankingsJson.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                    {
                        _logger.LogWarning("[v2] encounterRankings is null for {Name} - character may have no logs for encounter {EncounterId}", name, encounterId);
                        return null;
                    }

                    _logger.LogDebug("[v2] Encounter rankings payload type: {PayloadType}", encounterRankingsJson.Type);

                    // Parse the encounterRankings JSON
                    var encounterRankings = ParseNestedResponse<WclV2EncounterRankingsData>(encounterRankingsJson);

                    if (encounterRankings != null)
                    {
                        _logger.LogInformation("[v2] Parsed encounter rankings: TotalKills={TotalKills}, Ranks={RankCount}",
                            encounterRankings.TotalKills, encounterRankings.Ranks?.Count ?? 0);
                    }

                    return encounterRankings;
                }

                _logger.LogWarning("[v2] No character found for {Name}-{Server} ({Region})", name, serverSlug, serverRegion);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to get character encounter rankings ({ErrorType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Gets character encounter rankings for multiple bosses in a single query.
        /// More efficient than calling GetCharacterEncounterRankingsAsync multiple times.
        /// </summary>
        /// <param name="name">Character name</param>
        /// <param name="serverSlug">Realm slug</param>
        /// <param name="serverRegion">Region (us, eu, etc.)</param>
        /// <param name="encounterIds">List of encounter IDs to fetch</param>
        /// <param name="difficulty">Optional difficulty filter</param>
        /// <param name="partition">Optional partition filter</param>
        /// <param name="gameVersion">Game version</param>
        /// <returns>Dictionary mapping encounter ID to its rankings data</returns>
        public async Task<Dictionary<int, WclV2EncounterRankingsData>> GetCharacterEncounterRankingsBatchAsync(
            string name,
            string serverSlug,
            string serverRegion,
            List<int> encounterIds,
            int? difficulty = null,
            int? partition = null,
            WowGameVersion gameVersion = WowGameVersion.Retail)
        {
            if (encounterIds == null || encounterIds.Count == 0)
                return new Dictionary<int, WclV2EncounterRankingsData>();

            var difficultyParam = difficulty.HasValue ? $", difficulty: {difficulty.Value}" : "";
            var partitionParam = partition.HasValue ? $", partition: {partition.Value}" : "";

            // Build aliased query for all encounters
            var encounterFields = new System.Text.StringBuilder();
            for (int i = 0; i < encounterIds.Count; i++)
            {
                encounterFields.AppendLine($"                            enc{i}: encounterRankings(encounterID: {encounterIds[i]}{difficultyParam}{partitionParam})");
            }

            var query = $@"
                query($name: String!, $serverSlug: String!, $serverRegion: String!) {{
                    characterData {{
                        character(name: $name, serverSlug: $serverSlug, serverRegion: $serverRegion) {{
                            id
                            name
{encounterFields}
                        }}
                    }}
                }}
            ";

            var variables = new { name, serverSlug, serverRegion };
            var result = new Dictionary<int, WclV2EncounterRankingsData>();

            try
            {
                var rawResult = await ExecuteGraphQLAsync<Newtonsoft.Json.Linq.JObject>(query, variables, gameVersion);

                if (rawResult.Data?["characterData"]?["character"] != null)
                {
                    var characterJson = rawResult.Data["characterData"]["character"];

                    for (int i = 0; i < encounterIds.Count; i++)
                    {
                        var encJson = characterJson[$"enc{i}"];
                        if (encJson != null && encJson.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            var encData = encJson.ToObject<WclV2EncounterRankingsData>();
                            if (encData != null)
                            {
                                result[encounterIds[i]] = encData;
                            }
                        }
                    }

                    _logger.LogInformation("[v2] Batch fetched encounter rankings for {Name}: {Count}/{Total} encounters returned data",
                        name, result.Count, encounterIds.Count);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("[v2] Failed to batch fetch encounter rankings ({ErrorType})", ex.GetType().Name);
                return result;
            }
        }
    }
}
