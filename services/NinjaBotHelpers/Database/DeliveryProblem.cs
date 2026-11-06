using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotHelpers.Database;

/// <summary>
/// A feature Discord stopped letting the bot post for, reviewed by the bot owner on the admin
/// page. Shared table with NinjaBotCore, which owns its migrations and the owner's actions.
/// </summary>
[Table("DeliveryProblems")]
public class DeliveryProblem
{
    [Key]
    public long Id { get; set; }

    public long ServerId { get; set; }

    [MaxLength(32)]
    public string Feature { get; set; } = "";

    /// <summary>The channel the feature posts in, or 0 when it has none.</summary>
    public long ChannelId { get; set; }

    [MaxLength(32)]
    public string Problem { get; set; } = "";

    public int? DiscordCode { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public int Failures { get; set; }

    public DateTime? ResolvedAt { get; set; }

    [MaxLength(32)]
    public string? Resolution { get; set; }

    public DateTime? DismissedAt { get; set; }

    public DateTime? NotifiedAt { get; set; }

    public long? NotifiedUserId { get; set; }

    [MaxLength(16)]
    public string? NotifiedRole { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    [MaxLength(16)]
    public string? LastAttemptRole { get; set; }

    [MaxLength(32)]
    public string? LastAttemptResult { get; set; }
}

/// <summary>The same keys NinjaBotCore uses for the features the helpers post for.</summary>
public static class DeliveryFeature
{
    public const string NewLog = "new_log";
    public const string RealmAlert = "realm_alert";
}
