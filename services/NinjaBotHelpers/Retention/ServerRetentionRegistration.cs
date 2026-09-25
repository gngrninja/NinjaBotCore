using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.Workers;

namespace NinjaBotHelpers.Retention;

public static class ServerRetentionRegistration
{
    public static IServiceCollection AddServerRetention(this IServiceCollection services, HelpersConfiguration config)
    {
        services.AddHttpClient<IDiscordMembershipClient, DiscordMembershipClient>(client =>
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", config.DiscordToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NinjaBotHelpers/1.0");
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DepartedServerPurger(config.ConnectionString, sp.GetRequiredService<IDiscordMembershipClient>()));
        services.AddHostedService<ServerRetentionWorker>();
        return services;
    }
}
