#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public enum RaidRecapLiveState
    {
        /// <summary>The report is still growing and the card is being refreshed.</summary>
        Live = 0,

        /// <summary>The raid finished and the card shows its final summary.</summary>
        Ended = 1,

        /// <summary>Watching stopped early: the card was deleted, access was lost or the server turned it off.</summary>
        Stopped = 2
    }

    /// <summary>
    /// One posted live raid recap card. Everything needed to keep editing the message lives
    /// here, so watching resumes after a restart.
    /// </summary>
    public class RaidRecapLiveCard
    {
        [Key]
        public long Id { get; set; }

        public long DiscordGuildId { get; set; }

        public long ChannelId { get; set; }

        public long MessageId { get; set; }

        [Required]
        [MaxLength(16)]
        public string ReportCode { get; set; } = "";

        public RaidRecapLiveState State { get; set; }

        [MaxLength(100)]
        public string? GuildName { get; set; }

        [MaxLength(8)]
        public string? Region { get; set; }

        [MaxLength(200)]
        public string? ZoneName { get; set; }

        /// <summary>What the card last showed. The message is only edited when this changes.</summary>
        [MaxLength(200)]
        public string? Fingerprint { get; set; }

        /// <summary>Consecutive failed refreshes. Watching stops after too many.</summary>
        public int Failures { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime LastChangedAt { get; set; }

        public DateTime LastCheckedAt { get; set; }
    }
}
