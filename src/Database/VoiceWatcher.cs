using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class VoiceWatcher
    {
        [Key]
        public long DiscordGuildId { get; set; }
        public Nullable<bool> WatchVoice { get; set; }
        public Nullable<long> ChannelId { get; set; }
        public string ChannelName { get; set; }
        public Nullable<long> SetById { get; set; }
        public string SetByName { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }
    }
}
