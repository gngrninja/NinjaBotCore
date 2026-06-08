using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NinjaBotCore.Models.Wow;
using System.IO;
using NinjaBotCore.Database;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using Polly;
using Polly.Retry;
using NinjaBotCore.Common;

namespace NinjaBotCore.Modules.Wow
{
    public class RaiderIOApi : IRaiderIOApi
    {
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResiliencePipeline _resiliencePipeline;

        public RaiderIOApi(IServiceProvider services)
        {
            try
            {
                var innerLogger = services.GetRequiredService<ILogger<RaiderIOApi>>();
                _logger = new SanitizingLogger<RaiderIOApi>(innerLogger);
                _config = services.GetRequiredService<IConfigurationRoot>();
                _httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

                // Configure resilience pipeline for API calls
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
                                "Retry attempt {AttemptNumber} for RaiderIO API request. Delay: {Delay}",
                                args.AttemptNumber,
                                args.RetryDelay);
                            return ValueTask.CompletedTask;
                        }
                    })
                    .Build();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating RaiderIO class");
            }
        }

        private async Task<string> GetApiRequestAsync(string url, CancellationToken cancellationToken = default)
        {
            const string prefix = "https://raider.io/api/v1";
            string separator = url.Contains("?") ? "&" : "?";
            string fullUrl = $"{prefix}{url}{separator}access_key={_config["RioApi"]}";

            _logger.LogInformation("RaiderIO API request to {Url}", fullUrl);

            try
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async ct =>
                    {
                        using var client = _httpClientFactory.CreateClient();
                        client.DefaultRequestHeaders.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("application/json"));
                        var httpResponse = await client.GetAsync(fullUrl, ct);

                        // Don't retry 4xx client errors (bad request, not found, etc.)
                        // Only retry transient errors (5xx, network issues)
                        if (httpResponse.StatusCode >= HttpStatusCode.BadRequest &&
                            httpResponse.StatusCode < HttpStatusCode.InternalServerError)
                        {
                            var errorContent = await httpResponse.Content.ReadAsStringAsync(ct);
                            _logger.LogWarning(
                                "RaiderIO API returned client error {StatusCode} for {Url}: {Content}",
                                (int)httpResponse.StatusCode,
                                fullUrl,
                                errorContent);

                            // Throw without retrying
                            httpResponse.EnsureSuccessStatusCode();
                        }

                        // For 5xx errors, ensure throws so Polly can retry
                        httpResponse.EnsureSuccessStatusCode();

                        return await httpResponse.Content.ReadAsStringAsync(ct);
                    },
                    cancellationToken);
                return response;
            }
            catch (HttpRequestException ex) when (ex.StatusCode >= HttpStatusCode.BadRequest &&
                                                    ex.StatusCode < HttpStatusCode.InternalServerError)
            {
                // Client errors - provide helpful message
                _logger.LogError(ex, "RaiderIO API client error {StatusCode} for {Url}. Character may not exist or realm name may be incorrect.",
                    (int?)ex.StatusCode, fullUrl);
                throw new InvalidOperationException(
                    $"Character not found or invalid request. Please check the character name and realm. (Error: {ex.StatusCode})",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error making RaiderIO API request to {Url} after retries", fullUrl);
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "RaiderIO API request timed out for {Url}", fullUrl);
                throw;
            }
        }

        public async Task<RaiderIOModels.Affix> GetCurrentAffixAsync(string region = "us", string locale = "en", CancellationToken cancellationToken = default)
        {
            string url = $"/mythic-plus/affixes?region={region}&locale={locale}";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.Affix>(response);
        }

        public async Task<RaiderIOModels.RioGuildInfo> GetRioGuildInfoAsync(string guildName, string realmName, string region, CancellationToken cancellationToken = default)
        {
            guildName = guildName.Replace(" ", "%20");
            realmName = realmName.Replace(" ", "%20");
            string url = $"/guilds/profile?region={region}&realm={realmName}&name={guildName}&fields=raid_progression%2Craid_rankings";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RioGuildInfo>(response);
        }

        public async Task<RaiderIOModels.RioMythicPlusChar> GetCharMythicPlusInfoAsync(string charName, string realmName, string region = "us", CancellationToken cancellationToken = default)
        {
            string url = $"/characters/profile?region={region}&realm={realmName}&name={charName}" +
                $"&fields=gear" +
                $"%2Cmythic_plus_scores_by_season:current" +
                $"%2Cmythic_plus_ranks" +
                $"%2Cmythic_plus_scores" +
                $"%2Cmythic_plus_highest_level_runs" +
                $"%2Cmythic_plus_recent_runs" +
                $"%2Cmythic_plus_best_runs" +
                $"%2Cmythic_plus_weekly_highest_level_runs" +
                $"%2Cmythic_plus_previous_weekly_highest_level_runs" +
                $"%2Craid_progression" +
                $"%2Craid_achievement_curve" +
                $"%2Craid_achievement_meta";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(response);
        }

        public async Task<RaiderIOModels.MythicPlusStaticData> GetMythicPlusStaticDataAsync(int expansionId, CancellationToken cancellationToken = default)
        {
            string url = $"/mythic-plus/static-data?expansion_id={expansionId}";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.MythicPlusStaticData>(response);
        }
    }
}
