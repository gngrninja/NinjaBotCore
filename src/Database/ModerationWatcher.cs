using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class ModerationWatcher
    {
        [Key]
        public long DiscordGuildId { get; set; }

        // Channel configuration (shared by all watchers)
        public Nullable<long> ChannelId { get; set; }
        public string ChannelName { get; set; }

        // Watcher toggles (individual on/off per type)
        public Nullable<bool> WatchVoice { get; set; }
        public Nullable<bool> WatchMessages { get; set; }
        public Nullable<bool> WatchRoles { get; set; }
        public Nullable<bool> WatchBans { get; set; }
        public Nullable<bool> WatchNicknames { get; set; }

        // Future watcher types (Phase 2)
        public Nullable<bool> WatchProfiles { get; set; }
        public Nullable<bool> WatchAudit { get; set; }
        public Nullable<bool> WatchServer { get; set; }

        // Audit fields
        public Nullable<long> SetById { get; set; }
        public string SetByName { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }
    }
}
