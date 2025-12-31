using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Admin;
using NinjaBotCore.Modules.Steam;
using NinjaBotCore.Modules.Interactions.Away;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using NinjaBotCore.Modules.YouTube;
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
                    HandlerTimeout = null       // Disable handler timeout warnings
                }))
                .AddSingleton(_config)
                .AddSingleton(new CommandService(new CommandServiceConfig 
                { 
                    DefaultRunMode = Discord.Commands.RunMode.Async,
                    LogLevel = LogSeverity.Verbose,
                    CaseSensitiveCommands = false, 
                    ThrowOnError = false 
                }))  
                .AddDbContext<NinjaBotEntities>()
                .AddHttpClient()                
                .AddSingleton<WowApi>()                                                
                .AddSingleton<WowUtilities>()
                .AddSingleton<WarcraftLogs>()
                .AddSingleton<WarcraftLogsV2Client>()
                .AddSingleton<WarcraftLogsV2Test>()
                .AddSingleton<AwayCommands>()
                .AddSingleton<UserInteraction>()
                .AddSingleton<ModerationWatcherService>()
                .AddSingleton<CommandHandler>()
                .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordShardedClient>()))
                .AddSingleton<Services.ErrorHandling.GlobalExceptionHandler>()
                .AddSingleton<InteractionHandler>()
                .AddSingleton<StartupService>()
                // Repository pattern (Phase 2)
                .AddScoped<Repositories.IUnitOfWork, Repositories.UnitOfWork>()
                .AddTransient(typeof(Repositories.IRepository<>), typeof(Repositories.Repository<>))
                .AddSingleton<SteamApi>()         
                .AddSingleton<RaiderIOApi>()
                .AddSingleton<YouTubeApi>()                
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

            // interaction testing
            await serviceProvider.GetRequiredService<InteractionHandler>()
                .InitializeAsync();

            //Start the bot
            await serviceProvider.GetRequiredService<StartupService>().StartAsync();

            //Load up services
            serviceProvider.GetRequiredService<CommandHandler>();
            serviceProvider.GetRequiredService<UserInteraction>();
            serviceProvider.GetRequiredService<ModerationWatcherService>();            
                                                      
            //Block this program until it is closed.
            await Task.Delay(-1);
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
         public static bool IsDebug()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
