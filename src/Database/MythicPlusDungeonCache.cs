#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Cached M+ dungeon rotation pulled from Raider.IO's static-data endpoint.
    /// Refreshed periodically by MythicPlusDungeonService; persists so the pool is
    /// available instantly on restart (and survives brief Raider.IO outages).
    /// </summary>
    public class MythicPlusDungeonCache
    {
        /// <summary>Raider.IO dungeon slug (natural key, e.g. "algethar-academy").</summary>
        [Key]
        [MaxLength(100)]
        public string Slug { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(20)]
        public string ShortName { get; set; } = string.Empty;

        /// <summary>Slug of the season this pool belongs to (e.g. "season-mn-1").</summary>
        [Required]
        [MaxLength(50)]
        public string SeasonSlug { get; set; } = string.Empty;

        public DateTime CachedAt { get; set; }
    }
}
