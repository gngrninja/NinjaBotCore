using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowTokenPrices")]
    public class WowTokenPrices
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        [MaxLength(10)]
        public string Region { get; set; }

        public long Price { get; set; }

        public DateTime Timestamp { get; set; }
    }
}
