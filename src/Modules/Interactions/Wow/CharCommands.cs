using Discord;
using Discord.Interactions;
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
        private readonly WarcraftLogs _wclApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public CharCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<CharCommands> logger,
            CharacterResolver charResolver,
            IRaiderIOApi rioApi,
            WowApi wowApi,
            WarcraftLogs wclApi,
            WowUtilities wowUtils,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _charResolver = charResolver;
            _rioApi = rioApi;
            _wowApi = wowApi;
            _wclApi = wclApi;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
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
            string region = null,

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                // Resolve character (cast context to ShardedInteractionContext for guild lookup)
                var resolution = await _charResolver.ResolveCharacterAsync(
                    character, realm, region, Context.User.Id, (ShardedInteractionContext)Context);

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

                // Fetch data from all sources in parallel
                var rioTask = FetchRioDataAsync(charInfo);
                var armoryTask = FetchArmoryDataAsync(charInfo);
                var wclTask = FetchWclDataAsync(charInfo);

                await Task.WhenAll(rioTask, armoryTask, wclTask);

                var rioData = await rioTask;
                var (armorySummary, armoryEquipment, armoryMedia) = await armoryTask;
                var wclRankings = await wclTask;

                // Check if character is already saved
                var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

                // Build overview embed
                var embed = CharOverviewView.Build(
                    charInfo, rioData, armoryEquipment, armorySummary, armoryMedia, wclRankings);

                // Build components
                var components = CharOverviewView.BuildComponents(
                    Context.User.Id,
                    charInfo,
                    hasRioData: rioData != null,
                    hasArmoryData: armoryEquipment != null,
                    hasWclData: wclRankings != null && wclRankings.Any(),
                    isAlreadySaved: isAlreadySaved);

                await FollowupAsync(embed: embed.Build(), components: components.Build(), ephemeral: !publicDisplay);
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

            // Fetch all data
            var rioData = await FetchRioDataAsync(charInfo);
            var (armorySummary, armoryEquipment, armoryMedia) = await FetchArmoryDataAsync(charInfo);
            var wclRankings = await FetchWclDataAsync(charInfo);

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharOverviewView.Build(charInfo, rioData, armoryEquipment, armorySummary, armoryMedia, wclRankings);
            var components = CharOverviewView.BuildComponents(
                Context.User.Id, charInfo,
                hasRioData: rioData != null,
                hasArmoryData: armoryEquipment != null,
                hasWclData: wclRankings != null && wclRankings.Any(),
                isAlreadySaved: isAlreadySaved);

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

        [ComponentInteraction("char_view_logs~*~*")]
        public async Task HandleViewLogs(string userIdStr, string charParam)
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

            var wclRankings = await FetchWclDataAsync(charInfo);
            var rioData = await FetchRioDataAsync(charInfo); // For class/spec info

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharLogsView.Build(
                charInfo,
                wclRankings,
                specName: rioData?.ActiveSpecName,
                className: rioData?.Class);

            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "logs", isAlreadySaved);

            // Add encounter select menu if we have logs
            var encounterMenu = CharLogsView.BuildEncounterSelectMenu(Context.User.Id, charInfo, wclRankings);
            if (encounterMenu != null)
            {
                components.WithSelectMenu(encounterMenu, 2);
            }

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("char_logs_encounter~*~*")]
        public async Task HandleLogsEncounterSelect(string userIdStr, string charParam, string[] selections)
        {
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

            var charInfo = ParseCharParam(charParam);
            if (charInfo == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            // Fetch WCL data
            var wclRankings = await FetchWclDataAsync(charInfo);
            if (wclRankings == null || !wclRankings.Any())
            {
                await FollowupAsync("Could not load logs data.", ephemeral: true);
                return;
            }

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            // Build encounter detail embed
            var embed = CharLogsView.BuildEncounterDetail(charInfo, wclRankings, encounterId);

            var components = CharOverviewView.BuildDetailViewComponents(Context.User.Id, charInfo, "logs", isAlreadySaved);

            // Add encounter select menu
            var encounterMenu = CharLogsView.BuildEncounterSelectMenu(Context.User.Id, charInfo, wclRankings);
            if (encounterMenu != null)
            {
                components.WithSelectMenu(encounterMenu, 2);
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

            await DeferAsync(ephemeral: true);

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

            // Build item detail embed
            var embed = BuildItemDetailEmbed(selectedItem, charInfo);
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        private EmbedBuilder BuildItemDetailEmbed(ArmoryEquippedItem item, CharacterInfo charInfo)
        {
            var embed = new EmbedBuilder();
            var slotLabel = NormalizeSlot(item.Slot?.Type ?? "Unknown");
            var qualityEmoji = CharViews.CharViewHelpers.GetQualityEmoji(item.Quality?.Name);
            var wowheadUrl = item.Item?.Id > 0 ? $"https://www.wowhead.com/item={item.Item.Id}" : null;
            var itemLevel = item.Level?.Value ?? 0;

            embed.Title = $"{slotLabel} - {item.Name}";
            embed.WithColor(new Color(0, 200, 150));
            embed.Description = $"{qualityEmoji} {(wowheadUrl != null ? $"[{item.Name}]({wowheadUrl})" : item.Name)}\n`ilvl {itemLevel}`";

            embed.AddField("Slot", slotLabel, true);
            embed.AddField("Quality", item.Quality?.Name ?? "Unknown", true);

            var notes = new List<string>();

            // Enchantments
            if (item.Enchantments != null && item.Enchantments.Count > 0)
            {
                foreach (var ench in item.Enchantments)
                {
                    notes.Add($"✨ {ench.DisplayString}");
                }
            }
            else
            {
                notes.Add("⚠️ No enchant detected");
            }

            // Sockets
            if (item.Sockets != null && item.Sockets.Count > 0)
            {
                var emptySockets = item.Sockets.Count(s => s.Item == null);
                var filled = item.Sockets.Count - emptySockets;
                notes.Add($"💎 Sockets: {filled}/{item.Sockets.Count}" + (emptySockets > 0 ? " (empty)" : ""));
            }

            // Weapon info
            if (item.Weapon != null)
            {
                var damage = item.Weapon.Damage != null
                    ? $"{item.Weapon.Damage.MinValue}-{item.Weapon.Damage.MaxValue} dmg"
                    : "Weapon";
                var speedSec = item.Weapon.AttackSpeed?.Value > 0 ? (item.Weapon.AttackSpeed.Value / 1000.0).ToString("0.00") : "?";
                var dps = item.Weapon.DPS?.Value > 0 ? item.Weapon.DPS.Value.ToString() : "?";
                notes.Add($"🗡️ {damage}, {speedSec}s, {dps} dps");
            }

            // Spell effects
            if (item.Spells != null && item.Spells.Count > 0)
            {
                var spell = item.Spells.FirstOrDefault(s => !string.IsNullOrEmpty(s.Description));
                if (spell != null)
                {
                    var desc = spell.Description.Length > 180 ? spell.Description.Substring(0, 177) + "..." : spell.Description;
                    notes.Add($"📜 {desc}");
                }
            }

            if (notes.Count > 0)
            {
                embed.AddField("Details", string.Join("\n", notes));
            }

            // Set bonus info
            if (item.Set?.ItemSet?.Name != null)
            {
                embed.AddField("Set", $"🧩 {item.Set.ItemSet.Name}", true);
            }

            embed.AddField("Wowhead", wowheadUrl != null ? $"[View Item]({wowheadUrl})" : "N/A", true);

            return embed;
        }

        private static string NormalizeSlot(string slot)
        {
            return slot switch
            {
                "FINGER_1" => "Ring 1",
                "FINGER_2" => "Ring 2",
                "TRINKET_1" => "Trinket 1",
                "TRINKET_2" => "Trinket 2",
                "MAIN_HAND" => "Main Hand",
                "OFF_HAND" => "Off Hand",
                _ => slot.Replace('_', ' ').ToLowerInvariant()
                    .Split(' ')
                    .Select(w => char.ToUpper(w[0]) + w.Substring(1))
                    .Aggregate((a, b) => $"{a} {b}")
            };
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

                    // Check if already saved
                    var existing = await repo.FirstOrDefaultAsync(c =>
                        c.UserId == userId &&
                        c.CharName.ToLower() == charInfo.Name.ToLower() &&
                        c.WowRealm.ToLower() == charInfo.Realm.ToLower());

                    if (existing != null)
                    {
                        await RespondAsync($"**{charInfo.Name}** on **{charInfo.Realm}** is already saved!", ephemeral: true);
                        return;
                    }

                    // Count existing characters for this user
                    var allUserChars = await repo.WhereAsync(c => c.UserId == userId);
                    var count = allUserChars?.Count ?? 0;

                    var newChar = new WowCharAssociation
                    {
                        UserId = userId,
                        CharName = charInfo.Name,
                        WowRealm = charInfo.Realm,
                        WowRegion = charInfo.Region,
                        IsMain = count == 0, // First character becomes main
                        TimeSet = DateTime.UtcNow
                    };

                    await repo.AddAsync(newChar);
                    await uow.SaveChangesAsync();

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

            // Re-fetch all data
            var rioTask = FetchRioDataAsync(charInfo);
            var armoryTask = FetchArmoryDataAsync(charInfo);
            var wclTask = FetchWclDataAsync(charInfo);

            await Task.WhenAll(rioTask, armoryTask, wclTask);

            var rioData = await rioTask;
            var (armorySummary, armoryEquipment, armoryMedia) = await armoryTask;
            var wclRankings = await wclTask;

            // Check if character is already saved
            var isAlreadySaved = await IsCharacterSavedAsync(charInfo, Context.User.Id);

            var embed = CharOverviewView.Build(charInfo, rioData, armoryEquipment, armorySummary, armoryMedia, wclRankings);
            var components = CharOverviewView.BuildComponents(
                Context.User.Id, charInfo,
                hasRioData: rioData != null,
                hasArmoryData: armoryEquipment != null,
                hasWclData: wclRankings != null && wclRankings.Any(),
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

            // Fetch all data
            var rioData = await FetchRioDataAsync(charInfo);
            var (armorySummary, armoryEquipment, armoryMedia) = await FetchArmoryDataAsync(charInfo);
            var wclRankings = await FetchWclDataAsync(charInfo);

            var embed = CharOverviewView.Build(charInfo, rioData, armoryEquipment, armorySummary, armoryMedia, wclRankings);

            // Send as new public message (no components for shared version)
            await Context.Channel.SendMessageAsync(
                text: $"*Shared by {Context.User.Mention}*",
                embed: embed.Build());

            await FollowupAsync("Character profile shared!", ephemeral: true);
        }

        #endregion

        #region Helper Methods

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

        private async Task<List<LogCharRankings>> FetchWclDataAsync(CharacterInfo charInfo)
        {
            try
            {
                _logger.LogInformation("Fetching WCL data for {Character} on {Realm}-{Region}",
                    charInfo.Name, charInfo.RealmSlug, charInfo.Region);

                var result = await _wclApi.GetRankingFromCharName(
                    charInfo.Name,
                    charInfo.RealmSlug,
                    charInfo.Region);

                _logger.LogInformation("WCL returned {Count} rankings for {Character}",
                    result?.Count ?? 0, charInfo.Name);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch WCL data for {Character} on {Realm}", charInfo.Name, charInfo.RealmSlug);
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
                RealmSlug = parts[1].ToLower().Replace(" ", "-").Replace("'", ""),
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

                    var existing = await repo.FirstOrDefaultAsync(c =>
                        c.UserId == userIdLong &&
                        c.CharName.ToLower() == charInfo.Name.ToLower() &&
                        c.WowRealm.ToLower() == charInfo.Realm.ToLower());

                    return existing != null;
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking if character is saved");
                return false;
            }
        }

        #endregion
    }
}
