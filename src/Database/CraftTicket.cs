#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class CraftTicket
    {
        [Key]
        public long Id { get; set; }

        [Required]
        [MaxLength(200)]
        public string ItemName { get; set; } = string.Empty;

        public long? BlizzardItemId { get; set; }

        [MaxLength(500)]
        public string? ItemIconUrl { get; set; }

        [MaxLength(100)]
        public string? Profession { get; set; }

        [MaxLength(500)]
        public string? Note { get; set; }

        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = "Open";

        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? ClaimedAt { get; set; }
        public DateTime? CraftedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public long RequesterId { get; set; }

        [Required]
        [MaxLength(100)]
        public string RequesterName { get; set; } = string.Empty;

        public long? CrafterId { get; set; }

        [MaxLength(100)]
        public string? CrafterName { get; set; }

        public long GuildId { get; set; }
        public long ChannelId { get; set; }
        public long MessageId { get; set; }
        public long? ThreadId { get; set; }
        public long? ThreadMessageId { get; set; }

        /// <summary>
        /// Desired quality (e.g., "Max quality", "Rank 5", "Any rank")
        /// </summary>
        [MaxLength(100)]
        public string? QualityDesired { get; set; }

        /// <summary>
        /// Materials status (e.g., "Have all mats", "Need some mats")
        /// </summary>
        [MaxLength(100)]
        public string? MaterialsStatus { get; set; }

        /// <summary>
        /// Commission/tip offered (e.g., "5k tip", "negotiable")
        /// </summary>
        [MaxLength(100)]
        public string? Commission { get; set; }

        /// <summary>
        /// Requester's WoW realm (from character association). Null if no character linked.
        /// </summary>
        [MaxLength(100)]
        public string? RequesterRealm { get; set; }

        /// <summary>
        /// Comma-separated connected realm names for personal order eligibility.
        /// Null if no character linked or lookup failed.
        /// </summary>
        [MaxLength(2000)]
        public string? ConnectedRealms { get; set; }
    }
}
