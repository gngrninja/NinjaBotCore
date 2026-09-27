#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Modules.Interactions.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Thin timer around <see cref="RaidRecapLiveCoordinator.RunSweepAsync"/>. The coordinator
    /// decides what is due; this only makes sure sweeps never overlap.
    /// </summary>
    public class RaidRecapLiveService : IHostedService, IDisposable
    {
        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);

        /// <summary>After a sweep fails outright, for example when the database is unreachable.</summary>
        private static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(5);

        private readonly ILogger<RaidRecapLiveService> _logger;
        private readonly RaidRecapLiveCoordinator _coordinator;

        private Timer? _timer;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _tickGate = new(1, 1);
        private bool _disposed;
        private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

        public RaidRecapLiveService(ILogger<RaidRecapLiveService> logger, RaidRecapLiveCoordinator coordinator)
        {
            _logger = logger;
            _coordinator = coordinator;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RaidRecapLiveService starting (every {Interval})", SweepInterval);
            _timer = new Timer(_ => _ = TickAsync(_cts.Token), null, InitialDelay, SweepInterval);
            return Task.CompletedTask;
        }

        private async Task TickAsync(CancellationToken ct)
        {
            if (_disposed || ct.IsCancellationRequested) return;
            if (DateTimeOffset.UtcNow < _pausedUntil) return;
            if (!await _tickGate.WaitAsync(0, ct)) return; // previous sweep still running
            try
            {
                await _coordinator.RunSweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                // One line every few minutes, not one every sweep.
                _pausedUntil = DateTimeOffset.UtcNow + FailurePause;
                _logger.LogWarning(ex, "Raid recap live sweep failed; pausing for {Pause}", FailurePause);
            }
            finally
            {
                _tickGate.Release();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RaidRecapLiveService stopping");
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { /* already disposed */ }
            _timer?.Dispose();
            _cts?.Dispose();
            _tickGate?.Dispose();
        }
    }
}
