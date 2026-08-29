using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.WarcraftLogs;
using NinjaBotHelpers.Workers;
using Xunit;

namespace NinjaBotHelpers.Tests.Workers;

public class LogMonitoringWorkerTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HelpersConfiguration _config;
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly WarcraftLogsClient _wclClient;
    private readonly DiscordRestClient _discordClient;
    private readonly Mock<ILogger<LogMonitoringWorker>> _workerLogger;
    private readonly LogMonitoringWorker _worker;
    private readonly InMemoryDatabaseRoot _dbRoot;

    public LogMonitoringWorkerTests()
    {
        _config = new HelpersConfiguration
        {
            WclClientId = "test-client",
            WclClientSecret = "test-secret",
            LogMonitoring = new LogMonitoringSettings
            {
                Enabled = true,
                CheckIntervalMinutes = 15,
                InitialDelaySeconds = 0,
                Tier1ThresholdDays = 14,
                Tier2ThresholdDays = 30,
                Tier2IntervalHours = 3,
                Tier3IntervalHours = 24,
            }
        };

        _mockHandler = new Mock<HttpMessageHandler>();

        // WCL client
        var wclHttpClient = new HttpClient(_mockHandler.Object);
        _wclClient = new WarcraftLogsClient(wclHttpClient, NullLogger<WarcraftLogsClient>.Instance, _config);

        // Discord client
        var discordHttpClient = new HttpClient(_mockHandler.Object);
        discordHttpClient.DefaultRequestHeaders.Add("Authorization", "Bot test-token");
        _discordClient = new DiscordRestClient(discordHttpClient, NullLogger<DiscordRestClient>.Instance);

        // Database
        _dbRoot = new InMemoryDatabaseRoot();
        var services = new ServiceCollection();
        services.AddDbContext<HelpersDbContext>(options =>
            options.UseInMemoryDatabase("LogMonitoringTests", _dbRoot)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        _serviceProvider = services.BuildServiceProvider();

        // Setup WCL token response
        SetupWclTokenResponse();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));

        _workerLogger = new Mock<ILogger<LogMonitoringWorker>>();
        _worker = new LogMonitoringWorker(
            _workerLogger.Object,
            scopeFactory,
            _config,
            _discordClient,
            _wclClient,
            httpClientFactory.Object);
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    private void SetupWclTokenResponse()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/oauth/token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"access_token\":\"test-token\",\"expires_in\":3600}")
            });
    }

    private void SetupWclBatchResponse(string reportCode, string guildAlias = "guild_0")
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "www.warcraftlogs.com" &&
                    r.RequestUri.AbsolutePath == "/api/v2/client"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($@"{{
                    ""data"": {{
                        ""{guildAlias}"": {{
                            ""reports"": {{
                                ""data"": [{{
                                    ""code"": ""{reportCode}"",
                                    ""title"": ""Test Report"",
                                    ""owner"": {{ ""name"": ""TestUser"" }},
                                    ""startTime"": 1700000000000,
                                    ""endTime"": 1700003600000,
                                    ""zone"": {{ ""id"": 1, ""name"": ""Test Zone"" }}
                                }}]
                            }}
                        }}
                    }}
                }}")
            });
    }

    private void SetupDiscordSuccess()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "discord.com" &&
                    r.RequestUri.AbsolutePath.Contains("/messages")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"id\":\"123\"}")
            });
    }

    private void SetupDiscordFailure()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "discord.com" &&
                    r.RequestUri.AbsolutePath.Contains("/messages")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Forbidden,
                Content = new StringContent("{\"message\":\"Missing Access\",\"code\":50001}")
            });
    }

    private async Task SeedMonitoringConfig(long serverId = 1001, long channelId = 2001,
        string? retailReportId = null, DateTime? latestLogRetail = null,
        DateTime? lastCheckedRetail = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        db.LogMonitoring.Add(new LogMonitoring
        {
            Id = serverId, // Use serverId as Id for simplicity
            ServerId = serverId,
            ChannelId = channelId,
            ChannelName = "test-channel",
            ServerName = "Test Server",
            MonitorLogs = true,
            RetailReportId = retailReportId,
            LatestLogRetail = latestLogRetail,
            LastCheckedRetail = lastCheckedRetail,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedGuildAssociation(long serverId = 1001, string guildName = "Test Guild",
        string realmSlug = "area-52", string region = "us")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        db.WowGuildAssociations.Add(new WowGuildAssociations
        {
            ServerId = serverId,
            WowGuild = guildName,
            LocalRealmSlug = realmSlug,
            WowRegion = region,
        });
        await db.SaveChangesAsync();
    }

    #region ShouldCheckGuild Tests

    [Fact]
    public void ShouldCheckGuild_ReturnsTrue_WhenNeverChecked()
    {
        var result = _worker.ShouldCheckGuild(
            lastLogFound: DateTime.UtcNow.AddDays(-1),
            lastChecked: null,
            now: DateTime.UtcNow);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCheckGuild_ReturnsTrue_ForTier1ActiveGuild()
    {
        var now = DateTime.UtcNow;
        var result = _worker.ShouldCheckGuild(
            lastLogFound: now.AddDays(-5),  // Within Tier1ThresholdDays (14)
            lastChecked: now.AddMinutes(-1), // Recently checked
            now: now);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCheckGuild_ReturnsTrue_ForTier2_WhenIntervalElapsed()
    {
        var now = DateTime.UtcNow;
        var result = _worker.ShouldCheckGuild(
            lastLogFound: now.AddDays(-20),  // Between Tier1 (14) and Tier2 (30)
            lastChecked: now.AddHours(-4),   // More than Tier2IntervalHours (3)
            now: now);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCheckGuild_ReturnsFalse_ForTier2_WhenIntervalNotElapsed()
    {
        var now = DateTime.UtcNow;
        var result = _worker.ShouldCheckGuild(
            lastLogFound: now.AddDays(-20),  // Between Tier1 (14) and Tier2 (30)
            lastChecked: now.AddHours(-1),   // Less than Tier2IntervalHours (3)
            now: now);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCheckGuild_ReturnsTrue_ForTier3_WhenIntervalElapsed()
    {
        var now = DateTime.UtcNow;
        var result = _worker.ShouldCheckGuild(
            lastLogFound: now.AddDays(-60),  // Beyond Tier2ThresholdDays (30)
            lastChecked: now.AddHours(-25),  // More than Tier3IntervalHours (24)
            now: now);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCheckGuild_ReturnsFalse_ForTier3_WhenIntervalNotElapsed()
    {
        var now = DateTime.UtcNow;
        var result = _worker.ShouldCheckGuild(
            lastLogFound: now.AddDays(-60),  // Beyond Tier2ThresholdDays (30)
            lastChecked: now.AddHours(-10),  // Less than Tier3IntervalHours (24)
            now: now);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCheckGuild_TreatsNullLastLogAsInactive()
    {
        var now = DateTime.UtcNow;

        // Null lastLogFound = Tier3 (inactive), needs Tier3IntervalHours elapsed
        var resultRecent = _worker.ShouldCheckGuild(
            lastLogFound: null,
            lastChecked: now.AddHours(-10),  // Less than 24h
            now: now);
        Assert.False(resultRecent);

        var resultStale = _worker.ShouldCheckGuild(
            lastLogFound: null,
            lastChecked: now.AddHours(-25),  // More than 24h
            now: now);
        Assert.True(resultStale);
    }

    #endregion

    #region GetGuildsToCheckAsync Tests

    [Fact]
    public async Task GetGuildsToCheckAsync_ReturnsRetailGuilds_WithValidData()
    {
        await SeedMonitoringConfig(serverId: 1001);
        await SeedGuildAssociation(serverId: 1001, guildName: "Test Guild", realmSlug: "area-52");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = await _worker.GetGuildsToCheckAsync(WowGameVersion.Retail, configs, db, CancellationToken.None);

        Assert.Single(guilds);
        Assert.Equal("Test Guild", guilds[0].GuildName);
        Assert.Equal("area-52", guilds[0].ServerSlug);
        Assert.Equal("us", guilds[0].ServerRegion);
    }

    [Fact]
    public async Task GetGuildsToCheckAsync_SkipsGuilds_WithMissingGuildName()
    {
        await SeedMonitoringConfig(serverId: 1001);

        // Add guild with null WowGuild
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowGuildAssociations.Add(new WowGuildAssociations
            {
                ServerId = 1001,
                WowGuild = null,
                LocalRealmSlug = "area-52",
                WowRegion = "us",
            });
            await db.SaveChangesAsync();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db2.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = await _worker.GetGuildsToCheckAsync(WowGameVersion.Retail, configs, db2, CancellationToken.None);

        Assert.Empty(guilds);
    }

    [Fact]
    public async Task GetGuildsToCheckAsync_ReturnsClassicGuilds_WithSlugConversion()
    {
        await SeedMonitoringConfig(serverId: 1001);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowClassicGuild.Add(new WowClassicGuild
            {
                ServerId = 1001,
                WowGuild = "Classic Guild",
                WowRealm = "Grobbulus",
                WowRegion = "us",
            });
            await db.SaveChangesAsync();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db2.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = await _worker.GetGuildsToCheckAsync(WowGameVersion.Classic, configs, db2, CancellationToken.None);

        Assert.Single(guilds);
        Assert.Equal("grobbulus", guilds[0].ServerSlug); // Slug conversion
        Assert.Equal("Classic Guild", guilds[0].GuildName);
    }

    [Fact]
    public async Task GetGuildsToCheckAsync_FiltersBasedOnTieredChecking()
    {
        var now = DateTime.UtcNow;

        // Server with recent check, inactive guild => Tier3, should NOT be checked
        await SeedMonitoringConfig(serverId: 1001, lastCheckedRetail: now.AddHours(-1), latestLogRetail: now.AddDays(-60));
        await SeedGuildAssociation(serverId: 1001, guildName: "Inactive Guild");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = await _worker.GetGuildsToCheckAsync(WowGameVersion.Retail, configs, db, CancellationToken.None);

        Assert.Empty(guilds); // Should be filtered out by tiered checking
    }

    #endregion

    #region ProcessBatchAsync Tests

    [Fact]
    public async Task ProcessBatchAsync_PostsNewReport_WhenNotAlreadyPosted()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001);
        SetupWclBatchResponse("newReport123");
        SetupDiscordSuccess();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = new List<GuildCheckInfo>
        {
            new()
            {
                ServerId = 1001,
                GuildName = "Test Guild",
                ServerSlug = "area-52",
                ServerRegion = "us",
                GuildKey = "retail_1001_Test Guild_area-52",
                MonitoringConfig = configs[0],
            }
        };

        await _worker.ProcessBatchAsync(WowGameVersion.Retail, guilds, configs, db, CancellationToken.None);

        // Verify WclPosted record was created
        var posted = await db.WclPosted.FirstOrDefaultAsync(w => w.ReportId == "newReport123");
        Assert.NotNull(posted);
        Assert.Equal(1001, posted.ServerId);
    }

    [Fact]
    public async Task ProcessBatchAsync_SkipsReport_WhenAlreadyInWclPosted()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001);

        // Pre-seed as already posted
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WclPosted.Add(new WclPosted
            {
                ServerId = 1001,
                ChannelId = 2001,
                ReportId = "existingReport",
            });
            await db.SaveChangesAsync();
        }

        SetupWclBatchResponse("existingReport");

        using var scope2 = _serviceProvider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db2.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = new List<GuildCheckInfo>
        {
            new()
            {
                ServerId = 1001,
                GuildName = "Test Guild",
                ServerSlug = "area-52",
                ServerRegion = "us",
                GuildKey = "retail_1001_Test Guild_area-52",
                MonitoringConfig = configs[0],
            }
        };

        await _worker.ProcessBatchAsync(WowGameVersion.Retail, guilds, configs, db2, CancellationToken.None);

        // Discord should NOT have been called
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri!.Host == "discord.com" &&
                r.RequestUri.AbsolutePath.Contains("/messages")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatchAsync_SkipsReport_WhenSameAsLastReportId()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001, retailReportId: "sameReport");
        SetupWclBatchResponse("sameReport");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = new List<GuildCheckInfo>
        {
            new()
            {
                ServerId = 1001,
                GuildName = "Test Guild",
                ServerSlug = "area-52",
                ServerRegion = "us",
                GuildKey = "retail_1001_Test Guild_area-52",
                MonitoringConfig = configs[0],
            }
        };

        await _worker.ProcessBatchAsync(WowGameVersion.Retail, guilds, configs, db, CancellationToken.None);

        // No new WclPosted record should exist
        var postedCount = await db.WclPosted.CountAsync();
        Assert.Equal(0, postedCount);
    }

    [Fact]
    public async Task ProcessBatchAsync_UpdatesLastChecked_ForAllGuildsInBatch()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001);

        // Return empty data (no reports) - still should update LastChecked
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "www.warcraftlogs.com" &&
                    r.RequestUri.AbsolutePath == "/api/v2/client"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{""data"": {""guild_0"": null}}")
            });

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = new List<GuildCheckInfo>
        {
            new()
            {
                ServerId = 1001,
                GuildName = "Test Guild",
                ServerSlug = "area-52",
                ServerRegion = "us",
                GuildKey = "retail_1001_Test Guild_area-52",
                MonitoringConfig = configs[0],
            }
        };

        var beforeCheck = DateTime.UtcNow;
        await _worker.ProcessBatchAsync(WowGameVersion.Retail, guilds, configs, db, CancellationToken.None);

        var updatedConfig = await db.LogMonitoring.FirstAsync(m => m.ServerId == 1001);
        Assert.NotNull(updatedConfig.LastCheckedRetail);
        Assert.True(updatedConfig.LastCheckedRetail >= beforeCheck);
    }

    [Fact]
    public async Task ProcessBatchAsync_HandlesWclClientException_Gracefully()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001);

        // Make WCL return an error
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "www.warcraftlogs.com" &&
                    r.RequestUri.AbsolutePath == "/api/v2/client"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Content = new StringContent("Server Error")
            });

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var configs = await db.LogMonitoring.Where(m => m.MonitorLogs).ToListAsync();

        var guilds = new List<GuildCheckInfo>
        {
            new()
            {
                ServerId = 1001,
                GuildName = "Test Guild",
                ServerSlug = "area-52",
                ServerRegion = "us",
                GuildKey = "retail_1001_Test Guild_area-52",
                MonitoringConfig = configs[0],
            }
        };

        // Should not throw
        await _worker.ProcessBatchAsync(WowGameVersion.Retail, guilds, configs, db, CancellationToken.None);
    }

    #endregion

    #region PostNewLogAsync Tests

    [Fact]
    public async Task PostNewLogAsync_RecordsWclPosted_OnSuccess()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001);
        SetupDiscordSuccess();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var config = await db.LogMonitoring.FirstAsync(m => m.ServerId == 1001);

        var guild = new GuildCheckInfo
        {
            ServerId = 1001,
            GuildName = "Test Guild",
            ServerSlug = "area-52",
            ServerRegion = "us",
            GuildKey = "retail_1001_Test Guild_area-52",
            MonitoringConfig = config,
        };

        var report = new WclV2Report
        {
            Code = "successReport",
            Title = "My Report",
            Owner = new WclV2User { Name = "Uploader" },
            StartTime = 1700000000000,
            EndTime = 1700003600000,
            Zone = new WclV2Zone { Id = 1, Name = "Test Zone" },
        };

        await _worker.PostNewLogAsync(guild, report, WowGameVersion.Retail, db, CancellationToken.None);

        var posted = await db.WclPosted.FirstOrDefaultAsync(w => w.ReportId == "successReport");
        Assert.NotNull(posted);
        Assert.Equal(1001, posted.ServerId);
        Assert.Equal(2001, posted.ChannelId);
    }

    [Fact]
    public async Task PostNewLogAsync_UpdatesReportId_OnSuccess()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001, retailReportId: "oldReport");
        SetupDiscordSuccess();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var config = await db.LogMonitoring.FirstAsync(m => m.ServerId == 1001);

        var guild = new GuildCheckInfo
        {
            ServerId = 1001,
            GuildName = "Test Guild",
            ServerSlug = "area-52",
            ServerRegion = "us",
            GuildKey = "retail_1001_Test Guild_area-52",
            MonitoringConfig = config,
        };

        var report = new WclV2Report
        {
            Code = "newReport456",
            Title = "New Report",
            Owner = new WclV2User { Name = "Uploader" },
            StartTime = 1700000000000,
            EndTime = 1700003600000,
            Zone = new WclV2Zone { Id = 1, Name = "Test Zone" },
        };

        await _worker.PostNewLogAsync(guild, report, WowGameVersion.Retail, db, CancellationToken.None);

        var updatedConfig = await db.LogMonitoring.FirstAsync(m => m.ServerId == 1001);
        Assert.Equal("newReport456", updatedConfig.RetailReportId);
        Assert.NotNull(updatedConfig.LatestLogRetail);
    }

    [Fact]
    public async Task PostNewLogAsync_DoesNotRecordWclPosted_OnDiscordFailure()
    {
        await SeedMonitoringConfig(serverId: 1001, channelId: 2001);
        SetupDiscordFailure();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
        var config = await db.LogMonitoring.FirstAsync(m => m.ServerId == 1001);

        var guild = new GuildCheckInfo
        {
            ServerId = 1001,
            GuildName = "Test Guild",
            ServerSlug = "area-52",
            ServerRegion = "us",
            GuildKey = "retail_1001_Test Guild_area-52",
            MonitoringConfig = config,
        };

        var report = new WclV2Report
        {
            Code = "failedReport",
            Title = "Failed Report",
            Owner = new WclV2User { Name = "Uploader" },
            StartTime = 1700000000000,
            EndTime = 1700003600000,
            Zone = new WclV2Zone { Id = 1, Name = "Test Zone" },
        };

        await _worker.PostNewLogAsync(guild, report, WowGameVersion.Retail, db, CancellationToken.None);

        // No WclPosted record should exist
        var posted = await db.WclPosted.FirstOrDefaultAsync(w => w.ReportId == "failedReport");
        Assert.Null(posted);

        // RetailReportId should NOT be updated
        var updatedConfig = await db.LogMonitoring.FirstAsync(m => m.ServerId == 1001);
        Assert.Null(updatedConfig.RetailReportId);

        _workerLogger.Verify(
            logger => logger.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) =>
                    value.ToString()!.Contains("[LogMonitoring] Failed to post log", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task PostNewLogAsync_HandlesNullMonitoringConfig()
    {
        var guild = new GuildCheckInfo
        {
            ServerId = 1001,
            GuildName = "Test Guild",
            ServerSlug = "area-52",
            ServerRegion = "us",
            GuildKey = "retail_1001_Test Guild_area-52",
            MonitoringConfig = null, // Null config
        };

        var report = new WclV2Report
        {
            Code = "someReport",
            Title = "Some Report",
            Owner = new WclV2User { Name = "Uploader" },
            StartTime = 1700000000000,
            EndTime = 1700003600000,
            Zone = new WclV2Zone { Id = 1, Name = "Test Zone" },
        };

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        // Should not throw
        await _worker.PostNewLogAsync(guild, report, WowGameVersion.Retail, db, CancellationToken.None);

        // No Discord call should be made
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri!.Host == "discord.com" &&
                r.RequestUri.AbsolutePath.Contains("/messages")),
            ItExpr.IsAny<CancellationToken>());
    }

    #endregion
}
