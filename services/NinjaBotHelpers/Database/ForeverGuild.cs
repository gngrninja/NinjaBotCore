using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// The WoW Forever guild a Discord server is linked to with <c>/forever guild set</c>.
/// Shared table with NinjaBotCore; only the columns the log watcher reads are mapped.
/// </summary>
[Table("ForeverGuilds")]
public class ForeverGuild
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long DiscordGuildId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>NinjaBotCore's ForeverRegion: 0 US, 1 EU, 2 KR, 3 TW.</summary>
    public int Region { get; set; }

    /// <summary>When the link was last made or changed.</summary>
    public DateTime? TimeSet { get; set; }
}
