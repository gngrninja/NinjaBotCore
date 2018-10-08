using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class CharStats
    {
        [Key]
        public long CharStatId { get; set; }
        public string CharName { get; set; }
        public string GuildName { get; set; }
        public string RealmName { get; set; }
        public long LastModified { get; set; }
        public string ElixerConsumed { get; set; }
        public long Quantity { get; set; }

    }
}
