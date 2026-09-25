using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;

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
        private readonly TimeProvider _clock;
        private readonly object _observationLock = new();
        private long _lastObservationTicks;
        // Only presence not yet followed by an observed departure; remove on absence.
        // Register the ENTIRE snapshot synchronously, including guilds late in the batch.
        private readonly Dictionary<long, DateTime> _observedPresence = new();

        private (List<SocketGuild> Guilds, DateTime At) ObservePresence(Func<IEnumerable<SocketGuild>> snapshot)
        {
            lock (_observationLock)
            {
                var at = ObserveNow();
                var guilds = snapshot().ToList();
                foreach (var guild in guilds) _observedPresence[(long)guild.Id] = at;
                return (guilds, at);
            }
        }

        private (DateTime At, DateTime? PresenceAt) ObserveDeparture(long serverId)
        {
            lock (_observationLock)
            {
                var at = ObserveNow();
                return (at, _observedPresence.Remove(serverId, out var presenceAt) ? presenceAt : null);
            }
        }

        // Strictly increasing at PostgreSQL's microsecond precision, even when the clock
        // repeats or steps backwards. Capture BEFORE a snapshot/first await, never per write.
        private DateTime ObserveNow()
        {
            lock (_observationLock)
            {
                var ticks = _clock.GetUtcNow().UtcDateTime.Ticks;
                _lastObservationTicks = Math.Max(ticks - ticks % 10, _lastObservationTicks + 10);
                return new DateTime(_lastObservationTicks, DateTimeKind.Utc);
            }
        }

        public DiscordServerTrackingService(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<DiscordServerTrackingService>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;

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
                var (allGuilds, observedAt) = ObservePresence(() => _client.Guilds);

                _logger.LogInformation("Syncing {Count} Discord servers to database", allGuilds.Count);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                // Update/insert all current servers
                foreach (var guild in allGuilds)
                {
                    await UpsertDiscordServerAsync(db, guild, observedAt);
                }

                // A disconnected/incomplete gateway cache is not evidence of departure.
                // Only reconcile after every shard reports connected; REST is checked again at purge.
                if (_client.Shards.Any(s => s.ConnectionState != Discord.ConnectionState.Connected)) return;
                var currentGuildIds = allGuilds.Select(g => (long)g.Id).ToList();
                var staleServers = await db.DiscordServers.AsNoTracking()
                    .Where(s => s.BotPresent && s.JoinedAt != null && !currentGuildIds.Contains(s.ServerId))
                    .ToListAsync();
                foreach (var staleServer in staleServers)
                {
                    DateTime absentAt;
                    lock (_observationLock)
                    {
                        if (_client.GetGuild((ulong)staleServer.ServerId) != null) continue;
                        // A newer captured presence may still be queued for persistence.
                        if (_observedPresence.TryGetValue(staleServer.ServerId, out var presenceAt)
                            && presenceAt > staleServer.JoinedAt) continue;
                        absentAt = ObserveDeparture(staleServer.ServerId).At;
                    }
                    await DiscordServerLifecycle.MarkAbsentAsync(db, staleServer.ServerId, absentAt, staleServer.JoinedAt);
                }
                int cleanedCount = staleServers.Count;

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
                var (guilds, observedAt) = ObservePresence(() => shard.Guilds);
                _logger.LogInformation(
                    "Shard {ShardId} ready - syncing {Count} Discord servers",
                    shard.ShardId, guilds.Count);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                foreach (var guild in guilds)
                {
                    await UpsertDiscordServerAsync(db, guild, observedAt);
                }


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
                var (_, observedAt) = ObservePresence(() => new[] { guild });
                _logger.LogInformation(
                    "Bot joined Discord server: {ServerName} ({ServerId})",
                    guild.Name, guild.Id);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                await UpsertDiscordServerAsync(db, guild, observedAt);
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
                var (observedAt, presenceAt) = ObserveDeparture((long)guild.Id);
                _logger.LogInformation(
                    "Bot left Discord server: {ServerName} ({ServerId})",
                    guild.Name, guild.Id);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                await DiscordServerLifecycle.MarkAbsentAsync(db, (long)guild.Id, observedAt, precedingPresenceAt: presenceAt);
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
        private static Task UpsertDiscordServerAsync(NinjaBotEntities db, SocketGuild guild, DateTime observedAt) =>
            DiscordServerLifecycle.MarkPresentAsync(db, new DiscordServer
            {
                ServerId = (long)guild.Id,
                ServerName = guild.Name,
                OwnerId = (long?)guild.OwnerId,
                OwnerName = guild.Owner?.Username
            }, observedAt);

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
