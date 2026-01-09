using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Discord.WebSocket;
using NinjaBotCore.Services;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for StartupService - bot initialization and token validation
    /// </summary>
    public class ReadyHandlerTests
    {
        [Fact]
        public async Task StartAsync_WithEmptyToken_ThrowsException()
        {
            // Arrange - Create service with empty config
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("Token", "")
                })
                .Build();

            services.AddSingleton<IConfigurationRoot>(config);
            services.AddSingleton<DiscordShardedClient>();
            services.AddHttpClient();
            services.AddSingleton<WowApi>();
            services.AddSingleton<WowTokenService>();
            services.AddSingleton<WowStaticDataService>();

            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"ReadyHandlerTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            services.AddScoped<IRepository<WowMounts>>(sp =>
                new Repository<WowMounts>(sp.GetRequiredService<IServiceScopeFactory>()));
            services.AddScoped<IRepository<WowTokenPrices>>(sp =>
                new Repository<WowTokenPrices>(sp.GetRequiredService<IServiceScopeFactory>()));

            var provider = services.BuildServiceProvider();
            var startupService = new StartupService(provider);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(async () =>
            {
                await startupService.StartAsync();
            });

            Assert.Contains("Token missing from config.json", exception.Message);
        }

        [Fact]
        public async Task StartAsync_WithNullToken_ThrowsException()
        {
            // Arrange - Create service with no token key at all
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>())
                .Build();

            services.AddSingleton<IConfigurationRoot>(config);
            services.AddSingleton<DiscordShardedClient>();
            services.AddHttpClient();
            services.AddSingleton<WowApi>();
            services.AddSingleton<WowTokenService>();
            services.AddSingleton<WowStaticDataService>();

            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"ReadyHandlerTestDb2_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            services.AddScoped<IRepository<WowMounts>>(sp =>
                new Repository<WowMounts>(sp.GetRequiredService<IServiceScopeFactory>()));
            services.AddScoped<IRepository<WowTokenPrices>>(sp =>
                new Repository<WowTokenPrices>(sp.GetRequiredService<IServiceScopeFactory>()));

            var provider = services.BuildServiceProvider();
            var startupService = new StartupService(provider);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(async () =>
            {
                await startupService.StartAsync();
            });

            Assert.Contains("Token missing", exception.Message);
        }

        [Fact]
        public async Task StartAsync_WithWhitespaceToken_ThrowsException()
        {
            // Arrange - Create service with whitespace token
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("Token", "   ")
                })
                .Build();

            services.AddSingleton<IConfigurationRoot>(config);
            services.AddSingleton<DiscordShardedClient>();
            services.AddHttpClient();
            services.AddSingleton<WowApi>();
            services.AddSingleton<WowTokenService>();
            services.AddSingleton<WowStaticDataService>();

            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"ReadyHandlerTestDb3_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            services.AddScoped<IRepository<WowMounts>>(sp =>
                new Repository<WowMounts>(sp.GetRequiredService<IServiceScopeFactory>()));
            services.AddScoped<IRepository<WowTokenPrices>>(sp =>
                new Repository<WowTokenPrices>(sp.GetRequiredService<IServiceScopeFactory>()));

            var provider = services.BuildServiceProvider();
            var startupService = new StartupService(provider);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(async () =>
            {
                await startupService.StartAsync();
            });

            Assert.Contains("Token missing", exception.Message);
        }
    }
}
