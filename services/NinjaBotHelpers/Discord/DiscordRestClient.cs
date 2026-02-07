using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;

namespace NinjaBotHelpers.Discord;

/// <summary>
/// Lightweight Discord REST API client that doesn't require Gateway connection
/// Supports sending messages to channels and DMs
/// </summary>
public class DiscordRestClient
{
    private const string DiscordApiBase = "https://discord.com/api/v10";
    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscordRestClient> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;

    public DiscordRestClient(HttpClient httpClient, ILogger<DiscordRestClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Configure resilience pipeline for Discord REST API calls
        _resiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(response =>
                        (int)response.StatusCode == 429 ||
                        (int)response.StatusCode >= 500),
                DelayGenerator = args =>
                {
                    // Respect Discord's Retry-After header if present
                    if (args.Outcome.Result?.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                    {
                        return ValueTask.FromResult<TimeSpan?>(retryAfter);
                    }
                    return ValueTask.FromResult<TimeSpan?>(null); // Use default delay
                },
                OnRetry = args =>
                {
                    var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                    _logger.LogWarning(
                        "[Discord] Retry attempt {AttemptNumber}. Status: {StatusCode}",
                        args.AttemptNumber, statusCode);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Send a message to a Discord channel
    /// </summary>
    public async Task<bool> SendChannelMessageAsync(ulong channelId, DiscordEmbed embed, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                embeds = new[] { embed }
            };

            var json = JsonConvert.SerializeObject(payload);

            // Use Polly resilience pipeline - recreate content for each retry (POST)
            using var response = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                return await _httpClient.PostAsync(
                    $"{DiscordApiBase}/channels/{channelId}/messages",
                    content,
                    ct);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to send channel message to {ChannelId}: {StatusCode} - {Error}",
                    channelId, response.StatusCode, error);
                return false;
            }

            _logger.LogDebug("Sent message to channel {ChannelId}", channelId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to channel {ChannelId}", channelId);
            return false;
        }
    }

    /// <summary>
    /// Send a DM to a user
    /// </summary>
    public async Task<bool> SendDMAsync(ulong userId, DiscordEmbed embed, CancellationToken cancellationToken = default)
    {
        try
        {
            // First, create or get the DM channel
            var dmChannelId = await CreateDMChannelAsync(userId, cancellationToken);
            if (dmChannelId == null)
            {
                _logger.LogWarning("Failed to create DM channel for user {UserId}", userId);
                return false;
            }

            // Send the message
            return await SendChannelMessageAsync(dmChannelId.Value, embed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending DM to user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Create a DM channel with a user (or get existing one)
    /// </summary>
    private async Task<ulong?> CreateDMChannelAsync(ulong userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { recipient_id = userId.ToString() };
            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{DiscordApiBase}/users/@me/channels",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to create DM channel for user {UserId}: {StatusCode} - {Error}",
                    userId, response.StatusCode, error);
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var dmChannel = JsonConvert.DeserializeObject<DmChannelResponse>(responseBody);
            return dmChannel?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DM channel for user {UserId}", userId);
            return null;
        }
    }

    private class DmChannelResponse
    {
        [JsonProperty("id")]
        public ulong Id { get; set; }
    }
}

/// <summary>
/// Discord embed structure for REST API
/// </summary>
public class DiscordEmbed
{
    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("color")]
    public int? Color { get; set; }

    [JsonProperty("fields")]
    public List<DiscordEmbedField>? Fields { get; set; }

    [JsonProperty("footer")]
    public DiscordEmbedFooter? Footer { get; set; }

    [JsonProperty("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Create a simple embed with title and color
    /// </summary>
    public static DiscordEmbed Create(string title, int color)
    {
        return new DiscordEmbed
        {
            Title = title,
            Color = color,
            Timestamp = DateTime.UtcNow.ToString("o")
        };
    }

    public DiscordEmbed WithDescription(string description)
    {
        Description = description;
        return this;
    }

    public DiscordEmbed WithField(string name, string value, bool inline = false)
    {
        Fields ??= new List<DiscordEmbedField>();
        Fields.Add(new DiscordEmbedField { Name = name, Value = value, Inline = inline });
        return this;
    }

    public DiscordEmbed WithFooter(string text)
    {
        Footer = new DiscordEmbedFooter { Text = text };
        return this;
    }
}

public class DiscordEmbedField
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("value")]
    public string Value { get; set; } = string.Empty;

    [JsonProperty("inline")]
    public bool Inline { get; set; }
}

public class DiscordEmbedFooter
{
    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;
}
