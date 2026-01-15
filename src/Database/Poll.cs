using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class Poll
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        public Poll()
        {
            this.PollOptions = new HashSet<PollOption>();
            this.PollVotes = new HashSet<PollVote>();
        }

        [Key]
        public long Id { get; set; }

        // Poll content
        [MaxLength(500)]
        public string Question { get; set; }

        [MaxLength(50)]
        public string PollType { get; set; } // "YesNo", "SingleChoice", "MultipleChoice"

        // Settings
        public bool AllowVoteChange { get; set; } = true;
        public bool IsAnonymous { get; set; } = false;
        public bool IsClosed { get; set; } = false;

        // Timing
        public DateTime CreatedAt { get; set; }
        public Nullable<DateTime> ExpiresAt { get; set; }
        public Nullable<DateTime> ClosedAt { get; set; }

        // Creator tracking
        public long CreatedById { get; set; }

        [MaxLength(100)]
        public string CreatedByName { get; set; }

        // Discord context
        public long GuildId { get; set; }
        public long ChannelId { get; set; }
        public long MessageId { get; set; }

        // Role restrictions (comma-separated role IDs, null = everyone can vote)
        [MaxLength(1000)]
        public string AllowedRoleIds { get; set; }

        // Relationships
        public virtual ICollection<PollOption> PollOptions { get; set; }
        public virtual ICollection<PollVote> PollVotes { get; set; }
    }
}
