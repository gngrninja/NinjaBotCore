using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        private WebApplication? _app;
        private Task? _runTask;
        private readonly CancellationTokenSource _cts = new();

        public CommandsApiService(
            ILogger<CommandsApiService> logger,
            IConfigurationRoot config,
            HelpContentProvider helpProvider)
        {
            _logger = logger;
            _config = config;
            _helpProvider = helpProvider;
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
            var apiKey = _config.GetValue<string>("CommandsApi:ApiKey") ?? "";

            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = new[] { "--urls", $"http://localhost:{port}" }
                });

                // Suppress noisy Kestrel logs
                builder.Logging.SetMinimumLevel(LogLevel.Warning);

                _app = builder.Build();

                // Health check endpoint (no auth required)
                _app.MapGet("/api/commands/health", () => Results.Ok(new
                {
                    status = "healthy",
                    timestamp = DateTime.UtcNow
                }));

                // Commands endpoint (requires API key)
                _app.MapGet("/api/commands", (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            _logger.LogWarning("Commands API request with invalid API key from {IP}",
                                context.Connection.RemoteIpAddress);
                            return Results.Unauthorized();
                        }
                    }

                    var content = _helpProvider.GetHelpContent();
                    if (content == null)
                    {
                        return Results.NotFound(new { error = "Help content not available" });
                    }

                    _logger.LogDebug("Commands API request served successfully");

                    return Results.Json(content, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                        WriteIndented = false
                    });
                });

                // Regenerate endpoint (requires API key, triggers refresh)
                _app.MapPost("/api/commands/regenerate", (HttpContext context) =>
                {
                    // Validate API key
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
                        if (providedKey != apiKey)
                        {
                            return Results.Unauthorized();
                        }
                    }

                    _helpProvider.RegenerateHelpContent();

                    var content = _helpProvider.GetHelpContent();
                    return Results.Ok(new
                    {
                        success = true,
                        commands = content?.Metadata?.TotalCommands ?? 0,
                        categories = content?.Categories?.Count ?? 0,
                        last_updated = content?.Metadata?.LastUpdated
                    });
                });

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

                _logger.LogInformation("Commands API started on http://localhost:{Port}", port);
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
