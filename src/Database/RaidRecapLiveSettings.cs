#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Per-server switch for the live raid recap card, set by an officer, or by the bot owner
    /// when they select the server for the rollout.
    /// </summary>
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

        /// <summary>The bot owner picked this server for the rollout (rollout mode Selected).</summary>
        public bool Selected { get; set; }

        /// <summary>
        /// When the one-time message about the card and how to turn it off was posted. Set only
        /// for servers the rollout turned on, so it is never sent twice.
        /// </summary>
        public DateTime? NoticeSentAt { get; set; }
    }
}
