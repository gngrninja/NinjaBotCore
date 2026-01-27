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
    /// Generic method to fetch static data from Blizzard API
    /// </summary>
    public async Task<string?> GetStaticDataAsync(
        string endpoint,
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
        var url = $"https://{regionLower}.api.blizzard.com{endpoint}";

        // Add namespace if not present
        if (!url.Contains("namespace="))
        {
            url += url.Contains("?") ? "&" : "?";
            url += $"namespace=static-{regionLower}&locale=en_US";
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async ct => await _httpClient.SendAsync(request, ct),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Blizzard API returned {StatusCode} for {Endpoint}",
                    response.StatusCode, endpoint);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching static data from {Endpoint}", endpoint);
            return null;
        }
    }

    /// <summary>
    /// Get achievement index (list of all achievements)
    /// </summary>
    public async Task<AchievementIndexResponse?> GetAchievementIndexAsync(
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync("/data/wow/achievement/index", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<AchievementIndexResponse>(json);
    }

    /// <summary>
    /// Get a single achievement by ID
    /// </summary>
    public async Task<AchievementResponse?> GetAchievementAsync(
        long achievementId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync($"/data/wow/achievement/{achievementId}", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<AchievementResponse>(json);
    }

    /// <summary>
    /// Get achievement media
    /// </summary>
    public async Task<MediaResponse?> GetAchievementMediaAsync(
        long achievementId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync($"/data/wow/media/achievement/{achievementId}", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<MediaResponse>(json);
    }

    /// <summary>
    /// Get mount index (list of all mounts)
    /// </summary>
    public async Task<MountIndexResponse?> GetMountIndexAsync(
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync("/data/wow/mount/index", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<MountIndexResponse>(json);
    }

    /// <summary>
    /// Get a single mount by ID
    /// </summary>
    public async Task<MountResponse?> GetMountAsync(
        long mountId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync($"/data/wow/mount/{mountId}", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<MountResponse>(json);
    }

    /// <summary>
    /// Get creature display media (for mount icons)
    /// </summary>
    public async Task<MediaResponse?> GetCreatureDisplayMediaAsync(
        long creatureDisplayId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync($"/data/wow/media/creature-display/{creatureDisplayId}", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<MediaResponse>(json);
    }

    /// <summary>
    /// Get pet index (list of all pets)
    /// </summary>
    public async Task<PetIndexResponse?> GetPetIndexAsync(
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync("/data/wow/pet/index", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<PetIndexResponse>(json);
    }

    /// <summary>
    /// Get a single pet by ID
    /// </summary>
    public async Task<PetResponse?> GetPetAsync(
        long petId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync($"/data/wow/pet/{petId}", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<PetResponse>(json);
    }

    /// <summary>
    /// Get pet media
    /// </summary>
    public async Task<MediaResponse?> GetPetMediaAsync(
        long petId,
        string region = "us",
        CancellationToken cancellationToken = default)
    {
        var json = await GetStaticDataAsync($"/data/wow/media/pet/{petId}", region, cancellationToken);
        if (json == null) return null;
        return JsonConvert.DeserializeObject<MediaResponse>(json);
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

#region Static Data API Response Models

/// <summary>
/// Generic key reference in Blizzard API responses
/// </summary>
public class KeyRef
{
    [JsonProperty("key")]
    public HrefLink? Key { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("id")]
    public long Id { get; set; }
}

public class HrefLink
{
    [JsonProperty("href")]
    public string? Href { get; set; }
}

/// <summary>
/// Media asset from Blizzard API
/// </summary>
public class MediaAsset
{
    [JsonProperty("key")]
    public string? Key { get; set; }

    [JsonProperty("value")]
    public string? Value { get; set; }

    [JsonProperty("file_data_id")]
    public long? FileDataId { get; set; }
}

/// <summary>
/// Generic media response
/// </summary>
public class MediaResponse
{
    [JsonProperty("assets")]
    public List<MediaAsset>? Assets { get; set; }

    public string? GetIconUrl()
    {
        return Assets?.FirstOrDefault(a => a.Key == "icon")?.Value;
    }
}

/// <summary>
/// Achievement index response
/// </summary>
public class AchievementIndexResponse
{
    [JsonProperty("achievements")]
    public List<KeyRef>? Achievements { get; set; }
}

/// <summary>
/// Single achievement response
/// </summary>
public class AchievementResponse
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("points")]
    public int Points { get; set; }

    [JsonProperty("is_account_wide")]
    public bool IsAccountWide { get; set; }

    [JsonProperty("category")]
    public KeyRef? Category { get; set; }

    [JsonProperty("display_order")]
    public int DisplayOrder { get; set; }

    [JsonProperty("reward_description")]
    public string? RewardDescription { get; set; }

    [JsonProperty("criteria")]
    public AchievementCriteriaContainer? Criteria { get; set; }

    [JsonProperty("media")]
    public KeyRef? Media { get; set; }
}

public class AchievementCriteriaContainer
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("amount")]
    public int Amount { get; set; }

    [JsonProperty("child_criteria")]
    public List<AchievementCriteriaChild>? ChildCriteria { get; set; }
}

public class AchievementCriteriaChild
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("amount")]
    public int Amount { get; set; }

    [JsonProperty("order_index")]
    public int OrderIndex { get; set; }
}

/// <summary>
/// Mount index response
/// </summary>
public class MountIndexResponse
{
    [JsonProperty("mounts")]
    public List<KeyRef>? Mounts { get; set; }
}

/// <summary>
/// Single mount response
/// </summary>
public class MountResponse
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("source")]
    public LocalizedString? Source { get; set; }

    [JsonProperty("faction")]
    public TypeName? Faction { get; set; }

    [JsonProperty("creature_displays")]
    public List<CreatureDisplay>? CreatureDisplays { get; set; }

    [JsonProperty("should_exclude_if_uncollected")]
    public bool ShouldExcludeIfUncollected { get; set; }
}

public class LocalizedString
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }
}

public class TypeName
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }
}

public class CreatureDisplay
{
    [JsonProperty("key")]
    public HrefLink? Key { get; set; }

    [JsonProperty("id")]
    public long Id { get; set; }
}

/// <summary>
/// Pet index response
/// </summary>
public class PetIndexResponse
{
    [JsonProperty("pets")]
    public List<KeyRef>? Pets { get; set; }
}

/// <summary>
/// Single pet response
/// </summary>
public class PetResponse
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("battle_pet_type")]
    public TypeName? BattlePetType { get; set; }

    [JsonProperty("source")]
    public LocalizedString? Source { get; set; }

    [JsonProperty("is_capturable")]
    public bool IsCapturable { get; set; }

    [JsonProperty("is_tradable")]
    public bool IsTradable { get; set; }

    [JsonProperty("is_battlepet")]
    public bool IsBattlepet { get; set; }

    [JsonProperty("creature")]
    public KeyRef? Creature { get; set; }

    [JsonProperty("media")]
    public KeyRef? Media { get; set; }
}

#endregion
