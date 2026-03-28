using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services.Api;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Hosts a minimal Kestrel web server to expose the Commands API.
    /// Allows the web dashboard to fetch live command data.
    /// </summary>
    public class CommandsApiService : IHostedService, IDisposable
    {
        private readonly ILogger<CommandsApiService> _logger;
        private readonly IConfigurationRoot _config;
        private readonly HelpContentProvider _helpProvider;
        private readonly WowUtilities _wowUtilities;
        private readonly IServiceProvider _serviceProvider;
        private WebApplication? _app;
        private Task? _runTask;
        private readonly CancellationTokenSource _cts = new();

        public CommandsApiService(
            ILogger<CommandsApiService> logger,
            IConfigurationRoot config,
            HelpContentProvider helpProvider,
            WowUtilities wowUtilities,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _config = config;
            _helpProvider = helpProvider;
            _wowUtilities = wowUtilities;
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var enabled = _config.GetValue<bool>("CommandsApi:Enabled", false);
            if (!enabled)
            {
                _logger.LogInformation("Commands API is disabled");
                return Task.CompletedTask;
            }

            var port = _config.GetValue<int>("CommandsApi:Port", 5100);
            var host = _config.GetValue<string>("CommandsApi:Host") ?? "0.0.0.0";
            var apiKey = _config.GetValue<string>("CommandsApi:ApiKey") ?? "";

            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = new[] { "--urls", $"http://{host}:{port}" }
                });

                // Suppress noisy Kestrel logs
                builder.Logging.SetMinimumLevel(LogLevel.Warning);

                _app = builder.Build();

                var deps = new ApiDependencies(_logger, _serviceProvider, _helpProvider, _wowUtilities, apiKey);
                var filter = new ApiKeyEndpointFilter(apiKey, _logger);


                _app.MapAdminEndpoints(deps, filter);
                _app.MapPollEndpoints(deps, filter);
                _app.MapGuildSettingsEndpoints(deps, filter);
                _app.MapUserEndpoints(deps, filter);
                _app.MapRealmEndpoints(deps, filter);
                _app.MapStaticDataEndpoints(deps, filter);
                _app.MapCraftableItemEndpoints(deps, filter);

                // Start the web server in the background
                _runTask = Task.Run(async () =>
                {
                    try
                    {
                        await _app.RunAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Commands API server error");
                    }
                }, cancellationToken);

                _logger.LogInformation("Commands API started on http://{Host}:{Port}", host, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Commands API");
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_app == null) return;

            _logger.LogInformation("Stopping Commands API...");
            _cts.Cancel();

            try
            {
                await _app.StopAsync(cancellationToken);
                if (_runTask != null)
                {
                    await _runTask;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Commands API");
            }

            _logger.LogInformation("Commands API stopped");
        }

        public void Dispose()
        {
            _cts.Dispose();
            (_app as IDisposable)?.Dispose();
        }
    }

}
