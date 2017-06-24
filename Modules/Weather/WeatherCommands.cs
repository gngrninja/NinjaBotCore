using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Discord.Commands;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Weather
{
    public class WeatherCommands : ModuleBase
    {
        private static ChannelCheck _cc = null;
        private static WeatherApi _weather = null;      
        private static WowApi _wowApi = null;

        public WeatherCommands(ChannelCheck cc, WowApi wowApi)
        {
            if (_cc == null)
            {
                _cc = cc;
            }
            if (_weather == null)
            {
                _weather = new WeatherApi();
            }            
            if (_wowApi == null) 
            {
                _wowApi = wowApi;
            }
        }
        
        [Command("weather")]
        [Summary("Gets the weather for a given location")]
        public async Task GetWeather([Remainder] string args)
        {
            try
            {
                var guildInfo = Context.Guild;
                string thumbnailUrl = string.Empty;

                if (guildInfo == null)
                {
                    thumbnailUrl = Context.User.GetAvatarUrl();
                }
                else
                {
                    thumbnailUrl = Context.Guild.IconUrl;
                }
                var geocode = _weather.GetGeocode(args);
                string coords = $"{geocode.results[0].geometry.location.lat},{geocode.results[0].geometry.location.lng}";
                var forecast = _weather.GetForecast(coords);
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(0, 255, 255));
                embed.Title = $"**__Weather for [{geocode.results[0].formatted_address}] @ [{_wowApi.UnixTimeStampToDateTimeSeconds(forecast.currently.time)}]__**";                
                sb.AppendLine($"*Currently [**{forecast.currently.summary}**] [**{forecast.currently.apparentTemperature}**]degrees, with winds @ [**{forecast.currently.windSpeed}**]mph, from [**{forecast.currently.windBearing}**]degrees*");
                sb.AppendLine($"Forecast for [**Today**] => [**{forecast.daily.data[0].summary}**] [hi [**{forecast.daily.data[0].temperatureMax}** @ **{_wowApi.UnixTimeStampToDateTimeSeconds(forecast.daily.data[0].temperatureMaxTime).ToString("T")}**] lo [**{forecast.daily.data[0].temperatureMin}** @ **{_wowApi.UnixTimeStampToDateTimeSeconds(forecast.daily.data[0].temperatureMinTime).ToString("T")}**]");
                sb.AppendLine($"Forecast for [**Tomorrow**] => [**{forecast.daily.data[1].summary}**] [hi [**{forecast.daily.data[1].temperatureMax}** @ **{_wowApi.UnixTimeStampToDateTimeSeconds(forecast.daily.data[1].temperatureMaxTime).ToString("T")}**] lo [**{forecast.daily.data[1].temperatureMin}** @ **{_wowApi.UnixTimeStampToDateTimeSeconds(forecast.daily.data[1].temperatureMinTime).ToString("T")}**]");
                if (forecast.alerts != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($":warning:**__WEATHER ALERT__**:warning:");
                    sb.AppendLine(forecast.alerts[0].description);
                }
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Weather error {ex.Message}");
            }
        }
    }
}
