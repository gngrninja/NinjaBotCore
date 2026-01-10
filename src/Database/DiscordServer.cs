using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class DiscordServer
    {
        [Key]
        public long ServerId { get; set; }
        public string ServerName { get; set; }
        public Nullable<long> OwnerId { get; set; }
        public string OwnerName { get; set; }

        // Bot presence tracking
        public bool BotPresent { get; set; } = true;
        public DateTime? JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
    }
}