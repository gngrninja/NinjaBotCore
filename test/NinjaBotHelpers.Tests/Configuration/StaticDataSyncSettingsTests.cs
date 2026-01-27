using NinjaBotHelpers.Configuration;
using Xunit;

namespace NinjaBotHelpers.Tests.Configuration;

/// <summary>
/// Tests for StaticDataSyncSettings configuration
/// </summary>
public class StaticDataSyncSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var settings = new StaticDataSyncSettings();

        // Assert
        Assert.True(settings.Enabled);
        Assert.Equal(30, settings.SyncIntervalDays);
        Assert.Equal(60, settings.InitialDelaySeconds);
        Assert.Equal(100, settings.ApiCallDelayMs);
    }

    [Fact]
    public void Enabled_CanBeSet()
    {
        // Arrange
        var settings = new StaticDataSyncSettings();

        // Act
        settings.Enabled = false;

        // Assert
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void SyncIntervalDays_CanBeSet()
    {
        // Arrange
        var settings = new StaticDataSyncSettings();

        // Act
        settings.SyncIntervalDays = 7;

        // Assert
        Assert.Equal(7, settings.SyncIntervalDays);
    }

    [Fact]
    public void ApiCallDelayMs_CanBeSet()
    {
        // Arrange
        var settings = new StaticDataSyncSettings();

        // Act
        settings.ApiCallDelayMs = 200;

        // Assert
        Assert.Equal(200, settings.ApiCallDelayMs);
    }
}

/// <summary>
/// Tests for HelpersConfiguration
/// </summary>
public class HelpersConfigurationTests
{
    [Fact]
    public void DefaultValues_StaticDataSyncIsInitialized()
    {
        // Arrange & Act
        var config = new HelpersConfiguration();

        // Assert
        Assert.NotNull(config.StaticDataSync);
        Assert.True(config.StaticDataSync.Enabled);
        Assert.Equal(30, config.StaticDataSync.SyncIntervalDays);
    }

    [Fact]
    public void DefaultValues_RealmWatcherIsInitialized()
    {
        // Arrange & Act
        var config = new HelpersConfiguration();

        // Assert
        Assert.NotNull(config.RealmWatcher);
        Assert.True(config.RealmWatcher.Enabled);
    }

    [Fact]
    public void AllProperties_HaveEmptyStringDefaults()
    {
        // Arrange & Act
        var config = new HelpersConfiguration();

        // Assert
        Assert.Equal(string.Empty, config.DiscordToken);
        Assert.Equal(string.Empty, config.BlizzardClientId);
        Assert.Equal(string.Empty, config.BlizzardClientSecret);
        Assert.Equal(string.Empty, config.ConnectionString);
    }
}
