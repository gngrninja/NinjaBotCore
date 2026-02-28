using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;
using Polly;
using Polly.Retry;

namespace NinjaBotCore.Modules.Wow
{
    public class ClassicRaiderIOApi : IClassicRaiderIOApi
    {
        private const string BaseUrl = "https://classic.raider.io/api/v1";
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResiliencePipeline _resiliencePipeline;

        public ClassicRaiderIOApi(IServiceProvider services)
        {
            var innerLogger = services.GetRequiredService<ILogger<ClassicRaiderIOApi>>();
            _logger = new SanitizingLogger<ClassicRaiderIOApi>(innerLogger);
            _httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

            _resiliencePipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<HttpRequestException>()
                        .Handle<TaskCanceledException>(),
                    OnRetry = args =>
                    {
                        _logger.LogWarning(
                            "Retry attempt {AttemptNumber} for Classic RaiderIO API request. Delay: {Delay}",
                            args.AttemptNumber,
                            args.RetryDelay);
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        private async Task<string> GetApiRequestAsync(string url, CancellationToken cancellationToken = default)
        {
            // Classic RIO does NOT use access_key — it rejects requests that include one
            string fullUrl = $"{BaseUrl}{url}";

            _logger.LogInformation("Classic RaiderIO API request to {Url}", fullUrl);

            try
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async ct =>
                    {
                        using var client = _httpClientFactory.CreateClient();
                        client.DefaultRequestHeaders.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("application/json"));
                        var httpResponse = await client.GetAsync(fullUrl, ct);

                        // Don't retry 4xx client errors
                        if (httpResponse.StatusCode >= HttpStatusCode.BadRequest &&
                            httpResponse.StatusCode < HttpStatusCode.InternalServerError)
                        {
                            var errorContent = await httpResponse.Content.ReadAsStringAsync(ct);
                            _logger.LogWarning(
                                "Classic RaiderIO API returned client error {StatusCode} for {Url}: {Content}",
                                (int)httpResponse.StatusCode,
                                fullUrl,
                                errorContent);

                            httpResponse.EnsureSuccessStatusCode();
                        }

                        httpResponse.EnsureSuccessStatusCode();

                        return await httpResponse.Content.ReadAsStringAsync(ct);
                    },
                    cancellationToken);
                return response;
            }
            catch (HttpRequestException ex) when (ex.StatusCode >= HttpStatusCode.BadRequest &&
                                                    ex.StatusCode < HttpStatusCode.InternalServerError)
            {
                _logger.LogError(ex, "Classic RaiderIO API client error {StatusCode} for {Url}. Character may not exist or realm name may be incorrect.",
                    (int?)ex.StatusCode, fullUrl);
                throw new InvalidOperationException(
                    $"Character not found or invalid request. Please check the character name and realm. (Error: {ex.StatusCode})",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error making Classic RaiderIO API request to {Url} after retries", fullUrl);
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Classic RaiderIO API request timed out for {Url}", fullUrl);
                throw;
            }
        }

        public async Task<ClassicRaiderIOModels.ClassicCharProfile> GetCharacterProfileAsync(
            string charName, string realmName, string region = "us", CancellationToken cancellationToken = default)
        {
            charName = charName.Replace(" ", "%20");
            realmName = realmName.Replace(" ", "%20");
            string url = $"/characters/profile?region={region}&realm={realmName}&name={charName}" +
                $"&fields=gear%2Ctalents%2Cguild%2Craid_progression";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<ClassicRaiderIOModels.ClassicCharProfile>(response);
        }

        public async Task<ClassicRaiderIOModels.ClassicGuildProfile> GetGuildProfileAsync(
            string guildName, string realmName, string region = "us", CancellationToken cancellationToken = default)
        {
            guildName = guildName.Replace(" ", "%20");
            realmName = realmName.Replace(" ", "%20");
            string url = $"/guilds/profile?region={region}&realm={realmName}&name={guildName}" +
                $"&fields=raid_progression";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<ClassicRaiderIOModels.ClassicGuildProfile>(response);
        }
    }
}
