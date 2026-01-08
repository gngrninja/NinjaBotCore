using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("WowItemDetails")]
    public class WowItemDetails
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        public long ItemId { get; set; }

        public long? SetId { get; set; }

        [MaxLength(255)]
        public string SetName { get; set; }

        [Column(TypeName = "text")]
        public string SetEffects { get; set; }

        [Column(TypeName = "text")]
        public string BaseStats { get; set; }

        [Column(TypeName = "text")]
        public string SpellEffects { get; set; }

        public int SocketCount { get; set; }

        public DateTime LastUpdated { get; set; }

        [ForeignKey("ItemId")]
        public virtual WowItems Item { get; set; }
    }
}
