using System.Net;
using Microsoft.Extensions.Logging;
using NinjaBotHelpers.Discord;
using Xunit;

namespace NinjaBotHelpers.Tests.Discord;

public sealed class DiscordRestClientTests
{
    [Theory]
    [InlineData(10003)]
    [InlineData(50001)]
    [InlineData(50013)]
    public void DeliveryWarningPolicy_RecognizesExpectedConfigurationFailures(int discordCode)
    {
        Assert.True(DiscordDeliveryWarningPolicy.IsExpectedConfigurationFailure(discordCode));
    }

    [Fact]
    public async Task ExpectedFailure_IsReturnedAndWarnedOnlyOnceInsideInterval()
    {
        var handler = new QueueHandler(
            Failure(HttpStatusCode.Forbidden, 50001, "Missing Access"),
            Failure(HttpStatusCode.Forbidden, 50001, "Missing Access"));
        var logger = new CapturingLogger<DiscordRestClient>();
        var client = CreateClient(handler, logger);

        var first = await client.SendChannelMessageWithResultAsync(123, DiscordEmbed.Create("test", 0));
        var second = await client.SendChannelMessageWithResultAsync(123, DiscordEmbed.Create("test", 0));

        Assert.False(first.Success);
        Assert.True(first.IsExpectedConfigurationFailure);
        Assert.Equal(50001, first.DiscordCode);
        Assert.False(second.Success);
        Assert.Equal(1, logger.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task UnexpectedFailure_RemainsVisibleOnEveryOccurrence()
    {
        var handler = new QueueHandler(
            Failure(HttpStatusCode.BadRequest, 50278, "Unexpected failure"),
            Failure(HttpStatusCode.BadRequest, 50278, "Unexpected failure"));
        var logger = new CapturingLogger<DiscordRestClient>();
        var client = CreateClient(handler, logger);

        var first = await client.SendChannelMessageWithResultAsync(123, DiscordEmbed.Create("test", 0));
        var second = await client.SendChannelMessageWithResultAsync(123, DiscordEmbed.Create("test", 0));

        Assert.False(first.IsExpectedConfigurationFailure);
        Assert.False(second.IsExpectedConfigurationFailure);
        Assert.Equal(2, logger.Count(LogLevel.Warning));
    }

    private static DiscordRestClient CreateClient(
        HttpMessageHandler handler,
        ILogger<DiscordRestClient> logger)
    {
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("Authorization", "Bot test-token");
        return new DiscordRestClient(httpClient, logger);
    }

    private static HttpResponseMessage Failure(HttpStatusCode status, int code, string message) =>
        new(status)
        {
            Content = new StringContent($"{{\"message\":\"{message}\",\"code\":{code}}}")
        };

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHandler(params HttpResponseMessage[] responses) =>
            _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogLevel> _levels = new();

        public int Count(LogLevel level) => _levels.Count(value => value == level);
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _levels.Add(logLevel);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
