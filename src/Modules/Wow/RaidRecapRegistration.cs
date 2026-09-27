using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Modules.Interactions.Wow;

namespace NinjaBotCore.Modules.Wow;

public static class RaidRecapRegistration
{
    public static IServiceCollection AddRaidRecap(this IServiceCollection services) => services
        .AddSingleton<IRaidRecapSource>(sp => sp.GetRequiredService<WarcraftLogsV2Client>())
        .AddSingleton(_ => new RaidRecapCache())
        .AddSingleton(_ => new RaidRecapSessions())
        .AddSingleton<RaidRecapService>()
        .AddSingleton<IRaidRecapDiscord, RaidRecapDiscord>()
        // Live card: posted once per raid log and refreshed while the raid runs.
        .AddSingleton<IRaidRecapLiveDiscord, RaidRecapLiveDiscord>()
        .AddSingleton<RaidRecapLiveGate>()
        .AddSingleton(sp => new RaidRecapLiveCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IRaidRecapSource>(),
            sp.GetRequiredService<RaidRecapService>(),
            sp.GetRequiredService<RaidRecapCache>(),
            sp.GetRequiredService<RaidRecapLiveGate>(),
            sp.GetRequiredService<IRaidRecapLiveDiscord>(),
            sp.GetRequiredService<ILogger<RaidRecapLiveCoordinator>>()));
}
