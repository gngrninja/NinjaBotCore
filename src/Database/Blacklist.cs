using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class Blacklist
    {
        [Key]
        public long BlacklistId { get; set; }
        public Nullable<long> DiscordUserId { get; set; }
        public string DiscordUserName { get; set; }
        public string Reason { get; set; }
        public Nullable<System.DateTime> WhenBlacklisted { get; set; }
    }
}