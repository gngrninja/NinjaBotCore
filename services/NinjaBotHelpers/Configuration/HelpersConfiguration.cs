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

    /// <summary>
    /// WarcraftLogs V2 API client ID
    /// </summary>
    public string WclClientId { get; set; } = string.Empty;

    /// <summary>
    /// WarcraftLogs V2 API client secret
    /// </summary>
    public string WclClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Log monitoring settings (auto-poster for WarcraftLogs)
    /// </summary>
    public LogMonitoringSettings LogMonitoring { get; set; } = new();
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

/// <summary>
/// Settings for WarcraftLogs auto-poster background service
/// </summary>
public class LogMonitoringSettings
{
    /// <summary>
    /// Whether log monitoring is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to check for new logs (minutes)
    /// </summary>
    public int CheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Delay before first check after startup (seconds)
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 90;

    /// <summary>
    /// Days since last activity for Tier 1 (most frequent) checking
    /// </summary>
    public int Tier1ThresholdDays { get; set; } = 14;

    /// <summary>
    /// Days since last activity for Tier 2 (medium) checking
    /// </summary>
    public int Tier2ThresholdDays { get; set; } = 30;

    /// <summary>
    /// Check interval for Tier 1 guilds (minutes) - active guilds
    /// </summary>
    public int Tier1IntervalMinutes { get; set; } = 20;

    /// <summary>
    /// Check interval for Tier 2 guilds (hours) - semi-active guilds
    /// </summary>
    public int Tier2IntervalHours { get; set; } = 3;

    /// <summary>
    /// Check interval for Tier 3 guilds (hours) - inactive guilds
    /// </summary>
    public int Tier3IntervalHours { get; set; } = 24;

    /// <summary>
    /// Base URL for the main bot's Commands API (for cache invalidation).
    /// Example: "http://localhost:5100" or "http://ninjabot:5100" in Docker.
    /// Leave empty to disable cache invalidation calls.
    /// </summary>
    public string BotApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key for the main bot's Commands API (must match the bot's configured API key)
    /// </summary>
    public string BotApiKey { get; set; } = string.Empty;
}
