using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// WoW achievement data - shares table with NinjaBotCore
/// </summary>
[Table("WowAchievements")]
public class WowAchievements
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public int Points { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    public long? CategoryId { get; set; }

    [MaxLength(100)]
    public string? ParentCategory { get; set; }

    public bool IsAccountWide { get; set; }

    [MaxLength(500)]
    public string? RewardDescription { get; set; }

    public long? RewardItemId { get; set; }

    public long? RewardMountId { get; set; }

    [MaxLength(100)]
    public string? RewardTitle { get; set; }

    [MaxLength(20)]
    public string? Faction { get; set; }

    [MaxLength(255)]
    public string? MediaUrl { get; set; }

    public int DisplayOrder { get; set; }

    public DateTime LastUpdated { get; set; }

    public virtual ICollection<WowAchievementCriteria> Criteria { get; set; } = new List<WowAchievementCriteria>();
}
