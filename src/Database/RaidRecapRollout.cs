#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    public enum RaidRecapRolloutMode
    {
        /// <summary>Nothing is posted or refreshed anywhere. The kill switch.</summary>
        Off = 0,

        /// <summary>Only servers the bot owner is a member of.</summary>
        OwnerServers = 1,

        /// <summary>Every server.</summary>
        Everyone = 2
    }

    /// <summary>The bot owner's global switch for the live raid recap. A single row, Id 1.</summary>
    public class RaidRecapRollout
    {
        public const long SingletonId = 1;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; } = SingletonId;

        public RaidRecapRolloutMode Mode { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}
