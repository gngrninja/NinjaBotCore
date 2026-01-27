using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// WoW achievement criteria - shares table with NinjaBotCore
/// </summary>
[Table("WowAchievementCriteria")]
public class WowAchievementCriteria
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Id { get; set; }

    public long AchievementId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public int OrderIndex { get; set; }

    public int Amount { get; set; }

    public bool IsCompleted { get; set; }

    public DateTime LastUpdated { get; set; }

    [ForeignKey("AchievementId")]
    public virtual WowAchievements? Achievement { get; set; }
}
