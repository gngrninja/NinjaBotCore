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
    /// <summary>
    /// Unified character lookup command combining Raider.IO, Armory, and WarcraftLogs data.
    /// Provides a tabbed interface with button components to switch between views.
    /// </summary>
    public class CharCommands : NinjaBotBaseModule
    {
        private readonly ILogger<CharCommands> _logger;
        private readonly CharacterResolver _charResolver;
        private readonly IRaiderIOApi _rioApi;
        private readonly WowApi _wowApi;
        private readonly WarcraftLogsV2Client _wclV2Api;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;
        private readonly WowStaticDataService _wowStaticData;

        public CharCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<CharCommands> logger,
            CharacterResolver charResolver,
            IRaiderIOApi rioApi,
            WowApi wowApi,
            WarcraftLogsV2Client wclV2Api,
            WowUtilities wowUtils,
            WowCacheService wowCache,
            WowStaticDataService wowStaticData)
            : base(scopeFactory)
        {
            _logger = logger;
            _charResolver = charResolver;
            _rioApi = rioApi;
            _wowApi = wowApi;
            _wclV2Api = wclV2Api;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
            _wowStaticData = wowStaticData;
        }

        [SlashCommand("char", "View character profile with gear, M+, and logs")]
        public async Task GetCharacterProfile(
            [Summary("character", "Character name (leave empty to use your main character)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character = null,

            [Summary("realm", "Realm name (optional if using autocomplete)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = null)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Resolve character
                var resolution = await _charResolver.ResolveCharacterAsync(
                    character, realm, region, Context.User.Id, Context);

                if (!resolution.IsSuccess)
                {
                    var errorEmbed = new EmbedBuilder()
                        .WithTitle(resolution.ErrorTitle)
                        .WithDescription(resolution.ErrorMessage)
                        .WithColor(new Color(255, 0, 0))
                        .Build();
                    await FollowupAsync(embed: errorEmbed, ephemeral: true);
                    return;
                }

                var charInfo = resolution.Character;

                // Fetch data from RIO, Armory, and Achievements in parallel (WCL is lazy-loaded on button click)
                var rioTask = FetchRioDataAsync(charInfo);
                var armoryTask = FetchArmoryDataAsync(charInfo);
                var achievementsTask = FetchAchievementsAsync(charInfo);

                await Task.WhenAll(rioTask, armoryTask, achievementsTask);

                var rioData = await rioTask;
                var (armorySummary, armoryEquipment, armoryMedia) = await armoryTask;
                var achievements = await achievementsTask;

                // Log API usage (~4-5 calls: RIO, summary, equipment, media, achievements)
                _ = LogCharLookupAsync(charInfo);

                // Record search history for autocomplete (fire-and-forget)
                _ = _wowCache.RecordSearchHistoryAsync(
                    (long)Context.User.Id,
                    charInfo.Name,
                    charInfo.Realm,
                    charInfo.Region);

                // Update roster member's M+ score if they exist in any guild roster
                // Fire-and-forget to not delay the response
                _ = UpdateRosterMemberMPlusScoreAsync(charInfo, rioData);

                // Check if character is already saved
                var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

                // Build overview embed (WCL rankings null - lazy loaded)
                var embed = CharOverviewView.Build(
                    charInfo, rioData, armoryEquipment, armorySummary, armoryMedia, null, achievements);

                // Build components (WCL button always enabled for lazy loading)
                var components = CharOverviewView.BuildComponents(
                    Context.User.Id,
                    charInfo,
                    hasRioData: rioData != null,
                    hasArmoryData: armoryEquipment != null,
                    isAlreadySaved: isAlreadySaved,
                    hasAchievements: achievements?.RecentEvents?.Any() == true);

                await FollowupAsync(embed: embed.Build(), components: components.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetCharacterProfile command");
                await FollowupAsync("An error occurred while fetching character data. Please try again.", ephemeral: true);
            }
        }

        #region Component Handlers - View Navigation

        [ComponentInteraction("char_view_overview~*~*")]
        public async Task HandleViewOverview(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Fetch RIO, Armory, and Achievements data (WCL is lazy-loaded)
            var rioTask = FetchRioDataAsync(charInfo);
            var armoryTask = FetchArmoryDataAsync(charInfo);
            var achievementsTask = FetchAchievementsAsync(charInfo);
            await Task.WhenAll(rioTask, armoryTask, achievementsTask);

            var rioData = await rioTask;
            var (armorySummary, armoryEquipment, armoryMedia) = await armoryTask;
            var achievements = await achievementsTask;

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharOverviewView.Build(charInfo, rioData, armoryEquipment, armorySummary, armoryMedia, null, achievements);
            var components = CharOverviewView.BuildComponents(
                Context.User.Id, charInfo,
                hasRioData: rioData != null,
                hasArmoryData: armoryEquipment != null,
                isAlreadySaved: isAlreadySaved,
                hasAchievements: achievements?.RecentEvents?.Any() == true);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_view_gear~*~*")]
        public async Task HandleViewGear(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var (armorySummary, armoryEquipment, armoryMedia) = await FetchArmoryDataAsync(charInfo);

            if (armoryEquipment == null)
            {
                await FollowupAsync("Could not load gear data for this character.", ephemeral: true);
                return;
            }

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharGearView.Build(charInfo, armorySummary, armoryEquipment, armoryMedia);
            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "gear", isAlreadySaved);

            // Add item select menu
            var selectMenu = CharGearView.BuildItemSelectMenu(Context.User.Id, charInfo, armoryEquipment);
            if (selectMenu != null)
            {
                components.WithSelectMenu(selectMenu, 2);
            }

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_view_mplus~*~*")]
        public async Task HandleViewMythicPlus(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var rioData = await FetchRioDataAsync(charInfo);

            if (rioData == null)
            {
                await FollowupAsync("Could not load M+ data for this character.", ephemeral: true);
                return;
            }

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharMythicPlusView.Build(charInfo, rioData, _wowUtils);
            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "mplus", isAlreadySaved);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_view_logs~*~*~*~*")]
        public async Task HandleViewLogs(string userIdStr, string name, string realm, string region)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = new CharacterInfo
            {
                Name = name,
                Realm = realm,
                RealmSlug = CharViewHelpers.ToRealmSlug(realm),
                Region = region,
                Locale = CharacterResolver.GetLocaleFromRegion(region)
            };

            // Lazy load WCL data using V2 API
            var (zoneRankings, currentZoneId) = await FetchWclV2DataAsync(charInfo);
            var rioData = await FetchRioDataAsync(charInfo); // For class/spec info

            // Fetch encounter rankings batch for fight links and rank display
            Dictionary<int, WclV2EncounterRankingsData> encounterRankings = null;
            if (zoneRankings?.Rankings != null && zoneRankings.Rankings.Any())
            {
                var encounterIds = zoneRankings.Rankings
                    .Where(r => r.Encounter != null)
                    .Select(r => r.Encounter.Id)
                    .ToList();
                encounterRankings = await _wclV2Api.GetCharacterEncounterRankingsBatchAsync(
                    charInfo.Name, charInfo.RealmSlug, charInfo.Region, encounterIds);
            }

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            // Build logs embed using V2 data (default: all difficulties)
            var embed = CharLogsView.BuildV2(
                charInfo,
                zoneRankings,
                encounterRankings,
                difficulty: null,
                zoneId: currentZoneId,
                specName: rioData?.ActiveSpecName,
                className: rioData?.Class);

            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "logs", isAlreadySaved);

            // Add difficulty dropdown (row 2)
            if (currentZoneId > 0)
            {
                var difficultyMenu = CharLogsView.BuildDifficultySelectMenu(Context.User.Id, charInfo, currentZoneId);
                components.WithSelectMenu(difficultyMenu, 2);
            }

            // Add encounter select menu if we have rankings (row 3) - 0 = all difficulties
            var encounterMenu = CharLogsView.BuildEncounterSelectMenuV2(Context.User.Id, charInfo, zoneRankings, currentZoneId, 0);
            if (encounterMenu != null)
            {
                components.WithSelectMenu(encounterMenu, 3);
            }

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_logs_difficulty~*~*~*~*~*")]
        public async Task HandleLogsDifficultySelect(string userIdStr, string name, string realm, string region, string zoneIdStr, string[] selections)
        {
            _logger.LogInformation("[CharLogs] Difficulty select: userId={UserId}, char={Name}-{Realm}-{Region}, zone={Zone}, selection={Selection}",
                userIdStr, name, realm, region, zoneIdStr, selections?.FirstOrDefault() ?? "null");

            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0 || !int.TryParse(selections[0], out var difficulty))
            {
                await RespondAsync("Invalid difficulty selection.", ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = new CharacterInfo
            {
                Name = name,
                Realm = realm,
                RealmSlug = CharViewHelpers.ToRealmSlug(realm),
                Region = region,
                Locale = CharacterResolver.GetLocaleFromRegion(region)
            };

            if (!int.TryParse(zoneIdStr, out var zoneId))
            {
                await FollowupAsync("Invalid zone data.", ephemeral: true);
                return;
            }

            // Difficulty: 0 = All, 3 = Normal, 4 = Heroic, 5 = Mythic
            int? difficultyFilter = difficulty == 0 ? null : difficulty;

            // Fetch WCL data with difficulty filter
            var zoneRankings = await FetchWclV2DataWithFiltersAsync(charInfo, zoneId, difficultyFilter);
            var rioData = await FetchRioDataAsync(charInfo);

            // Fetch encounter rankings batch for fight links and rank display
            Dictionary<int, WclV2EncounterRankingsData> encounterRankings = null;
            if (zoneRankings?.Rankings != null && zoneRankings.Rankings.Any())
            {
                var encounterIds = zoneRankings.Rankings
                    .Where(r => r.Encounter != null)
                    .Select(r => r.Encounter.Id)
                    .ToList();
                encounterRankings = await _wclV2Api.GetCharacterEncounterRankingsBatchAsync(
                    charInfo.Name, charInfo.RealmSlug, charInfo.Region, encounterIds, difficultyFilter);
            }

            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharLogsView.BuildV2(
                charInfo,
                zoneRankings,
                encounterRankings,
                difficulty: difficultyFilter,
                zoneId: zoneId,
                specName: rioData?.ActiveSpecName,
                className: rioData?.Class);

            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "logs", isAlreadySaved);

            // Add difficulty dropdown (row 2) with current selection
            var difficultyMenu = CharLogsView.BuildDifficultySelectMenu(Context.User.Id, charInfo, zoneId, difficulty);
            components.WithSelectMenu(difficultyMenu, 2);

            // Add encounter select menu (row 3) with current difficulty
            var encounterMenu = CharLogsView.BuildEncounterSelectMenuV2(Context.User.Id, charInfo, zoneRankings, zoneId, difficulty);
            if (encounterMenu != null)
            {
                components.WithSelectMenu(encounterMenu, 3);
            }

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_logs_encounter_v2~*~*~*~*~*~*")]
        public async Task HandleLogsEncounterSelectV2(string userIdStr, string name, string realm, string region, string zoneIdStr, string difficultyStr, string[] selections)
        {
            _logger.LogInformation("[CharLogs] Encounter select: userId={UserId}, char={Name}-{Realm}-{Region}, zone={Zone}, diff={Diff}, selection={Selection}",
                userIdStr, name, realm, region, zoneIdStr, difficultyStr, selections?.FirstOrDefault() ?? "null");

            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0 || !int.TryParse(selections[0], out var encounterId))
            {
                await RespondAsync("Invalid encounter selection.", ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = new CharacterInfo
            {
                Name = name,
                Realm = realm,
                RealmSlug = CharViewHelpers.ToRealmSlug(realm),
                Region = region,
                Locale = CharacterResolver.GetLocaleFromRegion(region)
            };

            if (!int.TryParse(zoneIdStr, out var zoneId))
            {
                await FollowupAsync("Invalid zone data.", ephemeral: true);
                return;
            }

            // Parse difficulty (0 = all, 3 = normal, 4 = heroic, 5 = mythic)
            int.TryParse(difficultyStr, out var difficulty);
            int? difficultyFilter = difficulty == 0 ? null : difficulty;

            // Fetch zone rankings for the encounter select menu
            var zoneRankings = await FetchWclV2DataWithFiltersAsync(charInfo, zoneId, difficultyFilter);
            if (zoneRankings?.Rankings == null || !zoneRankings.Rankings.Any())
            {
                await FollowupAsync("Could not load logs data.", ephemeral: true);
                return;
            }

            // Fetch individual parses for this encounter (with caching)
            var encounterRankings = await FetchCharacterEncounterRankingsAsync(charInfo, encounterId, difficultyFilter);

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            // Build encounter detail embed with individual parses
            var embed = CharLogsView.BuildEncounterDetailV2(charInfo, zoneRankings, encounterId, encounterRankings, zoneId: zoneId, difficulty: difficultyFilter);

            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "logs", isAlreadySaved);

            // Add difficulty dropdown (row 2) with current selection
            var difficultyMenu = CharLogsView.BuildDifficultySelectMenu(Context.User.Id, charInfo, zoneId, difficulty);
            components.WithSelectMenu(difficultyMenu, 2);

            // Add encounter select menu (row 3) with current difficulty
            var encounterMenu = CharLogsView.BuildEncounterSelectMenuV2(Context.User.Id, charInfo, zoneRankings, zoneId, difficulty);
            if (encounterMenu != null)
            {
                components.WithSelectMenu(encounterMenu, 3);
            }

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_gear_select~*~*")]
        public async Task HandleGearItemSelect(string userIdStr, string charParam, string[] selections)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No item selected.", ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var selection = selections[0];
            var parts = selection.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var itemId))
            {
                await FollowupAsync("Could not read that item selection.", ephemeral: true);
                return;
            }

            var slotType = parts[0];

            // Fetch equipment data
            var (_, armoryEquipment, _) = await FetchArmoryDataAsync(charInfo);

            if (armoryEquipment?.EquippedItems == null)
            {
                await FollowupAsync("Could not load gear data.", ephemeral: true);
                return;
            }

            var selectedItem = armoryEquipment.EquippedItems.FirstOrDefault(i =>
                string.Equals(i.Slot?.Type, slotType, StringComparison.OrdinalIgnoreCase) ||
                i.Item?.Id == itemId);

            if (selectedItem == null)
            {
                await FollowupAsync("That item was not found on the character.", ephemeral: true);
                return;
            }

            // Fetch item media for the icon (with caching)
            var itemMedia = await GetItemMediaCachedAsync(itemId, charInfo.Region);

            // Build item detail embed with back button
            var embed = CharGearView.BuildItemDetail(selectedItem, charInfo, itemMedia);
            var components = CharGearView.BuildItemDetailComponents(Context.User.Id, charInfo, armoryEquipment);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_view_achievements~*~*")]
        public async Task HandleViewAchievements(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Fetch achievements and media in parallel (only 2 API calls instead of 4)
            var achievementsTask = FetchAchievementsAsync(charInfo);
            var mediaTask = FetchArmoryMediaAsync(charInfo);
            await Task.WhenAll(achievementsTask, mediaTask);

            var achievements = await achievementsTask;
            var armoryMedia = await mediaTask;

            if (achievements == null)
            {
                await FollowupAsync("Could not load achievements data for this character.", ephemeral: true);
                return;
            }

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var totalPages = CharViews.CharAchievementsView.GetTotalPages(achievements);
            var embed = CharViews.CharAchievementsView.Build(charInfo, achievements, armoryMedia, 0);
            var components = CharViews.CharAchievementsView.BuildComponents(Context.User.Id, charInfo, 0, totalPages, isAlreadySaved);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_achievements_page~*~*~*")]
        public async Task HandleAchievementsPage(string userIdStr, string charParam, string pageStr)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            if (!int.TryParse(pageStr, out var page))
            {
                page = 0;
            }

            // Fetch achievements and media in parallel (only 2 API calls instead of 4)
            var achievementsTask = FetchAchievementsAsync(charInfo);
            var mediaTask = FetchArmoryMediaAsync(charInfo);
            await Task.WhenAll(achievementsTask, mediaTask);

            var achievements = await achievementsTask;
            var armoryMedia = await mediaTask;

            if (achievements == null)
            {
                await FollowupAsync("Could not load achievements data.", ephemeral: true);
                return;
            }

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var totalPages = CharViews.CharAchievementsView.GetTotalPages(achievements);
            page = Math.Clamp(page, 0, Math.Max(0, totalPages - 1));

            var embed = CharViews.CharAchievementsView.Build(charInfo, achievements, armoryMedia, page);
            var components = CharViews.CharAchievementsView.BuildComponents(Context.User.Id, charInfo, page, totalPages, isAlreadySaved);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_view_pvp~*~*")]
        public async Task HandleViewPvP(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Fetch PvP data, media, and summary in parallel
            var pvpTask = FetchPvPSummaryAsync(charInfo);
            var mediaTask = FetchArmoryMediaAsync(charInfo);
            var summaryTask = _wowApi.GetArmorySummaryAsync(charInfo.Name, charInfo.Realm, charInfo.Region);

            await Task.WhenAll(pvpTask, mediaTask, summaryTask);

            var pvpSummary = await pvpTask;
            var media = await mediaTask;
            ArmorySummary summary = null;
            try { summary = await summaryTask; }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to fetch armory summary for PvP view"); }

            if (pvpSummary == null)
            {
                // Check if character is already saved for component building
                var isAlreadySavedNoData = await IsCharacterSavedAsync(charInfo, Context.User.Id);

                var noDataEmbed = new EmbedBuilder()
                    .WithTitle($"PvP - {charInfo.Name}")
                    .WithDescription("No PvP activity found for this character.\n\nThis character may not have participated in rated PvP this season.")
                    .WithColor(new Color(255, 165, 0))
                    .WithThumbnailUrl(media?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value)
                    .WithFooter($"{charInfo.Realm} ({charInfo.Region.ToUpper()})")
                    .Build();

                var noDataComponents = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "pvp", isAlreadySavedNoData);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = noDataEmbed;
                    msg.Components = noDataComponents.Build();
                });
                return;
            }

            // Fetch bracket details
            var bracketDetails = await FetchPvPBracketDetailsAsync(pvpSummary, charInfo.Region);

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharPvPView.Build(charInfo, pvpSummary, bracketDetails, summary, media);
            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "pvp", isAlreadySaved);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_view_mounts~*~*")]
        public async Task HandleViewMounts(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Fetch mount collection, all mounts, and media in parallel
            var mountCollectionTask = FetchMountCollectionAsync(charInfo);
            var allMountsTask = _wowStaticData.GetAllMountsAsync();
            var mediaTask = FetchArmoryMediaAsync(charInfo);

            await Task.WhenAll(mountCollectionTask, allMountsTask, mediaTask);

            var mountCollection = await mountCollectionTask;
            var allMounts = await allMountsTask;
            var media = await mediaTask;

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            if (mountCollection == null)
            {
                var noDataEmbed = new EmbedBuilder()
                    .WithTitle($"Mount Collection - {charInfo.Name}")
                    .WithDescription("Could not load mount collection data for this character.")
                    .WithColor(new Color(255, 0, 0))
                    .WithThumbnailUrl(media?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value)
                    .WithFooter($"{charInfo.Realm} ({charInfo.Region.ToUpper()})")
                    .Build();

                var noDataComponents = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "mounts", isAlreadySaved);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = noDataEmbed;
                    msg.Components = noDataComponents.Build();
                });
                return;
            }

            if (allMounts == null || allMounts.Count == 0)
            {
                var noDbEmbed = new EmbedBuilder()
                    .WithTitle($"Mount Collection - {charInfo.Name}")
                    .WithDescription("The mount database is empty. Mount data needs to be synced.\n\nUse `/mounts-needed` for basic collection info.")
                    .WithColor(new Color(255, 165, 0))
                    .WithThumbnailUrl(media?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value)
                    .WithFooter($"{charInfo.Realm} ({charInfo.Region.ToUpper()})")
                    .Build();

                var noDbComponents = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "mounts", isAlreadySaved);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = noDbEmbed;
                    msg.Components = noDbComponents.Build();
                });
                return;
            }

            var embed = CharMountsView.Build(charInfo, mountCollection, allMounts, media);
            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "mounts", isAlreadySaved);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        #endregion

        #region Component Handlers - Actions

        [ComponentInteraction("char_save~*~*")]
        public async Task HandleSaveCharacter(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await RespondAsync("Invalid character data.", ephemeral: true);
                return;
            }

            try
            {
                await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var repo = uow.Repository<WowCharAssociation>();
                    var userId = (long)Context.User.Id;

                    // Get all user chars and check for duplicates using normalized realm comparison
                    var allUserChars = await repo.WhereAsync(c => c.UserId == userId);
                    var normalizedName = charInfo.Name.ToLower();
                    var normalizedRealm = CharViewHelpers.NormalizeRealmForComparison(charInfo.Realm);

                    var existing = allUserChars?.FirstOrDefault(c =>
                        c.CharName.ToLower() == normalizedName &&
                        CharViewHelpers.NormalizeRealmForComparison(c.WowRealm) == normalizedRealm);

                    if (existing != null)
                    {
                        await RespondAsync($"**{charInfo.Name}** on **{charInfo.Realm}** is already saved!", ephemeral: true);
                        return;
                    }

                    var count = allUserChars?.Count ?? 0;

                    var newChar = new WowCharAssociation
                    {
                        UserId = userId,
                        CharName = charInfo.Name,
                        WowRealm = charInfo.Realm,
                        WowRegion = charInfo.Region,
                        LocalRealmSlug = charInfo.RealmSlug,
                        Locale = charInfo.Locale,
                        IsMain = count == 0, // First character becomes main
                        TimeSet = DateTime.UtcNow
                    };

                    await repo.AddAsync(newChar);
                    await uow.SaveChangesAsync();

                    // Invalidate cache so /char with no args picks up the new main
                    _wowCache.InvalidateUserCharacters((long)Context.User.Id);

                    var mainText = newChar.IsMain ? " (set as main)" : "";
                    await RespondAsync($"Saved **{charInfo.Name}** on **{charInfo.Realm}**{mainText}!", ephemeral: true);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving character");
                await RespondAsync("Failed to save character. Please try again.", ephemeral: true);
            }
        }

        [ComponentInteraction("char_refresh~*~*")]
        public async Task HandleRefreshCharacter(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Re-fetch RIO and Armory data (WCL is lazy-loaded)
            var rioTask = FetchRioDataAsync(charInfo);
            var armoryTask = FetchArmoryDataAsync(charInfo);

            await Task.WhenAll(rioTask, armoryTask);

            var rioData = await rioTask;
            var (armorySummary, armoryEquipment, armoryMedia) = await armoryTask;

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharOverviewView.Build(charInfo, rioData, armoryEquipment, armorySummary, armoryMedia);
            var components = CharOverviewView.BuildComponents(
                Context.User.Id, charInfo,
                hasRioData: rioData != null,
                hasArmoryData: armoryEquipment != null,
                isAlreadySaved: isAlreadySaved);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_share~*~*")]
        public async Task HandleShareCharacter(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Fetch RIO and Armory data (WCL not included in shared overview)
            var rioTask = FetchRioDataAsync(charInfo);
            var armoryTask = FetchArmoryDataAsync(charInfo);
            await Task.WhenAll(rioTask, armoryTask);

            var rioData = await rioTask;
            var (armorySummary, armoryEquipment, armoryMedia) = await armoryTask;

            var embed = CharOverviewView.Build(charInfo, rioData, armoryEquipment, armorySummary, armoryMedia);

            // Send as new public message (no components for shared version)
            await Context.Channel.SendMessageAsync(
                text: $"*Shared by {Context.User.Mention}*",
                embed: embed.Build());

            await FollowupAsync("Character profile shared!", ephemeral: true);
        }

        [ComponentInteraction("char_manage_ret~*~*~*~*")]
        public async Task HandleManageCharactersWithReturn(string userIdStr, string charName, string charRealm, string charRegion)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var savedChars = await _wowCache.GetUserCharactersAsync((long)Context.User.Id);
            savedChars = savedChars?
                .OrderByDescending(c => c.IsMain)
                .ThenBy(c => c.CharName)
                .ToList();

            // Build the return charParam for the back button
            var returnCharParam = $"{charName}~{charRealm}~{charRegion}";

            var embed = CharacterManagementView.Build(Context.User, savedChars);
            var components = CharacterManagementView.BuildComponents(savedChars, Context.User.Id, returnCharParam);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_manage~*")]
        public async Task HandleManageCharacters(string userIdStr)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var savedChars = await _wowCache.GetUserCharactersAsync((long)Context.User.Id);
            savedChars = savedChars?
                .OrderByDescending(c => c.IsMain)
                .ThenBy(c => c.CharName)
                .ToList();

            var embed = CharacterManagementView.Build(Context.User, savedChars);
            var components = CharacterManagementView.BuildComponents(savedChars);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Updates the M+ score in WowGuildRosterMember if this character exists in any guild roster.
        /// Called when /char is used to keep roster data fresh from RIO lookups.
        /// </summary>
        private async Task UpdateRosterMemberMPlusScoreAsync(CharacterInfo charInfo, RaiderIOModels.RioMythicPlusChar rioData)
        {
            if (rioData == null) return;

            var mplusScore = rioData.MythicPlusScores?.FirstOrDefault()?.Scores?.All;
            if (mplusScore == null || mplusScore <= 0) return;

            try
            {
                await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var repo = uow.Repository<WowGuildRosterMember>();

                    // Find all roster entries for this character (could be in multiple guilds)
                    var normalizedName = charInfo.Name.ToLower();
                    var normalizedRealm = charInfo.RealmSlug.ToLower();
                    var normalizedRegion = charInfo.Region.ToLower();

                    var rosterMembers = await repo.WhereAsync(m =>
                        m.CharacterName.ToLower() == normalizedName &&
                        m.RealmSlug.ToLower() == normalizedRealm &&
                        m.Region.ToLower() == normalizedRegion);

                    if (rosterMembers?.Any() == true)
                    {
                        foreach (var member in rosterMembers)
                        {
                            member.MythicPlusScore = mplusScore;
                        }
                        await uow.SaveChangesAsync();
                        _logger.LogDebug("Updated M+ score ({Score}) for {Character}-{Realm} in {Count} roster(s)",
                            mplusScore, charInfo.Name, charInfo.Realm, rosterMembers.Count);
                    }
                });
            }
            catch (Exception ex)
            {
                // Non-critical - don't fail the command if roster update fails
                _logger.LogDebug(ex, "Failed to update roster M+ score for {Character}", charInfo.Name);
            }
        }

        private async Task<RaiderIOModels.RioMythicPlusChar> FetchRioDataAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _rioApi.GetCharMythicPlusInfoAsync(
                    charInfo.Name,
                    charInfo.RealmEncoded,
                    charInfo.Region);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch RIO data for {Character}", charInfo.Name);
                return null;
            }
        }

        private async Task<(ArmorySummary Summary, ArmoryEquipment Equipment, ArmoryMedia Media)> FetchArmoryDataAsync(CharacterInfo charInfo)
        {
            try
            {
                var summaryTask = _wowApi.GetArmorySummaryAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
                var equipmentTask = _wowApi.GetArmoryEquipmentAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
                var mediaTask = _wowApi.GetArmoryMediaAsync(charInfo.Name, charInfo.Realm, charInfo.Region);

                await Task.WhenAll(summaryTask, equipmentTask, mediaTask);

                return (await summaryTask, await equipmentTask, await mediaTask);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch Armory data for {Character}", charInfo.Name);
                return (null, null, null);
            }
        }

        private async Task<ArmoryAchievementsSummary> FetchAchievementsAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetAchievementsSummaryAsync(charInfo.Name, charInfo.RealmSlug, charInfo.Region);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch achievements for {Character}", charInfo.Name);
                return null;
            }
        }

        private async Task<ArmoryMedia> FetchArmoryMediaAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetArmoryMediaAsync(charInfo.Name, charInfo.RealmSlug, charInfo.Region);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch armory media for {Character}", charInfo.Name);
                return null;
            }
        }

        private async Task<ArmoryPvPSummary> FetchPvPSummaryAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetPvPSummaryAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch PvP summary for {Character}", charInfo.Name);
                return null;
            }
        }

        private async Task<List<ArmoryPvPBracket>> FetchPvPBracketDetailsAsync(ArmoryPvPSummary summary, string region)
        {
            if (summary?.Brackets == null || summary.Brackets.Count == 0)
                return new List<ArmoryPvPBracket>();

            var validBrackets = summary.Brackets.Where(b => !string.IsNullOrEmpty(b.Href)).ToList();
            if (validBrackets.Count == 0)
                return new List<ArmoryPvPBracket>();

            // Fetch all brackets in parallel
            var tasks = validBrackets.Select(async bracketLink =>
            {
                try
                {
                    var response = await _wowApi.GetAPIRequestAsync(bracketLink.Href, true);
                    var settings = new Newtonsoft.Json.JsonSerializerSettings
                    {
                        Error = (sender, args) =>
                        {
                            _logger.LogDebug("JSON parse error at {Path}: {Message}", args.ErrorContext.Path, args.ErrorContext.Error.Message);
                            args.ErrorContext.Handled = true;
                        }
                    };
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<ArmoryPvPBracket>(response, settings);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch bracket details from {Href}", bracketLink.Href);
                    return null;
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(b => b != null).ToList();
        }

        private async Task<MountCollectionResponse> FetchMountCollectionAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetCharacterMountsAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch mount collection for {Character}", charInfo.Name);
                return null;
            }
        }

        /// <summary>
        /// Fetches WCL zone rankings using V2 API for current raid tier (cached for 10 hours)
        /// </summary>
        private async Task<(WclV2ZoneRankingsData Rankings, int ZoneId)> FetchWclV2DataAsync(CharacterInfo charInfo, int? difficultyFilter = null, int? partitionFilter = null)
        {
            try
            {
                // Get current raid tier zone ID from database
                var currentTier = await WithDbAsync(db => db.CurrentRaidTier.FirstOrDefaultAsync());
                var currentZoneId = (int)(currentTier?.WclZoneId ?? 0);
                if (currentZoneId == 0)
                {
                    _logger.LogWarning("No current raid tier configured - WCL rankings unavailable. " +
                        "Use /refresh-raid-tier to detect the current tier.");
                    return (null, 0);
                }

                // Check cache first
                var cached = _wowCache.GetCachedZoneRankings(charInfo.Name, charInfo.RealmSlug, charInfo.Region, currentZoneId, difficultyFilter);
                if (cached != null)
                {
                    _logger.LogDebug("Using cached zone rankings for {Name} on {Realm}-{Region}", charInfo.Name, charInfo.RealmSlug, charInfo.Region);
                    return (cached, currentZoneId);
                }

                _logger.LogInformation("Fetching WCL V2 data for {Character} on {Realm}-{Region}, Zone: {ZoneId}, Partition: {Partition}",
                    charInfo.Name, charInfo.RealmSlug, charInfo.Region, currentZoneId, partitionFilter ?? 0);

                var result = await _wclV2Api.GetCharacterZoneRankingsAsync(
                    charInfo.Name,
                    charInfo.RealmSlug,
                    charInfo.Region,
                    currentZoneId,
                    difficultyFilter,
                    partitionFilter);

                _logger.LogInformation("WCL V2 returned {Count} boss rankings for {Character}",
                    result?.Rankings?.Count ?? 0, charInfo.Name);

                // Cache the result
                if (result != null)
                {
                    _wowCache.SetCachedZoneRankings(charInfo.Name, charInfo.RealmSlug, charInfo.Region, currentZoneId, difficultyFilter, result);
                }

                return (result, currentZoneId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch WCL V2 data for {Character} on {Realm}", charInfo.Name, charInfo.RealmSlug);
                return (null, 0);
            }
        }

        /// <summary>
        /// Fetches individual character encounter rankings (parses) with caching
        /// </summary>
        private async Task<WclV2EncounterRankingsData> FetchCharacterEncounterRankingsAsync(CharacterInfo charInfo, int encounterId, int? difficultyFilter, int? partitionFilter = null)
        {
            // Check cache first
            var cached = _wowCache.GetCachedCharacterEncounterRankings(
                charInfo.Name, charInfo.RealmSlug, charInfo.Region, encounterId, difficultyFilter);
            if (cached != null)
            {
                return cached;
            }

            try
            {
                var result = await _wclV2Api.GetCharacterEncounterRankingsAsync(
                    charInfo.Name,
                    charInfo.RealmSlug,
                    charInfo.Region,
                    encounterId,
                    difficultyFilter,
                    partitionFilter);

                // Cache the result
                if (result != null)
                {
                    _wowCache.SetCachedCharacterEncounterRankings(
                        charInfo.Name, charInfo.RealmSlug, charInfo.Region, encounterId, difficultyFilter, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch character encounter rankings for {Character} on encounter {EncounterId}",
                    charInfo.Name, encounterId);
                return null;
            }
        }

        /// <summary>
        /// Fetches WCL zone rankings with specific difficulty and partition filter (cached for 1 hour)
        /// </summary>
        private async Task<WclV2ZoneRankingsData> FetchWclV2DataWithFiltersAsync(CharacterInfo charInfo, int zoneId, int? difficultyFilter, int? partitionFilter = null)
        {
            // Check cache first
            var cached = _wowCache.GetCachedZoneRankings(charInfo.Name, charInfo.RealmSlug, charInfo.Region, zoneId, difficultyFilter);
            if (cached != null)
            {
                _logger.LogDebug("Using cached zone rankings for {Name} on {Realm}-{Region}", charInfo.Name, charInfo.RealmSlug, charInfo.Region);
                return cached;
            }

            _logger.LogInformation("Fetching WCL V2 data for {Name} on {Realm}-{Region}, Zone: {Zone}, Difficulty: {Diff}, Partition: {Part}",
                charInfo.Name, charInfo.RealmSlug, charInfo.Region, zoneId, difficultyFilter?.ToString() ?? "null", partitionFilter?.ToString() ?? "null");

            try
            {
                var result = await _wclV2Api.GetCharacterZoneRankingsAsync(
                    charInfo.Name,
                    charInfo.RealmSlug,
                    charInfo.Region,
                    zoneId,
                    difficultyFilter,
                    partitionFilter);

                // Cache the result
                if (result != null)
                {
                    _wowCache.SetCachedZoneRankings(charInfo.Name, charInfo.RealmSlug, charInfo.Region, zoneId, difficultyFilter, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch WCL V2 data with filters for {Character}", charInfo.Name);
                return null;
            }
        }

        private bool ValidateUser(string userIdStr, out string errorMessage)
        {
            errorMessage = null;

            if (!ulong.TryParse(userIdStr, out var originalUserId))
            {
                errorMessage = "Invalid interaction data.";
                return false;
            }

            if (Context.User.Id != originalUserId)
            {
                errorMessage = "This interaction belongs to another user.";
                return false;
            }

            return true;
        }

        private CharacterInfo ParseCharParam(string charParam)
        {
            var parts = charParam.Split('~', 3);
            if (parts.Length < 3) return null;

            return new CharacterInfo
            {
                Name = parts[0],
                Realm = parts[1],
                RealmSlug = CharViewHelpers.ToRealmSlug(parts[1]),
                Region = parts[2],
                Locale = CharacterResolver.GetLocaleFromRegion(parts[2])
            };
        }

        /// <summary>
        /// Check if the character is already saved for this user
        /// </summary>
        private async Task<bool> IsCharacterSavedAsync(CharacterInfo charInfo, ulong userId)
        {
            try
            {
                return await WithScopedUnitOfWorkAsync(async uow =>
                {
                    var repo = uow.Repository<WowCharAssociation>();
                    var userIdLong = (long)userId;

                    // Get all user chars and check using normalized realm comparison
                    var allUserChars = await repo.WhereAsync(c => c.UserId == userIdLong);
                    var normalizedName = charInfo.Name.ToLower();
                    var normalizedRealm = CharViewHelpers.NormalizeRealmForComparison(charInfo.Realm);

                    var existing = allUserChars?.FirstOrDefault(c =>
                        c.CharName.ToLower() == normalizedName &&
                        CharViewHelpers.NormalizeRealmForComparison(c.WowRealm) == normalizedRealm);

                    return existing != null;
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking if character is saved");
                return false;
            }
        }

        private async Task LogCharLookupAsync(CharacterInfo charInfo)
        {
            try
            {
                // ~5 API calls: RIO, armory summary, equipment, media, achievements
                await WithDbAsync(async db =>
                {
                    db.ApiUsageLogs.Add(new ApiUsageLog
                    {
                        GuildId = Context.Guild != null ? (long)Context.Guild.Id : 0,
                        UserId = (long)Context.User.Id,
                        Operation = "CharLookup",
                        ApiCallCount = 5,
                        WowRealm = charInfo.Realm,
                        WowRegion = charInfo.Region,
                        CharacterName = charInfo.Name,
                        Timestamp = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to log char lookup API usage");
            }
        }

        /// <summary>
        /// Gets item media with caching. Checks DB cache first, then fetches from API and caches result.
        /// Item icons are static and never change, so we cache indefinitely.
        /// </summary>
        private async Task<ArmoryItemMedia> GetItemMediaCachedAsync(int itemId, string region)
        {
            try
            {
                // Check cache first
                var cached = await WithDbAsync(async db =>
                    await db.ItemMediaCache.FindAsync((long)itemId));

                if (cached != null)
                {
                    _logger.LogDebug("Item media cache hit for item {ItemId}", itemId);
                    return new ArmoryItemMedia
                    {
                        Assets = new List<ArmoryAsset>
                        {
                            new ArmoryAsset { Key = "icon", Value = cached.IconUrl }
                        }
                    };
                }

                // Cache miss - fetch from API
                _logger.LogDebug("Item media cache miss for item {ItemId}, fetching from API", itemId);
                var itemMedia = await _wowApi.GetItemMediaAsync(itemId, region);

                // Extract icon URL and cache it
                var iconUrl = itemMedia?.Assets?.FirstOrDefault(a => a.Key == "icon")?.Value;
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    await WithDbAsync(async db =>
                    {
                        // Use upsert pattern in case of race condition
                        var existing = await db.ItemMediaCache.FindAsync((long)itemId);
                        if (existing == null)
                        {
                            db.ItemMediaCache.Add(new ItemMediaCache
                            {
                                ItemId = itemId,
                                IconUrl = iconUrl,
                                CachedAt = DateTime.UtcNow
                            });
                            await db.SaveChangesAsync();
                        }
                    });
                }

                return itemMedia;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get item media for item {ItemId}", itemId);
                return null;
            }
        }

        #endregion
    }
}
