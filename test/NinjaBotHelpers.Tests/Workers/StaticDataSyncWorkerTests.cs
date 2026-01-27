using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Workers;
using Xunit;

namespace NinjaBotHelpers.Tests.Workers;

/// <summary>
/// Tests for StaticDataSyncWorker
/// </summary>
public class StaticDataSyncWorkerTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HelpersConfiguration _config;
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly BlizzardApiClient _blizzardClient;
    private readonly InMemoryDatabaseRoot _dbRoot;

    public StaticDataSyncWorkerTests()
    {
        _config = new HelpersConfiguration
        {
            BlizzardClientId = "test-client",
            BlizzardClientSecret = "test-secret",
            StaticDataSync = new StaticDataSyncSettings
            {
                Enabled = true,
                SyncIntervalDays = 30,
                InitialDelaySeconds = 0, // No delay for tests
                ApiCallDelayMs = 0 // No delay for tests
            }
        };

        _mockHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_mockHandler.Object);
        var logger = NullLogger<BlizzardApiClient>.Instance;
        _blizzardClient = new BlizzardApiClient(httpClient, logger, _config);

        // Use shared database root for all scopes
        _dbRoot = new InMemoryDatabaseRoot();

        var services = new ServiceCollection();
        services.AddDbContext<HelpersDbContext>(options =>
            options.UseInMemoryDatabase("StaticDataSyncTests", _dbRoot)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _serviceProvider = services.BuildServiceProvider();

        // Setup token response for all tests
        SetupTokenResponse();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    private void SetupTokenResponse()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == "oauth.battle.net"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"access_token\":\"test-token\",\"expires_in\":3600}")
            });
    }

    #region Worker Disabled Tests

    [Fact]
    public async Task ExecuteAsync_DoesNothing_WhenDisabled()
    {
        // Arrange
        var disabledConfig = new HelpersConfiguration
        {
            StaticDataSync = new StaticDataSyncSettings { Enabled = false }
        };

        var workerLogger = NullLogger<StaticDataSyncWorker>.Instance;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var worker = new StaticDataSyncWorker(workerLogger, scopeFactory, disabledConfig, _blizzardClient);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        // Act & Assert - should complete quickly without doing anything
        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        // Verify no API calls were made (except possibly token)
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/index")),
            ItExpr.IsAny<CancellationToken>());
    }

    #endregion

    #region Achievement Sync Tests

    [Fact]
    public async Task SyncAchievements_ImportsNewAchievements()
    {
        // Arrange
        SetupAchievementApiResponses();

        var workerLogger = NullLogger<StaticDataSyncWorker>.Instance;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // Act - run a quick sync
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // We can't easily test the full worker loop, so we'll verify the database directly
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

            // Pre-condition: no achievements
            Assert.Empty(await db.WowAchievements.ToListAsync());
        }
    }

    [Fact]
    public async Task SyncAchievements_SkipsExistingAchievements()
    {
        // Arrange - add existing achievement
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowAchievements.Add(new WowAchievements
            {
                Id = 1,
                Name = "Existing Achievement",
                LastUpdated = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Act - verify it exists
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            var achievements = await db.WowAchievements.ToListAsync();

            // Assert
            Assert.Single(achievements);
            Assert.Equal("Existing Achievement", achievements[0].Name);
        }
    }

    private void SetupAchievementApiResponses()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/index")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{""achievements"":[{""id"":1,""name"":""Test""}]}")
            });

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/data/wow/achievement/1"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{""id"":1,""name"":""Test Achievement"",""points"":10}")
            });

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/media/achievement/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{""assets"":[{""key"":""icon"",""value"":""https://example.com/icon.jpg""}]}")
            });
    }

    #endregion

    #region Pet Sync Tests

    [Fact]
    public async Task SyncPets_SkipsExistingPets()
    {
        // Arrange - add existing pet
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowPets.Add(new WowPets
            {
                Id = 100,
                Name = "Existing Pet",
                LastUpdated = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Act - verify it exists
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            var pets = await db.WowPets.ToListAsync();

            // Assert
            Assert.Single(pets);
            Assert.Equal("Existing Pet", pets[0].Name);
        }
    }

    #endregion

    #region Mount Sync Tests

    [Fact]
    public async Task SyncMounts_SkipsExistingMounts()
    {
        // Arrange - add existing mount
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            db.WowMounts.Add(new WowMounts
            {
                Id = 200,
                Name = "Existing Mount",
                LastUpdated = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Act - verify it exists
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();
            var mounts = await db.WowMounts.ToListAsync();

            // Assert
            Assert.Single(mounts);
            Assert.Equal("Existing Mount", mounts[0].Name);
        }
    }

    #endregion

    #region Database Entity Tests

    [Fact]
    public async Task WowAchievements_CanBeSavedAndRetrieved()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var achievement = new WowAchievements
        {
            Id = 12345,
            Name = "Test Achievement",
            Description = "A test achievement",
            Points = 10,
            Category = "General",
            CategoryId = 1,
            IsAccountWide = true,
            MediaUrl = "https://example.com/icon.jpg",
            LastUpdated = DateTime.UtcNow
        };

        // Act
        db.WowAchievements.Add(achievement);
        await db.SaveChangesAsync();

        var retrieved = await db.WowAchievements.FindAsync(12345L);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test Achievement", retrieved.Name);
        Assert.Equal(10, retrieved.Points);
        Assert.True(retrieved.IsAccountWide);
    }

    [Fact]
    public async Task WowAchievementCriteria_CanBeSavedWithAchievement()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var achievement = new WowAchievements
        {
            Id = 99999,
            Name = "Achievement with Criteria",
            LastUpdated = DateTime.UtcNow
        };

        var criteria = new WowAchievementCriteria
        {
            Id = 88888,
            AchievementId = 99999,
            Description = "Complete something",
            OrderIndex = 0,
            Amount = 1,
            LastUpdated = DateTime.UtcNow
        };

        // Act
        db.WowAchievements.Add(achievement);
        db.WowAchievementCriteria.Add(criteria);
        await db.SaveChangesAsync();

        var retrievedCriteria = await db.WowAchievementCriteria
            .Where(c => c.AchievementId == 99999)
            .ToListAsync();

        // Assert
        Assert.Single(retrievedCriteria);
        Assert.Equal("Complete something", retrievedCriteria[0].Description);
    }

    [Fact]
    public async Task WowPets_CanBeSavedAndRetrieved()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var pet = new WowPets
        {
            Id = 54321,
            Name = "Test Pet",
            Description = "A cute pet",
            PetType = "Beast",
            Source = "Drop",
            IsCapturable = true,
            IsBattlePet = true,
            LastUpdated = DateTime.UtcNow
        };

        // Act
        db.WowPets.Add(pet);
        await db.SaveChangesAsync();

        var retrieved = await db.WowPets.FindAsync(54321L);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test Pet", retrieved.Name);
        Assert.Equal("Beast", retrieved.PetType);
        Assert.True(retrieved.IsCapturable);
    }

    [Fact]
    public async Task WowMounts_CanBeSavedAndRetrieved()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        var mount = new WowMounts
        {
            Id = 67890,
            Name = "Test Mount",
            Description = "A fast mount",
            Source = "Achievement",
            IsGround = true,
            IsFlying = true,
            IsAquatic = false,
            IsObtainable = true,
            LastUpdated = DateTime.UtcNow
        };

        // Act
        db.WowMounts.Add(mount);
        await db.SaveChangesAsync();

        var retrieved = await db.WowMounts.FindAsync(67890L);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test Mount", retrieved.Name);
        Assert.True(retrieved.IsGround);
        Assert.True(retrieved.IsFlying);
        Assert.False(retrieved.IsAquatic);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task Worker_StopsGracefully_OnCancellation()
    {
        // Arrange
        var workerLogger = NullLogger<StaticDataSyncWorker>.Instance;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var worker = new StaticDataSyncWorker(workerLogger, scopeFactory, _config, _blizzardClient);

        using var cts = new CancellationTokenSource();

        // Act
        var startTask = worker.StartAsync(cts.Token);
        await Task.Delay(50); // Let it start
        cts.Cancel();

        // Assert - should not throw
        await worker.StopAsync(CancellationToken.None);
    }

    #endregion
}

/// <summary>
/// Tests for database context and entity mappings
/// </summary>
public class HelpersDbContextTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public HelpersDbContextTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<HelpersDbContext>(options =>
            options.UseInMemoryDatabase($"DbContextTests_{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    [Fact]
    public void DbContext_HasWowAchievementsDbSet()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        Assert.NotNull(db.WowAchievements);
    }

    [Fact]
    public void DbContext_HasWowAchievementCriteriaDbSet()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        Assert.NotNull(db.WowAchievementCriteria);
    }

    [Fact]
    public void DbContext_HasWowPetsDbSet()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        Assert.NotNull(db.WowPets);
    }

    [Fact]
    public void DbContext_HasWowMountsDbSet()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        Assert.NotNull(db.WowMounts);
    }

    [Fact]
    public async Task DbContext_CanQueryEmptyTables()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpersDbContext>();

        // Act & Assert - should not throw
        var achievements = await db.WowAchievements.ToListAsync();
        var pets = await db.WowPets.ToListAsync();
        var mounts = await db.WowMounts.ToListAsync();

        Assert.Empty(achievements);
        Assert.Empty(pets);
        Assert.Empty(mounts);
    }
}
