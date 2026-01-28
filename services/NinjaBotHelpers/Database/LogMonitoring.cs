using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Tracks WarcraftLogs monitoring settings per Discord server.
/// Shared table with NinjaBotCore.
/// </summary>
[Table("LogMonitoring")]
public class LogMonitoring
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Discord server (guild) ID
    /// </summary>
    public long ServerId { get; set; }

    /// <summary>
    /// Discord channel ID where logs are posted
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
    /// Whether log monitoring is enabled for this server
    /// </summary>
    public bool MonitorLogs { get; set; }

    /// <summary>
    /// Legacy field - use MonitorLogs instead
    /// </summary>
    public bool WatchLog { get; set; }

    /// <summary>
    /// Latest Retail report ID that was processed
    /// </summary>
    public string? RetailReportId { get; set; }

    /// <summary>
    /// Latest Classic report ID that was processed
    /// </summary>
    public string? ClassicReportId { get; set; }

    /// <summary>
    /// Latest Vanilla report ID that was processed
    /// </summary>
    public string? VanillaReportId { get; set; }

    /// <summary>
    /// Legacy field - use RetailReportId instead
    /// </summary>
    public string? ReportId { get; set; }

    /// <summary>
    /// Legacy field - use LatestLogRetail instead
    /// </summary>
    public DateTime? LatestLog { get; set; }

    /// <summary>
    /// Timestamp of last Retail log check
    /// </summary>
    public DateTime? LatestLogRetail { get; set; }

    /// <summary>
    /// Timestamp of last Classic log check
    /// </summary>
    public DateTime? LatestLogClassic { get; set; }

    /// <summary>
    /// Timestamp of last Vanilla log check
    /// </summary>
    public DateTime? LatestLogVanilla { get; set; }
}
