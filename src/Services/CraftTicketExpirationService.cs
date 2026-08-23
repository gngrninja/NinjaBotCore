using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Crafting;

namespace NinjaBotCore.Services
{
    public class CraftTicketExpirationService : IHostedService, IDisposable
    {
        private readonly ILogger<CraftTicketExpirationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DiscordShardedClient _client;
        private Timer _timer;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        private static string[] ActiveStatuses => CraftConstants.ActiveStatuses;
        private static readonly string[] ExpirationStatuses =
            CraftConstants.ActiveStatuses.Append("PendingProfession").ToArray();

        public CraftTicketExpirationService(
            ILogger<CraftTicketExpirationService> logger,
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _client = client;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("CraftTicketExpirationService starting");

            _timer = new Timer(
                _ => _ = CheckExpiredTicketsAsync(_cts.Token),
                null,
                TimeSpan.FromSeconds(45),
                Timeout.InfiniteTimeSpan);

            return Task.CompletedTask;
        }

        private async Task CheckExpiredTicketsAsync(CancellationToken cancellationToken)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var nextExpiration = await db.CraftTickets
                    .Where(t => ExpirationStatuses.Contains(t.Status) && t.ExpiresAt.HasValue)
                    .OrderBy(t => t.ExpiresAt)
                    .Select(t => t.ExpiresAt.Value)
                    .FirstOrDefaultAsync(cancellationToken);

                if (nextExpiration == default)
                {
                    _logger.LogDebug("No craft tickets with expiration found - checking again in 5 minutes");
                    _timer?.Change(TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
                    return;
                }

                var timeUntilExpiration = nextExpiration - DateTime.UtcNow;

                if (timeUntilExpiration <= TimeSpan.Zero)
                {
                    var now = DateTime.UtcNow;
                    var expirationCandidates = await db.CraftTickets
                        .AsNoTracking()
                        .Where(t => ActiveStatuses.Contains(t.Status)
                                    && t.ExpiresAt.HasValue
                                    && t.ExpiresAt.Value <= now)
                        .ToListAsync(cancellationToken);
                    var expiredTickets = new List<CraftTicket>();
                    foreach (var candidate in expirationCandidates)
                    {
                        var expired = await CraftTicketUpdater.ExpireTicketAsync(
                            db,
                            candidate.Id,
                            candidate.Status,
                            now,
                            cancellationToken);
                        if (expired != null)
                        {
                            expiredTickets.Add(expired);
                        }
                    }

                    if (expiredTickets.Any())
                    {
                        _logger.LogInformation("Found {Count} expired craft tickets to close", expiredTickets.Count);

                        // Discord updates: best-effort per ticket. Only successful
                        // status-conditioned expirations are published.
                        foreach (var ticket in expiredTickets)
                        {
                            try
                            {
                                _logger.LogInformation("Expired craft ticket {TicketId} in guild {GuildId}", ticket.Id, ticket.GuildId);
                                await CraftTicketUpdater.UpdateTicketAsync(_client, ticket, _logger,
                                    threadNotification: $"<@{(ulong)ticket.RequesterId}> — this crafting request has expired. You can create a new request with `/craft request`.",
                                    archiveThread: true);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error updating Discord for expired craft ticket {TicketId}", ticket.Id);
                            }
                        }
                    }

                    // Also clean up stale PendingProfession tickets (user never selected a profession).
                    // The status predicate remains in the DELETE so a concurrent finalization wins safely.
                    var stalePendingCount = await db.CraftTickets
                        .Where(t => t.Status == "PendingProfession"
                                    && t.ExpiresAt.HasValue
                                    && t.ExpiresAt.Value <= now)
                        .ExecuteDeleteAsync(cancellationToken);

                    if (stalePendingCount > 0)
                    {
                        _logger.LogInformation("Cleaned up {Count} stale PendingProfession tickets", stalePendingCount);
                    }

                    _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    var delay = timeUntilExpiration > TimeSpan.FromMinutes(5)
                        ? TimeSpan.FromMinutes(5)
                        : timeUntilExpiration;

                    _logger.LogDebug("Next craft ticket expires in {TimeUntil:hh\\:mm\\:ss} - scheduling check in {Delay:hh\\:mm\\:ss}",
                        timeUntilExpiration, delay);

                    _timer?.Change(delay, Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CraftTicketExpirationService check cycle - retrying in 1 minute");
                _timer?.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("CraftTicketExpirationService stopping");
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _timer?.Dispose();
            _cts?.Dispose();
            _logger.LogInformation("CraftTicketExpirationService disposed");
        }
    }
}
