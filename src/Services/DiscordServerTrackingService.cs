using System;
using System.Linq;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Tracks which Discord servers the bot is in by monitoring join/leave events
    /// and syncing to the DiscordServers table. This enables the web dashboard to
    /// know where the bot is installed.
    /// </summary>
    public class DiscordServerTrackingService : IDisposable
    {
        private readonly ILogger<DiscordServerTrackingService> _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private bool _disposed;

        public DiscordServerTrackingService(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<DiscordServerTrackingService>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

            // Discord.NET uses "Guild" in event names, but we call them "Discord servers"
            // to avoid confusion with WoW guilds
            _client.JoinedGuild += OnJoinedDiscordServer;
            _client.LeftGuild += OnLeftDiscordServer;
            _client.ShardReady += OnShardReady;

            _logger.LogInformation("DiscordServerTrackingService loaded");
        }

        /// <summary>
        /// Initialize the service by syncing all Discord servers.
        /// Call this after all shards are ready.
        /// </summary>
        public async Task InitializeAsync()
        {
            await SyncAllShardsAsync();
        }

        /// <summary>
        /// Syncs all Discord servers from all connected shards.
        /// Also cleans up any stale servers the bot was removed from while offline.
        /// </summary>
        private async Task SyncAllShardsAsync()
        {
            try
            {
                var allGuilds = _client.Guilds.ToList();

                _logger.LogInformation("Syncing {Count} Discord servers to database", allGuilds.Count);

                await using var repo = new Repository<DiscordServer>(_scopeFactory);

                // Update/insert all current servers
                foreach (var guild in allGuilds)
                {
                    await UpsertDiscordServerAsync(repo, guild, isJoining: true);
                }

                // Cleanup: Mark servers we're no longer in as BotPresent = false
                // Use database-side filtering for performance with large server counts
                // This runs even if allGuilds.Count == 0 to clean up all stale records
                var currentGuildIds = allGuilds.Select(g => (long)g.Id).ToList();
                var staleServers = await repo.Query
                    .Where(s => s.BotPresent && !currentGuildIds.Contains(s.ServerId))
                    .ToListAsync();

                foreach (var staleServer in staleServers)
                {
                    _logger.LogInformation(
                        "Cleaning up stale server record: {ServerName} ({ServerId}) - bot no longer present",
                        staleServer.ServerName, staleServer.ServerId);

                    staleServer.BotPresent = false;
                    staleServer.LeftAt = DateTime.UtcNow;
                    repo.Update(staleServer);
                }

                int cleanedCount = staleServers.Count;

                await repo.SaveChangesAsync();

                if (cleanedCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} stale server records", cleanedCount);
                }

                _logger.LogInformation("Sync complete - {Count} Discord servers tracked", allGuilds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing Discord servers");
            }
        }

        /// <summary>
        /// Syncs all Discord servers from a shard when it becomes ready.
        /// This handles initial population and re-sync on reconnection.
        /// </summary>
        private async Task OnShardReady(DiscordSocketClient shard)
        {
            try
            {
                var guilds = shard.Guilds.ToList();
                _logger.LogInformation(
                    "Shard {ShardId} ready - syncing {Count} Discord servers",
                    shard.ShardId, guilds.Count);

                await using var repo = new Repository<DiscordServer>(_scopeFactory);

                foreach (var guild in guilds)
                {
                    await UpsertDiscordServerAsync(repo, guild, isJoining: true);
                }

                await repo.SaveChangesAsync();

                _logger.LogInformation(
                    "Shard {ShardId} - synced {Count} Discord servers to database",
                    shard.ShardId, guilds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error syncing Discord servers for shard {ShardId}",
                    shard.ShardId);
            }
        }

        /// <summary>
        /// Handles bot joining a new Discord server
        /// </summary>
        private async Task OnJoinedDiscordServer(SocketGuild guild)
        {
            try
            {
                _logger.LogInformation(
                    "Bot joined Discord server: {ServerName} ({ServerId})",
                    guild.Name, guild.Id);

                await using var repo = new Repository<DiscordServer>(_scopeFactory);
                await UpsertDiscordServerAsync(repo, guild, isJoining: true);
                await repo.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error tracking join for Discord server {ServerId}",
                    guild.Id);
            }
        }

        /// <summary>
        /// Handles bot leaving (or being kicked from) a Discord server
        /// </summary>
        private async Task OnLeftDiscordServer(SocketGuild guild)
        {
            try
            {
                _logger.LogInformation(
                    "Bot left Discord server: {ServerName} ({ServerId})",
                    guild.Name, guild.Id);

                await using var repo = new Repository<DiscordServer>(_scopeFactory);

                var server = await repo.FirstOrDefaultAsync(s => s.ServerId == (long)guild.Id);
                if (server != null)
                {
                    server.BotPresent = false;
                    server.LeftAt = DateTime.UtcNow;
                    repo.Update(server);
                    await repo.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error tracking leave for Discord server {ServerId}",
                    guild.Id);
            }
        }

        /// <summary>
        /// Upserts a Discord server record - creates if not exists, updates if exists
        /// </summary>
        private async Task UpsertDiscordServerAsync(
            Repository<DiscordServer> repo,
            SocketGuild guild,
            bool isJoining)
        {
            var server = await repo.FirstOrDefaultAsync(s => s.ServerId == (long)guild.Id);

            if (server == null)
            {
                // New server - create record
                await repo.AddAsync(new DiscordServer
                {
                    ServerId = (long)guild.Id,
                    ServerName = guild.Name,
                    OwnerId = (long?)guild.OwnerId,
                    OwnerName = guild.Owner?.Username,
                    BotPresent = true,
                    JoinedAt = DateTime.UtcNow,
                    LeftAt = null
                });
            }
            else
            {
                // Existing server - update record
                server.ServerName = guild.Name;
                server.OwnerId = (long?)guild.OwnerId;
                server.OwnerName = guild.Owner?.Username;
                server.BotPresent = true;
                server.LeftAt = null;

                // Only update JoinedAt if this is a rejoin (was previously marked as left)
                if (isJoining && server.JoinedAt == null)
                {
                    server.JoinedAt = DateTime.UtcNow;
                }

                repo.Update(server);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _client.JoinedGuild -= OnJoinedDiscordServer;
                _client.LeftGuild -= OnLeftDiscordServer;
                _client.ShardReady -= OnShardReady;

                _logger.LogInformation("DiscordServerTrackingService disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing DiscordServerTrackingService");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
