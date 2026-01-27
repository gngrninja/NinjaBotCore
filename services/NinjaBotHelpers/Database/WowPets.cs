using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// WoW pet data - shares table with NinjaBotCore
/// </summary>
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
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(50)]
    public string? PetType { get; set; }

    [MaxLength(100)]
    public string? Source { get; set; }

    [MaxLength(255)]
    public string? SourceDetail { get; set; }

    [MaxLength(100)]
    public string? SourceZone { get; set; }

    public bool IsCapturable { get; set; }

    public bool IsTradable { get; set; }

    public bool IsBattlePet { get; set; }

    [MaxLength(20)]
    public string? Faction { get; set; }

    [MaxLength(255)]
    public string? MediaUrl { get; set; }

    [MaxLength(255)]
    public string? IconUrl { get; set; }

    public DateTime LastUpdated { get; set; }
}
