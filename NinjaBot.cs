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

namespace NinjaBotCore
{
    public class NinjaBot
    {
        private DiscordSocketClient _client;
        private CommandHandler _handler;        
        public static DiscordSocketClient Client;
        private IConfigurationRoot _config;

        public async Task StartAsync()
        {            
            //Create the configuration
            var _builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(path: "config.json");            
            _config = _builder.Build();
            
            var services = new ServiceCollection()
                .AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
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
                .AddSingleton<LoggingService>()
                .AddSingleton<WowApi>()
                .AddSingleton<WarcraftLogs>()
                .AddSingleton<ChannelCheck>()   
                .AddSingleton<OxfordApi>()
                .AddSingleton<AwayCommands>()
                //.AddSingleton<RlStatsApi>()
                .AddSingleton<UserInteraction>()
                .AddSingleton<CommandHandler>()
                .AddSingleton<StartupService>()
                .AddSingleton<SteamApi>()        
                .AddSingleton<GiphyApi>()    
                .AddSingleton<WeatherApi>()
                .AddSingleton<RaiderIOApi>()
                .AddSingleton<YouTubeApi>()
                .AddSingleton<AudioService>();
                
            var serviceProvider = services.BuildServiceProvider();
                                      
            serviceProvider.GetRequiredService<DiscordSocketClient>().Log += Log;   

            //Start the bot
            await serviceProvider.GetRequiredService<StartupService>().StartAsync(); 

            //Load up services
            serviceProvider.GetRequiredService<CommandHandler>();
            serviceProvider.GetRequiredService<LoggingService>();        
            serviceProvider.GetRequiredService<UserInteraction>();              
            serviceProvider.GetRequiredService<AwayCommands>();
            serviceProvider.GetRequiredService<WowApi>();
            serviceProvider.GetRequiredService<WarcraftLogs>();
            serviceProvider.GetRequiredService<RaiderIOApi>();
            
            /*             
            Not loading these on statup for now
            //serviceProvider.GetRequiredService<RlStatsApi>();
            serviceProvider.GetRequiredService<OxfordApi>();
            serviceProvider.GetRequiredService<ChannelCheck>();
            serviceProvider.GetRequiredService<SteamApi>();
            serviceProvider.GetRequiredService<GiphyApi>();
            serviceProvider.GetRequiredService<WeatherApi>();
            serviceProvider.GetRequiredService<YouTubeApi>();
            */                                   
                                 
            // Block this program until it is closed.
            await Task.Delay(-1);
        }

        private Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }
    }
}