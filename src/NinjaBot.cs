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

namespace NinjaBotCore
{
    public class NinjaBot
    {                   
        private IConfigurationRoot _config;
        
        public async Task StartAsync()
        {    
            //Create the configuration
            var _builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(path: "config.json");            
            _config = _builder.Build();
            
            //Configure services
            var services = new ServiceCollection()
                .AddSingleton(new DiscordShardedClient(new DiscordSocketConfig
                {
                    GatewayIntents = GatewayIntents.AllUnprivileged |
                    GatewayIntents.GuildMembers,                           
                    LogLevel = LogSeverity.Error,                     
                    MessageCacheSize = 1000,    
                    AlwaysDownloadUsers = true               
                }))
                .AddSingleton(_config)
                .AddSingleton(new CommandService(new CommandServiceConfig 
                { 
                    DefaultRunMode = Discord.Commands.RunMode.Async,
                    LogLevel = LogSeverity.Verbose,
                    CaseSensitiveCommands = false, 
                    ThrowOnError = false 
                }))  
                .AddHttpClient()                
                .AddSingleton<WowApi>()                                                
                .AddSingleton<WowUtilities>()
                .AddSingleton<WarcraftLogs>()
                .AddSingleton<ChannelCheck>()   
                .AddSingleton<AwayCommands>()
                .AddSingleton<UserInteraction>()
                .AddSingleton<CommandHandler>()
                .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordShardedClient>()))
                .AddSingleton<InteractionHandler>()
                .AddSingleton<StartupService>()
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

            // interaction testing
            await serviceProvider.GetRequiredService<InteractionHandler>()
                .InitializeAsync();

            //Start the bot
            await serviceProvider.GetRequiredService<StartupService>().StartAsync();

            //Load up services
            serviceProvider.GetRequiredService<CommandHandler>();
            serviceProvider.GetRequiredService<UserInteraction>();            
                                                      
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
            Log.Logger = new LoggerConfiguration()
                    .WriteTo.File("logs/njabot.log", rollingInterval: RollingInterval.Day)
                    .WriteTo.Console()             
                    .MinimumLevel.Is(level)                                                                          
                    .CreateLogger();  
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