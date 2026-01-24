using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Caches item media (icons) from Blizzard API to reduce API calls.
    /// Item icons are static and never change, so cache indefinitely.
    /// </summary>
    public class ItemMediaCache
    {
        /// <summary>
        /// Blizzard item ID (used as primary key since it's unique)
        /// </summary>
        [Key]
        public long ItemId { get; set; }

        /// <summary>
        /// URL to the item icon image
        /// </summary>
        public string IconUrl { get; set; }

        /// <summary>
        /// When this entry was cached (for diagnostics/cleanup if ever needed)
        /// </summary>
        public DateTime CachedAt { get; set; }
    }
}
