#nullable enable

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// One request to Blizzard's Forever API and the reading of what comes back. Blizzard hasn't
    /// documented these routes, so "not found", an outage and an unexpected shape are all ordinary
    /// answers here and none of them throws.
    /// </summary>
    internal static class ForeverApi
    {
        public static string Locale(ForeverRegion region) => region == ForeverRegion.EU ? "en_GB" : "en_US";

        /// <param name="space">"profile" for characters and guilds, "static" for game data such as recipes.</param>
        public static async Task<ForeverLookup<JObject>> GetAsync(
            IWowApi wowApi, ILogger logger, ForeverRegion region, string space, string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var regionName = region.ToString().ToLowerInvariant();
            var url = $"{path}?namespace={space}-forever-{regionName}&locale={Locale(region)}";
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                var response = await wowApi.TryGetAPIRequestAsync(url, regionName, timeoutSource.Token);
                if (response.Status == HttpStatusCode.NotFound) return ForeverLookup<JObject>.NotFound();
                if (response.Status != HttpStatusCode.OK)
                {
                    logger.LogWarning("Forever API returned {Status} for {Path} in {Region}", (int)response.Status, path, region);
                    return ForeverLookup<JObject>.Unavailable();
                }

                return ForeverLookup<JObject>.Found(JObject.Parse(response.Body));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Message only: a timeout or outage here is routine and shouldn't read as a crash.
                logger.LogWarning("Forever API request for {Path} in {Region} failed: {Type}: {Message}",
                    path, region, ex.GetType().Name, ex.Message);
                return ForeverLookup<JObject>.Unavailable();
            }
        }

        // Names are plain strings when a locale is sent; without one Blizzard sends a map per locale.
        public static string? Text(JToken? token) => token?.Type switch
        {
            JTokenType.String => token.Value<string>(),
            JTokenType.Object => Text(token["en_US"] ?? token["en_GB"] ?? token.First?.First),
            _ => null
        };

        public static int? Int(JToken? token) => Long(token) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

        public static long? Long(JToken? token) => token?.Type switch
        {
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => (long)token.Value<double>(),
            _ => null
        };

        public static double? Number(JToken? token) => token?.Type switch
        {
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => token.Value<double>(),
            _ => null
        };
    }
}
