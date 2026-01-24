using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Stores guild roster members with timestamp for caching
    /// Refreshes from WoW API only if LastUpdated > 10 minutes ago
    /// </summary>
    public partial class WowGuildRosterMember
    {
        [Key]
        public long Id { get; set; }

        /// <summary>
        /// Guild name (from WoW API)
        /// </summary>
        public string GuildName { get; set; }

        /// <summary>
        /// Realm slug (e.g. "sisters-of-elune")
        /// </summary>
        public string RealmSlug { get; set; }

        /// <summary>
        /// Guild realm slug (the guild's home)
        /// </summary>
        public string GuildRealmSlug { get; set; }

        /// <summary>
        /// Guild faction
        /// </summary>
        public string Faction { get; set; }

        /// <summary>
        /// Region (us, eu, ru)
        /// </summary>
        public string Region { get; set; }

        /// <summary>
        /// Character name
        /// </summary>
        public string CharacterName { get; set; }

        /// <summary>
        /// Character level
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        /// Guild rank (0 = Guild Master)
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// When this roster data was last fetched from WoW API
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Character's equipped item level (from Armory API)
        /// </summary>
        public int? ItemLevel { get; set; }

        /// <summary>
        /// Character's Mythic+ score (from M+ Profile API)
        /// </summary>
        public double? MythicPlusScore { get; set; }

        /// <summary>
        /// Character's class ID (from guild roster API)
        /// </summary>
        public long? ClassId { get; set; }
    }
}
