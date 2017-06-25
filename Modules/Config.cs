using System;
using System.IO;
using System.Collections.Generic;
using NinjaBotCore.Models;
using Newtonsoft.Json;

namespace NinjaBotCore
{
    public class Config
    {        
        private static string _token;
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

        public Config()
        {
            string configFile = "config.json";
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
        public static string Token 
        {
            get 
            {
                return _token;
            }
            set 
            {
                _token = value;
            }
        }
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
    }
}