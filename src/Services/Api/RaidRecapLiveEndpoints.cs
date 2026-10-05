using System;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services.Api
{
    public static class RaidRecapLiveEndpoints
    {
        /// <summary>How the health is written: snake_case names and keys.</summary>
        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public static void MapRaidRecapLiveEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // The live raid recap watcher at a glance, for the owner's admin page. The same
            // numbers as /raid-recap-rollout Status.
            group.MapGet("/api/raid-recap-live/health", async () =>
            {
                var coordinator = deps.ServiceProvider.GetService<RaidRecapLiveCoordinator>();
                var gate = deps.ServiceProvider.GetService<RaidRecapLiveGate>();
                if (coordinator == null || gate == null)
                {
                    return Results.NotFound(new { error = "Live raid recap is not running" });
                }

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                var status = await RaidRecapLiveStatus.BuildAsync(
                    db,
                    await gate.ModeAsync(),
                    coordinator.Health.Snapshot(),
                    deps.ServiceProvider.GetService<WarcraftLogsV2Client>()?.LastRateLimit,
                    DateTimeOffset.UtcNow);
                return Results.Json(status, Json);
            });
        }
    }
}
