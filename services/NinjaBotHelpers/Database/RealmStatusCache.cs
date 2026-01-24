using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Caches realm status to detect changes between polling cycles
/// </summary>
[Table("RealmStatusCache")]
public class RealmStatusCache
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Region (us, eu, kr, tw)
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Connected realm ID from Blizzard API
    /// </summary>
    public long ConnectedRealmId { get; set; }

    /// <summary>
    /// Display name for logging/alerts
    /// </summary>
    public string RealmName { get; set; } = string.Empty;

    public bool IsOnline { get; set; }

    public bool HasQueue { get; set; }

    public DateTime LastCheckedAt { get; set; }
}
