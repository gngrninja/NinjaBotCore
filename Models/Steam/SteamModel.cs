namespace NinjaBotCore.Models.Steam
{
    public class SteamModel
    {
        public class UserInfo
        {
            public Response response { get; set; }
        }

        public class Response
        {
            public Player[] players { get; set; }
        }

        public class Player
        {
            public string steamid { get; set; }
            public int communityvisibilitystate { get; set; }
            public int profilestate { get; set; }
            public string personaname { get; set; }
            public int lastlogoff { get; set; }
            public string profileurl { get; set; }
            public string avatar { get; set; }
            public string avatarmedium { get; set; }
            public string avatarfull { get; set; }
            public int personastate { get; set; }
            public string realname { get; set; }
            public string primaryclanid { get; set; }
            public int timecreated { get; set; }
            public int personastateflags { get; set; }
        }

        public class VanityResponse
        {
            public VanitySteam response { get; set; }
        }

        public class VanitySteam
        {
            public string steamid { get; set; }
            public int success { get; set; }
        }
    }
}