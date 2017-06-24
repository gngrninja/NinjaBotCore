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
        private string _token;
        string configFile;
        private static char _prefix;
        private static string _wowApi;
        private static string _giphyApi;
        private static string _googleMapsApi;
        private static string _youTubeApi;
        private static string _darkSkyApi;
        private static string _steamApi;
        private static string _faceApi;
        private static string _imageApi;
        private static string _oxfordDictionaryApi;
        private static string _donateUrl;
        private static string _warcraftlogsApi;
        public static List<ChannelOutput> OutputChannels { get; private set; }

        public static string WarcraftLogsApi
        {
            get
            {
                return _warcraftlogsApi;
            }
            set
            {
                _warcraftlogsApi = value;
            }
        }
        public static string DonateUrl
        {
            get
            {
                return _donateUrl;
            }
            set
            {
                _donateUrl = value;
            }
        }
        public static string OxfordDictionaryApi
        {
            get
            {
                return _oxfordDictionaryApi;
            }
            private set
            {
                _oxfordDictionaryApi = value;
            }
        }
        public static string FaceApi
        {
            get
            {
                return _faceApi;
            }
            private set
            {
                _faceApi = value;
            }
        }
        public static char Prefix
        {
            get
            {
                return _prefix;
            }
            private set
            {
                _prefix = value;
            }
        }
        public static string WowApi
        {
            get
            {
                return _wowApi;
            }
            private set
            {
                _wowApi = value;
            }
        }
        public static string GiphyApi
        {
            get
            {
                return _giphyApi;
            }
            private set
            {
                _giphyApi = value;
            }
        }
        public static string GoogleMapsApi
        {
            get
            {
                return _googleMapsApi;
            }
            private set
            {
                _googleMapsApi = value;
            }
        }
        public static string YouTubeApi
        {
            get
            {
                return _youTubeApi;
            }
            private set
            {
                _youTubeApi = value;
            }
        }
        public static string DarkSkyApi
        {
            get
            {
                return _darkSkyApi;
            }
            private set
            {
                _darkSkyApi = value;
            }
        }
        public static string SteamApi
        {
            get
            {
                return _steamApi;
            }
            private set
            {
                _steamApi = value;
            }
        }
        public static string ImageApi
        {
            get
            {
                return _imageApi;
            }
            private set
            {
                _imageApi = value;
            }
        }

        public NinjaBot()
        {
            //Make sure we have a db file (if not, create it)
            using (var db = new NinjaBotEntities())
            {
                db.Database.EnsureCreated();
            }
            configFile = "config.json";
            ConfigData botConfig = GetConfigData(configFile);
            if (botConfig == null)
            {
                throw new InvalidDataException("Config file access error!");
            }
            else
            {
                _token = botConfig.Token;
                Prefix = botConfig.Prefix;
                WowApi = botConfig.WowApi;
                GiphyApi = botConfig.GiphyApi;
                GoogleMapsApi = botConfig.GoogleMapsApi;
                YouTubeApi = botConfig.YouTubeApi;
                DarkSkyApi = botConfig.DarkSkyApi;
                SteamApi = botConfig.SteamApi;
                FaceApi = botConfig.FaceApi;
                ImageApi = botConfig.ImageApi;
                OxfordDictionaryApi = botConfig.OxfordDictionaryApi;
                WarcraftLogsApi = botConfig.WarcraftlogsApi;
                DonateUrl = botConfig.DonateUrl;
            }
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
            await _client.LoginAsync(TokenType.Bot, _token);
            await _client.StartAsync();
            _handler = new CommandHandler(serviceProvider);
            await _handler.ConfigureAsync();
            new UserInteraction(serviceProvider);    
            //BotTimers timer = new BotTimers();       
            //await timer.StartTimer();            
            // Block this program until it is closed.                            
            await Task.Delay(-1);
        }

        ConfigData GetConfigData(string configFile)
        {
            ConfigData configData = null;
            try
            {
                if (File.Exists(configFile))
                {
                    configData = JsonConvert.DeserializeObject<ConfigData>(File.ReadAllText(configFile));
                }
                return configData;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting configuration information -> {ex.Message}");
                return configData;
            }
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