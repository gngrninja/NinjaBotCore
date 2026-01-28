using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Tracks which WarcraftLogs reports have been posted to Discord.
/// Used to prevent duplicate postings.
/// Shared table with NinjaBotCore.
/// </summary>
[Table("WclPosted")]
public class WclPosted
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Discord server (guild) ID
    /// </summary>
    public long ServerId { get; set; }

    /// <summary>
    /// Discord channel ID where the log was posted
    /// </summary>
    public long ChannelId { get; set; }

    /// <summary>
    /// Discord channel name (for display/debugging)
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>
    /// Discord server name (for display/debugging)
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// WarcraftLogs report ID (the unique code like "abc123XYZ")
    /// </summary>
    public string? ReportId { get; set; }
}
