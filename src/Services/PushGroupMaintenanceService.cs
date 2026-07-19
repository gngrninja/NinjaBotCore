#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Thin timer around <see cref="PushGroupCoordinator.RunMaintenanceSweepAsync"/>:
    /// T-15min start reminders and auto-closing stale groups, once a minute.
    /// </summary>
    public class PushGroupMaintenanceService : IHostedService, IDisposable
    {
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);

        private readonly ILogger<PushGroupMaintenanceService> _logger;
        private readonly PushGroupCoordinator _coordinator;

        private Timer? _timer;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _tickGate = new(1, 1);
        private bool _disposed;

        public PushGroupMaintenanceService(
            ILogger<PushGroupMaintenanceService> logger,
            PushGroupCoordinator coordinator)
        {
            _logger = logger;
            _coordinator = coordinator;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PushGroupMaintenanceService starting (every {Interval})", SweepInterval);
            _timer = new Timer(_ => _ = TickAsync(_cts.Token), null, InitialDelay, SweepInterval);
            return Task.CompletedTask;
        }

        private async Task TickAsync(CancellationToken ct)
        {
            if (_disposed || ct.IsCancellationRequested) return;
            if (!await _tickGate.WaitAsync(0, ct)) return; // previous sweep still running
            try
            {
                await _coordinator.RunMaintenanceSweepAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Push group maintenance sweep failed");
            }
            finally
            {
                _tickGate.Release();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PushGroupMaintenanceService stopping");
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Cancel before disposing so an in-flight sweep observes cancellation instead of
            // hitting disposed scopes/semaphores during provider teardown.
            try { _cts.Cancel(); } catch { /* already disposed */ }
            _timer?.Dispose();
            _cts?.Dispose();
            _tickGate?.Dispose();
        }
    }
}
