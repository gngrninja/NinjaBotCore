using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Tracks the current sync status for each data type.
/// Updated by NinjaBotHelpers after each sync.
/// </summary>
[Table("StaticDataSyncStatus")]
public class StaticDataSyncStatus
{
    /// <summary>
    /// Type of data: "achievements", "pets", "mounts"
    /// </summary>
    [Key]
    [MaxLength(50)]
    public string SyncType { get; set; } = string.Empty;

    /// <summary>
    /// When the last sync started
    /// </summary>
    public DateTime? LastSyncStarted { get; set; }

    /// <summary>
    /// When the last sync completed
    /// </summary>
    public DateTime? LastSyncCompleted { get; set; }

    /// <summary>
    /// Number of items processed in the last sync
    /// </summary>
    public int? LastSyncItemCount { get; set; }

    /// <summary>
    /// Total number of items currently in the database
    /// </summary>
    public int? TotalItemsInDatabase { get; set; }

    /// <summary>
    /// Result of last sync: "success", "failed", "partial"
    /// </summary>
    [MaxLength(20)]
    public string? LastSyncStatus { get; set; }

    /// <summary>
    /// When the next scheduled sync will occur
    /// </summary>
    public DateTime? NextScheduledSync { get; set; }
}
