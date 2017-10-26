using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Net;
using NinjaBotCore.Models.Steam;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NinjaBotCore.Modules.Steam
{
    public class Steam
    {
        private string _key = $"?key={Config.SteamApi}";

        private string getAPIRequest(string url)
        {
            string response;
            string prefix;
            prefix = "http://api.steampowered.com/ISteamUser/";
            url = $"{prefix}{url}&format=json";

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;                             
                response = httpClient.GetStringAsync(url).Result;
            }

            return response;
        }

        public SteamModel.Player GetProfileInfoBySteamID(long steamId)
        {
            SteamModel.UserInfo u;
            SteamModel.Player p;
            string url = string.Empty;
            url = $"GetPlayerSummaries/v0002/{_key}&steamids={steamId}";
            u = JsonConvert.DeserializeObject<SteamModel.UserInfo>(getAPIRequest(url));
            p = u.response.players[0];

            return p;
        }

        public SteamModel.VanitySteam GetSteamIDbyVanityURL(string vanityName)
        {
            SteamModel.VanityResponse p = new SteamModel.VanityResponse();
            SteamModel.VanitySteam vs = new SteamModel.VanitySteam();
            string url = string.Empty;
            url = $"ResolveVanityURL/v0001/{_key}&vanityurl={vanityName}";
            p = JsonConvert.DeserializeObject<SteamModel.VanityResponse>(getAPIRequest(url));
            vs = p.response;

            return vs;
        }

        public SteamModel.Player GetSteamPlayerInfo(string lookupPlayer)
        {
            Steam s = new Steam();
            SteamModel.Player p = new SteamModel.Player();
            try
            {
                long steamID = 0;
                if (lookupPlayer.Length == 17)
                {
                    long.TryParse(lookupPlayer, out steamID);
                    Console.WriteLine($"{steamID}");
                    if (steamID != 0)
                    {
                        try
                        {
                            p = s.GetProfileInfoBySteamID(steamID);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{ex.Message}");
                        }
                    }
                }
                else
                {
                    SteamModel.VanitySteam v;
                    v = s.GetSteamIDbyVanityURL(lookupPlayer);
                    if (v.success == 1)
                    {
                        p = s.GetProfileInfoBySteamID(long.Parse(v.steamid));
                        Console.WriteLine(p.steamid);
                    }
                    else
                    {

                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.Message}");
            }
            return p;
        }
    }
}
