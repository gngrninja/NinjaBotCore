#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// A user's self-reported current keystone (their main's key). One row per user —
    /// no game API exposes the held keystone, so members register it themselves via
    /// /keys board. Rows from a previous reset window are treated as expired.
    /// </summary>
    public class UserKeystone
    {
        [Key]
        public long UserId { get; set; }

        [MaxLength(100)]
        public string? CharacterName { get; set; }

        [Required]
        [MaxLength(100)]
        public string DungeonSlug { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string DungeonName { get; set; } = string.Empty;

        public int KeyLevel { get; set; }

        /// <summary>Reset window the key belongs to; stale rows are ignored/cleared.</summary>
        public DateTime WeekStartUtc { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}
