using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NinjaBotHelpers.Blizzard;
using NinjaBotHelpers.Configuration;
using Xunit;

namespace NinjaBotHelpers.Tests.Blizzard;

/// <summary>
/// Tests for BlizzardApiClient static data methods
/// </summary>
public class BlizzardApiClientTests
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly HelpersConfiguration _config;
    private readonly ILogger<BlizzardApiClient> _logger;

    public BlizzardApiClientTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object);
        _config = new HelpersConfiguration
        {
            BlizzardClientId = "test-client-id",
            BlizzardClientSecret = "test-client-secret"
        };
        _logger = NullLogger<BlizzardApiClient>.Instance;
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

    #region Achievement Index Tests

    [Fact]
    public async Task GetAchievementIndexAsync_ReturnsAchievements_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/index")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""achievements"": [
                        { ""key"": { ""href"": ""https://api.blizzard.com/data/wow/achievement/1"" }, ""name"": ""Test Achievement"", ""id"": 1 },
                        { ""key"": { ""href"": ""https://api.blizzard.com/data/wow/achievement/2"" }, ""name"": ""Another Achievement"", ""id"": 2 }
                    ]
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetAchievementIndexAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Achievements);
        Assert.Equal(2, result.Achievements.Count);
        Assert.Equal(1, result.Achievements[0].Id);
        Assert.Equal("Test Achievement", result.Achievements[0].Name);
    }

    [Fact]
    public async Task GetAchievementIndexAsync_ReturnsNull_WhenApiFails()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/index")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetAchievementIndexAsync();

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Single Achievement Tests

    [Fact]
    public async Task GetAchievementAsync_ReturnsAchievement_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/123")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""id"": 123,
                    ""name"": ""Test Achievement"",
                    ""description"": ""A test achievement"",
                    ""points"": 10,
                    ""is_account_wide"": true,
                    ""display_order"": 5,
                    ""category"": { ""id"": 1, ""name"": ""General"" }
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetAchievementAsync(123);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(123, result.Id);
        Assert.Equal("Test Achievement", result.Name);
        Assert.Equal("A test achievement", result.Description);
        Assert.Equal(10, result.Points);
        Assert.True(result.IsAccountWide);
        Assert.Equal(5, result.DisplayOrder);
        Assert.NotNull(result.Category);
        Assert.Equal("General", result.Category.Name);
    }

    [Fact]
    public async Task GetAchievementAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/999")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetAchievementAsync(999);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Mount Index Tests

    [Fact]
    public async Task GetMountIndexAsync_ReturnsMounts_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/mount/index")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""mounts"": [
                        { ""key"": { ""href"": ""https://api.blizzard.com/data/wow/mount/1"" }, ""name"": ""Horse"", ""id"": 1 },
                        { ""key"": { ""href"": ""https://api.blizzard.com/data/wow/mount/2"" }, ""name"": ""Dragon"", ""id"": 2 }
                    ]
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetMountIndexAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Mounts);
        Assert.Equal(2, result.Mounts.Count);
        Assert.Equal("Horse", result.Mounts[0].Name);
    }

    #endregion

    #region Single Mount Tests

    [Fact]
    public async Task GetMountAsync_ReturnsMount_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/mount/456")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""id"": 456,
                    ""name"": ""Swift Spectral Tiger"",
                    ""description"": ""A rare mount"",
                    ""source"": { ""type"": ""TCG"", ""name"": ""Trading Card Game"" },
                    ""faction"": { ""type"": ""NEUTRAL"", ""name"": ""Neutral"" },
                    ""creature_displays"": [{ ""id"": 12345 }],
                    ""should_exclude_if_uncollected"": false
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetMountAsync(456);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(456, result.Id);
        Assert.Equal("Swift Spectral Tiger", result.Name);
        Assert.Equal("Trading Card Game", result.Source?.Name);
        Assert.Equal("Neutral", result.Faction?.Name);
        Assert.NotNull(result.CreatureDisplays);
        Assert.Single(result.CreatureDisplays);
        Assert.Equal(12345, result.CreatureDisplays[0].Id);
    }

    #endregion

    #region Pet Index Tests

    [Fact]
    public async Task GetPetIndexAsync_ReturnsPets_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/pet/index")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""pets"": [
                        { ""key"": { ""href"": ""https://api.blizzard.com/data/wow/pet/1"" }, ""name"": ""Cat"", ""id"": 1 },
                        { ""key"": { ""href"": ""https://api.blizzard.com/data/wow/pet/2"" }, ""name"": ""Dog"", ""id"": 2 }
                    ]
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetPetIndexAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Pets);
        Assert.Equal(2, result.Pets.Count);
        Assert.Equal("Cat", result.Pets[0].Name);
    }

    #endregion

    #region Single Pet Tests

    [Fact]
    public async Task GetPetAsync_ReturnsPet_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/pet/789")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""id"": 789,
                    ""name"": ""Lil' Ragnaros"",
                    ""description"": ""A fiery pet"",
                    ""battle_pet_type"": { ""type"": ""ELEMENTAL"", ""name"": ""Elemental"" },
                    ""source"": { ""type"": ""STORE"", ""name"": ""Blizzard Store"" },
                    ""is_capturable"": false,
                    ""is_tradable"": false,
                    ""is_battlepet"": true,
                    ""creature"": { ""id"": 54321 }
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetPetAsync(789);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(789, result.Id);
        Assert.Equal("Lil' Ragnaros", result.Name);
        Assert.Equal("Elemental", result.BattlePetType?.Name);
        Assert.Equal("Blizzard Store", result.Source?.Name);
        Assert.False(result.IsCapturable);
        Assert.False(result.IsTradable);
        Assert.True(result.IsBattlepet);
    }

    #endregion

    #region Media Tests

    [Fact]
    public async Task GetAchievementMediaAsync_ReturnsMedia_WhenApiSucceeds()
    {
        // Arrange
        SetupTokenResponse();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/media/achievement/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""assets"": [
                        { ""key"": ""icon"", ""value"": ""https://render.worldofwarcraft.com/icons/56/achievement_bg_wineos.jpg"" }
                    ]
                }")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetAchievementMediaAsync(123);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Assets);
        Assert.Single(result.Assets);
        Assert.Equal("icon", result.Assets[0].Key);
        Assert.Contains("achievement_bg_wineos", result.Assets[0].Value);
    }

    [Fact]
    public void MediaResponse_GetIconUrl_ReturnsIconValue()
    {
        // Arrange
        var media = new MediaResponse
        {
            Assets = new List<MediaAsset>
            {
                new() { Key = "icon", Value = "https://example.com/icon.jpg" },
                new() { Key = "background", Value = "https://example.com/bg.jpg" }
            }
        };

        // Act
        var iconUrl = media.GetIconUrl();

        // Assert
        Assert.Equal("https://example.com/icon.jpg", iconUrl);
    }

    [Fact]
    public void MediaResponse_GetIconUrl_ReturnsNull_WhenNoIconAsset()
    {
        // Arrange
        var media = new MediaResponse
        {
            Assets = new List<MediaAsset>
            {
                new() { Key = "background", Value = "https://example.com/bg.jpg" }
            }
        };

        // Act
        var iconUrl = media.GetIconUrl();

        // Assert
        Assert.Null(iconUrl);
    }

    #endregion

    #region Token Failure Tests

    [Fact]
    public async Task GetAchievementIndexAsync_ReturnsNull_WhenTokenFails()
    {
        // Arrange - token request fails
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == "oauth.battle.net"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Content = new StringContent("{\"error\":\"invalid_client\"}")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        var result = await client.GetAchievementIndexAsync();

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Region Tests

    [Fact]
    public async Task GetStaticDataAsync_UsesCorrectRegion()
    {
        // Arrange
        SetupTokenResponse();
        string? capturedUrl = null;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/achievement/index")),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{""achievements"":[]}")
            });

        var client = new BlizzardApiClient(_httpClient, _logger, _config);

        // Act
        await client.GetAchievementIndexAsync("eu");

        // Assert
        Assert.NotNull(capturedUrl);
        Assert.Contains("eu.api.blizzard.com", capturedUrl);
        Assert.Contains("namespace=static-eu", capturedUrl);
    }

    #endregion
}
