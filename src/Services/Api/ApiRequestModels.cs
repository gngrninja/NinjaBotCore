using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NinjaBotCore.Services.Api
{
    /// <summary>
    /// Request body for the refresh-roster endpoint.
    /// </summary>
    public record RefreshRosterRequest(string DiscordGuildId);

    /// <summary>
    /// Request body for WCL cache invalidation (called by NinjaBotHelpers when new log detected).
    /// </summary>
    public record WclCacheInvalidateRequest(string GuildName, string RealmSlug, string Region);

    /// <summary>
    /// Request body for the add-character endpoint.
    /// </summary>
    public record AddCharacterRequest(
        string DiscordUserId,
        string? DiscordServerId,
        string CharacterName,
        string Realm,
        string? Region
    );

    /// <summary>
    /// Request body for the vote poll endpoint.
    /// </summary>
    public record VotePollRequest(
        string? UserId,
        string? OptionId,
        string? UserName
    );

    /// <summary>
    /// Request body for the close poll endpoint.
    /// </summary>
    public record ClosePollRequest(string? UserId);
    public record DeletePollRequest(string? UserId, string? GuildId);
    public record CleanupPollsRequest(string? UserId);

    /// <summary>
    /// Request body for the create poll endpoint.
    /// </summary>
    public record CreatePollRequest(
        string? GuildId,
        string? ChannelId,
        string? UserId,
        string? Question,
        List<string>? Options,
        string? Duration,
        bool? AllowVoteChange,
        bool? IsAnonymous,
        bool? AllowMultipleSelections
    );

    /// <summary>
    /// Request body for updating poll settings.
    /// </summary>
    public record UpdatePollSettingsRequest(
        [property: JsonPropertyName("results_channel_id")] string? ResultsChannelId,
        [property: JsonPropertyName("mention_voters_on_close")] bool? MentionVotersOnClose,
        [property: JsonPropertyName("default_anonymous")] bool? DefaultAnonymous,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("user_name")] string? UserName
    );

    /// <summary>
    /// Request body for updating log monitoring settings.
    /// </summary>
    public record UpdateLogMonitoringRequest(
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("channel_name")] string? ChannelName,
        [property: JsonPropertyName("monitor_logs")] bool? MonitorLogs,
        [property: JsonPropertyName("server_name")] string? ServerName
    );

    /// <summary>
    /// Request body for updating greeting settings.
    /// </summary>
    public record UpdateGreetingSettingsRequest(
        [property: JsonPropertyName("greet_users")] bool? GreetUsers,
        [property: JsonPropertyName("part_users")] bool? PartUsers,
        [property: JsonPropertyName("greeting")] string? Greeting,
        [property: JsonPropertyName("greeting_channel_id")] string? GreetingChannelId,
        [property: JsonPropertyName("greeting_channel_name")] string? GreetingChannelName,
        [property: JsonPropertyName("parting_message")] string? PartingMessage,
        [property: JsonPropertyName("parting_channel_id")] string? PartingChannelId,
        [property: JsonPropertyName("set_by_id")] string? SetById,
        [property: JsonPropertyName("set_by_name")] string? SetByName
    );

    /// <summary>
    /// Request body for updating moderation watcher settings.
    /// </summary>
    public record UpdateModerationWatcherRequest(
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("channel_name")] string? ChannelName,
        [property: JsonPropertyName("watch_voice")] bool? WatchVoice,
        [property: JsonPropertyName("watch_messages")] bool? WatchMessages,
        [property: JsonPropertyName("watch_roles")] bool? WatchRoles,
        [property: JsonPropertyName("watch_bans")] bool? WatchBans,
        [property: JsonPropertyName("watch_nicknames")] bool? WatchNicknames,
        [property: JsonPropertyName("set_by_id")] string? SetById,
        [property: JsonPropertyName("set_by_name")] string? SetByName
    );

    /// <summary>
    /// Request body for updating WoW guild association.
    /// </summary>
    public record UpdateWowAssociationRequest(
        [property: JsonPropertyName("wow_guild_name")] string? WowGuildName,
        [property: JsonPropertyName("wow_realm")] string? WowRealm,
        [property: JsonPropertyName("wow_realm_slug")] string? WowRealmSlug,
        [property: JsonPropertyName("wow_region")] string? WowRegion,
        [property: JsonPropertyName("locale")] string? Locale,
        [property: JsonPropertyName("server_name")] string? ServerName,
        [property: JsonPropertyName("set_by_id")] string? SetById,
        [property: JsonPropertyName("set_by_name")] string? SetByName
    );

    /// <summary>
    /// Request body for updating away status.
    /// </summary>
    public record UpdateAwayStatusRequest(
        [property: JsonPropertyName("user_name")] string? UserName,
        [property: JsonPropertyName("is_away")] bool? IsAway,
        [property: JsonPropertyName("message")] string? Message
    );

    /// <summary>
    /// Request body for setting a character as main.
    /// </summary>
    public record SetMainCharacterRequest(
        [property: JsonPropertyName("user_id")] string? UserId
    );

    /// <summary>
    /// Request body for adding a realm watch.
    /// </summary>
    public record AddRealmWatchRequest(
        [property: JsonPropertyName("realm_slug")] string? RealmSlug,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("alert_on_online")] bool? AlertOnOnline,
        [property: JsonPropertyName("alert_on_offline")] bool? AlertOnOffline,
        [property: JsonPropertyName("alert_on_queue")] bool? AlertOnQueue
    );

    /// <summary>
    /// Request body for updating an existing realm watch subscription.
    /// </summary>
    public record UpdateRealmWatchRequest(
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("alert_on_online")] bool? AlertOnOnline,
        [property: JsonPropertyName("alert_on_offline")] bool? AlertOnOffline,
        [property: JsonPropertyName("alert_on_queue")] bool? AlertOnQueue
    );

    /// <summary>
    /// Request body for triggering a static data sync.
    /// </summary>
    public record TriggerSyncRequest(
        [property: JsonPropertyName("sync_type")] string? SyncType,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("source")] string? Source
    );
}
