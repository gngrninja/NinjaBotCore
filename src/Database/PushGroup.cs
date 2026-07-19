#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class PushGroup
    {
        public PushGroup()
        {
            Signups = new HashSet<PushGroupSignup>();
        }

        [Key]
        public long Id { get; set; }

        public long GuildId { get; set; }
        public long ChannelId { get; set; }
        public long MessageId { get; set; }
        public long? FollowupMessageId { get; set; }

        public long CreatorUserId { get; set; }

        [Required]
        [MaxLength(100)]
        public string CreatorUserName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string DungeonSlug { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string DungeonName { get; set; } = string.Empty;

        public int TargetKeyLevel { get; set; }

        public decimal? IoRatingTarget { get; set; }
        public decimal? IoRatingMin { get; set; }
        public decimal? IoRatingMax { get; set; }

        public long? KeyHolderUserId { get; set; }

        [MaxLength(100)]
        public string? KeyHolderDungeonName { get; set; }

        public int? KeyHolderKeyLevel { get; set; }

        public DateTime? ScheduledForUtc { get; set; }

        /// <summary>Set once the T-15min start reminder has been sent (dedupe guard).</summary>
        public DateTime? ReminderSentAt { get; set; }

        [Required]
        [MaxLength(30)]
        public string Status { get; set; } = "Open";

        [MaxLength(500)]
        public string? Notes { get; set; }

        [MaxLength(10)]
        public string? Region { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? ArchivedAt { get; set; }

        public virtual ICollection<PushGroupSignup> Signups { get; set; }
    }
}
