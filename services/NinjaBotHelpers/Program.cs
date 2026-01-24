using Microsoft.EntityFrameworkCore;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Discord;
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

    // Workers
    builder.Services.AddHostedService<RealmWatcherWorker>();

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

    Log.Information("Configuration loaded:");
    Log.Information("  RealmWatcher Enabled: {Enabled}", config.RealmWatcher.Enabled);
    Log.Information("  RealmWatcher Interval: {Interval}s", config.RealmWatcher.CheckIntervalSeconds);

    return config;
}
