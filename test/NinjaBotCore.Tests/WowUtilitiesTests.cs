using System;
using Xunit;
using NinjaBotCore.Modules.Wow;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Services;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Serilog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;

namespace NinjaBotCore.Tests
{
    public class WowUtilitiesTests
    {        
        WowUtilities _utils;
        Microsoft.Extensions.Logging.ILogger logger;
        IConfigurationRoot _config;
        IServiceProvider _provider;

        public WowUtilitiesTests()
        {        
            var _builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory);         
            _config = _builder.Build();    
            var services = new ServiceCollection()              
                .AddHttpClient() 
                .AddSingleton<WowApi>()                                                                
                .AddSingleton<WarcraftLogs>()
                .AddSingleton<ChannelCheck>()            
                .AddSingleton<RaiderIOApi>()    
                .AddSingleton<DiscordShardedClient>()                   
                .AddSingleton<WowUtilities>()               
                .AddSingleton<ILoggerFactory, NullLoggerFactory>()
                .AddSingleton(_config);    

            Log.Logger = new LoggerConfiguration()                    
                    .WriteTo.Console()                                 
                    .CreateLogger();  

            services.AddLogging(); 

            var serviceProvider = services.BuildServiceProvider();
            _provider = serviceProvider;
            
        }                

        [Fact]
        public void GetEmojiFromStringTest()
        {
            //Arrange
            _utils = _provider.GetRequiredService<WowUtilities>();
            var numMap = new List<(int, string)>()
                {
                    (1, ":one:"),
                    (2, ":two:"),
                    (3, ":three:"),
                    (4, ":four:"),
                    (5, ":five:"),
                    (6, ":six:"),
                    (7, ":seven:"),
                    (8, ":eight:"),
                    (9, ":nine:"),
                    (0, ":zero:"),
                    (10, ":one::zero:"),
                    (11, ":one::one:"),
                    (12, ":one::two:"),
                    (13, ":one::three:"),
                    (14, ":one::four:"),
                    (15, ":one::five:"),
                    (16, ":one::six:"),
                    (17, ":one::seven:"),
                    (18, ":one::eight:"),
                    (19, ":one::nine:"),
                    (20, ":two::zero:"),
                };

            //Act and Assert
            foreach (var map in numMap)
            {
                var result = _utils.GetNumberEmojiFromString(map.Item1);
                Assert.Equal(map.Item2, result);
            }
        }        
    }
}
