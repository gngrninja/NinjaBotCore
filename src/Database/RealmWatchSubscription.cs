using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Stores realm watch subscriptions for status alerts
    /// Users can receive alerts when realms go online/offline or have queue changes
    /// </summary>
    public class RealmWatchSubscription
    {
        [Key]
        public long Id { get; set; }

        /// <summary>
        /// Discord guild ID where this subscription was created
        /// </summary>
        public long GuildId { get; set; }

        /// <summary>
        /// Channel ID for alerts (null = DM only)
        /// </summary>
        public long? ChannelId { get; set; }

        /// <summary>
        /// User ID for DM alerts (null = channel only)
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// Realm slug (e.g., "area-52")
        /// </summary>
        public string RealmSlug { get; set; }

        /// <summary>
        /// Realm display name (e.g., "Area 52")
        /// </summary>
        public string RealmName { get; set; }

        /// <summary>
        /// Region (us, eu, kr, tw)
        /// </summary>
        public string Region { get; set; }

        /// <summary>
        /// Connected realm ID from Blizzard API (for status lookups)
        /// </summary>
        public long ConnectedRealmId { get; set; }

        /// <summary>
        /// Alert when realm comes online
        /// </summary>
        public bool AlertOnOnline { get; set; } = true;

        /// <summary>
        /// Alert when realm goes offline
        /// </summary>
        public bool AlertOnOffline { get; set; } = true;

        /// <summary>
        /// Alert on queue changes
        /// </summary>
        public bool AlertOnQueue { get; set; } = true;

        /// <summary>
        /// When subscription was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Last time an alert was sent for this subscription
        /// </summary>
        public DateTime? LastAlertAt { get; set; }
    }
}
