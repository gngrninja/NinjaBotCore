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

namespace NinjaBotCore.Modules.Wow
{
    public class RaiderIOApi
    {
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly ResiliencePipeline _resiliencePipeline;

        public RaiderIOApi(IServiceProvider services)
        {
            try
            {
                _logger = services.GetRequiredService<ILogger<RaiderIOApi>>();
                _config = services.GetRequiredService<IConfigurationRoot>();
                _httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient();

                // Configure default headers
                _httpClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

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
                    async ct => await _httpClient.GetStringAsync(fullUrl, ct),
                    cancellationToken);
                return response;
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
            string url = $"/characters/profile?region={region}&realm={realmName}&name={charName}&fields=mythic_plus_scores_by_season:current%2Cmythic_plus_ranks%2Cmythic_plus_scores%2Cmythic_plus_highest_level_runs%2Cmythic_plus_recent_runs%2Cmythic_plus_best_runs%2Craid_progression";
            var response = await GetApiRequestAsync(url, cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(response);
        }

        #region Synchronous Wrappers (for backward compatibility - consider removing)

        [Obsolete("Use GetCurrentAffixAsync instead. Synchronous methods will be removed in a future version.")]
        public RaiderIOModels.Affix GetCurrentAffix(string region = "us", string locale = "en")
        {
            return GetCurrentAffixAsync(region, locale).GetAwaiter().GetResult();
        }

        [Obsolete("Use GetRioGuildInfoAsync instead. Synchronous methods will be removed in a future version.")]
        public RaiderIOModels.RioGuildInfo GetRioGuildInfo(string guildName, string realmName, string region)
        {
            return GetRioGuildInfoAsync(guildName, realmName, region).GetAwaiter().GetResult();
        }

        [Obsolete("Use GetCharMythicPlusInfoAsync instead. Synchronous methods will be removed in a future version.")]
        public RaiderIOModels.RioMythicPlusChar GetCharMythicPlusInfo(string charName, string realmName, string region = "us")
        {
            return GetCharMythicPlusInfoAsync(charName, realmName, region).GetAwaiter().GetResult();
        }

        #endregion
    }
}
