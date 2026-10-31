using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    /// <summary>
    /// Bot-owner commands, registered only to the owner's server.
    /// </summary>
    [NinjaBotCore.Attributes.OwnerGuildOnly]
    public class OwnerCommands : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<OwnerCommands> _logger;
        private readonly WarcraftLogsV2Client _warcraftLogsV2;
        private readonly HelpContentProvider _helpProvider;

        public OwnerCommands(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<OwnerCommands> logger,
            WarcraftLogsV2Client warcraftLogsV2,
            HelpContentProvider helpProvider)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
            _warcraftLogsV2 = warcraftLogsV2;
            _helpProvider = helpProvider;
        }

        [SlashCommand("numservers", "list number of servers the bot is in")]
        [RequireOwner]
        public async Task GetNumGuilds()
        {
            var client = (IDiscordClient)Context.Client;            
            var numGuilds = await client.GetGuildsAsync();
            await RespondAsync($"I am connected to {numGuilds.Count()} guilds!", ephemeral: true);
        }

        [SlashCommand("leave-server", "leave a server")]
        [RequireOwner]
        public async Task LeaveServer(ulong serverId)
        {
            await _client.GetGuild(serverId).LeaveAsync();
        }

        [SlashCommand("setstatus", "set status of the bot")]
        [RequireOwner]
        public async Task SetStatus(string args = null)
        {                        
            await _client.SetGameAsync(args);
            await RespondAsync(text: $"Status set to {args}", ephemeral: true);
        }

        [SlashCommand("blocklist", "block a user from using the bot")]
        [RequireOwner]
        public async Task BlockList(IGuildUser user, string reason = null)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            try
            {
                await WithDbAsync(async db =>
                {
                    var getUser = await db.Blacklist
                        .Where(b => b.DiscordUserId == (long)user.Id)
                        .FirstOrDefaultAsync();
                    if (getUser != null)
                    {
                        sb.AppendLine($"Removing {user.Username} from blocklist");
                        db.Blacklist.Remove(getUser);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(reason))
                        {
                            reason = "just because";
                        }
                        db.Blacklist.Add(new Blacklist
                        {
                            DiscordUserId = (long)user.Id,
                            DiscordUserName = user.Username,
                            Reason = reason,
                            WhenBlacklisted = DateTime.UtcNow
                        });
                        sb.AppendLine($"Adding [**{user.Username}**] to blocklist -> [*{reason}*]");
                    }
                    embed.Title = "[Blocklist]";
                    embed.Description = sb.ToString();
                    await db.SaveChangesAsync();
                });
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error updating blocklist for [{user.Username}] -> [{ex.Message}]");
            }
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("force-greeting-clear", "force a greeting clear")]
        [RequireOwner]
        public async Task ForceGreetingClear(long serverId)
        {
            var greetingInfo = await WithDbAsync(async db =>
            {
                return await db.ServerGreetings.FirstOrDefaultAsync(g => g.DiscordGuildId == serverId);
            });

            if (greetingInfo != null)
            {
                try
                {
                    await WithDbAsync(async db =>
                    {
                        var greeting = await db.ServerGreetings.FirstOrDefaultAsync(g => g.DiscordGuildId == serverId);
                        if (greeting != null)
                        {
                            db.Remove(greeting);
                            await db.SaveChangesAsync();
                        }
                    });
                    await RespondAsync("Cleared!");
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error clearing greeting -> [{ex.Message}]", ephemeral: true);
                }
            }
            else
            {
                await RespondAsync($"No association found for [{serverId}]!", ephemeral: true);
            }
        }

        [SlashCommand("add-wow-resource", "add a wow resource")]
        [RequireOwner]
        public async Task AddWoWResource(string args = null)
        {
            if (args != null)
            {
                try
                {
                    var parts = args.Split(',');
                    if (parts.Length == 4)
                    {
                        await WithDbAsync(async db =>
                        {
                            db.WowResources.Add(new WowResources
                            {
                                ClassName = parts[0].Trim(),
                                Specialization = parts[1].Trim(),
                                Resource = parts[2].Trim(),
                                ResourceDescription = parts[3].Trim(),
                            });
                            await db.SaveChangesAsync();
                        });
                    }
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error adding resource: [{ex.Message}]", ephemeral: true);
                }
            }
        }

        [SlashCommand("remove-wow-resource", "remove wow resource")]
        [RequireOwner]
        public async Task RemoveWoWResource(int resourceId = 0)
        {
            if (resourceId > 0)
            {
                try
                {
                    await WithDbAsync(async db =>
                    {
                        var resource = await db.WowResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                        if (resource != null)
                        {
                            db.WowResources.Remove(resource);
                            await db.SaveChangesAsync();
                        }
                    });
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error removing resource: [{ex.Message}]", ephemeral: true);
                }
            }
        }

        [SlashCommand("refresh-raid-tier", "Refresh current raid tier from WarcraftLogs API")]
        [RequireOwner]
        public async Task RefreshRaidTier(
            [Summary("expansion", "Expansion ID (0=auto, 10=The War Within, 9=Dragonflight)")]
            int expansionId = 0)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Get current tier from database
                var oldTier = await WithDbAsync(db => db.CurrentRaidTier.FirstOrDefaultAsync());
                var oldTierName = oldTier?.RaidName ?? "none";
                var oldZoneId = oldTier?.WclZoneId ?? 0;

                // Get new tier from v2 API
                var zoneTier = await _warcraftLogsV2.GetCurrentRaidTierAsync(expansionId);

                if (zoneTier == null)
                {
                    await FollowupAsync("Failed to detect current raid tier from API.", ephemeral: true);
                    return;
                }

                var defaultPartition = zoneTier.Partitions?.FirstOrDefault(p => p.IsDefault == true)
                    ?? zoneTier.Partitions?.LastOrDefault();

                // Update database
                await WithDbAsync(async db =>
                {
                    var existingTier = await db.CurrentRaidTier.FirstOrDefaultAsync();
                    if (existingTier != null)
                    {
                        existingTier.WclZoneId = zoneTier.Id;
                        existingTier.RaidName = zoneTier.Name;
                        existingTier.Partition = defaultPartition?.Id;
                    }
                    else
                    {
                        db.CurrentRaidTier.Add(new CurrentRaidTier
                        {
                            WclZoneId = zoneTier.Id,
                            RaidName = zoneTier.Name,
                            Partition = defaultPartition?.Id
                        });
                    }
                    await db.SaveChangesAsync();
                });

                // Clear in-memory cache so all commands pick up the new tier immediately
                _warcraftLogsV2.ClearRaidTierCache();

                var embed = new EmbedBuilder()
                    .WithTitle("Raid Tier Refreshed")
                    .WithColor(new Color(0, 200, 100))
                    .AddField("Previous", $"{oldTierName} (Zone ID: {oldZoneId})", true)
                    .AddField("Current", $"{zoneTier.Name} (Zone ID: {zoneTier.Id})", true)
                    .AddField("Partition", defaultPartition?.Id.ToString() ?? "default", true)
                    .WithFooter($"Expansion ID: {expansionId}")
                    .Build();

                await FollowupAsync(embed: embed, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing raid tier");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("regenerate-help", "Regenerate help-commands.json by scanning all slash commands")]
        [RequireOwner]
        public async Task RegenerateHelp()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _helpProvider.RegenerateHelpContent();

                var content = _helpProvider.GetHelpContent();
                var commandCount = content?.Metadata?.TotalCommands ?? 0;
                var categoryCount = content?.Categories?.Count ?? 0;

                await FollowupAsync(
                    $"✅ Successfully regenerated help-commands.json!\n\n" +
                    $"**Commands found:** {commandCount}\n" +
                    $"**Categories:** {categoryCount}\n" +
                    $"**Last updated:** {content?.Metadata?.LastUpdated ?? "unknown"}",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error regenerating help file");
                await FollowupAsync($"❌ Error regenerating help file: {ex.Message}", ephemeral: true);
            }
        }

        [SlashCommand("listclassicfights", "list classic fights")]
        [RequireOwner]
        public async Task GetClassicZones(string zoneName = null)
        {
            await DeferAsync();
            var sb = new StringBuilder();

            try
            {
                // Get all expansions for Classic
                var expansions = await _warcraftLogsV2.GetExpansionsAsync(WowGameVersion.Classic);

                if (expansions == null || expansions.Count == 0)
                {
                    await FollowupAsync("No expansions found for Classic.");
                    return;
                }

                // Get zones from all expansions
                var allZones = new List<WclV2ZoneDetail>();
                foreach (var expansion in expansions)
                {
                    var zones = await _warcraftLogsV2.GetZonesAsync(expansion.Id, WowGameVersion.Classic);
                    if (zones != null)
                    {
                        allZones.AddRange(zones);
                    }
                }

                if (allZones.Count == 0)
                {
                    await FollowupAsync("No zones found for Classic.");
                    return;
                }

                WclV2ZoneDetail targetZone = null;

                if (string.IsNullOrEmpty(zoneName))
                {
                    // Show all zones
                    sb.AppendLine("**Classic Zones:**");
                    foreach (var zone in allZones.OrderBy(z => z.Id))
                    {
                        sb.AppendLine($"[{zone.Id}] {zone.Name} - {zone.Encounters?.Count ?? 0} encounters");
                    }
                }
                else
                {
                    // Handle shortcuts
                    var searchName = zoneName.ToLower() switch
                    {
                        "bwl" => "blackwing lair",
                        "mc" => "molten core",
                        "aq40" => "temple of ahn'qiraj",
                        "aq20" => "ruins of ahn'qiraj",
                        "naxx" => "naxxramas",
                        "ony" => "onyxia's lair",
                        "icc" => "icecrown citadel",
                        "uld" => "ulduar",
                        "toc" => "trial of the crusader",
                        _ => zoneName.ToLower()
                    };

                    targetZone = allZones.FirstOrDefault(z => z.Name.ToLower().Contains(searchName));

                    if (targetZone != null && targetZone.Encounters != null)
                    {
                        sb.AppendLine($"**Encounters for [{targetZone.Name}]:**");
                        foreach (var encounter in targetZone.Encounters)
                        {
                            sb.AppendLine($"  [{encounter.Id}] {encounter.Name}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"Zone '{zoneName}' not found. Use command without args to list all zones.");
                    }
                }

                // Discord has a 2000 char limit
                var response = sb.ToString();
                if (response.Length > 1900)
                {
                    response = response.Substring(0, 1900) + "\n... (truncated)";
                }
                await FollowupAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching classic zones");
                await FollowupAsync($"Error: {ex.Message}");
            }
        }

        [SlashCommand("listvanillafights", "list vanilla fights")]
        [RequireOwner]
        public async Task GetvanillaZones(string zoneName = null)
        {
            await DeferAsync();
            var sb = new StringBuilder();

            try
            {
                // Get all expansions for Vanilla
                var expansions = await _warcraftLogsV2.GetExpansionsAsync(WowGameVersion.Vanilla);

                if (expansions == null || expansions.Count == 0)
                {
                    await FollowupAsync("No expansions found for Vanilla.");
                    return;
                }

                // Get zones from the first/main expansion
                var allZones = new List<WclV2ZoneDetail>();
                foreach (var expansion in expansions)
                {
                    var zones = await _warcraftLogsV2.GetZonesAsync(expansion.Id, WowGameVersion.Vanilla);
                    if (zones != null)
                    {
                        allZones.AddRange(zones);
                    }
                }

                if (allZones.Count == 0)
                {
                    await FollowupAsync("No zones found for Vanilla.");
                    return;
                }

                WclV2ZoneDetail targetZone = null;

                if (string.IsNullOrEmpty(zoneName))
                {
                    // Show all zones
                    sb.AppendLine("**Vanilla Zones:**");
                    foreach (var zone in allZones.OrderBy(z => z.Id))
                    {
                        sb.AppendLine($"[{zone.Id}] {zone.Name} - {zone.Encounters?.Count ?? 0} encounters");
                    }
                }
                else
                {
                    // Handle shortcuts
                    var searchName = zoneName.ToLower() switch
                    {
                        "bwl" => "blackwing lair",
                        "mc" => "molten core",
                        "aq40" => "temple of ahn'qiraj",
                        "aq20" => "ruins of ahn'qiraj",
                        "naxx" => "naxxramas",
                        "ony" => "onyxia's lair",
                        _ => zoneName.ToLower()
                    };

                    targetZone = allZones.FirstOrDefault(z => z.Name.ToLower().Contains(searchName));

                    if (targetZone != null && targetZone.Encounters != null)
                    {
                        sb.AppendLine($"**Encounters for [{targetZone.Name}]:**");
                        foreach (var encounter in targetZone.Encounters)
                        {
                            sb.AppendLine($"  [{encounter.Id}] {encounter.Name}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"Zone '{zoneName}' not found. Use command without args to list all zones.");
                    }
                }

                // Discord has a 2000 char limit
                var response = sb.ToString();
                if (response.Length > 1900)
                {
                    response = response.Substring(0, 1900) + "\n... (truncated)";
                }
                await FollowupAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching vanilla zones");
                await FollowupAsync($"Error: {ex.Message}");
            }
        }
    }
}
