using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowGuildAssociations
    {
        [Key]
        public long Id { get; set; }
        public Nullable<long> ServerId { get; set; }
        public string ServerName { get; set; }
        public string WowGuild { get; set; }
        public string WowRealm { get; set; }
        public string WowRegion { get; set; }
        public string LocalRealmSlug { get; set; }
        public string Locale { get; set; }
        public string SetBy { get; set; }
        public Nullable<long> SetById { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }

        /// <summary>
        /// Last time M+ scores were bulk refreshed for this guild
        /// </summary>
        public DateTime? LastMPlusRefresh { get; set; }

        /// <summary>
        /// Number of M+ refreshes today (resets daily)
        /// </summary>
        public int MPlusRefreshCountToday { get; set; }

        /// <summary>
        /// Date for the refresh count (to detect daily reset)
        /// </summary>
        public DateTime? MPlusRefreshDate { get; set; }
    }
}