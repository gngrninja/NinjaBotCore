using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class RlUserStat
    {
        [Key]
        public long Id { get; set; }
        public Nullable<long> SteamID { get; set; }
        public string RankedSolo { get; set; }
        public string Ranked2v2 { get; set; }
        public string RankedDuel { get; set; }
        public string Ranked3v3 { get; set; }
        public string Unranked { get; set; }
    }
}