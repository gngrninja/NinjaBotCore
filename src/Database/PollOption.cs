using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class PollOption
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        public PollOption()
        {
            this.PollVotes = new HashSet<PollVote>();
        }

        [Key]
        public long Id { get; set; }

        // Foreign key
        public long PollId { get; set; }

        // Option content
        [MaxLength(200)]
        public string OptionText { get; set; }

        public int DisplayOrder { get; set; }

        // For emoji support (optional)
        [MaxLength(50)]
        public string Emote { get; set; }

        // Relationships
        public virtual Poll Poll { get; set; }
        public virtual ICollection<PollVote> PollVotes { get; set; }
    }
}
