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

namespace NinjaBotCore
{
    public class NinjaBot
    {
        private DiscordSocketClient _client;
        private CommandHandler _handler;        
        public static DiscordSocketClient Client;

        public NinjaBot()
        {
            //Make sure we have a db file (if not, create one)
            using (var db = new NinjaBotEntities())
            {
                db.Database.EnsureCreated();
            }
            new Config();
        }

        public async Task Start()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
#if DEBUG
                LogLevel = LogSeverity.Debug,
#else
                LogLevel = LogSeverity.Verbose,
#endif                
            });
            // Login and connect to Discord.
            _client.Log += Log;
            var serviceProvider = ConfigureServices();
            await _client.LoginAsync(TokenType.Bot, Config.Token);
            await _client.StartAsync();
            _handler = new CommandHandler(serviceProvider);
            await _handler.ConfigureAsync();
            new UserInteraction(serviceProvider);            
            Client = _client;    
            // Block this program until it is closed.                            
            await Task.Delay(-1);
        }
        private IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(new CommandService(new CommandServiceConfig { CaseSensitiveCommands = false, ThrowOnError = false }))
                .AddSingleton(new WowApi())
                .AddSingleton(new WarcraftLogs())
                .AddSingleton(new ChannelCheck())
                .AddSingleton(new RocketLeague())
                .AddSingleton(new OxfordApi())
                .AddSingleton(new AwayCommands(_client))
                .AddSingleton(new Steam());
            var provider = new DefaultServiceProviderFactory().CreateServiceProvider(services);
            //provider.GetService<PaginationService>();
            return provider;
        }

        private Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }
    }
}