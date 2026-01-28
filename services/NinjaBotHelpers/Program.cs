using Microsoft.EntityFrameworkCore;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.Wago;
using NinjaBotHelpers.WarcraftLogs;
using NinjaBotHelpers.Workers;
using Serilog;
using System.Net.Http.Headers;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/helpers-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Starting NinjaBotHelpers...");

    var builder = Host.CreateApplicationBuilder(args);

    // Add environment variables with NINJABOT_ prefix (same as main bot)
    builder.Configuration.AddEnvironmentVariables(prefix: "NINJABOT_");

    // Add Serilog
    builder.Services.AddSerilog();

    // Load configuration
    var config = LoadConfiguration(builder.Configuration);
    builder.Services.AddSingleton(config);

    // Database
    builder.Services.AddDbContext<HelpersDbContext>(options =>
    {
        options.UseNpgsql(config.ConnectionString);
    });

    // HTTP clients
    builder.Services.AddHttpClient<DiscordRestClient>(client =>
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bot", config.DiscordToken);
        client.DefaultRequestHeaders.Add("User-Agent", "NinjaBotHelpers/1.0");
    });

    builder.Services.AddHttpClient<BlizzardApiClient>();

    // Register services as singletons (they use the HttpClient from the factory)
    builder.Services.AddSingleton<BlizzardApiClient>(sp =>
    {
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        var logger = sp.GetRequiredService<ILogger<BlizzardApiClient>>();
        return new BlizzardApiClient(httpClient, logger, config);
    });

    // Register WagoToolsClient with configured timeout
    builder.Services.AddHttpClient<WagoToolsClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(config.StaticDataSync.WagoTimeoutSeconds);
        client.DefaultRequestHeaders.Add("User-Agent", "NinjaBotHelpers/1.0");
    });

    builder.Services.AddSingleton<WagoToolsClient>(sp =>
    {
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(nameof(WagoToolsClient));
        httpClient.Timeout = TimeSpan.FromSeconds(config.StaticDataSync.WagoTimeoutSeconds);
        var logger = sp.GetRequiredService<ILogger<WagoToolsClient>>();
        return new WagoToolsClient(httpClient, logger);
    });

    // Register WarcraftLogsClient
    builder.Services.AddHttpClient<WarcraftLogsClient>();
    builder.Services.AddSingleton<WarcraftLogsClient>(sp =>
    {
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(nameof(WarcraftLogsClient));
        var logger = sp.GetRequiredService<ILogger<WarcraftLogsClient>>();
        return new WarcraftLogsClient(httpClient, logger, config);
    });

    // Workers
    builder.Services.AddHostedService<RealmWatcherWorker>();
    builder.Services.AddHostedService<StaticDataSyncWorker>();
    builder.Services.AddHostedService<LogMonitoringWorker>();

    var host = builder.Build();

    // Validate database connection on startup
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        Log.Information("Testing database connection...");
        await db.Database.CanConnectAsync();
        Log.Information("Database connection successful");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "NinjaBotHelpers terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

static HelpersConfiguration LoadConfiguration(IConfiguration configuration)
{
    var config = new HelpersConfiguration();

    // Support both environment variables and appsettings.json
    // Uses same env var names as main NinjaBotCore for shared .env files

    config.DiscordToken = Environment.GetEnvironmentVariable("NINJABOT_Token")
        ?? configuration["Discord:Token"]
        ?? throw new InvalidOperationException("Discord token is required (NINJABOT_Token)");

    config.BlizzardClientId = Environment.GetEnvironmentVariable("NINJABOT_WoWClient")
        ?? configuration["Blizzard:ClientId"]
        ?? throw new InvalidOperationException("Blizzard client ID is required (NINJABOT_WoWClient)");

    config.BlizzardClientSecret = Environment.GetEnvironmentVariable("NINJABOT_WoWSecret")
        ?? configuration["Blizzard:ClientSecret"]
        ?? throw new InvalidOperationException("Blizzard client secret is required (NINJABOT_WoWSecret)");

    config.ConnectionString = Environment.GetEnvironmentVariable("NINJABOT_ConnectionStrings__NinjaBot")
        ?? configuration.GetConnectionString("NinjaBot")
        ?? throw new InvalidOperationException("Database connection string is required (NINJABOT_ConnectionStrings__NinjaBot)");

    // RealmWatcher settings (optional, with defaults)
    var rwSection = configuration.GetSection("RealmWatcher");
    if (rwSection.Exists())
    {
        config.RealmWatcher.Enabled = rwSection.GetValue("Enabled", true);
        config.RealmWatcher.CheckIntervalSeconds = rwSection.GetValue("CheckIntervalSeconds", 60);
        config.RealmWatcher.InitialDelaySeconds = rwSection.GetValue("InitialDelaySeconds", 30);
        config.RealmWatcher.ApiCallDelayMs = rwSection.GetValue("ApiCallDelayMs", 100);
    }

    // Environment variable overrides for RealmWatcher
    if (bool.TryParse(Environment.GetEnvironmentVariable("NINJABOT_REALMWATCHER_ENABLED"), out var rwEnabled))
        config.RealmWatcher.Enabled = rwEnabled;

    if (int.TryParse(Environment.GetEnvironmentVariable("NINJABOT_REALMWATCHER_INTERVAL"), out var rwInterval))
        config.RealmWatcher.CheckIntervalSeconds = rwInterval;

    // StaticDataSync settings (optional, with defaults)
    var sdsSection = configuration.GetSection("StaticDataSync");
    if (sdsSection.Exists())
    {
        config.StaticDataSync.Enabled = sdsSection.GetValue("Enabled", true);
        config.StaticDataSync.SyncIntervalDays = sdsSection.GetValue("SyncIntervalDays", 30);
        config.StaticDataSync.InitialDelaySeconds = sdsSection.GetValue("InitialDelaySeconds", 60);
        config.StaticDataSync.ApiCallDelayMs = sdsSection.GetValue("ApiCallDelayMs", 100);
        config.StaticDataSync.ItemDataSource = sdsSection.GetValue("ItemDataSource", "auto") ?? "auto";
        config.StaticDataSync.WagoTimeoutSeconds = sdsSection.GetValue("WagoTimeoutSeconds", 120);
    }

    // Environment variable overrides for StaticDataSync
    if (bool.TryParse(Environment.GetEnvironmentVariable("NINJABOT_STATICDATASYNC_ENABLED"), out var sdsEnabled))
        config.StaticDataSync.Enabled = sdsEnabled;

    if (int.TryParse(Environment.GetEnvironmentVariable("NINJABOT_STATICDATASYNC_INTERVAL"), out var sdsInterval))
        config.StaticDataSync.SyncIntervalDays = sdsInterval;

    var itemSourceEnv = Environment.GetEnvironmentVariable("NINJABOT_STATICDATASYNC_ITEMSOURCE");
    if (!string.IsNullOrEmpty(itemSourceEnv))
        config.StaticDataSync.ItemDataSource = itemSourceEnv;

    if (int.TryParse(Environment.GetEnvironmentVariable("NINJABOT_STATICDATASYNC_WAGOTIMEOUT"), out var wagoTimeout))
        config.StaticDataSync.WagoTimeoutSeconds = wagoTimeout;

    // WarcraftLogs credentials (optional - worker will disable itself if not set)
    // Uses same env vars as main bot: NINJABOT_WCLClientId, NINJABOT_WCLClientSecret
    // The NINJABOT_ prefix is stripped by AddEnvironmentVariables, so we access as WCLClientId
    config.WclClientId = configuration["WCLClientId"]
        ?? configuration["WclClientId"]  // Also check alternate casing
        ?? string.Empty;

    config.WclClientSecret = configuration["WCLClientSecret"]
        ?? configuration["WclClientSecret"]  // Also check alternate casing
        ?? string.Empty;

    // LogMonitoring settings (optional, with defaults)
    var lmSection = configuration.GetSection("LogMonitoring");
    if (lmSection.Exists())
    {
        config.LogMonitoring.Enabled = lmSection.GetValue("Enabled", true);
        config.LogMonitoring.CheckIntervalMinutes = lmSection.GetValue("CheckIntervalMinutes", 15);
        config.LogMonitoring.InitialDelaySeconds = lmSection.GetValue("InitialDelaySeconds", 90);
        config.LogMonitoring.Tier1ThresholdDays = lmSection.GetValue("Tier1ThresholdDays", 14);
        config.LogMonitoring.Tier2ThresholdDays = lmSection.GetValue("Tier2ThresholdDays", 30);
        config.LogMonitoring.Tier1IntervalMinutes = lmSection.GetValue("Tier1IntervalMinutes", 20);
        config.LogMonitoring.Tier2IntervalHours = lmSection.GetValue("Tier2IntervalHours", 3);
        config.LogMonitoring.Tier3IntervalHours = lmSection.GetValue("Tier3IntervalHours", 24);
    }

    // Environment variable overrides for LogMonitoring
    if (bool.TryParse(Environment.GetEnvironmentVariable("NINJABOT_LOGMONITORING_ENABLED"), out var lmEnabled))
        config.LogMonitoring.Enabled = lmEnabled;

    if (int.TryParse(Environment.GetEnvironmentVariable("NINJABOT_LOGMONITORING_INTERVAL"), out var lmInterval))
        config.LogMonitoring.CheckIntervalMinutes = lmInterval;

    Log.Information("Configuration loaded:");
    Log.Information("  RealmWatcher Enabled: {Enabled}", config.RealmWatcher.Enabled);
    Log.Information("  RealmWatcher Interval: {Interval}s", config.RealmWatcher.CheckIntervalSeconds);
    Log.Information("  StaticDataSync Enabled: {Enabled}", config.StaticDataSync.Enabled);
    Log.Information("  StaticDataSync Interval: {Interval} days", config.StaticDataSync.SyncIntervalDays);
    Log.Information("  StaticDataSync ItemDataSource: {Source}", config.StaticDataSync.ItemDataSource);
    Log.Information("  StaticDataSync WagoTimeout: {Timeout}s", config.StaticDataSync.WagoTimeoutSeconds);
    Log.Information("  LogMonitoring Enabled: {Enabled}", config.LogMonitoring.Enabled);
    Log.Information("  LogMonitoring Interval: {Interval}m", config.LogMonitoring.CheckIntervalMinutes);
    Log.Information("  WarcraftLogs Configured: {Configured}", !string.IsNullOrEmpty(config.WclClientId));

    return config;
}
