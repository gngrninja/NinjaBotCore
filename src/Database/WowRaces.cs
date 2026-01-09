using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowRaces")]
    public class WowRaces
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string Name { get; set; }

        [MaxLength(20)]
        public string Faction { get; set; }

        public bool IsPlayable { get; set; }

        public bool IsAlliedRace { get; set; }

        [MaxLength(500)]
        public string MediaUrl { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
