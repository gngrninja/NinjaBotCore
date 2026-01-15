using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class PollVote
    {
        [Key]
        public long Id { get; set; }

        // Foreign keys
        public long PollId { get; set; }
        public long OptionId { get; set; }

        // Voter tracking
        public long UserId { get; set; }

        [MaxLength(100)]
        public string UserName { get; set; }

        // Timing
        public DateTime VotedAt { get; set; }

        // Relationships
        public virtual Poll Poll { get; set; }
        public virtual PollOption PollOption { get; set; }
    }
}
