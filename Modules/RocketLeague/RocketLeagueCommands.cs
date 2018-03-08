using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Net;
using Discord;
using Discord.Commands;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Steam;
using NinjaBotCore.Modules.Steam;
using Discord.WebSocket;
using NinjaBotCore.Models.RocketLeague;
using NinjaBotCore.Modules.RocketLeague;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using RLSApi;
using RLSApi.Data;
using RLSApi.Net.Requests;

namespace NinjaBotCore.Modules.RocketLeague
{
    public class RocketLeagueCommands : ModuleBase
    {
        private SteamApi _steam;
        private static ChannelCheck _cc;
        private string _rlStatsKey;
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private RLSClient _rlsClient; 

        public RocketLeagueCommands(SteamApi steam, ChannelCheck cc, IConfigurationRoot config)
        {          
            _steam = steam;                           
            _cc = cc;                            
            _config = config;
            _rlStatsKey = $"{_config["RlStatsApi"]}";
            _prefix = _config["prefix"];
            _rlsClient = new RLSClient(_rlStatsKey);
        }
    }
}