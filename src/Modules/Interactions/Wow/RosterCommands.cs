using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    public enum RosterSortOption
    {
        [ChoiceDisplay("M+ Score (High to Low)")]
        MythicPlus,
        [ChoiceDisplay("Item Level (High to Low)")]
        ItemLevel,
        [ChoiceDisplay("Name (A-Z)")]
        Name,
        [ChoiceDisplay("Rank")]
        Rank
    }

    public class RosterCommands : NinjaBotBaseModule
    {
        private readonly ILogger<RosterCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly WowUtilities _wowUtils;
        private const int PageSize = 15;
        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromHours(1);

        public RosterCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<RosterCommands> logger,
            WowApi wowApi,
            WowUtilities wowUtils)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _wowUtils = wowUtils;
        }

        [SlashCommand("roster", "View guild roster with M+ scores")]
        public async Task ViewRoster(
            [Summary("guild", "Guild name (optional if guild is set)")] string guildName = null,
            [Summary("realm", "Server realm")][Autocomplete(typeof(RealmAutocomplete))] string realm = null,
            [Summary("region", "Region (us/eu)")][Choice("US", "us")][Choice("EU", "eu")] string region = "us",
            [Summary("sort", "Sort by")] RosterSortOption sort = RosterSortOption.MythicPlus,
            [Summary("refresh-mplus", "Refresh M+ scores (slower)")] bool refreshMplus = false)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // If no guild specified, try to use server's associated guild
                var guildObject = await _wowUtils.GetGuildName(Context);
                if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realm))
                {
                    if (guildObject?.guildName == null)
                    {
                        await FollowupAsync(
                            "No guild association found for this server. Please specify a guild name and realm, or set a guild association with `/setguild`.",
                            ephemeral: true);
                        return;
                    }
                    guildName = guildName ?? guildObject.guildName;
                    realm = realm ?? guildObject.realmSlug;
                    region = guildObject.regionName ?? region;
                }
                else
                {
                    // Create a guild object for the specified guild
                    guildObject = new NinjaObjects.GuildObject
                    {
                        guildName = guildName,
                        realmSlug = RealmHelper.ToSlug(realm),
                        realmName = realm,
                        regionName = region
                    };
                }

                // Refresh roster from WoW API (uses 60-minute cache)
                try
                {
                    await _wowUtils.RefreshGuildRosterAsync(guildObject);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh guild roster for {Guild}-{Realm}-{Region}", guildName, realm, region);
                    await FollowupAsync($"Could not find guild **{guildName}** on **{realm}** ({region.ToUpper()}). Please check the spelling.", ephemeral: true);
                    return;
                }

                // Load cached roster from database
                var members = await LoadRosterMembersAsync(guildObject.guildName, guildObject.realmSlug, guildObject.regionName);

                if (members == null || members.Count == 0)
                {
                    await FollowupAsync("Guild roster is empty or could not be loaded.", ephemeral: true);
                    return;
                }

                // Optionally refresh M+ scores
                if (refreshMplus)
                {
                    await FollowupAsync($"Refreshing M+ scores for {members.Count} members... This may take a moment.", ephemeral: true);
                    members = await RefreshMPlusScoresAsync(members, region);
                }

                // Convert and sort
                var rosterData = ConvertToRosterData(members);
                rosterData = SortRoster(rosterData, sort);

                // Build embed for first page (use | as separator since guild names can't contain pipes)
                var guildParam = $"{guildObject.guildName}|{guildObject.realmSlug}|{region}";
                var guildInfo = BuildGuildInfo(guildObject.guildName, guildObject.realmName ?? guildObject.realmSlug, guildObject.realmSlug, members);

                var hasMPlusData = rosterData.Any(m => m.MythicPlusScore > 0);
                var embed = RosterView.Build(guildInfo, rosterData, 0, PageSize, sort, hasMPlusData);
                var components = RosterView.BuildComponents(Context.User.Id, guildParam, 0, (rosterData.Count + PageSize - 1) / PageSize, sort, hasMPlusData);

                await FollowupAsync(embed: embed.Build(), components: components.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ViewRoster command");
                await FollowupAsync("An error occurred while fetching the roster. Please try again.", ephemeral: true);
            }
        }

        [ComponentInteraction("roster_page~*~*~*~*")]
        public async Task HandlePageChange(string userIdStr, string guildParam, string pageStr, string sortStr)
        {
            if (!ulong.TryParse(userIdStr, out var expectedUserId) || Context.User.Id != expectedUserId)
            {
                await RespondAsync("This roster belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();

            var page = int.Parse(pageStr);
            var sort = Enum.Parse<RosterSortOption>(sortStr);
            var (guildName, realmSlug, region) = ParseGuildParam(guildParam);

            try
            {
                var members = await LoadRosterMembersAsync(guildName, realmSlug, region);
                var rosterData = ConvertToRosterData(members);
                rosterData = SortRoster(rosterData, sort);

                var totalPages = (rosterData.Count + PageSize - 1) / PageSize;
                page = Math.Clamp(page, 0, Math.Max(0, totalPages - 1));

                var realmDisplayName = await GetRealmDisplayNameAsync(realmSlug, region);
                var guildInfo = BuildGuildInfo(guildName, realmDisplayName, realmSlug, members);

                var hasMPlusData = rosterData.Any(m => m.MythicPlusScore > 0);
                var embed = RosterView.Build(guildInfo, rosterData, page, PageSize, sort, hasMPlusData);
                var components = RosterView.BuildComponents(Context.User.Id, guildParam, page, totalPages, sort, hasMPlusData);

                await ModifyOriginalResponseAsync(m =>
                {
                    m.Embed = embed.Build();
                    m.Components = components.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandlePageChange");
                await FollowupAsync("An error occurred while loading the page.", ephemeral: true);
            }
        }

        [ComponentInteraction("roster_sort~*~*")]
        public async Task HandleSortChange(string userIdStr, string guildParam, string[] selectedSort)
        {
            if (!ulong.TryParse(userIdStr, out var expectedUserId) || Context.User.Id != expectedUserId)
            {
                await RespondAsync("This roster belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();

            var sort = Enum.Parse<RosterSortOption>(selectedSort[0]);
            var (guildName, realmSlug, region) = ParseGuildParam(guildParam);

            try
            {
                var members = await LoadRosterMembersAsync(guildName, realmSlug, region);
                var rosterData = ConvertToRosterData(members);
                rosterData = SortRoster(rosterData, sort);

                var totalPages = (rosterData.Count + PageSize - 1) / PageSize;
                var realmDisplayName = await GetRealmDisplayNameAsync(realmSlug, region);
                var guildInfo = BuildGuildInfo(guildName, realmDisplayName, realmSlug, members);

                var hasMPlusData = rosterData.Any(m => m.MythicPlusScore > 0);
                var embed = RosterView.Build(guildInfo, rosterData, 0, PageSize, sort, hasMPlusData);
                var components = RosterView.BuildComponents(Context.User.Id, guildParam, 0, totalPages, sort, hasMPlusData);

                await ModifyOriginalResponseAsync(m =>
                {
                    m.Embed = embed.Build();
                    m.Components = components.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleSortChange");
                await FollowupAsync("An error occurred while sorting.", ephemeral: true);
            }
        }

        [ComponentInteraction("roster_refresh_mplus~*~*")]
        public async Task HandleRefreshMplus(string userIdStr, string guildParam)
        {
            if (!ulong.TryParse(userIdStr, out var expectedUserId) || Context.User.Id != expectedUserId)
            {
                await RespondAsync("This roster belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync();

            var (guildName, realmSlug, region) = ParseGuildParam(guildParam);

            try
            {
                // Atomic rate limit check and update (1-hour cooldown)
                var refreshResult = await WithDbAsync(async db =>
                {
                    var association = await db.WowGuildAssociations
                        .FirstOrDefaultAsync(a =>
                            a.WowGuild == guildName &&
                            a.LocalRealmSlug == realmSlug &&
                            a.WowRegion.ToLower() == region.ToLower());

                    if (association == null)
                    {
                        // No association - allow refresh (can't track without association)
                        return (CanRefresh: true, LastRefresh: (DateTime?)null, MinutesRemaining: 0);
                    }

                    var now = DateTime.UtcNow;
                    var lastRefresh = association.LastMPlusRefresh;

                    // Check 1-hour cooldown
                    if (lastRefresh.HasValue)
                    {
                        var elapsed = now - lastRefresh.Value;
                        if (elapsed < RefreshCooldown)
                        {
                            var remaining = RefreshCooldown - elapsed;
                            return (CanRefresh: false, LastRefresh: lastRefresh, MinutesRemaining: (int)Math.Ceiling(remaining.TotalMinutes));
                        }
                    }

                    // Update timestamp atomically (before doing the actual refresh)
                    association.LastMPlusRefresh = now;
                    await db.SaveChangesAsync();

                    return (CanRefresh: true, LastRefresh: lastRefresh, MinutesRemaining: 0);
                });

                if (!refreshResult.CanRefresh)
                {
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = $"Please wait {refreshResult.MinutesRemaining} minute(s) before refreshing again. Use `/char` to update individual members.";
                    });
                    return;
                }

                var members = await LoadRosterMembersAsync(guildName, realmSlug, region);

                await ModifyOriginalResponseAsync(m =>
                {
                    m.Content = $"Refreshing M+ scores for {members.Count} members... This may take 1-2 minutes.";
                });

                members = await RefreshMPlusScoresAsync(members, region);

                // Log API usage (1 M+ call per member)
                await LogApiUsageAsync(
                    operation: "RosterMPlusRefresh",
                    apiCallCount: members.Count,
                    wowGuild: guildName,
                    wowRealm: realmSlug,
                    wowRegion: region);

                var rosterData = ConvertToRosterData(members);
                rosterData = SortRoster(rosterData, RosterSortOption.MythicPlus);

                var totalPages = (rosterData.Count + PageSize - 1) / PageSize;
                var realmDisplayName = await GetRealmDisplayNameAsync(realmSlug, region);
                var guildInfo = BuildGuildInfo(guildName, realmDisplayName, realmSlug, members);

                var embed = RosterView.Build(guildInfo, rosterData, 0, PageSize, RosterSortOption.MythicPlus, true);
                var components = RosterView.BuildComponents(Context.User.Id, guildParam, 0, totalPages, RosterSortOption.MythicPlus, true);

                await ModifyOriginalResponseAsync(m =>
                {
                    m.Content = $"M+ scores refreshed! (1 refresh per hour)";
                    m.Embed = embed.Build();
                    m.Components = components.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleRefreshMplus");
                await FollowupAsync("An error occurred while refreshing M+ scores.", ephemeral: true);
            }
        }

        #region Helper Methods

        private static (string guildName, string realmSlug, string region) ParseGuildParam(string guildParam)
        {
            var parts = guildParam.Split('|');
            if (parts.Length < 3)
            {
                throw new ArgumentException($"Invalid guild parameter format: expected 'guild|realm|region', got '{guildParam}'");
            }
            return (parts[0], parts[1], parts[2]);
        }

        private async Task<List<WowGuildRosterMember>> LoadRosterMembersAsync(string guildName, string realmSlug, string region)
        {
            return await WithDbAsync(async db => await db.WowGuildRosterMembers
                .Where(x =>
                    x.GuildName == guildName &&
                    x.GuildRealmSlug == realmSlug &&
                    x.Region == region &&
                    x.Level >= 70)
                .ToListAsync());
        }

        private async Task<string> GetRealmDisplayNameAsync(string realmSlug, string region)
        {
            var realm = await WithDbAsync(async db => await db.WowRealms
                .FirstOrDefaultAsync(r => r.Slug == realmSlug && r.Region.ToLower() == region.ToLower()));
            return realm?.Name ?? realmSlug;
        }

        private static List<RosterMemberData> ConvertToRosterData(List<WowGuildRosterMember> members)
        {
            return members.Select(m => new RosterMemberData
            {
                Name = m.CharacterName,
                Realm = m.RealmSlug,
                Rank = m.Rank,
                Level = m.Level,
                ClassId = (int)(m.ClassId ?? 0),
                ItemLevel = m.ItemLevel ?? 0,
                MythicPlusScore = m.MythicPlusScore ?? 0
            }).ToList();
        }

        private static ArmoryGuildInfo BuildGuildInfo(string guildName, string realmName, string realmSlug, List<WowGuildRosterMember> members)
        {
            return new ArmoryGuildInfo
            {
                Name = guildName,
                Realm = new ArmoryRealm { Name = realmName, Slug = realmSlug },
                Faction = new ArmoryType { Name = members.FirstOrDefault()?.Faction }
            };
        }

        /// <summary>
        /// Refreshes M+ scores for roster members.
        /// Note: Item level is NOT fetched here - it requires a separate API call per character.
        /// Neither Blizzard nor Raider.IO APIs provide bulk ilvl/M+ data for guild members.
        /// Users can update individual character ilvl via /char command.
        /// </summary>
        private async Task<List<WowGuildRosterMember>> RefreshMPlusScoresAsync(List<WowGuildRosterMember> members, string region)
        {
            var updated = new List<WowGuildRosterMember>();

            // Process in batches with delay to avoid rate limits
            var batches = members.Chunk(10).ToList();
            var batchCount = 0;

            foreach (var batch in batches)
            {
                batchCount++;

                var tasks = batch.Select(async member =>
                {
                    try
                    {
                        var mplusProfile = await _wowApi.GetMythicKeystoneProfileAsync(
                            member.CharacterName,
                            member.RealmSlug,
                            region);
                        member.MythicPlusScore = mplusProfile?.CurrentMythicRating?.Rating ?? 0;
                    }
                    catch (Exception ex)
                    {
                        // M+ data not available (404 for chars who haven't done M+)
                        _logger.LogDebug(ex, "Could not fetch M+ data for {CharName}-{Realm}", member.CharacterName, member.RealmSlug);
                    }

                    return member;
                });

                var batchResults = await Task.WhenAll(tasks);
                updated.AddRange(batchResults);

                // Delay between batches to avoid rate limits (500ms)
                if (batchCount < batches.Count)
                {
                    await Task.Delay(500);
                }
            }

            // Save updated M+ scores to database (update ALL members, including those with 0 score)
            await WithScopedUnitOfWorkAsync(async uow =>
            {
                var repo = uow.Repository<WowGuildRosterMember>();
                foreach (var member in updated)
                {
                    var existing = await repo.FirstOrDefaultAsync(x => x.Id == member.Id);
                    if (existing != null)
                    {
                        existing.MythicPlusScore = member.MythicPlusScore;
                    }
                }
                await uow.SaveChangesAsync();
            });

            return updated;
        }

        private static List<RosterMemberData> SortRoster(List<RosterMemberData> roster, RosterSortOption sort)
        {
            return sort switch
            {
                RosterSortOption.MythicPlus => roster.OrderByDescending(m => m.MythicPlusScore).ThenBy(m => m.Name).ToList(),
                RosterSortOption.ItemLevel => roster.OrderByDescending(m => m.ItemLevel).ThenBy(m => m.Name).ToList(),
                RosterSortOption.Name => roster.OrderBy(m => m.Name).ToList(),
                RosterSortOption.Rank => roster.OrderBy(m => m.Rank).ThenBy(m => m.Name).ToList(),
                _ => roster
            };
        }

        private async Task LogApiUsageAsync(
            string operation,
            int apiCallCount,
            string wowGuild = null,
            string wowRealm = null,
            string wowRegion = null,
            string characterName = null)
        {
            try
            {
                await WithDbAsync(async db =>
                {
                    db.ApiUsageLogs.Add(new ApiUsageLog
                    {
                        GuildId = Context.Guild != null ? (long)Context.Guild.Id : 0,
                        UserId = (long)Context.User.Id,
                        Operation = operation,
                        ApiCallCount = apiCallCount,
                        WowGuild = wowGuild,
                        WowRealm = wowRealm,
                        WowRegion = wowRegion,
                        CharacterName = characterName,
                        Timestamp = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                });
            }
            catch (Exception ex)
            {
                // Don't fail the main operation if logging fails
                _logger.LogWarning(ex, "Failed to log API usage for {Operation}", operation);
            }
        }

        #endregion
    }

    public class RosterMemberData
    {
        public string Name { get; set; }
        public string Realm { get; set; }
        public int Rank { get; set; }
        public int Level { get; set; }
        public int ClassId { get; set; }
        public int ItemLevel { get; set; }
        public double MythicPlusScore { get; set; }
    }
}
