using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class RioSearchHistory
    {
        [Key]
        public long Id { get; set; }

        public long DiscordUserId { get; set; }

        public string CharacterName { get; set; }

        public string RealmName { get; set; }

        public string Region { get; set; }

        public DateTime LastSearched { get; set; }

        public int SearchCount { get; set; }

        [MaxLength(20)]
        public string GameVersion { get; set; }
    }
}
