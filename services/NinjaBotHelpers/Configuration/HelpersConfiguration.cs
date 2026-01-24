namespace NinjaBotHelpers.Configuration;

/// <summary>
/// Configuration for NinjaBotHelpers service
/// </summary>
public class HelpersConfiguration
{
    /// <summary>
    /// Discord bot token for REST API calls
    /// </summary>
    public string DiscordToken { get; set; } = string.Empty;

    /// <summary>
    /// Blizzard API client ID
    /// </summary>
    public string BlizzardClientId { get; set; } = string.Empty;

    /// <summary>
    /// Blizzard API client secret
    /// </summary>
    public string BlizzardClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// PostgreSQL connection string
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// RealmWatcher specific settings
    /// </summary>
    public RealmWatcherSettings RealmWatcher { get; set; } = new();
}

public class RealmWatcherSettings
{
    /// <summary>
    /// Whether the realm watcher is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to check realm status (seconds)
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Delay before first check after startup (seconds)
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Delay between API calls to avoid rate limiting (milliseconds)
    /// </summary>
    public int ApiCallDelayMs { get; set; } = 100;
}
