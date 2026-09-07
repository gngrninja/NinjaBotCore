using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Modules.Interactions.Wow;
namespace NinjaBotCore.Modules.Wow;
public static class RaidRecapRegistration
{
    public static IServiceCollection AddRaidRecap(this IServiceCollection services) => services
        .AddSingleton<IRaidRecapSource>(sp=>sp.GetRequiredService<WarcraftLogsV2Client>())
        .AddSingleton(_=>new RaidRecapCache())
        .AddSingleton(_=>new RaidRecapSessions())
        .AddSingleton<RaidRecapService>()
        .AddSingleton<IRaidRecapDiscord,RaidRecapDiscord>();
}
