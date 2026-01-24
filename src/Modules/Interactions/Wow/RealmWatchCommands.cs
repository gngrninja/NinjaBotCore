using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    [Group("realm-watch", "Realm status alert commands")]
    public class RealmWatchCommands : NinjaBotBaseModule
    {
        private readonly ILogger<RealmWatchCommands> _logger;
        private readonly WowApi _wowApi;

        public RealmWatchCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<RealmWatchCommands> logger,
            WowApi wowApi)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
        }

        [SlashCommand("add", "Add realm status alerts")]
        public async Task AddWatch(
            [Summary("realm", "Realm to watch")][Autocomplete(typeof(RealmAutocomplete))] string realm,
            [Summary("region", "Region (us/eu)")][Choice("US", "us")][Choice("EU", "eu")] string region = "us",
            [Summary("channel", "Channel for alerts (leave empty for DM)")] ITextChannel channel = null,
            [Summary("alert-online", "Alert when realm comes online")] bool alertOnline = true,
            [Summary("alert-offline", "Alert when realm goes offline")] bool alertOffline = true,
            [Summary("alert-queue", "Alert on queue changes")] bool alertQueue = true)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Get realm info and connected realm ID
                var (realmInfo, connectedRealmId, error) = await GetRealmWithConnectedIdAsync(realm, region);

                if (error != null)
                {
                    await FollowupAsync(error, ephemeral: true);
                    return;
                }

                var realmSlug = RealmHelper.ToSlug(realm);

                // Check if subscription already exists
                var existingSub = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                    .FirstOrDefaultAsync(s =>
                        s.GuildId == (long)Context.Guild.Id &&
                        s.UserId == (long)Context.User.Id &&
                        s.RealmSlug == realmSlug &&
                        s.Region == region));

                if (existingSub != null)
                {
                    await FollowupAsync($"You already have a watch on **{realmInfo.Name}** ({region.ToUpper()}). Use `/realm-watch remove` to remove it first.", ephemeral: true);
                    return;
                }

                // Create subscription
                var subscription = new RealmWatchSubscription
                {
                    GuildId = (long)Context.Guild.Id,
                    UserId = (long)Context.User.Id,
                    ChannelId = channel != null ? (long)channel.Id : null,
                    RealmSlug = realmSlug,
                    RealmName = realmInfo.Name,
                    Region = region,
                    ConnectedRealmId = connectedRealmId,
                    AlertOnOnline = alertOnline,
                    AlertOnOffline = alertOffline,
                    AlertOnQueue = alertQueue,
                    CreatedAt = DateTime.UtcNow
                };

                await WithDbAsync(async db =>
                {
                    db.RealmWatchSubscriptions.Add(subscription);
                    await db.SaveChangesAsync();
                });

                var alertTypes = new StringBuilder();
                if (alertOnline) alertTypes.Append("Online ");
                if (alertOffline) alertTypes.Append("Offline ");
                if (alertQueue) alertTypes.Append("Queue");

                var destination = channel != null ? $"<#{channel.Id}>" : "DM";

                var embed = new EmbedBuilder()
                    .WithTitle("Realm Watch Added")
                    .WithColor(Color.Green)
                    .AddField("Realm", realmInfo.Name, true)
                    .AddField("Region", region.ToUpper(), true)
                    .AddField("Alerts To", destination, true)
                    .AddField("Alert Types", alertTypes.ToString().Trim(), false)
                    .WithFooter("You'll receive alerts when realm status changes")
                    .Build();

                await FollowupAsync(embed: embed, ephemeral: true);

                _logger.LogInformation("User {UserId} added realm watch for {Realm} ({Region}) in guild {GuildId}",
                    Context.User.Id, realmInfo.Name, region, Context.Guild.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding realm watch");
                await FollowupAsync("An error occurred while adding the realm watch. Please try again.", ephemeral: true);
            }
        }

        [SlashCommand("remove", "Remove realm watch")]
        public async Task RemoveWatch(
            [Summary("realm", "Realm to stop watching")][Autocomplete(typeof(RealmAutocomplete))] string realm,
            [Summary("region", "Region (us/eu)")][Choice("US", "us")][Choice("EU", "eu")] string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var realmSlug = RealmHelper.ToSlug(realm);

                var removed = await WithDbAsync(async db =>
                {
                    var sub = await db.RealmWatchSubscriptions
                        .FirstOrDefaultAsync(s =>
                            s.GuildId == (long)Context.Guild.Id &&
                            s.UserId == (long)Context.User.Id &&
                            s.RealmSlug == realmSlug &&
                            s.Region == region);

                    if (sub != null)
                    {
                        db.RealmWatchSubscriptions.Remove(sub);
                        await db.SaveChangesAsync();
                        return true;
                    }
                    return false;
                });

                if (removed)
                {
                    await FollowupAsync($"Removed realm watch for **{realm}** ({region.ToUpper()}).", ephemeral: true);
                    _logger.LogInformation("User {UserId} removed realm watch for {Realm} ({Region})",
                        Context.User.Id, realm, region);
                }
                else
                {
                    await FollowupAsync($"You don't have a watch on **{realm}** ({region.ToUpper()}).", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing realm watch");
                await FollowupAsync("An error occurred while removing the realm watch. Please try again.", ephemeral: true);
            }
        }

        [SlashCommand("list", "List your realm watches")]
        public async Task ListWatches()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var subscriptions = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                    .Where(s => s.GuildId == (long)Context.Guild.Id && s.UserId == (long)Context.User.Id)
                    .OrderBy(s => s.Region)
                    .ThenBy(s => s.RealmName)
                    .ToListAsync());

                if (!subscriptions.Any())
                {
                    await FollowupAsync("You don't have any realm watches set up. Use `/realm-watch add` to add one.", ephemeral: true);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Your Realm Watches")
                    .WithColor(Color.Blue);

                var sb = new StringBuilder();
                foreach (var sub in subscriptions)
                {
                    var destination = sub.ChannelId.HasValue ? $"<#{sub.ChannelId}>" : "DM";
                    var alerts = new StringBuilder();
                    if (sub.AlertOnOnline) alerts.Append("🟢");
                    if (sub.AlertOnOffline) alerts.Append("🔴");
                    if (sub.AlertOnQueue) alerts.Append("⏳");

                    sb.AppendLine($"**{sub.RealmName}** ({sub.Region.ToUpper()})");
                    sb.AppendLine($"  Alerts: {alerts} → {destination}");
                }

                embed.Description = sb.ToString();
                embed.WithFooter($"{subscriptions.Count} realm watch(es)");

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing realm watches");
                await FollowupAsync("An error occurred while listing realm watches. Please try again.", ephemeral: true);
            }
        }

        [SlashCommand("status", "Check current realm status")]
        public async Task CheckStatus(
            [Summary("realm", "Realm to check")][Autocomplete(typeof(RealmAutocomplete))] string realm,
            [Summary("region", "Region (us/eu)")][Choice("US", "us")][Choice("EU", "eu")] string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Get realm info and connected realm ID
                var (realmInfo, connectedRealmId, error) = await GetRealmWithConnectedIdAsync(realm, region);

                if (error != null)
                {
                    await FollowupAsync(error, ephemeral: true);
                    return;
                }

                // Fetch fresh status from Blizzard API
                var status = await _wowApi.GetConnectedRealmStatusAsync(connectedRealmId, region);

                if (status == null)
                {
                    await FollowupAsync($"Could not get status for **{realmInfo.Name}**. Please try again.", ephemeral: true);
                    return;
                }

                var isOnline = status.Status?.Type?.ToLower() == "up";
                var hasQueue = status.HasQueue;

                var statusEmoji = isOnline ? "🟢" : "🔴";
                var statusText = isOnline ? "Online" : "Offline";
                var queueText = hasQueue ? "Yes (queue active)" : "No";

                var embed = new EmbedBuilder()
                    .WithTitle($"{statusEmoji} {realmInfo.Name}")
                    .WithColor(isOnline ? Color.Green : Color.Red)
                    .AddField("Status", statusText, true)
                    .AddField("Queue", queueText, true)
                    .AddField("Region", region.ToUpper(), true)
                    .AddField("Type", realmInfo.Type ?? "Normal", true)
                    .AddField("Population", realmInfo.Population ?? "Unknown", true)
                    .AddField("Timezone", realmInfo.Timezone ?? "Unknown", true)
                    .WithFooter("Live data from Blizzard API");

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking realm status");
                await FollowupAsync("An error occurred while checking realm status. Please try again.", ephemeral: true);
            }
        }

        [SlashCommand("test", "Send test alerts to verify your watch is working")]
        public async Task TestWatch(
            [Summary("realm", "Realm to test")][Autocomplete(typeof(WatchedRealmAutocomplete))] string realm)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var realmSlug = RealmHelper.ToSlug(realm);

                var result = await WithDbAsync(async db =>
                {
                    // Find user's subscription for this realm (region is stored in subscription)
                    var subscription = await db.RealmWatchSubscriptions
                        .FirstOrDefaultAsync(s =>
                            s.UserId == (long)Context.User.Id &&
                            s.RealmSlug == realmSlug);

                    if (subscription == null)
                        return (false, (string)null, (string)null);

                    // Flip the cached status - watcher will detect change and alert
                    var cached = await db.RealmStatusCache
                        .FirstOrDefaultAsync(c =>
                            c.Region.ToLower() == subscription.Region.ToLower() &&
                            c.ConnectedRealmId == subscription.ConnectedRealmId);

                    if (cached != null)
                    {
                        var oldStatus = cached.IsOnline ? "online" : "offline";
                        cached.IsOnline = !cached.IsOnline;
                        await db.SaveChangesAsync();
                        var newStatus = cached.IsOnline ? "online" : "offline";
                        return (true, subscription.RealmName, $"{oldStatus} → {newStatus}");
                    }

                    return (true, subscription.RealmName, "no cached status yet - will alert on first check");
                });

                if (!result.Item1)
                {
                    await FollowupAsync($"You don't have a watch on **{realm}**. Use `/realm-watch add` first.", ephemeral: true);
                    return;
                }

                await FollowupAsync($"Flipped cached status for **{result.Item2}** ({result.Item3}). Alert will be sent on next watcher cycle (within 60 seconds).", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering test alert");
                await FollowupAsync("An error occurred. Please try again.", ephemeral: true);
            }
        }

        #region Helper Methods

        /// <summary>
        /// Gets realm info and connected realm ID, fetching from API and caching if needed.
        /// Returns (realmInfo, connectedRealmId, errorMessage). If errorMessage is not null, the operation failed.
        /// </summary>
        private async Task<(WowRealms realmInfo, long connectedRealmId, string error)> GetRealmWithConnectedIdAsync(string realm, string region)
        {
            var realmSlug = RealmHelper.ToSlug(realm);

            var realmInfo = await WithDbAsync(async db => await db.WowRealms
                .FirstOrDefaultAsync(r => r.Slug == realmSlug && r.Region.ToLower() == region.ToLower()));

            if (realmInfo == null)
            {
                return (null, 0, $"Could not find realm **{realm}** in region **{region.ToUpper()}**. Please check the spelling.");
            }

            // Return cached connected realm ID if available
            if (realmInfo.ConnectedRealmId.HasValue)
            {
                return (realmInfo, realmInfo.ConnectedRealmId.Value, null);
            }

            // Fetch connected realm ID from API
            try
            {
                var singleRealmInfo = await _wowApi.GetSingleRealmInfoAsync(realmSlug, region);
                if (singleRealmInfo?.ConnectedRealm?.Href == null)
                {
                    _logger.LogWarning("GetSingleRealmInfoAsync returned null connected realm data for {RealmSlug}", realmSlug);
                    return (realmInfo, 0, $"Could not get connected realm data for **{realmInfo.Name}**. Please try again later.");
                }

                var connectedRealmInfo = await _wowApi.GetConnectedRealmInfoAsync(singleRealmInfo.ConnectedRealm.Href.ToString(), region);
                if (connectedRealmInfo == null)
                {
                    _logger.LogWarning("GetConnectedRealmInfoAsync returned null for {RealmSlug}", realmSlug);
                    return (realmInfo, 0, $"Could not get connected realm data for **{realmInfo.Name}**. Please try again later.");
                }

                var connectedRealmId = connectedRealmInfo.Id;

                // Cache it for future use
                await WithDbAsync(async db =>
                {
                    var realmToUpdate = await db.WowRealms.FindAsync(realmInfo.Id);
                    if (realmToUpdate != null)
                    {
                        realmToUpdate.ConnectedRealmId = connectedRealmId;
                        await db.SaveChangesAsync();
                    }
                });

                return (realmInfo, connectedRealmId, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch connected realm ID for {RealmSlug}", realmSlug);
                return (realmInfo, 0, $"Could not get connected realm data for **{realmInfo.Name}**. Please try again later.");
            }
        }

        #endregion
    }
}
