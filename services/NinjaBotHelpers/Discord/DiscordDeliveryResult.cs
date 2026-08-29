using System.Net;

namespace NinjaBotHelpers.Discord;

public readonly record struct DiscordDeliveryResult(
    bool Success,
    HttpStatusCode? HttpStatusCode,
    int? DiscordCode,
    bool IsExpectedConfigurationFailure)
{
    public static DiscordDeliveryResult Delivered() => new(true, null, null, false);

    public static DiscordDeliveryResult Failed(
        HttpStatusCode? httpStatusCode,
        int? discordCode,
        bool isExpectedConfigurationFailure) =>
        new(false, httpStatusCode, discordCode, isExpectedConfigurationFailure);
}
