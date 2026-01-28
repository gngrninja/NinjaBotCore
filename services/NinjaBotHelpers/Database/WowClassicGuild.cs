using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Discord server to WoW Classic guild association.
/// Shared table with NinjaBotCore.
/// </summary>
[Table("WowClassicGuild")]
public class WowClassicGuild
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Discord server (guild) ID
    /// </summary>
    public long? ServerId { get; set; }

    /// <summary>
    /// Discord server name
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// WoW Classic guild name
    /// </summary>
    public string? WowGuild { get; set; }

    /// <summary>
    /// WoW Classic realm name
    /// </summary>
    public string? WowRealm { get; set; }

    /// <summary>
    /// WoW region (us, eu, kr, tw)
    /// </summary>
    public string? WowRegion { get; set; }

    /// <summary>
    /// Who set this association
    /// </summary>
    public string? SetBy { get; set; }

    /// <summary>
    /// Discord user ID who set this
    /// </summary>
    public long? SetById { get; set; }

    /// <summary>
    /// When the association was set
    /// </summary>
    public DateTime? TimeSet { get; set; }
}
