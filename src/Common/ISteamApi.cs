using NinjaBotCore.Models.Steam;

namespace NinjaBotCore.Common
{
    public interface ISteamApi
    {
        SteamModel.Player GetProfileInfoBySteamID(long steamId);
        SteamModel.VanitySteam GetSteamIDbyVanityURL(string vanityName);
        SteamModel.Player GetSteamPlayerInfo(string lookupPlayer);
    }
}
