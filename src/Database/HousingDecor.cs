using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Static data for WoW housing decor items.
    /// Synced from Blizzard API /data/wow/decor endpoints.
    /// </summary>
    [Table("HousingDecor")]
    public class HousingDecor
    {
        /// <summary>
        /// Blizzard decor ID (not auto-generated)
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        /// <summary>
        /// Decor item name
        /// </summary>
        [Required]
        [MaxLength(255)]
        public string Name { get; set; }

        /// <summary>
        /// Linked WoW item ID (if any)
        /// </summary>
        public long? LinkedItemId { get; set; }

        /// <summary>
        /// Icon URL from item media API
        /// </summary>
        [MaxLength(500)]
        public string IconUrl { get; set; }

        /// <summary>
        /// When this record was last updated from the API
        /// </summary>
        public DateTime LastUpdated { get; set; }
    }
}
