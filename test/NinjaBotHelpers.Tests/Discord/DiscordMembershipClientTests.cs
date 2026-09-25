using System.Net;
using NinjaBotHelpers.Discord;
using Xunit;

namespace NinjaBotHelpers.Tests.Discord;

public class DiscordMembershipClientTests
{
    [Theory]
    [InlineData(200, "{\"id\":\"123\"}", GuildMembership.Present)]
    [InlineData(404, "{\"code\":10004}", GuildMembership.Absent)]
    [InlineData(404, "{\"code\":10003}", GuildMembership.Unknown)]
    [InlineData(404, "{}", GuildMembership.Unknown)]
    [InlineData(403, "{\"code\":50001}", GuildMembership.Unknown)]
    [InlineData(401, "{\"code\":10004}", GuildMembership.Unknown)]
    [InlineData(429, "{\"code\":10004}", GuildMembership.Unknown)]
    [InlineData(500, "{\"code\":10004}", GuildMembership.Unknown)]
    [InlineData(200, "{\"id\":\"456\"}", GuildMembership.Unknown)]
    [InlineData(200, "not json", GuildMembership.Unknown)]
    public async Task Only_explicit_unknown_guild_is_absence(int status, string body, GuildMembership expected)
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://discord.com/api/v10/guilds/123", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }));
        Assert.Equal(expected, await new DiscordMembershipClient(http).CheckAsync(123, default));
    }

    [Fact]
    public async Task Network_failure_is_unknown()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("synthetic")));
        Assert.Equal(GuildMembership.Unknown, await new DiscordMembershipClient(http).CheckAsync(123, default));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var http = new HttpClient(new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiscordMembershipClient(http).CheckAsync(123, cancelled.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
