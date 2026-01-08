using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowItems")]
    public class WowItems
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string Name { get; set; }

        public int Quality { get; set; }

        [MaxLength(50)]
        public string QualityName { get; set; }

        public int ItemLevel { get; set; }

        [MaxLength(50)]
        public string InventoryType { get; set; }

        [MaxLength(100)]
        public string ItemClass { get; set; }

        [MaxLength(100)]
        public string ItemSubclass { get; set; }

        [MaxLength(500)]
        public string MediaUrl { get; set; }

        public bool IsEquippable { get; set; }

        public int RequiredLevel { get; set; }

        [MaxLength(100)]
        public string Source { get; set; }

        [MaxLength(500)]
        public string SourceDetail { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
