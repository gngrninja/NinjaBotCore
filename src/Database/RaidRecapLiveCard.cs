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

        /// <summary>
        /// Where in the log this card's raid begins: the start of its first raid pull, in
        /// milliseconds from the start of the log. One log can hold several raids, for example
        /// two zones in one night or several nights appended, and each gets its own card.
        /// Cards made before this existed hold 0, meaning the first raid in the log.
        /// </summary>
        public long SessionStartMs { get; set; }

        /// <summary>The in-game zone of this card's raid, when WarcraftLogs reported it.</summary>
        public long? ZoneId { get; set; }

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
