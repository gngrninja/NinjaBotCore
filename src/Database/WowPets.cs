using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowPets")]
    public class WowPets
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        public long? SpeciesId { get; set; }

        public long? CreatureId { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; }

        [MaxLength(1000)]
        public string Description { get; set; }

        [MaxLength(50)]
        public string PetType { get; set; } // "Beast", "Mechanical", "Elemental", etc.

        [MaxLength(100)]
        public string Source { get; set; } // "Drop", "Vendor", "Achievement", "Wild", "Quest", etc.

        [MaxLength(255)]
        public string SourceDetail { get; set; } // Boss name, vendor name, zone, etc.

        [MaxLength(100)]
        public string SourceZone { get; set; } // Zone where pet can be found

        public bool IsCapturable { get; set; } // Can be caught in wild

        public bool IsTradable { get; set; }

        public bool IsBattlePet { get; set; }

        [MaxLength(20)]
        public string Faction { get; set; } // "Alliance", "Horde", "Both"

        [MaxLength(255)]
        public string MediaUrl { get; set; }

        [MaxLength(255)]
        public string IconUrl { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
