using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// Represents a request to sync static WoW data (achievements, pets, mounts).
    /// Written by bot/API, processed by NinjaBotHelpers.
    /// </summary>
    [Table("StaticDataSyncRequests")]
    public class StaticDataSyncRequest
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        /// <summary>
        /// Type of data to sync: "achievements", "pets", "mounts", or "all"
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string SyncType { get; set; } = string.Empty;

        /// <summary>
        /// Current status: "pending", "in_progress", "completed", "failed", "cancelled"
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = "pending";

        /// <summary>
        /// Discord user ID who requested the sync (null for scheduled syncs)
        /// </summary>
        public long? RequestedByUserId { get; set; }

        /// <summary>
        /// Source of the request: "slash_command", "api", "scheduled"
        /// </summary>
        [MaxLength(100)]
        public string? RequestSource { get; set; }

        /// <summary>
        /// When the request was created
        /// </summary>
        public DateTime RequestedAt { get; set; }

        /// <summary>
        /// When processing started
        /// </summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>
        /// When processing completed (success or failure)
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Error message if sync failed
        /// </summary>
        [MaxLength(500)]
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Number of items successfully processed
        /// </summary>
        public int? ItemsProcessed { get; set; }

        /// <summary>
        /// Number of items skipped (already in DB)
        /// </summary>
        public int? ItemsSkipped { get; set; }

        /// <summary>
        /// Number of items that failed to process
        /// </summary>
        public int? ItemsFailed { get; set; }
    }
}
