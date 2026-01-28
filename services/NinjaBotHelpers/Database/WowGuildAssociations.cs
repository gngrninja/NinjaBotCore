using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Discord server to WoW guild association.
/// Shared table with NinjaBotCore.
/// </summary>
[Table("WowGuildAssociations")]
public class WowGuildAssociations
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
    /// WoW guild name
    /// </summary>
    public string? WowGuild { get; set; }

    /// <summary>
    /// WoW realm display name (e.g., "Sisters of Elune")
    /// </summary>
    public string? WowRealm { get; set; }

    /// <summary>
    /// WoW region (us, eu, kr, tw, cn)
    /// </summary>
    public string? WowRegion { get; set; }

    /// <summary>
    /// WoW realm slug for API calls (e.g., "sisters-of-elune")
    /// </summary>
    public string? LocalRealmSlug { get; set; }

    /// <summary>
    /// Locale for API calls (e.g., "en_US")
    /// </summary>
    public string? Locale { get; set; }

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
