using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    /// <summary>
    /// Owner-only commands for controlling static data sync (achievements, pets, mounts).
    /// Sync requests are processed by NinjaBotHelpers service.
    /// </summary>
    [Group("sync", "Static data sync control")]
    [RequireOwner]
    public class SyncCommands : NinjaBotBaseModule
    {
        private readonly ILogger<SyncCommands> _logger;

        public SyncCommands(IServiceScopeFactory scopeFactory, ILogger<SyncCommands> logger)
            : base(scopeFactory)
        {
            _logger = logger;
        }

        [SlashCommand("trigger", "Trigger a static data sync")]
        public async Task TriggerSync(
            [Summary("type", "Type of data to sync")]
            [Choice("Achievements", "achievements")]
            [Choice("Pets", "pets")]
            [Choice("Mounts", "mounts")]
            [Choice("Items", "items")]
            [Choice("Housing Decor", "housing_decor")]
            [Choice("Recipes", "recipes")]
            [Choice("All", "all")]
            string type,
            [Summary("fresh", "Clear existing data before syncing (full refresh)")]
            bool fresh = false)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var request = await WithDbAsync(async db =>
                {
                    // Check for existing pending request of same type
                    var existing = await db.StaticDataSyncRequests
                        .FirstOrDefaultAsync(r => r.SyncType == type && r.Status == "pending");

                    if (existing != null)
                    {
                        return null; // Signal that one already exists
                    }

                    // Clear existing data if fresh sync requested
                    if (fresh)
                    {
                        var cleared = type switch
                        {
                            "recipes" => await db.CraftableItems.ExecuteDeleteAsync(),
                            "achievements" => await db.WowAchievements.ExecuteDeleteAsync(),
                            "pets" => await db.WowPets.ExecuteDeleteAsync(),
                            "mounts" => await db.WowMounts.ExecuteDeleteAsync(),
                            _ => 0
                        };

                        if (cleared > 0)
                            _logger.LogInformation("Cleared {Count} {Type} records for fresh sync", cleared, type);
                    }

                    var req = new StaticDataSyncRequest
                    {
                        SyncType = type,
                        Status = "pending",
                        RequestedByUserId = (long)Context.User.Id,
                        RequestSource = "slash_command",
                        RequestedAt = DateTime.UtcNow
                    };
                    db.StaticDataSyncRequests.Add(req);
                    await db.SaveChangesAsync();
                    return req;
                });

                if (request == null)
                {
                    await FollowupAsync($"A sync request for **{type}** is already pending.", ephemeral: true);
                    return;
                }

                _logger.LogInformation("Sync request #{Id} queued for {Type} by user {UserId}",
                    request.Id, type, Context.User.Id);

                var freshNote = fresh ? " (fresh — existing data cleared)" : "";
                await FollowupAsync($"Sync request **#{request.Id}** queued for **{type}**{freshNote}.\n" +
                    $"The helpers service will process this within 60 seconds.", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating sync request");
                await FollowupAsync($"Error creating sync request: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("status", "Show sync status for all data types")]
        public async Task SyncStatus()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var (statuses, pendingRequests) = await WithDbAsync(async db =>
                {
                    var stats = await db.StaticDataSyncStatus.ToListAsync();
                    var pending = await db.StaticDataSyncRequests
                        .Where(r => r.Status == "pending" || r.Status == "in_progress")
                        .OrderBy(r => r.RequestedAt)
                        .ToListAsync();
                    return (stats, pending);
                });

                var embed = new EmbedBuilder()
                    .WithTitle("Static Data Sync Status")
                    .WithColor(Color.Blue)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                // Add status for each type
                var types = new[] { "achievements", "pets", "mounts", "items", "housing_decor", "recipes" };
                foreach (var type in types)
                {
                    var status = statuses.FirstOrDefault(s => s.SyncType == type);
                    var fieldValue = status == null
                        ? "Never synced"
                        : $"Last: {FormatTime(status.LastSyncCompleted)}\n" +
                          $"Status: {status.LastSyncStatus ?? "unknown"}\n" +
                          $"Items: {status.TotalItemsInDatabase?.ToString("N0") ?? "?"}\n" +
                          $"Next: {FormatTime(status.NextScheduledSync)}";

                    embed.AddField(char.ToUpper(type[0]) + type[1..], fieldValue, true);
                }

                // Add pending requests
                if (pendingRequests.Any())
                {
                    var pendingSb = new StringBuilder();
                    foreach (var req in pendingRequests.Take(5))
                    {
                        var statusEmoji = req.Status == "in_progress" ? ":arrows_counterclockwise:" : ":hourglass:";
                        pendingSb.AppendLine($"{statusEmoji} #{req.Id} - {req.SyncType} ({req.Status})");
                    }
                    embed.AddField("Pending Requests", pendingSb.ToString(), false);
                }
                else
                {
                    embed.AddField("Pending Requests", "None", false);
                }

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sync status");
                await FollowupAsync($"Error getting sync status: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("history", "Show recent sync request history")]
        public async Task SyncHistory(
            [Summary("limit", "Number of requests to show (1-25)")]
            [MinValue(1)]
            [MaxValue(25)]
            int limit = 10)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var requests = await WithDbAsync(async db =>
                {
                    return await db.StaticDataSyncRequests
                        .OrderByDescending(r => r.RequestedAt)
                        .Take(limit)
                        .ToListAsync();
                });

                if (!requests.Any())
                {
                    await FollowupAsync("No sync requests found.", ephemeral: true);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Sync Request History")
                    .WithColor(Color.DarkGrey)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                var sb = new StringBuilder();
                foreach (var req in requests)
                {
                    var statusEmoji = req.Status switch
                    {
                        "completed" => ":white_check_mark:",
                        "failed" => ":x:",
                        "in_progress" => ":arrows_counterclockwise:",
                        "pending" => ":hourglass:",
                        "cancelled" => ":no_entry:",
                        _ => ":grey_question:"
                    };

                    var stats = req.Status == "completed"
                        ? $" ({req.ItemsProcessed}+/{req.ItemsSkipped}=/{req.ItemsFailed}-)"
                        : "";

                    sb.AppendLine($"{statusEmoji} **#{req.Id}** {req.SyncType} - {req.Status}{stats}");
                    sb.AppendLine($"   {FormatTime(req.RequestedAt)} via {req.RequestSource ?? "unknown"}");
                }

                embed.WithDescription(sb.ToString());
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sync history");
                await FollowupAsync($"Error getting sync history: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("cancel", "Cancel a pending sync request")]
        public async Task CancelSync(
            [Summary("id", "Request ID to cancel")]
            long id)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var result = await WithDbAsync(async db =>
                {
                    var request = await db.StaticDataSyncRequests.FindAsync(id);
                    if (request == null)
                        return "not_found";

                    if (request.Status != "pending")
                        return $"cannot_cancel:{request.Status}";

                    request.Status = "cancelled";
                    request.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return "cancelled";
                });

                var response = result switch
                {
                    "not_found" => $"Sync request #{id} not found.",
                    "cancelled" => $"Sync request #{id} has been cancelled.",
                    var s when s.StartsWith("cannot_cancel:") =>
                        $"Cannot cancel request #{id} - status is '{s.Split(':')[1]}'.",
                    _ => "Unknown error"
                };

                await FollowupAsync(response, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling sync request");
                await FollowupAsync($"Error cancelling sync request: {ex.Message}", ephemeral: true);
            }
        }

        private static string FormatTime(DateTime? time)
        {
            if (time == null) return "N/A";
            var utc = DateTime.SpecifyKind(time.Value, DateTimeKind.Utc);
            return $"<t:{((DateTimeOffset)utc).ToUnixTimeSeconds()}:R>";
        }
    }
}
