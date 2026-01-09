using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for WowTokenService - WoW Token price tracking
    /// </summary>
    public class WowTokenServiceTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly WowTokenService _tokenService;

        public WowTokenServiceTests()
        {
            var services = new ServiceCollection();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("WowTokenPriceUpdateIntervalMinutes", "15")
                })
                .Build();

            // Ensure all contexts share the same in-memory database instance
            var dbRoot = new InMemoryDatabaseRoot();

            services.AddSingleton<IConfigurationRoot>(config);
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddHttpClient();

            // Use in-memory database with shared root
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase("WowTokenServiceTests", dbRoot)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            // WowApi requires these dependencies
            services.AddSingleton<WowApi>();
            services.AddSingleton<WowTokenService>();

            _serviceProvider = services.BuildServiceProvider();
            _tokenService = _serviceProvider.GetRequiredService<WowTokenService>();
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
        }

        [Fact]
        public void GetUpdateInterval_ReturnsConfiguredInterval()
        {
            // Act
            var interval = _tokenService.GetUpdateInterval();

            // Assert
            Assert.Equal(TimeSpan.FromMinutes(15), interval);
        }

        [Fact]
        public void GetUpdateInterval_DefaultsTo15Minutes_WhenNotConfigured()
        {
            // Arrange - create service with empty config
            var services = new ServiceCollection();
            var emptyConfig = new ConfigurationBuilder().Build();

            services.AddSingleton<IConfigurationRoot>(emptyConfig);
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddHttpClient();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"WowTokenServiceTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddSingleton<WowApi>();
            services.AddSingleton<WowTokenService>();

            var provider = services.BuildServiceProvider();
            var tokenService = provider.GetRequiredService<WowTokenService>();

            // Act
            var interval = tokenService.GetUpdateInterval();

            // Assert
            Assert.Equal(TimeSpan.FromMinutes(15), interval);
        }

        [Fact]
        public async Task GetCurrentPriceAsync_ReturnsNull_WhenNoData()
        {
            // Act
            var result = await _tokenService.GetCurrentPriceAsync("us");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetCurrentPriceAsync_ReturnsMostRecent_WhenMultiplePrices()
        {
            // Arrange - add multiple prices using scoped context
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.AddRange(
                    new WowTokenPrices { Region = "us", Price = 1000000, Timestamp = DateTime.UtcNow.AddHours(-2) },
                    new WowTokenPrices { Region = "us", Price = 1100000, Timestamp = DateTime.UtcNow.AddHours(-1) },
                    new WowTokenPrices { Region = "us", Price = 1200000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var result = await _tokenService.GetCurrentPriceAsync("us");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(1200000, result.Price);
        }

        [Fact]
        public async Task GetCurrentPriceAsync_FiltersRegion_ReturnsCorrectRegion()
        {
            // Arrange - add prices for different regions
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.AddRange(
                    new WowTokenPrices { Region = "us", Price = 1000000, Timestamp = DateTime.UtcNow },
                    new WowTokenPrices { Region = "eu", Price = 2000000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var usResult = await _tokenService.GetCurrentPriceAsync("us");
            var euResult = await _tokenService.GetCurrentPriceAsync("eu");

            // Assert
            Assert.Equal(1000000, usResult.Price);
            Assert.Equal(2000000, euResult.Price);
        }

        [Fact]
        public async Task GetPriceTrendAsync_ReturnsNull_WhenNoData()
        {
            // Act
            var result = await _tokenService.GetPriceTrendAsync("kr"); // Use different region to avoid shared state

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetPriceTrendAsync_ReturnsNull_WhenOnlyCurrentData()
        {
            // Arrange - only current data, no 24h old data
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.Add(
                    new WowTokenPrices { Region = "tw", Price = 1000000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var result = await _tokenService.GetPriceTrendAsync("tw");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetPriceTrendAsync_ReturnsPositiveTrend_WhenPriceIncreased()
        {
            // Arrange - price increased over 24h
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.AddRange(
                    new WowTokenPrices { Region = "trend-up", Price = 1000000, Timestamp = DateTime.UtcNow.AddHours(-25) },
                    new WowTokenPrices { Region = "trend-up", Price = 1200000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var result = await _tokenService.GetPriceTrendAsync("trend-up");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(200000, result.Value); // 1200000 - 1000000
        }

        [Fact]
        public async Task GetPriceTrendAsync_ReturnsNegativeTrend_WhenPriceDecreased()
        {
            // Arrange - price decreased over 24h
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.AddRange(
                    new WowTokenPrices { Region = "trend-down", Price = 1500000, Timestamp = DateTime.UtcNow.AddHours(-25) },
                    new WowTokenPrices { Region = "trend-down", Price = 1200000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var result = await _tokenService.GetPriceTrendAsync("trend-down");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(-300000, result.Value); // 1200000 - 1500000
        }

        [Fact]
        public async Task GetPriceTrendAsync_UsesClosestTo24HoursAgo()
        {
            // Arrange - multiple historical prices, should use the one closest to 24h
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.AddRange(
                    new WowTokenPrices { Region = "multi", Price = 900000, Timestamp = DateTime.UtcNow.AddHours(-48) },
                    new WowTokenPrices { Region = "multi", Price = 1000000, Timestamp = DateTime.UtcNow.AddHours(-25) },
                    new WowTokenPrices { Region = "multi", Price = 1050000, Timestamp = DateTime.UtcNow.AddHours(-26) },
                    new WowTokenPrices { Region = "multi", Price = 1200000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var result = await _tokenService.GetPriceTrendAsync("multi");

            // Assert - should use 1000000 (25h ago, closest to 24h threshold but still over)
            Assert.NotNull(result);
            Assert.Equal(200000, result.Value); // 1200000 - 1000000
        }

        [Fact]
        public async Task GetPriceTrendAsync_FiltersRegion()
        {
            // Arrange - different trends for different regions
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                db.WowTokenPrices.AddRange(
                    new WowTokenPrices { Region = "region-a", Price = 1000000, Timestamp = DateTime.UtcNow.AddHours(-25) },
                    new WowTokenPrices { Region = "region-a", Price = 1200000, Timestamp = DateTime.UtcNow },
                    new WowTokenPrices { Region = "region-b", Price = 2000000, Timestamp = DateTime.UtcNow.AddHours(-25) },
                    new WowTokenPrices { Region = "region-b", Price = 1800000, Timestamp = DateTime.UtcNow }
                );
                await db.SaveChangesAsync();
            }

            // Act
            var aTrend = await _tokenService.GetPriceTrendAsync("region-a");
            var bTrend = await _tokenService.GetPriceTrendAsync("region-b");

            // Assert
            Assert.Equal(200000, aTrend.Value);  // region-a went up
            Assert.Equal(-200000, bTrend.Value); // region-b went down
        }
    }
}
