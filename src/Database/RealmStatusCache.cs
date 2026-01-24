using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Caches realm status to detect changes between polling cycles
    /// </summary>
    public class RealmStatusCache
    {
        [Key]
        public long Id { get; set; }

        /// <summary>
        /// Region (us, eu, kr, tw)
        /// </summary>
        public string Region { get; set; }

        /// <summary>
        /// Connected realm ID from Blizzard API
        /// </summary>
        public long ConnectedRealmId { get; set; }

        /// <summary>
        /// Display name for logging/alerts
        /// </summary>
        public string RealmName { get; set; }

        public bool IsOnline { get; set; }

        public bool HasQueue { get; set; }

        public DateTime LastCheckedAt { get; set; }
    }
}
