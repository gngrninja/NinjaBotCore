#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class WeeklyKeyHistory
    {
        [Key]
        public long Id { get; set; }

        public long UserId { get; set; }

        [Required]
        [MaxLength(100)]
        public string WowCharacterName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string WowCharacterRealm { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string DungeonSlug { get; set; } = string.Empty;

        public DateTime WeekStartUtc { get; set; }

        public int BestKeyLevel { get; set; }

        public DateTime LastRefreshedAt { get; set; }
    }
}
