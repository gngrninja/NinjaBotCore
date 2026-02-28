using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowRealms")]
    public class WowRealms
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; }

        [Required]
        [MaxLength(100)]
        public string Slug { get; set; }

        [Required]
        [MaxLength(10)]
        public string Region { get; set; }

        [MaxLength(50)]
        public string Timezone { get; set; }

        [MaxLength(20)]
        public string Type { get; set; }

        [MaxLength(20)]
        public string Population { get; set; }

        public long? ConnectedRealmId { get; set; }

        [MaxLength(10)]
        public string Locale { get; set; }

        public bool IsTournament { get; set; }

        public DateTime LastUpdated { get; set; }

        [MaxLength(20)]
        public string GameVersion { get; set; }
    }
}
