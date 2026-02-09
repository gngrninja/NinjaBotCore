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
            [Summary("channel", "Channel for alerts (leave empty for DM, requires Admin)")] ITextChannel channel = null,
            [Summary("alert-online", "Alert when realm comes online")] bool alertOnline = true,
            [Summary("alert-offline", "Alert when realm goes offline")] bool alertOffline = true,
            [Summary("alert-queue", "Alert on queue changes")] bool alertQueue = true)
        {
            await DeferAsync(ephemeral: true);

            // Channel alerts require Administrator permission
            if (channel != null)
            {
                var guildUser = Context.User as IGuildUser;
                if (guildUser == null || !guildUser.GuildPermissions.Administrator)
                {
                    await FollowupAsync("Only server administrators can set up channel alerts. Leave the channel empty to receive alerts via DM instead.", ephemeral: true);
                    return;
                }
            }

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
                // Channel alerts: one per guild per realm
                // DM alerts: one per user per realm
                if (channel != null)
                {
                    // Check for existing channel-based subscription for this guild+realm
                    var existingChannelSub = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                        .FirstOrDefaultAsync(s =>
                            s.GuildId == (long)Context.Guild.Id &&
                            s.ChannelId.HasValue &&
                            s.RealmSlug == realmSlug &&
                            s.Region == region));

                    if (existingChannelSub != null)
                    {
                        await FollowupAsync($"This server already has a channel alert for **{realmInfo.Name}** ({region.ToUpper()}) in <#{existingChannelSub.ChannelId}>. Remove it first with `/realm-watch remove`.", ephemeral: true);
                        return;
                    }
                }
                else
                {
                    // Check for existing DM subscription for this user+realm
                    var existingDmSub = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                        .FirstOrDefaultAsync(s =>
                            s.UserId == (long)Context.User.Id &&
                            !s.ChannelId.HasValue &&
                            s.RealmSlug == realmSlug &&
                            s.Region == region));

                    if (existingDmSub != null)
                    {
                        await FollowupAsync($"You already have a DM alert for **{realmInfo.Name}** ({region.ToUpper()}). Use `/realm-watch remove` to remove it first.", ephemeral: true);
                        return;
                    }
                }

                // Check limits based on alert type
                const int MaxChannelWatchesPerGuild = 5;
                const int MaxDmWatchesPerUser = 4;

                if (channel != null)
                {
                    // Channel alerts: count only channel-based watches for this guild
                    var channelWatchCount = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                        .CountAsync(s => s.GuildId == (long)Context.Guild.Id && s.ChannelId.HasValue));

                    if (channelWatchCount >= MaxChannelWatchesPerGuild)
                    {
                        await FollowupAsync($"This server has reached the maximum of {MaxChannelWatchesPerGuild} channel realm watches. Remove an existing channel watch first with `/realm-watch remove`.", ephemeral: true);
                        return;
                    }
                }
                else
                {
                    // DM alerts: count only DM watches for this user (across all guilds)
                    var dmWatchCount = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                        .CountAsync(s => s.UserId == (long)Context.User.Id && !s.ChannelId.HasValue));

                    if (dmWatchCount >= MaxDmWatchesPerUser)
                    {
                        await FollowupAsync($"You have reached the maximum of {MaxDmWatchesPerUser} DM realm watches. Remove an existing DM watch first with `/realm-watch remove`.", ephemeral: true);
                        return;
                    }
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
            [Summary("watch", "Select a watch to remove")][Autocomplete(typeof(WatchedRealmRemoveAutocomplete))] string watch)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Parse the encoded value: "type~realmSlug~region"
                var parts = watch.Split('~');
                if (parts.Length != 3 || watch == "none")
                {
                    await FollowupAsync("Please select a valid watch from the list.", ephemeral: true);
                    return;
                }

                var type = parts[0];
                var realmSlug = parts[1];
                var region = parts[2];
                var isAdmin = (Context.User as IGuildUser)?.GuildPermissions.Administrator ?? false;

                if (type == "channel")
                {
                    if (!isAdmin)
                    {
                        await FollowupAsync("Only server administrators can remove channel alerts.", ephemeral: true);
                        return;
                    }

                    var result = await WithDbAsync(async db =>
                    {
                        var sub = await db.RealmWatchSubscriptions
                            .FirstOrDefaultAsync(s =>
                                s.GuildId == (long)Context.Guild.Id &&
                                s.ChannelId.HasValue &&
                                s.RealmSlug == realmSlug &&
                                s.Region == region);

                        if (sub != null)
                        {
                            var realmName = sub.RealmName;
                            db.RealmWatchSubscriptions.Remove(sub);
                            await db.SaveChangesAsync();
                            return (true, realmName);
                        }
                        return (false, (string)null);
                    });

                    if (result.Item1)
                    {
                        await FollowupAsync($"Removed channel alert for **{result.Item2}** ({region.ToUpper()}).", ephemeral: true);
                        _logger.LogInformation("Admin {UserId} removed channel alert for {Realm} ({Region}) in guild {GuildId}",
                            Context.User.Id, result.Item2, region, Context.Guild.Id);
                    }
                    else
                    {
                        await FollowupAsync($"Could not find that channel alert.", ephemeral: true);
                    }
                }
                else
                {
                    // Remove user's DM alert
                    var result = await WithDbAsync(async db =>
                    {
                        var sub = await db.RealmWatchSubscriptions
                            .FirstOrDefaultAsync(s =>
                                s.UserId == (long)Context.User.Id &&
                                !s.ChannelId.HasValue &&
                                s.RealmSlug == realmSlug &&
                                s.Region == region);

                        if (sub != null)
                        {
                            var realmName = sub.RealmName;
                            db.RealmWatchSubscriptions.Remove(sub);
                            await db.SaveChangesAsync();
                            return (true, realmName);
                        }
                        return (false, (string)null);
                    });

                    if (result.Item1)
                    {
                        await FollowupAsync($"Removed DM alert for **{result.Item2}** ({region.ToUpper()}).", ephemeral: true);
                        _logger.LogInformation("User {UserId} removed DM alert for {Realm} ({Region})",
                            Context.User.Id, result.Item2, region);
                    }
                    else
                    {
                        await FollowupAsync($"Could not find that DM alert.", ephemeral: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing realm watch");
                await FollowupAsync("An error occurred while removing the realm watch. Please try again.", ephemeral: true);
            }
        }

        [SlashCommand("list", "List realm watches")]
        public async Task ListWatches()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Get user's DM alerts
                var dmAlerts = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                    .Where(s => s.UserId == (long)Context.User.Id && !s.ChannelId.HasValue)
                    .OrderBy(s => s.Region)
                    .ThenBy(s => s.RealmName)
                    .ToListAsync());

                // Get guild's channel alerts (visible to all)
                var channelAlerts = await WithDbAsync(async db => await db.RealmWatchSubscriptions
                    .Where(s => s.GuildId == (long)Context.Guild.Id && s.ChannelId.HasValue)
                    .OrderBy(s => s.Region)
                    .ThenBy(s => s.RealmName)
                    .ToListAsync());

                if (!dmAlerts.Any() && !channelAlerts.Any())
                {
                    await FollowupAsync("No realm watches set up. Use `/realm-watch add` to add one.", ephemeral: true);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Realm Watches")
                    .WithColor(Color.Blue);

                var sb = new StringBuilder();

                if (channelAlerts.Any())
                {
                    sb.AppendLine("**Server Channel Alerts**");
                    foreach (var sub in channelAlerts)
                    {
                        var alerts = new StringBuilder();
                        if (sub.AlertOnOnline) alerts.Append("🟢");
                        if (sub.AlertOnOffline) alerts.Append("🔴");
                        if (sub.AlertOnQueue) alerts.Append("⏳");

                        sb.AppendLine($"• **{sub.RealmName}** ({sub.Region.ToUpper()}) {alerts} → <#{sub.ChannelId}>");
                    }
                    sb.AppendLine();
                }

                if (dmAlerts.Any())
                {
                    sb.AppendLine("**Your DM Alerts**");
                    foreach (var sub in dmAlerts)
                    {
                        var alerts = new StringBuilder();
                        if (sub.AlertOnOnline) alerts.Append("🟢");
                        if (sub.AlertOnOffline) alerts.Append("🔴");
                        if (sub.AlertOnQueue) alerts.Append("⏳");

                        sb.AppendLine($"• **{sub.RealmName}** ({sub.Region.ToUpper()}) {alerts}");
                    }
                }

                embed.Description = sb.ToString();
                embed.WithFooter($"{channelAlerts.Count} channel alert(s), {dmAlerts.Count} DM alert(s)");

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
