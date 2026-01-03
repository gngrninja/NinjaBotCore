using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class ModerationWatcherServiceTests : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ModerationWatcherService _service;
        private readonly IMemoryCache _cache;

        public ModerationWatcherServiceTests()
        {
            var services = new ServiceCollection();

            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddMemoryCache();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"ModWatcherTests_{Guid.NewGuid()}"));
            services.AddSingleton<Discord.WebSocket.DiscordShardedClient>();

            _provider = services.BuildServiceProvider();
            _cache = _provider.GetRequiredService<IMemoryCache>();
            _service = new ModerationWatcherService(_provider);
        }

        [Fact]
        public void InvalidateSettingsCache_RemovesCachedSettings()
        {
            const long guildId = 12345;
            var cacheKey = $"modwatch_settings_{guildId}";
            _cache.Set(cacheKey, new ModerationWatcher { DiscordGuildId = guildId });

            Assert.True(_cache.TryGetValue(cacheKey, out _));

            _service.InvalidateSettingsCache(guildId);

            Assert.False(_cache.TryGetValue(cacheKey, out _));
        }

        public void Dispose()
        {
            _provider?.Dispose();
        }
    }
}
