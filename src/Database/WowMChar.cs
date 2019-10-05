using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowMChar
    {
        [Key]
        public long Id { get; set; }
        public long ServerId { get; set; }
        public long DiscordUserId { get; set; }
        public string CharName { get; set; }
        public string ClassName { get; set;}
        public long ItemLevel { get; set; }
        public long Traits { get; set; }
        public string MainSpec { get; set; }
        public string OffSpec { get; set; }   
        public bool IsMain { get; set; }                
    }
}