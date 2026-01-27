using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// WoW mount data - shares table with NinjaBotCore
/// </summary>
[Table("WowMounts")]
public class WowMounts
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(100)]
    public string? Source { get; set; }

    [MaxLength(255)]
    public string? SourceDetail { get; set; }

    [MaxLength(255)]
    public string? DropLocation { get; set; }

    public bool IsGround { get; set; }

    public bool IsFlying { get; set; }

    public bool IsAquatic { get; set; }

    [MaxLength(20)]
    public string? Faction { get; set; }

    [MaxLength(500)]
    public string? MediaUrl { get; set; }

    public long? CreatureDisplayId { get; set; }

    public long? JournalEncounterId { get; set; }

    public long? JournalInstanceId { get; set; }

    [MaxLength(255)]
    public string? InstanceName { get; set; }

    [MaxLength(255)]
    public string? EncounterName { get; set; }

    [MaxLength(50)]
    public string? Expansion { get; set; }

    public bool IsObtainable { get; set; } = true;

    public DateTime LastUpdated { get; set; }
}
