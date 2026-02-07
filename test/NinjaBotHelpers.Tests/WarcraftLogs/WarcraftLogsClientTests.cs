using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.WarcraftLogs;
using Xunit;

namespace NinjaBotHelpers.Tests.WarcraftLogs;

public class WarcraftLogsClientTests
{
    private readonly HelpersConfiguration _config;
    private readonly Mock<HttpMessageHandler> _mockHandler;

    public WarcraftLogsClientTests()
    {
        _config = new HelpersConfiguration
        {
            WclClientId = "test-client",
            WclClientSecret = "test-secret",
        };

        _mockHandler = new Mock<HttpMessageHandler>();
        SetupTokenResponse();
    }

    private WarcraftLogsClient CreateClient(HttpMessageHandler? handler = null)
    {
        var httpClient = new HttpClient(handler ?? _mockHandler.Object);
        return new WarcraftLogsClient(httpClient, NullLogger<WarcraftLogsClient>.Instance, _config);
    }

    private void SetupTokenResponse()
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

    private void SetupGraphQLResponse(string jsonData, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.AbsolutePath == "/api/v2/client" &&
                    r.Method == HttpMethod.Post &&
                    !r.RequestUri.AbsolutePath.Contains("/oauth/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(jsonData)
            });
    }

    #region GetBatchGuildReportsAsync Tests

    [Fact]
    public async Task ReturnsReports_WhenQuerySucceeds()
    {
        var client = CreateClient();

        SetupGraphQLResponse(@"{
            ""data"": {
                ""guild_0"": {
                    ""reports"": {
                        ""data"": [{
                            ""code"": ""abc123"",
                            ""title"": ""Raid Night"",
                            ""owner"": { ""name"": ""TestUser"" },
                            ""startTime"": 1700000000000,
                            ""endTime"": 1700003600000,
                            ""zone"": { ""id"": 1, ""name"": ""Nerub-ar Palace"" }
                        }]
                    }
                }
            }
        }");

        var guilds = new List<(string, string, string, string)>
        {
            ("Test Guild", "area-52", "us", "retail_1001_Test Guild_area-52")
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        Assert.Single(result.Reports);
        Assert.True(result.Reports.ContainsKey("retail_1001_Test Guild_area-52"));
        var report = result.Reports["retail_1001_Test Guild_area-52"];
        Assert.Equal("abc123", report.Code);
        Assert.Equal("Raid Night", report.Title);
        Assert.Equal("TestUser", report.OwnerName);
        Assert.Equal("Nerub-ar Palace", report.ZoneName);
    }

    [Fact]
    public async Task ReturnsEmptyResult_WhenNotConfigured()
    {
        var unconfiguredConfig = new HelpersConfiguration
        {
            WclClientId = "",
            WclClientSecret = "",
        };
        var httpClient = new HttpClient(_mockHandler.Object);
        var client = new WarcraftLogsClient(httpClient, NullLogger<WarcraftLogsClient>.Instance, unconfiguredConfig);

        var guilds = new List<(string, string, string, string)>
        {
            ("Test Guild", "area-52", "us", "key1")
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        Assert.Empty(result.Reports);
        Assert.Empty(result.NonExistentGuilds);
    }

    [Fact]
    public async Task ReturnsEmptyResult_WhenGuildsListEmpty()
    {
        var client = CreateClient();

        var result = await client.GetBatchGuildReportsAsync(new List<(string, string, string, string)>());

        Assert.Empty(result.Reports);
    }

    [Fact]
    public async Task SkipsDuplicateGuildKeys()
    {
        var client = CreateClient();

        SetupGraphQLResponse(@"{
            ""data"": {
                ""guild_0"": {
                    ""reports"": {
                        ""data"": [{
                            ""code"": ""abc123"",
                            ""title"": ""Raid"",
                            ""owner"": { ""name"": ""User"" },
                            ""startTime"": 1700000000000,
                            ""endTime"": 1700003600000,
                            ""zone"": { ""id"": 1, ""name"": ""Zone"" }
                        }]
                    }
                }
            }
        }");

        var guilds = new List<(string, string, string, string)>
        {
            ("Test Guild", "area-52", "us", "samekey"),
            ("Test Guild", "area-52", "us", "samekey"), // Duplicate
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        // Should only have one result despite two entries with same key
        Assert.Single(result.Reports);
    }

    [Fact]
    public async Task SkipsInvalidGuildEntries()
    {
        var client = CreateClient();

        SetupGraphQLResponse(@"{ ""data"": {} }");

        var guilds = new List<(string, string, string, string)>
        {
            ("", "area-52", "us", "key1"),          // Empty guild name
            ("Guild", "", "us", "key2"),              // Empty server slug
            ("Guild", "area-52", "", "key3"),          // Empty region
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        Assert.Empty(result.Reports);
    }

    [Fact]
    public async Task IdentifiesNonExistentGuilds()
    {
        var client = CreateClient();

        SetupGraphQLResponse(@"{
            ""data"": {
                ""guild_0"": null
            },
            ""errors"": [{
                ""message"": ""No guild exists with the given name/server/region."",
                ""path"": [""guild_0""]
            }]
        }");

        var guilds = new List<(string, string, string, string)>
        {
            ("Nonexistent Guild", "area-52", "us", "key1")
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        Assert.Empty(result.Reports);
        Assert.Contains("key1", result.NonExistentGuilds);
    }

    [Fact]
    public async Task ParsesReportFields_Correctly()
    {
        var client = CreateClient();

        SetupGraphQLResponse(@"{
            ""data"": {
                ""guild_0"": {
                    ""reports"": {
                        ""data"": [{
                            ""code"": ""XYZ789"",
                            ""title"": ""Mythic Progression"",
                            ""owner"": { ""name"": ""RaidLeader"" },
                            ""startTime"": 1706745600000,
                            ""endTime"": 1706756400000,
                            ""zone"": { ""id"": 38, ""name"": ""Nerub-ar Palace"" }
                        }]
                    }
                }
            }
        }");

        var guilds = new List<(string, string, string, string)>
        {
            ("My Guild", "illidan", "us", "key1")
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        var report = result.Reports["key1"];
        Assert.Equal("XYZ789", report.Code);
        Assert.Equal("XYZ789", report.Id);
        Assert.Equal("Mythic Progression", report.Title);
        Assert.Equal("RaidLeader", report.OwnerName);
        Assert.Equal("Nerub-ar Palace", report.ZoneName);
        Assert.Equal("https://www.warcraftlogs.com/reports/XYZ789", report.ReportURL);
        Assert.Equal(1706745600000, report.StartTime);
        Assert.Equal(38, report.Zone!.Id);
    }

    #endregion

    #region Rate Limit Tests

    [Fact]
    public async Task WaitsForRateReset_WhenAtCriticalThreshold()
    {
        var client = CreateClient();

        // Seed rate limit data at critical threshold with very short wait
        client.LastRateLimitData = new WclV2RateLimitData
        {
            LimitPerHour = 100,
            PointsSpentThisHour = 96, // 96% > 95% critical
            PointsResetIn = 1,        // 1 second wait (will be clamped to 10s min)
        };

        // After waiting, the rate limit check should show we're under the limit
        // First call is the rate limit check after waiting, second is the actual query
        var callCount = 0;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.AbsolutePath == "/api/v2/client" &&
                    r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Rate limit check after waiting - shows recovered
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(@"{
                            ""data"": {
                                ""rateLimitData"": {
                                    ""limitPerHour"": 100,
                                    ""pointsSpentThisHour"": 10,
                                    ""pointsResetIn"": 3600
                                }
                            }
                        }")
                    });
                }

                // Actual batch query
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(@"{
                        ""data"": {
                            ""guild_0"": {
                                ""reports"": {
                                    ""data"": [{
                                        ""code"": ""afterWait"",
                                        ""title"": ""Post Wait Report"",
                                        ""owner"": { ""name"": ""User"" },
                                        ""startTime"": 1700000000000,
                                        ""endTime"": 1700003600000,
                                        ""zone"": { ""id"": 1, ""name"": ""Zone"" }
                                    }]
                                }
                            }
                        }
                    }")
                });
            });

        var guilds = new List<(string, string, string, string)>
        {
            ("Test Guild", "area-52", "us", "key1")
        };

        // This will wait the minimum 10s - use a cancellation token to limit test time
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await client.GetBatchGuildReportsAsync(guilds, cancellationToken: cts.Token);

        Assert.Single(result.Reports);
        Assert.Equal("afterWait", result.Reports["key1"].Code);
    }

    [Fact]
    public async Task ThrowsAfterWait_WhenStillOverLimit()
    {
        var client = CreateClient();

        // Seed rate limit data at critical threshold
        client.LastRateLimitData = new WclV2RateLimitData
        {
            LimitPerHour = 100,
            PointsSpentThisHour = 96,
            PointsResetIn = 1,
        };

        // Rate limit check after waiting still shows critical
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.AbsolutePath == "/api/v2/client" &&
                    r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""data"": {
                        ""rateLimitData"": {
                            ""limitPerHour"": 100,
                            ""pointsSpentThisHour"": 97,
                            ""pointsResetIn"": 300
                        }
                    }
                }")
            });

        var guilds = new List<(string, string, string, string)>
        {
            ("Test Guild", "area-52", "us", "key1")
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<WclRateLimitException>(() =>
            client.GetBatchGuildReportsAsync(guilds, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RetriesOnTransientFailure()
    {
        // Use a custom handler that fails once then succeeds
        var callCount = 0;
        var retryHandler = new Mock<HttpMessageHandler>();

        // Token response
        retryHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("/oauth/token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"access_token\":\"test-token\",\"expires_in\":3600}")
            });

        // API calls: first fails with 500, then succeeds
        retryHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.AbsolutePath == "/api/v2/client" &&
                    r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.InternalServerError,
                        Content = new StringContent("Server Error")
                    });
                }

                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(@"{
                        ""data"": {
                            ""guild_0"": {
                                ""reports"": {
                                    ""data"": [{
                                        ""code"": ""retried"",
                                        ""title"": ""After Retry"",
                                        ""owner"": { ""name"": ""User"" },
                                        ""startTime"": 1700000000000,
                                        ""endTime"": 1700003600000,
                                        ""zone"": { ""id"": 1, ""name"": ""Zone"" }
                                    }]
                                }
                            }
                        }
                    }")
                });
            });

        var client = CreateClient(retryHandler.Object);

        var guilds = new List<(string, string, string, string)>
        {
            ("Test Guild", "area-52", "us", "key1")
        };

        var result = await client.GetBatchGuildReportsAsync(guilds);

        Assert.Single(result.Reports);
        Assert.Equal("retried", result.Reports["key1"].Code);
        Assert.True(callCount >= 2, "Should have retried at least once");
    }

    #endregion
}
