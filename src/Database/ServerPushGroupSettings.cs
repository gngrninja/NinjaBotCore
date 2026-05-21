#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class ServerPushGroupSettings
    {
        [Key]
        public long DiscordGuildId { get; set; }

        /// <summary>
        /// Maximum open push groups allowed per guild. Null = unlimited.
        /// </summary>
        public int? MaxOpenGroups { get; set; }

        /// <summary>
        /// Default channel new push groups post into when no channel is specified.
        /// </summary>
        public long? DefaultChannelId { get; set; }

        /// <summary>
        /// Default ±IO rating window for auto-ping eligibility (default 200).
        /// </summary>
        public int DefaultIoWindow { get; set; } = 200;

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime? TimeSet { get; set; }
    }
}
