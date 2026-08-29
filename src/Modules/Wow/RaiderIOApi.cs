using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Modules.Wow
{
    public class RaiderIOApi : IRaiderIOApi
    {
        private const string ApiBaseUrl = "https://raider.io/api/v1";
        private const int MaxRetryAttempts = 3;
        private static readonly TimeSpan MaxInteractiveRetryDelay = TimeSpan.FromSeconds(5);

        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly SemaphoreSlim[] _cacheGates = Enumerable.Range(0, 32)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();

        public RaiderIOApi(
            IConfigurationRoot config,
            ILogger<RaiderIOApi> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = new SanitizingLogger<RaiderIOApi>(
                logger ?? throw new ArgumentNullException(nameof(logger)));
            _httpClientFactory = httpClientFactory
                ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _cache = cache;
        }

        private async Task<string> GetApiRequestAsync(
            string path,
            IReadOnlyCollection<KeyValuePair<string, string>> parameters,
            CancellationToken cancellationToken = default)
        {
            var fullUrl = BuildUrl(path, parameters);
            _logger.LogInformation("RaiderIO API request to {Endpoint}", path);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));
                    using var response = await client.GetAsync(fullUrl, cancellationToken);
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
                        if (retryAfter > MaxInteractiveRetryDelay)
                        {
                            throw new RaiderIORateLimitException(
                                "Raider.IO requested a retry delay longer than the interactive request budget.",
                                retryAfter);
                        }

                        if (attempt >= MaxRetryAttempts)
                        {
                            throw new RaiderIORateLimitException(
                                "Raider.IO rate limit remained active after retries.",
                                retryAfter);
                        }

                        LogRetry(attempt + 1, retryAfter, response.StatusCode);
                        await Task.Delay(retryAfter, cancellationToken);
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound ||
                        IsCharacterNotFoundBadRequest(response.StatusCode, content))
                    {
                        throw new RaiderIONotFoundException(
                            "Raider.IO could not find the requested character, guild, or resource.");
                    }

                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    {
                        _logger.LogWarning(
                            "RaiderIO API rejected request with status {StatusCode}: {Content}",
                            (int)response.StatusCode,
                            TruncateForLog(content));
                        throw new RaiderIOApiException(
                            $"Raider.IO rejected the request with HTTP {(int)response.StatusCode}.",
                            response.StatusCode);
                    }

                    if ((int)response.StatusCode >= 500)
                    {
                        if (attempt < MaxRetryAttempts)
                        {
                            var delay = ExponentialDelay(attempt);
                            LogRetry(attempt + 1, delay, response.StatusCode);
                            await Task.Delay(delay, cancellationToken);
                            continue;
                        }

                        throw new RaiderIOApiException(
                            $"Raider.IO remained unavailable with HTTP {(int)response.StatusCode}.",
                            response.StatusCode);
                    }

                    return content;
                }
                catch (RaiderIOApiException)
                {
                    throw;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested
                                                    && attempt < MaxRetryAttempts)
                {
                    var delay = ExponentialDelay(attempt);
                    LogRetry(attempt + 1, delay, null);
                    await Task.Delay(delay, cancellationToken);
                }
                catch (HttpRequestException) when (attempt < MaxRetryAttempts)
                {
                    var delay = ExponentialDelay(attempt);
                    LogRetry(attempt + 1, delay, null);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        private static bool IsCharacterNotFoundBadRequest(HttpStatusCode statusCode, string content)
        {
            if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            try
            {
                var error = JsonConvert.DeserializeObject<RaiderIOErrorResponse>(content);
                return string.Equals(
                    error?.Message,
                    "Could not find requested character",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private string BuildUrl(
            string path,
            IReadOnlyCollection<KeyValuePair<string, string>> parameters)
        {
            var query = new List<KeyValuePair<string, string>>(
                parameters ?? Array.Empty<KeyValuePair<string, string>>());
            var accessKey = _config["RioApi"]?.Trim();
            if (!string.IsNullOrWhiteSpace(accessKey))
            {
                query.Add(new KeyValuePair<string, string>("access_key", accessKey));
            }

            var encoded = string.Join("&", query
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                .Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            return string.IsNullOrEmpty(encoded)
                ? $"{ApiBaseUrl}{path}"
                : $"{ApiBaseUrl}{path}?{encoded}";
        }

        private void LogRetry(int attempt, TimeSpan delay, HttpStatusCode? statusCode)
        {
            _logger.LogWarning(
                "Retry attempt {AttemptNumber} for RaiderIO API request after {StatusCode}. Delay: {Delay}",
                attempt,
                statusCode.HasValue ? ((int)statusCode.Value).ToString(CultureInfo.InvariantCulture) : "transport error",
                delay);
        }

        private static TimeSpan GetRetryAfter(RetryConditionHeaderValue retryAfter)
        {
            if (retryAfter?.Delta is { } delta)
            {
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            if (retryAfter?.Date is { } date)
            {
                var remaining = date - DateTimeOffset.UtcNow;
                return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }

            return TimeSpan.FromSeconds(1);
        }

        private static TimeSpan ExponentialDelay(int attempt) =>
            TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 2)));

        private static string TruncateForLog(string content)
        {
            if (string.IsNullOrEmpty(content) || content.Length <= 500)
            {
                return content;
            }

            return content[..500] + "…";
        }

        private async Task<T> GetOrCreateCachedAsync<T>(
            string cacheKey,
            TimeSpan ttl,
            Func<Task<T>> factory,
            CancellationToken cancellationToken)
            where T : class
        {
            if (_cache == null)
            {
                return await factory();
            }

            if (_cache.TryGetValue(cacheKey, out T cached))
            {
                return cached;
            }

            var hash = StringComparer.Ordinal.GetHashCode(cacheKey) & int.MaxValue;
            var gate = _cacheGates[hash % _cacheGates.Length];
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (_cache.TryGetValue(cacheKey, out cached))
                {
                    return cached;
                }

                var result = await factory();
                if (result != null)
                {
                    _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = ttl,
                        Size = 1
                    });
                }
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<RaiderIOModels.Affix> GetCurrentAffixAsync(
            string region = "us",
            string locale = "en",
            CancellationToken cancellationToken = default)
        {
            var response = await GetApiRequestAsync(
                "/mythic-plus/affixes",
                Params(("region", region), ("locale", locale)),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.Affix>(response);
        }

        public async Task<RaiderIOModels.RioGuildInfo> GetRioGuildInfoAsync(
            string guildName,
            string realmName,
            string region,
            CancellationToken cancellationToken = default)
        {
            var response = await GetApiRequestAsync(
                "/guilds/profile",
                Params(
                    ("region", region),
                    ("realm", realmName),
                    ("name", guildName),
                    ("fields", "raid_progression,raid_rankings")),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RioGuildInfo>(response);
        }

        public async Task<RaiderIOModels.RioMythicPlusChar> GetCharMythicPlusInfoAsync(
            string charName,
            string realmName,
            string region = "us",
            CancellationToken cancellationToken = default)
        {
            const string fields =
                "gear," +
                "mythic_plus_scores_by_season:current," +
                "mythic_plus_ranks," +
                "mythic_plus_highest_level_runs," +
                "mythic_plus_recent_runs," +
                "mythic_plus_best_runs," +
                "mythic_plus_weekly_highest_level_runs," +
                "mythic_plus_previous_weekly_highest_level_runs," +
                "raid_progression," +
                "raid_achievement_curve," +
                "raid_achievement_meta";
            var response = await GetApiRequestAsync(
                "/characters/profile",
                Params(
                    ("region", region),
                    ("realm", realmName),
                    ("name", charName),
                    ("fields", fields)),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(response);
        }

        public async Task<RaiderIOModels.RioMythicPlusChar> GetCharInsightsInfoAsync(
            string charName,
            string realmName,
            string region = "us",
            CancellationToken cancellationToken = default)
        {
            const string fields =
                "gear," +
                "mythic_plus_scores_by_season:current," +
                "mythic_plus_ranks," +
                "mythic_plus_recent_runs," +
                "mythic_plus_best_runs:all," +
                "mythic_plus_alternate_runs:all," +
                "mythic_plus_dungeon_run_counts," +
                "mythic_plus_weekly_highest_level_runs," +
                "talents:categorized," +
                "raid_progression";
            var response = await GetApiRequestAsync(
                "/characters/profile",
                Params(
                    ("region", region),
                    ("realm", realmName),
                    ("name", charName),
                    ("fields", fields)),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(response);
        }

        public async Task<RaiderIOModels.MythicPlusStaticData> GetMythicPlusStaticDataAsync(
            int expansionId,
            CancellationToken cancellationToken = default)
        {
            var response = await GetApiRequestAsync(
                "/mythic-plus/static-data",
                Params(("expansion_id", expansionId.ToString(CultureInfo.InvariantCulture))),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.MythicPlusStaticData>(response);
        }

        public async Task<RaiderIOModels.CharacterRivalsResponse> GetCharacterRivalsAsync(
            string charName,
            string realmName,
            string region,
            string scope = "region",
            long? specId = null,
            CancellationToken cancellationToken = default)
        {
            var response = await GetApiRequestAsync(
                "/client/character-rivals",
                Params(
                    ("region", region),
                    ("realm", realmName),
                    ("name", charName),
                    ("scope", scope),
                    ("specId", specId?.ToString(CultureInfo.InvariantCulture))),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.CharacterRivalsResponse>(response);
        }

        public async Task<RaiderIOModels.RunReviewResponse> GetRunReviewAsync(
            string charName,
            string realmName,
            string region,
            RaiderIOModels.MythicPlusRun run,
            string scope = "region",
            CancellationToken cancellationToken = default)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (run.ZoneId <= 0)
            {
                throw new ArgumentException("A Raider.IO dungeon zone ID is required.", nameof(run));
            }

            var affixes = string.Join(",", (run.Affixes ?? Array.Empty<RaiderIOModels.AffixInfo>())
                .Where(affix => affix.Id > 0)
                .Select(affix => affix.Id.ToString(CultureInfo.InvariantCulture)));
            var response = await GetApiRequestAsync(
                "/client/run-review",
                Params(
                    ("region", region),
                    ("realm", realmName),
                    ("name", charName),
                    ("dungeonId", run.ZoneId.ToString(CultureInfo.InvariantCulture)),
                    ("keyLevel", run.MythicLevel.ToString(CultureInfo.InvariantCulture)),
                    ("clearTimeMs", run.ClearTimeMs.ToString(CultureInfo.InvariantCulture)),
                    ("affixes", affixes),
                    ("scope", scope),
                    ("specId", run.Spec?.Id > 0 ? run.Spec.Id.ToString(CultureInfo.InvariantCulture) : null),
                    ("completedAt", run.CompletedAt == default ? null : run.CompletedAt.ToString("O", CultureInfo.InvariantCulture))),
                cancellationToken);
            return JsonConvert.DeserializeObject<RaiderIOModels.RunReviewResponse>(response);
        }

        public async Task<RaiderIOModels.SeasonCutoffsResponse> GetSeasonCutoffsAsync(
            string region,
            string season,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"rio:cutoffs:{region?.ToLowerInvariant()}:{season ?? "current"}";
            return await GetOrCreateCachedAsync(
                cacheKey,
                TimeSpan.FromMinutes(10),
                async () =>
                {
                    var response = await GetApiRequestAsync(
                        "/mythic-plus/season-cutoffs",
                        Params(("region", region), ("season", season)),
                        cancellationToken);
                    return JsonConvert.DeserializeObject<RaiderIOModels.SeasonCutoffsResponse>(response);
                },
                cancellationToken);
        }

        public async Task<RaiderIOModels.LeaderboardCapacityResponse> GetLeaderboardCapacityAsync(
            string region,
            string realm = null,
            string scope = "current",
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"rio:capacity:{region?.ToLowerInvariant()}:{realm?.ToLowerInvariant()}:{scope}";
            return await GetOrCreateCachedAsync(
                cacheKey,
                TimeSpan.FromMinutes(5),
                async () =>
                {
                    var response = await GetApiRequestAsync(
                        "/mythic-plus/leaderboard-capacity",
                        Params(("region", region), ("realm", realm), ("scope", scope)),
                        cancellationToken);
                    return JsonConvert.DeserializeObject<RaiderIOModels.LeaderboardCapacityResponse>(response);
                },
                cancellationToken);
        }

        public async Task<RaiderIOModels.GuildLiveRaidResponse> GetGuildLiveRaidProgressAsync(
            string guildName,
            string realmName,
            string region,
            string raid = "latest",
            string difficulty = "latest",
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"rio:live-raid:{region?.ToLowerInvariant()}:{realmName?.ToLowerInvariant()}:{guildName?.ToLowerInvariant()}:{raid}:{difficulty}";
            return await GetOrCreateCachedAsync(
                cacheKey,
                TimeSpan.FromSeconds(20),
                async () =>
                {
                    var response = await GetApiRequestAsync(
                        "/live-tracking/guild/raid-progress",
                        Params(
                            ("region", region),
                            ("realm", realmName),
                            ("guild", guildName),
                            ("raid", raid),
                            ("difficulty", difficulty),
                            ("period", "current")),
                        cancellationToken);
                    return JsonConvert.DeserializeObject<RaiderIOModels.GuildLiveRaidResponse>(response);
                },
                cancellationToken);
        }

        private sealed class RaiderIOErrorResponse
        {
            [JsonProperty("message")]
            public string Message { get; set; }
        }

        private static IReadOnlyCollection<KeyValuePair<string, string>> Params(
            params (string Key, string Value)[] values) =>
            values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)).ToArray();
    }
}
