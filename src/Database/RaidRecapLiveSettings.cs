#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    /// <summary>Per-server switch for the live raid recap card, set by an officer.</summary>
    public class RaidRecapLiveSettings
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long DiscordGuildId { get; set; }

        public bool Enabled { get; set; }

        /// <summary>Channel the live card is posted in.</summary>
        public long? ChannelId { get; set; }

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime? TimeSet { get; set; }
    }
}
