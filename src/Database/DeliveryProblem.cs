#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// A feature that Discord stopped letting the bot post for, such as New log posts in a
    /// deleted channel. Recorded by the bot and the helpers, reviewed by the bot owner on the
    /// admin page, and resolved by the next post that works.
    /// </summary>
    public class DeliveryProblem
    {
        [Key]
        public long Id { get; set; }

        public long ServerId { get; set; }

        /// <summary>One of <see cref="DeliveryFeature"/>.</summary>
        [MaxLength(32)]
        public string Feature { get; set; } = "";

        /// <summary>The channel the feature posts in, or 0 when it has none.</summary>
        public long ChannelId { get; set; }

        /// <summary>One of <see cref="DeliveryProblemKind"/>.</summary>
        [MaxLength(32)]
        public string Problem { get; set; } = "";

        public int? DiscordCode { get; set; }

        public DateTime FirstSeenAt { get; set; }

        public DateTime LastSeenAt { get; set; }

        public int Failures { get; set; }

        public DateTime? ResolvedAt { get; set; }

        /// <summary>One of <see cref="DeliveryResolution"/>.</summary>
        [MaxLength(32)]
        public string? Resolution { get; set; }

        public DateTime? DismissedAt { get; set; }

        /// <summary>When the last DM about this problem was delivered, and to whom.</summary>
        public DateTime? NotifiedAt { get; set; }

        public long? NotifiedUserId { get; set; }

        /// <summary>"setter" or "owner".</summary>
        [MaxLength(16)]
        public string? NotifiedRole { get; set; }

        /// <summary>The owner's last DM attempt, delivered or not, so a failure never hides who was told.</summary>
        public DateTime? LastAttemptAt { get; set; }

        [MaxLength(16)]
        public string? LastAttemptRole { get; set; }

        /// <summary>One of <see cref="DeliveryNotifyResult"/>.</summary>
        [MaxLength(32)]
        public string? LastAttemptResult { get; set; }
    }

    public static class DeliveryFeature
    {
        public const string NewLog = "new_log";
        public const string Welcome = "welcome";
        public const string Leave = "leave";
        public const string ModerationLog = "moderation_log";
        public const string RealmAlert = "realm_alert";
        public const string WordFilter = "word_filter";

        public static readonly string[] All = { NewLog, Welcome, Leave, ModerationLog, RealmAlert, WordFilter };
    }

    public static class DeliveryProblemKind
    {
        /// <summary>The channel was deleted (10003) or is no longer in the server.</summary>
        public const string ChannelMissing = "channel_missing";

        /// <summary>The bot can't see the channel (50001).</summary>
        public const string NoAccess = "no_access";

        /// <summary>The bot lacks a permission it needs there (50013).</summary>
        public const string MissingPermissions = "missing_permissions";

        public const string Other = "other";

        public static string FromDiscordCode(int? code) => code switch
        {
            10003 => ChannelMissing,
            50001 => NoAccess,
            50013 => MissingPermissions,
            _ => Other
        };
    }

    public static class DeliveryResolution
    {
        /// <summary>A later post worked.</summary>
        public const string Delivered = "delivered";

        /// <summary>The owner's re-check found the bot can post again.</summary>
        public const string Fixed = "fixed";

        public const string TurnedOff = "turned_off";

        /// <summary>The server moved the feature to another channel, so this one no longer matters.</summary>
        public const string Moved = "moved";

        /// <summary>The server turned the feature off itself.</summary>
        public const string FeatureOff = "feature_off";
    }

    public static class DeliveryNotifyResult
    {
        public const string Sent = "sent";
        public const string DmsClosed = "dms_closed";
        public const string NotInServer = "not_in_server";
        public const string Failed = "failed";
    }
}
