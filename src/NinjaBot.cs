using Discord.Net;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System;
using NinjaBotCore.Database;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Admin;
using NinjaBotCore.Modules.Steam;
using NinjaBotCore.Modules.RocketLeague;
using NinjaBotCore.Modules.Fun;
using NinjaBotCore.Modules.Away;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Services;
using NinjaBotCore.Modules.Giphy;
using NinjaBotCore.Modules.Weather;
using NinjaBotCore.Modules.YouTube;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Sinks.File;
using Serilog.Sinks.SystemConsole;
using Microsoft;

namespace NinjaBotCore
{
    public class NinjaBot
    {
        private DiscordShardedClient _client;
        private CommandHandler _handler;        
        public static DiscordShardedClient Client;        
        private IConfigurationRoot _config;

        public async Task StartAsync()
        {    
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File("logs/njabot.log", rollingInterval: RollingInterval.Day)
                .WriteTo.Console()      
                .CreateLogger();  

            //Create the configuration
            var _builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(path: "config.json");            
            _config = _builder.Build();
            
            var services = new ServiceCollection()
                .AddSingleton(new DiscordShardedClient(new DiscordSocketConfig
                {
                    //LogLevel = LogSeverity.Debug,
                    LogLevel = LogSeverity.Verbose, 
                    MessageCacheSize = 1000
                }))
                .AddSingleton(_config)
                .AddSingleton(new CommandService(new CommandServiceConfig 
                { 
                    DefaultRunMode = RunMode.Async,
                    LogLevel = LogSeverity.Verbose,
                    CaseSensitiveCommands = false, 
                    ThrowOnError = false 
                }))         
                .AddHttpClient()
                .AddSingleton<WowApi>()
                .AddSingleton<WowUtilities>()
                .AddSingleton<WarcraftLogs>()
                .AddSingleton<ChannelCheck>()   
                .AddSingleton<OxfordApi>()
                .AddSingleton<AwayCommands>()
                .AddSingleton<UserInteraction>()
                .AddSingleton<CommandHandler>()
                .AddSingleton<StartupService>()
                .AddSingleton<SteamApi>()        
                .AddSingleton<GiphyApi>()    
                .AddSingleton<WeatherApi>()
                .AddSingleton<RaiderIOApi>()
                .AddSingleton<YouTubeApi>()                
                .AddSingleton<AudioService>()
                .AddSingleton<LoggingService>();

            //Add logging                    
            ConfigureServices(services);    

            //Build services
            var serviceProvider = services.BuildServiceProvider();                                     

            //Instantiate logger/tie-in logging
            serviceProvider.GetRequiredService<LoggingService>();
            


            //Start the bot
            await serviceProvider.GetRequiredService<StartupService>().StartAsync();

            //Load up services
            serviceProvider.GetRequiredService<CommandHandler>();
            serviceProvider.GetRequiredService<UserInteraction>();
            serviceProvider.GetRequiredService<WowApi>();                                                     
            serviceProvider.GetRequiredService<AwayCommands>();            
            serviceProvider.GetRequiredService<WarcraftLogs>();
            serviceProvider.GetRequiredService<RaiderIOApi>(); 
            serviceProvider.GetRequiredService<WowUtilities>();    


                                                   
            // Block this program until it is closed.
            await Task.Delay(-1);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(configure => configure.AddSerilog());
        }

    }
}