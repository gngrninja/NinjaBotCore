using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Admin;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using NinjaBotCore.Common;
using Serilog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using ArgentPonyWarcraftClient;
using ArgentPonyWarcraftClient.Extensions.DependencyInjection;
using System.IO;
using System.Linq;
using Microsoft.Extensions.FileProviders;
using NinjaBotCore.Database;

namespace NinjaBotCore
{
    public class NinjaBot
    {                   
        private IConfigurationRoot _config;
        
        public async Task StartAsync()
        {
            try
            {
                await StartAsyncInternal();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "FATAL ERROR during bot startup: {Message}", ex.Message);
                Log.Fatal("Stack trace: {StackTrace}", ex.StackTrace);
                throw;
            }
        }

        private async Task StartAsyncInternal()
        {
            //Create the configuration
            var basePath = Directory.GetCurrentDirectory();
            var configCandidates = new[]
            {
                Environment.GetEnvironmentVariable("NINJABOT_CONFIG_PATH"),
                Path.Combine("config", "config.json"),
                "config.json"
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(basePath, path))
            .ToList();

            var resolvedConfigPath = configCandidates.FirstOrDefault(File.Exists);
            if (resolvedConfigPath == null)
            {
                throw new FileNotFoundException($"Unable to locate NinjaBot configuration. Looked in: {string.Join(", ", configCandidates)}");
            }

            var fileProvider = new PhysicalFileProvider(Path.GetDirectoryName(resolvedConfigPath)!);
            var configFileName = Path.GetFileName(resolvedConfigPath);

            var _builder = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(fileProvider, configFileName, optional: false, reloadOnChange: false)
                .AddEnvironmentVariables(prefix: "NINJABOT_");            
            _config = _builder.Build();
            DatabaseConfigurator.ConfigureFrom(_config);
            
            //Configure services
            var services = new ServiceCollection()
                .AddSingleton(new DiscordShardedClient(new DiscordSocketConfig
                {
                    GatewayIntents = GatewayIntents.AllUnprivileged |
                    GatewayIntents.GuildMembers |
                    GatewayIntents.MessageContent,  // Required for message edit/delete content
                    LogLevel = LogSeverity.Error,
                    MessageCacheSize = 1000,
                    AlwaysDownloadUsers = true,
                    ConnectionTimeout = 60000,  // Increase from default 30s to 60s for shard connections
                    HandlerTimeout = null,      // Disable handler timeout warnings
                    UseInteractionSnowflakeDate = false  // Fix for autocomplete "already acknowledged" clock sync errors
                }))
                .AddSingleton(_config)
                .AddDbContext<NinjaBotEntities>()
                .AddHttpClient()
                .AddMemoryCache(options =>
                {
                    // Configure memory cache size limit to prevent unbounded growth
                    options.SizeLimit = 1000; // Limit to 1000 cached entries across all caches
                })
                .AddSingleton<WowApi>()
                .AddSingleton<IWowApi>(sp => sp.GetRequiredService<WowApi>())
                .AddSingleton<WowUtilities>()
                .AddSingleton<WarcraftLogsV2Client>()
                .AddSingleton<UserInteraction>()
                .AddSingleton<ModerationWatcherService>()
                .AddSingleton<AwaySystemService>()
                .AddSingleton<WordFilterService>()
                .AddSingleton<DiscordServerTrackingService>()
                .AddSingleton<HelpContentProvider>()
                .AddSingleton<CommandsApiService>()
                .AddSingleton<PollExpirationService>()
                // RealmWatcherService moved to NinjaBotHelpers container
                .AddSingleton<WowCacheService>()
                .AddSingleton<CharacterResolver>()
                .AddSingleton<WowTokenService>()
                .AddSingleton<WowStaticDataService>()
                .AddSingleton(x => new InteractionService(
                    x.GetRequiredService<DiscordShardedClient>(),
                    new InteractionServiceConfig
                    {
                        // Enable automatic scope creation per interaction (Pattern #3)
                        // This ensures each slash command gets its own service scope automatically
                        AutoServiceScopes = true
                    }))
                .AddSingleton<Services.ErrorHandling.GlobalExceptionHandler>()
                .AddSingleton<InteractionHandler>()
                .AddSingleton<StartupService>()
                // Repository pattern (Pattern #3 - Scope-at-Boundary)
                .AddScoped<Repositories.IUnitOfWork, Repositories.UnitOfWork>()
                // Repository uses [ActivatorUtilitiesConstructor] to prefer NinjaBotEntities constructor
                .AddScoped(typeof(Repositories.IRepository<>), typeof(Repositories.Repository<>))
                .AddSingleton<RaiderIOApi>()
                .AddSingleton<IRaiderIOApi>(sp => sp.GetRequiredService<RaiderIOApi>())
                .AddSingleton<AudioService>()       
                .AddWarcraftClients(_config["WoWClient"], _config["WoWSecret"])         
                .AddSingleton<LoggingService>();                   
                        
            //Add logging      
            ConfigureServices(services);    

            //Build services
            var serviceProvider = services.BuildServiceProvider();                                     

            //Instantiate logger/tie-in logging
            serviceProvider.GetRequiredService<LoggingService>();

            var configuredProvider = _config["Database:Provider"];
            var providerLabel = string.IsNullOrWhiteSpace(configuredProvider)
                ? "SQLite (embedded ninjabot.db)"
                : configuredProvider;
            var connectionString = _config.GetConnectionString("NinjaBot");
            var connectionLabel = string.IsNullOrWhiteSpace(connectionString)
                ? "Default local database file"
                : "External connection string configured";
            Log.Information("NinjaBot database provider: {Provider} ({ConnectionContext})", providerLabel, connectionLabel);

            //Start the bot FIRST (shards must connect before command registration)
            await serviceProvider.GetRequiredService<StartupService>().StartAsync();

            // Now initialize interaction handler (will wait for shards and register commands)
            await serviceProvider.GetRequiredService<InteractionHandler>()
                .InitializeAsync();

            //Load up services
            serviceProvider.GetRequiredService<UserInteraction>();
            serviceProvider.GetRequiredService<ModerationWatcherService>();
            serviceProvider.GetRequiredService<AwaySystemService>();
            serviceProvider.GetRequiredService<WordFilterService>();

            // Initialize Discord server tracking (sync after shards are ready)
            var serverTracking = serviceProvider.GetRequiredService<DiscordServerTrackingService>();
            await serverTracking.InitializeAsync();

            // Start Commands API server (if enabled)
            var commandsApi = serviceProvider.GetRequiredService<CommandsApiService>();
            await commandsApi.StartAsync(CancellationToken.None);

            // Start Poll Expiration Service
            var pollExpirationService = serviceProvider.GetRequiredService<PollExpirationService>();
            await pollExpirationService.StartAsync(CancellationToken.None);

            // RealmWatcherService runs in separate NinjaBotHelpers container

            //Setup graceful shutdown
            var shutdownCts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true; // Prevent immediate termination
                Log.Information("Shutdown signal received (Ctrl+C). Initiating graceful shutdown...");
                shutdownCts.Cancel();
            };

            Log.Information("NinjaBot is running. Press Ctrl+C to shutdown gracefully.");

            //Block this program until shutdown signal received
            try
            {
                await Task.Delay(-1, shutdownCts.Token);
            }
            catch (TaskCanceledException)
            {
                // Expected when shutdown is triggered
            }

            // Graceful shutdown sequence
            Log.Information("Shutting down NinjaBot...");

            try
            {
                // Stop Commands API first
                Log.Information("Stopping Commands API...");
                await commandsApi.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping Commands API");
            }

            try
            {
                // Stop Discord client
                var client = serviceProvider.GetRequiredService<DiscordShardedClient>();
                Log.Information("Disconnecting Discord client...");
                await client.StopAsync();
                Log.Information("Discord client disconnected");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping Discord client");
            }

            // Dispose all services
            try
            {
                if (serviceProvider is IDisposable disposable)
                {
                    Log.Information("Disposing services...");
                    disposable.Dispose();
                    Log.Information("Services disposed");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error disposing services");
            }

            Log.Information("NinjaBot shutdown complete");
            Log.CloseAndFlush();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            //Add SeriLog
            services.AddLogging(configure => configure.AddSerilog()); 
            //Remove default HttpClient logging as it is extremely verbose
            services.RemoveAll<IHttpMessageHandlerBuilderFilter>();       
            //Configure logging level              
            var logLevel = "info";
            //var logLevel = Environment.GetEnvironmentVariable("NJA_LOG_LEVEL");
            var level = Serilog.Events.LogEventLevel.Error;
            if (!string.IsNullOrEmpty(logLevel))
            {
                switch (logLevel.ToLower())
                {
                    case "error":
                    {
                        level = Serilog.Events.LogEventLevel.Error;
                        break;
                    }
                    case "info":
                    {
                        level = Serilog.Events.LogEventLevel.Information;
                        break;
                    }
                    case "debug":
                    {
                        level = Serilog.Events.LogEventLevel.Debug;
                        break;
                    }
                    case "crit":
                    {
                        level = Serilog.Events.LogEventLevel.Fatal;
                        break;
                    }
                    case "warn":
                    {
                        level = Serilog.Events.LogEventLevel.Warning;
                        break;
                    }
                    case "trace":
                    {
                        level = Serilog.Events.LogEventLevel.Debug;
                        break;
                    }
                }
            }

            // Read NINJABOT_SuppressEFLogs environment variable
            var suppressEFLogs = false;
            if (bool.TryParse(Environment.GetEnvironmentVariable("NINJABOT_SuppressEFLogs"), out var suppressEF))
            {
                suppressEFLogs = suppressEF;
            }

            // Configure Serilog with optional EF Core log suppression
            var logConfig = new LoggerConfiguration()
                    .WriteTo.File("logs/njabot.log", rollingInterval: RollingInterval.Day)
                    .WriteTo.Console()
                    .MinimumLevel.Is(level);

            if (suppressEFLogs)
            {
                logConfig.MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command",
                    Serilog.Events.LogEventLevel.Warning);
            }

            Log.Logger = logConfig.CreateLogger();  
        }
        /// <summary>
        /// Returns true if running in development mode.
        /// Checks both compile-time DEBUG flag and runtime NINJABOT_DEV_MODE environment variable.
        /// </summary>
        public static bool IsDebug()
        {
#if DEBUG
            return true;
#else
            // Allow runtime override via environment variable for Docker dev harness
            var devMode = Environment.GetEnvironmentVariable("NINJABOT_DEV_MODE");
            return !string.IsNullOrEmpty(devMode) &&
                   (devMode.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    devMode.Equals("1", StringComparison.OrdinalIgnoreCase));
#endif
        }
    }
}
