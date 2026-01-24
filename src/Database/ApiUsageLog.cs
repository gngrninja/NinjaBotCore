using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Tracks API usage per operation for auditing and potential billing.
    /// One row per operation (not per API call) - lightweight.
    /// </summary>
    public class ApiUsageLog
    {
        [Key]
        public long Id { get; set; }

        /// <summary>
        /// Discord server ID
        /// </summary>
        public long GuildId { get; set; }

        /// <summary>
        /// Discord user who triggered the operation
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// Operation type: RosterRefresh, CharLookup, RealmStatus, etc.
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Operation { get; set; }

        /// <summary>
        /// Estimated API calls for this operation
        /// </summary>
        public int ApiCallCount { get; set; }

        /// <summary>
        /// WoW guild name (for roster operations)
        /// </summary>
        [MaxLength(100)]
        public string WowGuild { get; set; }

        /// <summary>
        /// WoW realm slug
        /// </summary>
        [MaxLength(100)]
        public string WowRealm { get; set; }

        /// <summary>
        /// WoW region (us, eu, etc.)
        /// </summary>
        [MaxLength(10)]
        public string WowRegion { get; set; }

        /// <summary>
        /// Character name (for char lookups)
        /// </summary>
        [MaxLength(50)]
        public string CharacterName { get; set; }

        public DateTime Timestamp { get; set; }
    }
}
