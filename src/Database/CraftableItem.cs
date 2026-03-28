#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    [Table("CraftableItems")]
    public class CraftableItem
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string RecipeName { get; set; } = string.Empty;

        [MaxLength(255)]
        public string? CraftedItemName { get; set; }

        public long? CraftedItemId { get; set; }

        [Required]
        [MaxLength(100)]
        public string Profession { get; set; } = string.Empty;

        public long ProfessionId { get; set; }

        [MaxLength(100)]
        public string? SkillTier { get; set; }

        [MaxLength(200)]
        public string? Category { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
