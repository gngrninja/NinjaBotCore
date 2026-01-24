using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotHelpers.Configuration;
using Polly;
using Polly.Retry;

namespace NinjaBotHelpers.Blizzard;

/// <summary>
/// Client for Blizzard WoW API - specifically for realm status
/// </summary>
public class BlizzardApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BlizzardApiClient> _logger;
    private readonly HelpersConfiguration _config;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public BlizzardApiClient(
        HttpClient httpClient,
        ILogger<BlizzardApiClient> logger,
        HelpersConfiguration config)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;

        // Configure resilience pipeline for API calls
        _resiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(response =>
                        (int)response.StatusCode == 429 || // Rate limit
                        (int)response.StatusCode >= 500),  // Server errors
                OnRetry = args =>
                {
                    var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                    _logger.LogWarning(
                        "Retry attempt {AttemptNumber} for Blizzard API. Status: {StatusCode}",
                        args.AttemptNumber, statusCode);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Get the connected realm status from Blizzard API
    /// </summary>
    public async Task<ConnectedRealmStatus?> GetConnectedRealmStatusAsync(
        long connectedRealmId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token == null)
        {
            _logger.LogError("Failed to get Blizzard access token");
            return null;
        }

        var regionLower = region.ToLowerInvariant();
        var url = $"https://{regionLower}.api.blizzard.com/data/wow/connected-realm/{connectedRealmId}?namespace=dynamic-{regionLower}&locale=en_US";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async ct => await _httpClient.SendAsync(request, ct),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Blizzard API returned {StatusCode} for connected realm {RealmId}",
                    response.StatusCode, connectedRealmId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonConvert.DeserializeObject<ConnectedRealmStatus>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connected realm status for {RealmId}", connectedRealmId);
            return null;
        }
    }

    /// <summary>
    /// Get or refresh the Blizzard API access token
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Check if current token is still valid
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return _accessToken;
            }

            _logger.LogInformation("Refreshing Blizzard API access token");

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_config.BlizzardClientId}:{_config.BlizzardClientSecret}"));

            var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get Blizzard access token: {StatusCode} - {Error}",
                    response.StatusCode, error);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(json);

            if (tokenResponse == null)
            {
                _logger.LogError("Failed to parse Blizzard token response");
                return null;
            }

            _accessToken = tokenResponse.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            _logger.LogInformation("Blizzard access token refreshed, expires in {ExpiresIn} seconds",
                tokenResponse.ExpiresIn);

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private class TokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }
    }
}

/// <summary>
/// Connected realm status from Blizzard API
/// </summary>
public class ConnectedRealmStatus
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("has_queue")]
    public bool HasQueue { get; set; }

    [JsonProperty("status")]
    public RealmStatusType? Status { get; set; }

    [JsonProperty("population")]
    public PopulationType? Population { get; set; }
}

public class RealmStatusType
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public class PopulationType
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}
