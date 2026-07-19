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

        /// <summary>
        /// How many completed runs of this dungeon appear in the character's weekly top-10
        /// list. Summed across dungeons this gives Great Vault progress (thresholds 1/4/8
        /// all sit inside the top-10 window raider.io exposes).
        /// </summary>
        public int RunCount { get; set; }

        public DateTime LastRefreshedAt { get; set; }
    }
}
