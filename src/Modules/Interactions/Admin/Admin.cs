using Discord;
using Discord.Net;
using Discord.WebSocket;
using Discord.Interactions;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class Admin : NinjaBotBaseModule
    {
        private readonly IConfigurationRoot _config;
        private readonly ILogger<Admin> _logger;
        private readonly WordFilterService _wordFilterService;
        private readonly WowCacheService _wowCache;
        private readonly WowUtilities _wowUtils;

        // Event handling is now done by WordFilterService
        // This module only handles slash commands
        public Admin(
            IServiceScopeFactory scopeFactory,
            ILogger<Admin> logger,
            IConfigurationRoot config,
            WordFilterService wordFilterService,
            WowCacheService wowCache,
            WowUtilities wowUtils)
            : base(scopeFactory)
        {
            _logger = logger;
            _config = config;
            _wordFilterService = wordFilterService;
            _wowCache = wowCache;
            _wowUtils = wowUtils;
            _logger.LogInformation("Admin module loaded!");
        }

        [SlashCommand("list-wow-resources", "list wow resources")]
        public async Task ListWoWResource(string args = null)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                await RespondAsync("Please provide a search term (e.g., class name).", ephemeral: true);
                return;
            }

            var resources = await WithDbAsync(async db =>
            {
                return await db.WowResources.Where(r => EF.Functions.ILike(r.ClassName, $"%{args}%")).ToListAsync();
            });

            if (resources != null && resources.Any())
            {
                var embed = new EmbedBuilder();
                embed.Title = $"WoW Resource List Search: [{args}]";
                foreach (var resource in resources)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"Class: [{resource.ClassName}]");
                    sb.AppendLine($"Specialization: [{resource.Specialization}]");
                    sb.AppendLine($"Resource: [{resource.Resource}]");
                    sb.AppendLine($"ResourceDescription: [{resource.ResourceDescription}]");
                    embed.AddField(new EmbedFieldBuilder
                    {
                        Name = $"{resource.Id}",
                        Value = sb.ToString()
                    });
                }
                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
            else
            {
                await RespondAsync($"No WoW resources found matching '{args}'.", ephemeral: true);
            }
        }

        [SlashCommand("add-word", "add word to blocklist")]
        [RequireUserPermission(GuildPermission.Administrator)]
        [DefaultMemberPermissions(GuildPermission.Administrator)]
        public async Task AddWord(string word)
        {
            var sb = new StringBuilder();
            var serverId = (long)Context.Guild.Id;

            var wasAdded = await WithDbAsync(async db =>
            {
                var foundWord = await db.WordList
                    .FirstOrDefaultAsync(w => w.ServerId == serverId && w.Word.ToLower() == word.ToLower());

                if (foundWord != null)
                {
                    sb.AppendLine($"[{word}] is already in the list!");
                    return false;
                }
                else
                {
                    sb.AppendLine($"Adding [{word}] to the list!");
                    db.Add(new WordList
                    {
                        ServerId = serverId,
                        ServerName = Context.Guild.Name,
                        Word = word,
                        SetById = (long)Context.User.Id
                    });
                    await db.SaveChangesAsync();
                    return true;
                }
            });

            // Invalidate cache if word was added
            if (wasAdded)
            {
                _wordFilterService.InvalidateWordListCache(serverId);
            }

            await RespondAsync(sb.ToString(), ephemeral: true);
        }        
    
        [SlashCommand("remove-word", "remove a word from the blocklist")]
        [RequireUserPermission(GuildPermission.Administrator)]
        [DefaultMemberPermissions(GuildPermission.Administrator)]
        public async Task RemoveWord(string word)
        {
            var sb = new StringBuilder();
            var serverId = (long)Context.Guild.Id;

            var deletedCount = await WithDbAsync(async db =>
            {
                try
                {
                    var searchWord = word.ToLower();
                    var count = await db.WordList
                        .Where(w => w.ServerId == serverId && w.Word.ToLower() == searchWord)
                        .ExecuteDeleteAsync();

                    if (count > 0)
                    {
                        sb.AppendLine($"[{word}] removed!");
                    }
                    else
                    {
                        sb.AppendLine($"[{word}] not found in the database!");
                    }

                    return count;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Error attempting to remove: [{word}] -> [{ex.Message}]");
                    return 0;
                }
            });

            // Invalidate cache if word was removed
            if (deletedCount > 0)
            {
                _wordFilterService.InvalidateWordListCache(serverId);
            }

            await RespondAsync(sb.ToString(), ephemeral: true);
        }

        [SlashCommand("wcl-refresh", "Clear WarcraftLogs cache entries")]
        [RequireUserPermission(GuildPermission.Administrator)]
        [DefaultMemberPermissions(GuildPermission.Administrator)]
        public async Task RefreshWclCache(
            [Summary("type", "Which cache to clear")]
            [Choice("top10", "top10")]
            [Choice("logs", "logs")]
            [Choice("character", "character")]
            [Choice("all", "all")]
            string type = "all",
            [Summary("character", "Character name (for character cache type)")]
            string characterName = null,
            [Summary("realm", "Realm name (for character cache type)")]
            string realmName = null)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var guildObject = await _wowUtils.GetGuildName(Context);
                string realmSlug = realmName?.ToLower().Replace(" ", "-").Replace("'", "")
                    ?? guildObject.realmSlug
                    ?? guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "") ?? "";
                string region = guildObject.regionName ?? "us";
                string guildName = guildObject.guildName;

                var cleared = new List<string>();

                if (type == "top10" || type == "all")
                {
                    _wowCache.InvalidateTop10Rankings(realmSlug, region);
                    cleared.Add("Top10 rankings");
                }

                if (type == "logs" || type == "all")
                {
                    if (!string.IsNullOrEmpty(guildName))
                    {
                        _wowCache.InvalidateGuildReports(guildName, realmSlug, region);
                        cleared.Add($"Guild reports ({guildName})");
                    }
                }

                if (type == "character" || type == "all")
                {
                    // Get current zone for cache key
                    var currentTier = await WithDbAsync(db => db.CurrentRaidTier.FirstOrDefaultAsync());
                    var zoneId = (int)(currentTier?.WclZoneId ?? 0);

                    if (zoneId > 0)
                    {
                        if (!string.IsNullOrEmpty(characterName))
                        {
                            // Clear specific character
                            InvalidateCharacterZoneRankings(characterName, realmSlug, region, zoneId);
                            cleared.Add($"Character rankings ({characterName})");
                        }
                        else
                        {
                            // Clear all saved characters for this Discord server
                            var serverChars = await WithDbAsync(db => db.WowCharAssociation
                                .Where(c => c.ServerId == (long)Context.Guild.Id)
                                .Select(c => new { c.CharName, c.LocalRealmSlug, c.WowRegion })
                                .Distinct()
                                .ToListAsync());

                            foreach (var ch in serverChars)
                            {
                                var charRealm = ch.LocalRealmSlug ?? realmSlug;
                                var charRegion = ch.WowRegion ?? region;
                                InvalidateCharacterZoneRankings(ch.CharName, charRealm, charRegion, zoneId);
                            }

                            if (serverChars.Count > 0)
                            {
                                cleared.Add($"Character rankings ({serverChars.Count} characters)");
                            }
                        }
                    }
                }

                if (cleared.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("WCL Cache")
                        .WithColor(new Color(255, 165, 0))
                        .WithDescription("No cache entries to clear. Specify a character name for character cache, or ensure guild association is set for logs cache.")
                        .Build(), ephemeral: true);
                    return;
                }

                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("WCL Cache Cleared")
                    .WithColor(new Color(0, 200, 100))
                    .WithDescription($"Cleared: {string.Join(", ", cleared)}")
                    .AddField("Realm", realmSlug, true)
                    .AddField("Region", region.ToUpper(), true)
                    .WithFooter("WCL caches: 10hr TTL, auto-invalidated on new logs")
                    .Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing WCL cache");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }

        /// <summary>
        /// Helper to invalidate all zone rankings cache entries for a character (all difficulties)
        /// </summary>
        private void InvalidateCharacterZoneRankings(string characterName, string realmSlug, string region, int zoneId)
        {
            _wowCache.InvalidateZoneRankings(characterName, realmSlug, region, zoneId, null);
            _wowCache.InvalidateZoneRankings(characterName, realmSlug, region, zoneId, 3);
            _wowCache.InvalidateZoneRankings(characterName, realmSlug, region, zoneId, 4);
            _wowCache.InvalidateZoneRankings(characterName, realmSlug, region, zoneId, 5);
        }

        [SlashCommand("top10-refresh", "Clear cached top10 rankings for this server's realm")]
        [RequireOwner]
        public async Task RefreshTop10Cache(
            [Summary("scope", "Which rankings to refresh (server or guild)")]
            [Choice("server", "server")]
            [Choice("guild", "guild")]
            string scope = "server",
            [Summary("encounter", "Boss encounter ID (get from /top10 dropdown)")]
            int? encounterId = null,
            [Summary("metric", "Metric type")]
            [Choice("dps", "dps")]
            [Choice("hps", "hps")]
            string metric = "dps",
            [Summary("difficulty", "Raid difficulty")]
            [Choice("Heroic", "heroic")]
            [Choice("Mythic", "mythic")]
            [Choice("Normal", "normal")]
            [Choice("LFR", "lfr")]
            string difficulty = "heroic")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Get guild association for realm info
                var guildObject = await _wowUtils.GetGuildName(Context);
                string realmSlug = guildObject.realmSlug ?? guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "") ?? "";
                string region = guildObject.regionName ?? "us";
                string guildName = scope == "guild" ? guildObject.guildName : null;

                if (string.IsNullOrEmpty(realmSlug))
                {
                    await FollowupAsync("No guild association found for this server. Use `/setguild` first.", ephemeral: true);
                    return;
                }

                if (encounterId.HasValue)
                {
                    // Invalidate specific cache entry (cache key uses difficulty string, not ID)
                    _wowCache.InvalidateTop10Rankings(scope, realmSlug, region, encounterId.Value, metric, difficulty.ToLower(), guildName);

                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("Top10 Cache Cleared")
                        .WithColor(new Color(0, 200, 100))
                        .AddField("Scope", scope, true)
                        .AddField("Realm", realmSlug, true)
                        .AddField("Region", region.ToUpper(), true)
                        .AddField("Encounter", encounterId.Value.ToString(), true)
                        .AddField("Metric", metric, true)
                        .AddField("Difficulty", difficulty, true)
                        .WithFooter(guildName != null ? $"Guild: {guildName}" : null)
                        .Build(), ephemeral: true);
                }
                else
                {
                    // No specific encounter - inform about TTL-based expiration
                    _wowCache.InvalidateTop10Rankings(realmSlug, region);

                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("Top10 Cache Info")
                        .WithColor(new Color(255, 165, 0))
                        .WithDescription("Cache entries expire automatically after 1 hour.\n\nTo clear a specific entry, provide the encounter ID from the /top10 dropdown.")
                        .AddField("Realm", realmSlug, true)
                        .AddField("Region", region.ToUpper(), true)
                        .Build(), ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing top10 cache");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }
    }
}
