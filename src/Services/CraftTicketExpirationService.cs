using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
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
                    .Where(t => ActiveStatuses.Contains(t.Status) && t.ExpiresAt.HasValue)
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
                    var expiredTickets = await db.CraftTickets
                        .Where(t => ActiveStatuses.Contains(t.Status)
                                    && t.ExpiresAt.HasValue
                                    && t.ExpiresAt.Value <= DateTime.UtcNow)
                        .ToListAsync(cancellationToken);

                    if (expiredTickets.Any())
                    {
                        _logger.LogInformation("Found {Count} expired craft tickets to close", expiredTickets.Count);

                        // Batch DB update first — all-or-nothing for persistence
                        foreach (var ticket in expiredTickets)
                        {
                            ticket.Status = "Expired";
                            ticket.CompletedAt = DateTime.UtcNow;
                        }
                        await db.SaveChangesAsync(cancellationToken);

                        // Discord updates: best-effort per ticket
                        foreach (var ticket in expiredTickets)
                        {
                            try
                            {
                                _logger.LogInformation("Expired craft ticket {TicketId} in guild {GuildId}", ticket.Id, ticket.GuildId);
                                await UpdateTicketMessageAsync(ticket);
                                await PostExpiredInThreadAsync(ticket);
                                await ArchiveThreadAsync(ticket);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error updating Discord for expired craft ticket {TicketId}", ticket.Id);
                            }
                        }
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

        private async Task UpdateTicketMessageAsync(CraftTicket ticket)
        {
            try
            {
                var guild = _client.GetGuild((ulong)ticket.GuildId);
                if (guild == null) return;

                var channel = guild.GetTextChannel((ulong)ticket.ChannelId);
                if (channel == null) return;

                var message = await channel.GetMessageAsync((ulong)ticket.MessageId);
                if (message is not IUserMessage userMessage) return;

                var embed = CraftEmbedBuilder.BuildTicketEmbed(ticket);
                var components = CraftEmbedBuilder.BuildComponents(ticket);

                await userMessage.ModifyAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Discord message for expired craft ticket {TicketId}", ticket.Id);
            }
        }

        private async Task PostExpiredInThreadAsync(CraftTicket ticket)
        {
            if (!ticket.ThreadId.HasValue) return;

            try
            {
                var guild = _client.GetGuild((ulong)ticket.GuildId);
                var thread = guild?.GetThreadChannel((ulong)ticket.ThreadId.Value);
                if (thread != null)
                {
                    await thread.SendMessageAsync(
                        $"<@{(ulong)ticket.RequesterId}> — this crafting request has expired. You can create a new request with `/craft request`.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error posting expiration message to thread for craft ticket {TicketId}", ticket.Id);
            }
        }

        private async Task ArchiveThreadAsync(CraftTicket ticket)
        {
            if (!ticket.ThreadId.HasValue) return;

            try
            {
                var guild = _client.GetGuild((ulong)ticket.GuildId);
                var thread = guild?.GetThreadChannel((ulong)ticket.ThreadId.Value);
                if (thread != null)
                {
                    await thread.ModifyAsync(t => t.Archived = true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error archiving thread for expired craft ticket {TicketId}", ticket.Id);
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
