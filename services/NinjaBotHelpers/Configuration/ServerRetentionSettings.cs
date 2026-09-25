namespace NinjaBotHelpers.Configuration;

public sealed class ServerRetentionSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 360;
    public int InitialDelaySeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 25;
    public static ServerRetentionSettings Load(IConfiguration configuration)
    {
        var settings = new ServerRetentionSettings();
        configuration.GetSection("ServerRetention").Bind(settings);
        if (settings.BatchSize is < 1 or > 100 || settings.CheckIntervalMinutes is < 1 or > 1440 ||
            settings.InitialDelaySeconds is < 0 or > 3600)
            throw new InvalidOperationException("ServerRetention requires BatchSize 1..100, CheckIntervalMinutes 1..1440 and InitialDelaySeconds 0..3600.");
        return settings;
    }
}
