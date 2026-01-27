using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Database;
using NinjaBotHelpers.Workers;
using Xunit;

namespace NinjaBotHelpers.Tests.Workers;

/// <summary>
/// Tests for sync request processing in StaticDataSyncWorker.
/// </summary>
public class SyncRequestProcessingTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HelpersDbContext _dbContext;
    private static readonly InMemoryDatabaseRoot _databaseRoot = new();

    public SyncRequestProcessingTests()
    {
        var services = new ServiceCollection();
        var dbName = $"SyncRequestTests_{Guid.NewGuid()}";

        services.AddDbContext<HelpersDbContext>(options =>
            options.UseInMemoryDatabase(dbName, _databaseRoot));

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<HelpersDbContext>();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task StaticDataSyncRequest_CanBeAddedAndRetrieved()
    {
        // Arrange
        var request = new StaticDataSyncRequest
        {
            SyncType = "achievements",
            Status = "pending",
            RequestedByUserId = 123456789,
            RequestSource = "slash_command",
            RequestedAt = DateTime.UtcNow
        };

        // Act
        _dbContext.StaticDataSyncRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        var retrieved = await _dbContext.StaticDataSyncRequests
            .FirstOrDefaultAsync(r => r.SyncType == "achievements");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("achievements", retrieved.SyncType);
        Assert.Equal("pending", retrieved.Status);
        Assert.Equal(123456789, retrieved.RequestedByUserId);
        Assert.Equal("slash_command", retrieved.RequestSource);
    }

    [Fact]
    public async Task StaticDataSyncRequest_StatusCanBeUpdated()
    {
        // Arrange
        var request = new StaticDataSyncRequest
        {
            SyncType = "pets",
            Status = "pending",
            RequestedAt = DateTime.UtcNow
        };
        _dbContext.StaticDataSyncRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        // Act
        request.Status = "in_progress";
        request.StartedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var retrieved = await _dbContext.StaticDataSyncRequests.FindAsync(request.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("in_progress", retrieved.Status);
        Assert.NotNull(retrieved.StartedAt);
    }

    [Fact]
    public async Task StaticDataSyncRequest_CanTrackProcessingStats()
    {
        // Arrange
        var request = new StaticDataSyncRequest
        {
            SyncType = "mounts",
            Status = "in_progress",
            RequestedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow
        };
        _dbContext.StaticDataSyncRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        // Act - simulate completion
        request.Status = "completed";
        request.CompletedAt = DateTime.UtcNow;
        request.ItemsProcessed = 150;
        request.ItemsSkipped = 350;
        request.ItemsFailed = 5;
        await _dbContext.SaveChangesAsync();

        var retrieved = await _dbContext.StaticDataSyncRequests.FindAsync(request.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("completed", retrieved.Status);
        Assert.Equal(150, retrieved.ItemsProcessed);
        Assert.Equal(350, retrieved.ItemsSkipped);
        Assert.Equal(5, retrieved.ItemsFailed);
    }

    [Fact]
    public async Task StaticDataSyncStatus_CanBeAddedAndRetrieved()
    {
        // Arrange
        var status = new StaticDataSyncStatus
        {
            SyncType = "achievements",
            LastSyncStarted = DateTime.UtcNow.AddMinutes(-10),
            LastSyncCompleted = DateTime.UtcNow,
            LastSyncItemCount = 3500,
            TotalItemsInDatabase = 3500,
            LastSyncStatus = "success",
            NextScheduledSync = DateTime.UtcNow.AddDays(30)
        };

        // Act
        _dbContext.StaticDataSyncStatus.Add(status);
        await _dbContext.SaveChangesAsync();

        var retrieved = await _dbContext.StaticDataSyncStatus.FindAsync("achievements");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("achievements", retrieved.SyncType);
        Assert.Equal(3500, retrieved.TotalItemsInDatabase);
        Assert.Equal("success", retrieved.LastSyncStatus);
    }

    [Fact]
    public async Task PendingRequests_AreRetrievedInOrder()
    {
        // Arrange
        var request1 = new StaticDataSyncRequest
        {
            SyncType = "achievements",
            Status = "pending",
            RequestedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var request2 = new StaticDataSyncRequest
        {
            SyncType = "pets",
            Status = "pending",
            RequestedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var request3 = new StaticDataSyncRequest
        {
            SyncType = "mounts",
            Status = "completed",  // Should not be included
            RequestedAt = DateTime.UtcNow.AddMinutes(-15)
        };

        _dbContext.StaticDataSyncRequests.AddRange(request1, request2, request3);
        await _dbContext.SaveChangesAsync();

        // Act
        var pendingRequests = await _dbContext.StaticDataSyncRequests
            .Where(r => r.Status == "pending")
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();

        // Assert
        Assert.Equal(2, pendingRequests.Count);
        Assert.Equal("pets", pendingRequests[0].SyncType);  // Older request first
        Assert.Equal("achievements", pendingRequests[1].SyncType);
    }

    [Fact]
    public async Task FailedRequest_StoresErrorMessage()
    {
        // Arrange
        var request = new StaticDataSyncRequest
        {
            SyncType = "achievements",
            Status = "in_progress",
            RequestedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow
        };
        _dbContext.StaticDataSyncRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        // Act - simulate failure
        var errorMessage = "API rate limit exceeded";
        request.Status = "failed";
        request.CompletedAt = DateTime.UtcNow;
        request.ErrorMessage = errorMessage;
        await _dbContext.SaveChangesAsync();

        var retrieved = await _dbContext.StaticDataSyncRequests.FindAsync(request.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("failed", retrieved.Status);
        Assert.Equal(errorMessage, retrieved.ErrorMessage);
    }

    [Fact]
    public async Task RequestSource_DistinguishesBetweenSources()
    {
        // Arrange
        var slashCommand = new StaticDataSyncRequest
        {
            SyncType = "achievements",
            Status = "pending",
            RequestSource = "slash_command",
            RequestedByUserId = 123456789,
            RequestedAt = DateTime.UtcNow
        };
        var apiRequest = new StaticDataSyncRequest
        {
            SyncType = "pets",
            Status = "pending",
            RequestSource = "api",
            RequestedByUserId = 987654321,
            RequestedAt = DateTime.UtcNow
        };
        var scheduled = new StaticDataSyncRequest
        {
            SyncType = "mounts",
            Status = "pending",
            RequestSource = "scheduled",
            RequestedByUserId = null,  // No user for scheduled
            RequestedAt = DateTime.UtcNow
        };

        _dbContext.StaticDataSyncRequests.AddRange(slashCommand, apiRequest, scheduled);
        await _dbContext.SaveChangesAsync();

        // Act
        var requests = await _dbContext.StaticDataSyncRequests.ToListAsync();

        // Assert
        Assert.Equal(3, requests.Count);
        Assert.Contains(requests, r => r.RequestSource == "slash_command" && r.RequestedByUserId == 123456789);
        Assert.Contains(requests, r => r.RequestSource == "api" && r.RequestedByUserId == 987654321);
        Assert.Contains(requests, r => r.RequestSource == "scheduled" && r.RequestedByUserId == null);
    }
}
