using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowPlayableClasses")]
    public class WowPlayableClass
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string Name { get; set; }

        [MaxLength(50)]
        public string PowerType { get; set; }

        [MaxLength(500)]
        public string MediaUrl { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
