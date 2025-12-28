using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaBotCore.Services
{
    public class StartupService
    {
        private readonly DiscordShardedClient _discord;
        private readonly IConfigurationRoot _config;
        private readonly IServiceProvider _services;
        private readonly ILogger<StartupService> _logger;

        // Shard readiness tracking
        private int _readyShards = 0;
        private readonly TaskCompletionSource<bool> _allShardsReady = new();

        public Task AllShardsReady => _allShardsReady.Task;

        public StartupService(IServiceProvider services)
        {
            _services = services;
            _config = _services.GetRequiredService<IConfigurationRoot>();
            _discord = _services.GetRequiredService<DiscordShardedClient>();
            _logger = _services.GetRequiredService<ILogger<StartupService>>();

            // Subscribe to shard events for monitoring
            _discord.ShardReady += OnShardReady;
            _discord.ShardConnected += OnShardConnected;
            _discord.ShardDisconnected += OnShardDisconnected;
        }

        private Task OnShardConnected(DiscordSocketClient shard)
        {
            _logger.LogInformation("Shard {ShardId} connected to gateway", shard.ShardId);
            return Task.CompletedTask;
        }

        private Task OnShardDisconnected(Exception exception, DiscordSocketClient shard)
        {
            _logger.LogError("Shard {ShardId} disconnected: {Error}",
                shard.ShardId, exception?.Message ?? "Unknown reason");
            return Task.CompletedTask;
        }

        private Task OnShardReady(DiscordSocketClient shard)
        {
            var readyCount = Interlocked.Increment(ref _readyShards);
            _logger.LogInformation("Shard {ShardId} ready ({ReadyCount}/{TotalCount})",
                shard.ShardId, readyCount, _discord.Shards.Count);

            if (readyCount == _discord.Shards.Count)
            {
                _logger.LogInformation("✅ All {TotalCount} shards ready - guilds are now accessible", _discord.Shards.Count);
                _allShardsReady.TrySetResult(true);
            }

            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            string discordToken = _config["Token"]; 
            if (string.IsNullOrWhiteSpace(discordToken))
            {
                throw new Exception("Token missing from config.json! Please enter your token there (root directory)");
            }

            await _discord.LoginAsync(TokenType.Bot, discordToken);
            await _discord.StartAsync();
        }
    }
}