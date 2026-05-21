#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    public class PushGroupSignup
    {
        [Key]
        public long Id { get; set; }

        public long PushGroupId { get; set; }

        [ForeignKey(nameof(PushGroupId))]
        public virtual PushGroup? PushGroup { get; set; }

        public long UserId { get; set; }

        [Required]
        [MaxLength(100)]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [MaxLength(10)]
        public string RoleSlot { get; set; } = "DPS";

        public int SlotIndex { get; set; }

        [MaxLength(100)]
        public string? WowCharacterName { get; set; }

        [MaxLength(100)]
        public string? WowCharacterRealm { get; set; }

        [MaxLength(50)]
        public string? WowClass { get; set; }

        [MaxLength(50)]
        public string? WowSpec { get; set; }

        public decimal? IoRating { get; set; }

        public int? IoBestThisWeek { get; set; }

        public DateTime SignedUpAt { get; set; }

        public DateTime? WithdrewAt { get; set; }
    }
}
