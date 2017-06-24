using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class RlStat
    {
        [Key]
        public long Id { get; set; }
        public Nullable<long> SteamID { get; set; }
        public Nullable<long> DiscordUserID { get; set; }
        public string DiscordUserName { get; set; }
    }
}