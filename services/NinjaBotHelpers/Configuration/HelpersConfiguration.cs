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

    /// <summary>
    /// Static data sync settings (achievements, pets, mounts)
    /// </summary>
    public StaticDataSyncSettings StaticDataSync { get; set; } = new();
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

/// <summary>
/// Settings for static data sync (achievements, pets, mounts)
/// </summary>
public class StaticDataSyncSettings
{
    /// <summary>
    /// Whether the static data sync is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to sync static data (days)
    /// </summary>
    public int SyncIntervalDays { get; set; } = 30;

    /// <summary>
    /// Delay before first sync after startup (seconds)
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Delay between API calls to avoid rate limiting (milliseconds)
    /// </summary>
    public int ApiCallDelayMs { get; set; } = 100;

    /// <summary>
    /// Data source for item sync: "auto", "wago", or "blizzard".
    /// - "auto" (default): Try wago.tools first, fall back to Blizzard API on failure
    /// - "wago": Use wago.tools only (faster, ~171k items in one request)
    /// - "blizzard": Use Blizzard API only (paginated, ~175+ API calls)
    /// </summary>
    public string ItemDataSource { get; set; } = "auto";

    /// <summary>
    /// Timeout in seconds for wago.tools requests (default 120s for ~10MB download)
    /// </summary>
    public int WagoTimeoutSeconds { get; set; } = 120;
}
