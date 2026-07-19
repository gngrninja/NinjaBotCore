using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Repositories;
using Newtonsoft.Json;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Service for tracking WoW Token prices across all regions
    /// </summary>
    public class WowTokenService
    {
        private readonly ILogger<WowTokenService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfigurationRoot _config;
        private readonly WowApi _wowApi;

        private const string DEFAULT_UPDATE_INTERVAL_MINUTES = "15";

        public WowTokenService(
            IServiceScopeFactory scopeFactory,
            ILogger<WowTokenService> logger,
            IConfigurationRoot config,
            WowApi wowApi)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
            _wowApi = wowApi;
        }

        /// <summary>
        /// Get the configured update interval
        /// </summary>
        public TimeSpan GetUpdateInterval()
        {
            return TimeSpan.FromMinutes(
                int.Parse(_config["WowTokenPriceUpdateIntervalMinutes"] ?? DEFAULT_UPDATE_INTERVAL_MINUTES));
        }

        /// <summary>
        /// Run periodic token price updates using the provided timer
        /// </summary>
        public async Task RunPriceUpdatesAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await UpdateAllRegionsAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when service is disposed
            }
        }

        /// <summary>
        /// Update token prices for all regions
        /// </summary>
        public async Task UpdateAllRegionsAsync(CancellationToken cancellationToken = default)
        {
            var regions = new[] { "us", "eu", "kr", "tw" };

            foreach (var region in regions)
            {
                try
                {
                    await UpdatePriceAsync(region, cancellationToken);
                    _logger.LogInformation("Token price update completed for {Region}", region.ToUpper());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating token prices for {Region}", region.ToUpper());
                }
            }
        }

        /// <summary>
        /// Update WoW token price for a specific region
        /// </summary>
        public async Task UpdatePriceAsync(string region = "us", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Fetching WoW token price for region: {Region}", region);

                var url = $"/data/wow/token/index?namespace=dynamic-{region}";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", region, cancellationToken);
                var tokenData = JsonConvert.DeserializeObject<dynamic>(response);

                if (tokenData?.price != null)
                {
                    long price = tokenData.price;

                    await using var repo = new Repository<WowTokenPrices>(_scopeFactory);

                    var tokenPrice = new WowTokenPrices
                    {
                        Region = region,
                        Price = price,
                        Timestamp = DateTime.UtcNow
                    };

                    await repo.AddAsync(tokenPrice);
                    await repo.SaveChangesAsync();

                    _logger.LogInformation(
                        "Token price updated: {Region}={Price}g",
                        region,
                        price / 10000); // Convert copper to gold
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating token price for region {Region}", region);
                throw;
            }
        }

        /// <summary>
        /// Get current WoW token price for a region
        /// </summary>
        public async Task<WowTokenPrices> GetCurrentPriceAsync(string region = "us")
        {
            await using var repo = new Repository<WowTokenPrices>(_scopeFactory);

            var allPrices = await repo.WhereAsync(t => t.Region == region);
            var recentPrice = allPrices.OrderByDescending(t => t.Timestamp).FirstOrDefault();

            return recentPrice;
        }

        /// <summary>
        /// Get token price trend (24h change in copper)
        /// </summary>
        public async Task<long?> GetPriceTrendAsync(string region = "us")
        {
            await using var repo = new Repository<WowTokenPrices>(_scopeFactory);

            var dayAgo = DateTime.UtcNow.AddHours(-24);

            var allPrices = await repo.WhereAsync(t => t.Region == region);

            var current = allPrices.OrderByDescending(t => t.Timestamp).FirstOrDefault();

            var previous = allPrices
                .Where(t => t.Timestamp <= dayAgo)
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            if (current != null && previous != null)
            {
                return current.Price - previous.Price;
            }

            return null;
        }
    }
}
