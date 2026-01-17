using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Armory-related commands for viewing character gear and equipment details.
    /// Includes: /armory, armory_item_select component
    /// </summary>
    public class ArmoryCommands : NinjaBotBaseModule
    {
        private readonly ILogger<ArmoryCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public ArmoryCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<ArmoryCommands> logger,
            WowApi wowApi,
            WowUtilities wowUtils,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
        }

        [SlashCommand("armory", "Show a character's gear from the Armory")]
        public async Task GetArmoryGear(
            [Summary("character", "Character name (leave empty to use your main character)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character = null,

            [Summary("realm", "Realm name (optional if using autocomplete)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("RU", "ru")]
            string region = null,

            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            try
            {
                await DeferAsync(ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to defer interaction for /armory command");
                await RespondAsync("The request took too long to process. Please try again.", ephemeral: true);
                return;
            }

            string charName = null;
            string realmName = null;
            string regionName = region;
            var embed = new EmbedBuilder();

            if (string.IsNullOrEmpty(character))
            {
                var charAssociation = await _wowCache.GetUserMainCharacterAsync((long)Context.User.Id);

                if (charAssociation != null)
                {
                    charName = charAssociation.CharName;
                    realmName = charAssociation.WowRealm;
                    regionName ??= charAssociation.WowRegion;
                }
                else
                {
                    embed.Title = "No Main Character Set";
                    embed.WithColor(new Color(255, 165, 0));
                    embed.Description = "You haven't set a main character yet!\n\nUse `/getchars` to manage your saved characters.";
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
            }
            else
            {
                var parts = character.Split('~', 3);
                charName = parts[0];

                if (string.IsNullOrEmpty(realmName) && parts.Length >= 2)
                {
                    realmName = parts[1];
                }
                else if (!string.IsNullOrEmpty(realm))
                {
                    realmName = realm;
                }

                if (string.IsNullOrEmpty(regionName) && parts.Length >= 3)
                {
                    regionName = parts[2];
                }
            }

            if (string.IsNullOrEmpty(realmName))
            {
                var guildObject = await _wowUtils.GetGuildName(Context);

                if (!string.IsNullOrEmpty(guildObject.guildName))
                {
                    var guildie = await _wowApi.GetCharFromGuildAsync(
                        charName,
                        guildObject.realmName,
                        guildObject.guildName,
                        guildObject.regionName);

                    if (!string.IsNullOrEmpty(guildie.charName))
                    {
                        realmName = guildie.realmName;
                        regionName ??= guildie.regionName;
                    }
                }

                if (string.IsNullOrEmpty(realmName))
                {
                    var chars = await _wowApi.SearchArmoryAsync(charName);
                    if (chars != null && chars.Count > 0)
                    {
                        realmName = chars[0].realmName;
                    }
                    else
                    {
                        embed.Title = "Character Not Found";
                        embed.WithColor(new Color(255, 0, 0));
                        embed.Description = $"Could not find character **{charName}**.\n\nPlease specify the realm name using the `realm` parameter, or use autocomplete to select your character.";
                        await FollowupAsync(embed: embed.Build(), ephemeral: true);
                        return;
                    }
                }
            }

            regionName ??= "us";

            ArmorySummary armorySummary;
            ArmoryEquipment armoryEquipment;
            ArmoryMedia armoryMedia = null;
            var realmSlugForCache = realmName.Replace("'", string.Empty).Replace(" ", "-").ToLowerInvariant();

            try
            {
                var summaryTask = _wowApi.GetArmorySummaryAsync(charName, realmName, regionName);
                var equipmentTask = _wowApi.GetArmoryEquipmentAsync(charName, realmName, regionName);
                var mediaTask = _wowApi.GetArmoryMediaAsync(charName, realmName, regionName);

                await Task.WhenAll(summaryTask, equipmentTask, mediaTask);
                armorySummary = summaryTask.Result;
                armoryEquipment = equipmentTask.Result;
                armoryMedia = mediaTask.Result;

                // Cache the equipment and media data for subsequent item detail requests
                if (armoryEquipment != null && armoryMedia != null)
                {
                    _wowCache.SetCachedArmoryEquipment(charName, realmSlugForCache, regionName, armoryEquipment, armoryMedia);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching armory gear for {Character} on {Realm}", charName, realmName);
                embed.Title = "Error Fetching Armory";
                embed.WithColor(new Color(255, 0, 0));
                embed.Description = $"Could not load **{charName}** on **{realmName}** ({regionName.ToUpper()}). Please try again later.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            if (armorySummary == null || armoryEquipment?.EquippedItems == null)
            {
                embed.Title = "No Gear Found";
                embed.WithColor(new Color(255, 165, 0));
                embed.Description = $"The gear for **{charName}** on **{realmName}** could not be loaded.";
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }

            var slotOrder = new[]
            {
                "HEAD","NECK","SHOULDER","BACK","CHEST","WRIST","HANDS","WAIST","LEGS","FEET","FINGER_1","FINGER_2","TRINKET_1","TRINKET_2","MAIN_HAND","OFF_HAND"
            };

            var gearFields = new List<EmbedFieldBuilder>();
            var selectOptions = new List<SelectMenuOptionBuilder>();
            var setProgress = new Dictionary<int, (string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)>();
            var statTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in slotOrder)
            {
                var slotLabel = NormalizeSlot(slot);
                var item = armoryEquipment.EquippedItems.FirstOrDefault(i => string.Equals(i.Slot?.Type, slot, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    gearFields.Add(new EmbedFieldBuilder
                    {
                        Name = slotLabel,
                        Value = "_empty_",
                        IsInline = true
                    });
                    continue;
                }

                var qualityEmoji = GetQualityEmojiByName(item.Quality?.Name);
                var wowheadUrl = item.Item?.Id > 0 ? $"https://www.wowhead.com/item={item.Item.Id}" : null;
                var itemLevel = item.Level?.Value ?? 0;
                var fieldValue = new StringBuilder();

                if (!string.IsNullOrEmpty(qualityEmoji))
                {
                    fieldValue.Append($"{qualityEmoji} ");
                }

                fieldValue.Append(!string.IsNullOrEmpty(wowheadUrl) ? $"[{item.Name}]({wowheadUrl})" : item.Name);
                fieldValue.Append($"\n`ilvl {itemLevel}`");

                var notes = new List<string>();
                if (item.Enchantments == null || item.Enchantments.Count == 0)
                {
                    notes.Add("⚠️ no enchant");
                }

                if (item.Sockets != null && item.Sockets.Count > 0)
                {
                    var emptySockets = item.Sockets.Count(s => s.Item == null);
                    if (emptySockets > 0)
                    {
                        notes.Add($"🟥 {emptySockets} empty socket(s)");
                    }
                }

                if (notes.Count > 0)
                {
                    fieldValue.Append($"\n{string.Join(" · ", notes)}");
                }

                gearFields.Add(new EmbedFieldBuilder
                {
                    Name = slotLabel,
                    Value = fieldValue.ToString(),
                    IsInline = true
                });

                if (item.Stats != null)
                {
                    foreach (var stat in item.Stats)
                    {
                        if (string.IsNullOrEmpty(stat?.Type?.Type))
                        {
                            continue;
                        }

                        if (statTotals.ContainsKey(stat.Type.Type))
                        {
                            statTotals[stat.Type.Type] += stat.Value;
                        }
                        else
                        {
                            statTotals[stat.Type.Type] = stat.Value;
                        }
                    }
                }

                if (item.Set?.ItemSet?.Id > 0)
                {
                    if (!setProgress.TryGetValue(item.Set.ItemSet.Id, out var progress))
                    {
                        progress = (
                            item.Set.ItemSet.Name,
                            new HashSet<int>(),
                            item.Set.Effects ?? new List<ArmorySetEffect>(),
                            item.Set.Items?.Count ?? 0);
                    }

                    if (item.Item?.Id > 0)
                    {
                        progress.ItemIds.Add(item.Item.Id);
                    }

                    setProgress[item.Set.ItemSet.Id] = progress;
                }

                if (item.Item?.Id > 0)
                {
                    var optionLabel = $"{slotLabel} • ilvl {itemLevel}";
                    if (optionLabel.Length > 100)
                    {
                        optionLabel = optionLabel.Substring(0, 100);
                    }

                    var optionDescription = item.Name.Length > 100
                        ? $"{item.Name.Substring(0, 97)}..."
                        : item.Name;

                    selectOptions.Add(new SelectMenuOptionBuilder
                    {
                        Label = optionLabel,
                        Value = $"{slot}:{item.Item.Id}",
                        Description = optionDescription
                    });
                }
            }

            var specLabel = string.IsNullOrEmpty(armorySummary.ActiveSpec?.Name)
                ? $"Level {armorySummary.Level}"
                : armorySummary.ActiveSpec.Name;
            var className = armorySummary.CharacterClass?.Name ?? "Unknown Class";
            var realmSlug = armorySummary.Realm?.Slug ?? realmName.Replace(" ", "-");

            embed.Title = $"{specLabel} {className} - {armorySummary.Name}";
            embed.WithColor(new Color(0, 200, 150));
            embed.ThumbnailUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            embed.ImageUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "main-raw")?.Value;
            var equippedIlvl = armoryEquipment.EquippedItemLevel > 0
                ? armoryEquipment.EquippedItemLevel
                : armorySummary.EquippedItemLevel;
            var averageIlvl = armoryEquipment.AverageItemLevel > 0
                ? armoryEquipment.AverageItemLevel
                : (armorySummary.AverageItemLevel > 0 ? armorySummary.AverageItemLevel : equippedIlvl);

            var descriptionBuilder = new StringBuilder();
            descriptionBuilder.AppendLine($"**Item Level:** {equippedIlvl} (equipped) / {averageIlvl} (max)");

            embed.AddField("Class / Spec", $"{className} — {specLabel}", true);
            var guideUrl = BuildWowheadGuideUrl(className, specLabel);
            embed.AddField("Wowhead Guide", $"[Open guide]({guideUrl})", true);

            var statsSummary = BuildStatsSummary(statTotals);
            if (!string.IsNullOrEmpty(statsSummary))
            {
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("**Stats**");
                descriptionBuilder.AppendLine(statsSummary);
            }

            foreach (var field in gearFields)
            {
                embed.AddField(field);
            }

            var setsSection = BuildSetsSection(setProgress.Values);
            if (!string.IsNullOrEmpty(setsSection))
            {
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("**Sets**");
                descriptionBuilder.AppendLine(setsSection);
            }

            embed.Description = descriptionBuilder.ToString();

            var armoryLocale = regionName.ToLower() switch
            {
                "us" => "en-us",
                "eu" => "en-gb",
                "ru" => "ru-ru",
                _ => "en-us"
            };

            var armoryUrl = $"https://worldofwarcraft.blizzard.com/{armoryLocale}/character/{regionName.ToLower()}/{realmSlug}/{armorySummary.Name.ToLower()}";
            embed.AddField("Armory", $"[View on Battle.net]({armoryUrl})", true);
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{realmSlug} ({regionName.ToUpper()})"
            };

            MessageComponent components = null;
            if (selectOptions.Any())
            {
                var componentBuilder = new ComponentBuilder();
                var itemSelectorId = $"armory_item_select~{Context.User.Id}~{armorySummary.Name}~{realmSlug}~{regionName.ToLower()}";
                componentBuilder.WithSelectMenu(
                    customId: itemSelectorId,
                    options: selectOptions,
                    placeholder: "Select an item for details",
                    minValues: 1,
                    maxValues: 1,
                    row: 0);

                components = componentBuilder.Build();
            }

            await FollowupAsync(embed: embed.Build(), components: components, ephemeral: !publicDisplay);
        }

        [ComponentInteraction("armory_item_select~*~*~*~*")]
        public async Task HandleArmoryItemSelection(string originalUserIdStr, string characterName, string realmSlug, string regionName, string[] selections)
        {
            if (!ulong.TryParse(originalUserIdStr, out var originalUserId))
            {
                await RespondAsync("❌ Invalid interaction data. Please try again.", ephemeral: true);
                return;
            }

            if (Context.User.Id != originalUserId)
            {
                _logger.LogWarning(
                    "User {AttemptingUserId} ({AttemptingUsername}) tried to interact with User {OriginalUserId}'s armory gear selector",
                    Context.User.Id, Context.User.Username, originalUserId);
                await RespondAsync("❌ This selection belongs to another user.", ephemeral: true);
                return;
            }

            if (selections == null || selections.Length == 0)
            {
                await RespondAsync("No item was selected.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var selection = selections[0];
            var parts = selection.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var itemId))
            {
                await FollowupAsync("Could not read that item selection. Please try again.", ephemeral: true);
                return;
            }

            var slotType = parts[0];

            ArmoryEquipment armoryEquipment = null;
            ArmoryItemMedia itemMedia = null;

            // Try to get cached equipment first
            var cachedData = _wowCache.GetCachedArmoryEquipment(characterName, realmSlug, regionName);
            if (cachedData.HasValue)
            {
                armoryEquipment = cachedData.Value.Equipment;
                _logger.LogDebug("Using cached armory equipment for {Character} on {Realm}", characterName, realmSlug);

                // Still fetch item media for the icon (relatively cheap API call)
                try
                {
                    itemMedia = await _wowApi.GetItemMediaAsync(itemId, regionName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching item media for item {ItemId}, continuing without icon", itemId);
                    // Continue without item media - not critical
                }
            }
            else
            {
                // Cache miss - fall back to API calls
                _logger.LogDebug("Cache miss for armory equipment, fetching from API for {Character} on {Realm}", characterName, realmSlug);
                try
                {
                    var equipmentTask = _wowApi.GetArmoryEquipmentAsync(characterName, realmSlug, regionName);
                    var itemMediaTask = _wowApi.GetItemMediaAsync(itemId, regionName);

                    await Task.WhenAll(equipmentTask, itemMediaTask);
                    armoryEquipment = equipmentTask.Result;
                    itemMedia = itemMediaTask.Result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching armory item details for {Character} on {Realm}", characterName, realmSlug);
                    await FollowupAsync("Could not load that item right now. Please try again.", ephemeral: true);
                    return;
                }
            }

            var selectedItem = armoryEquipment?.EquippedItems?.FirstOrDefault(i =>
                string.Equals(i.Slot?.Type, slotType, StringComparison.OrdinalIgnoreCase) ||
                (i.Item?.Id == itemId));

            if (selectedItem == null)
            {
                await FollowupAsync("That item was not found on the character.", ephemeral: true);
                return;
            }

            var qualityEmoji = GetQualityEmojiByName(selectedItem.Quality?.Name) ?? GetQualityEmoji(null);
            var wowheadUrl = selectedItem.Item?.Id > 0 ? $"https://www.wowhead.com/item={selectedItem.Item.Id}" : null;
            var iconUrl = itemMedia?.Assets?.FirstOrDefault(a => a.Key == "icon")?.Value
                ?? itemMedia?.Assets?.FirstOrDefault()?.Value;
            var slotLabel = NormalizeSlot(selectedItem.Slot?.Type ?? slotType);
            var itemLevel = selectedItem.Level?.Value ?? 0;
            var notes = new List<string>();

            if (selectedItem.Enchantments != null && selectedItem.Enchantments.Count > 0)
            {
                notes.AddRange(selectedItem.Enchantments.Select(e => $"✨ {e.DisplayString}"));
            }
            else
            {
                notes.Add("⚠️ No enchant detected");
            }

            if (selectedItem.Sockets != null && selectedItem.Sockets.Count > 0)
            {
                var emptySockets = selectedItem.Sockets.Count(s => s.Item == null);
                var filled = selectedItem.Sockets.Count - emptySockets;
                notes.Add($"💎 Sockets: {filled}/{selectedItem.Sockets.Count}" + (emptySockets > 0 ? " (empty sockets)" : string.Empty));
            }

            if (selectedItem.Weapon != null)
            {
                var weapon = selectedItem.Weapon;
                var damage = weapon.Damage != null
                    ? $"{weapon.Damage.MinValue}-{weapon.Damage.MaxValue} dmg"
                    : "Weapon";
                var speedSec = weapon.AttackSpeed?.Value > 0 ? (weapon.AttackSpeed.Value / 1000.0).ToString("0.00") : "?";
                var dps = weapon.DPS?.Value > 0 ? weapon.DPS.Value.ToString() : "?";
                notes.Add($"🗡️ {damage}, {speedSec}s, {dps} dps");
            }

            if (selectedItem.Spells != null && selectedItem.Spells.Count > 0)
            {
                var spell = selectedItem.Spells.FirstOrDefault(s => !string.IsNullOrEmpty(s.Description));
                if (spell != null)
                {
                    var desc = spell.Description.Length > 180 ? spell.Description.Substring(0, 177) + "..." : spell.Description;
                    notes.Add($"📜 {desc}");
                }
            }

            var detailEmbed = new EmbedBuilder();
            detailEmbed.Title = $"{slotLabel} — {selectedItem.Name}";
            detailEmbed.WithColor(new Color(0, 200, 150));
            detailEmbed.Description = $"{qualityEmoji} {(wowheadUrl != null ? $"[{selectedItem.Name}]({wowheadUrl})" : selectedItem.Name)}\n`ilvl {itemLevel}`";

            if (!string.IsNullOrEmpty(iconUrl))
            {
                detailEmbed.ThumbnailUrl = iconUrl;
            }

            detailEmbed.AddField("Slot", slotLabel, true);
            detailEmbed.AddField("Quality", selectedItem.Quality?.Name ?? "Unknown", true);

            if (notes.Count > 0)
            {
                detailEmbed.AddField("Details", string.Join("\n", notes));
            }

            if (selectedItem.Set?.ItemSet?.Name != null)
            {
                var total = selectedItem.Set.Items?.Count ?? 0;
                var equipped = armoryEquipment?.EquippedItems?
                    .Where(i => i.Set?.ItemSet?.Id == selectedItem.Set.ItemSet.Id)
                    .Select(i => i.Item?.Id)
                    .Where(id => id.HasValue)
                    .Distinct()
                    .Count() ?? 1;
                var setSb = new StringBuilder();
                setSb.AppendLine();
                setSb.AppendLine($"🧩 **{selectedItem.Set.ItemSet.Name}** ({equipped}/{(total > 0 ? total : equipped)})");
                foreach (var effect in selectedItem.Set.Effects ?? Enumerable.Empty<ArmorySetEffect>())
                {
                    var marker = effect.IsActive ? "✅" : "▫️";
                    var display = effect.DisplayString;
                    if (!string.IsNullOrEmpty(display) && display.Length > 170)
                    {
                        display = display.Substring(0, 167) + "...";
                    }
                    setSb.AppendLine($" • {marker} ({effect.RequiredCount}) {display}");
                }

                detailEmbed.Description += setSb.ToString();
            }

            var armoryLocale = regionName.ToLower() switch
            {
                "us" => "en-us",
                "eu" => "en-gb",
                "ru" => "ru-ru",
                _ => "en-us"
            };
            var armoryUrl = $"https://worldofwarcraft.blizzard.com/{armoryLocale}/character/{regionName.ToLower()}/{realmSlug}/{characterName.ToLower()}";
            detailEmbed.AddField("Armory", $"[View on Battle.net]({armoryUrl})", true);

            await FollowupAsync(embed: detailEmbed.Build(), ephemeral: true);
        }

        #region Private Helper Methods

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
                _ => slot.Replace('_', ' ').ToLowerInvariant().Split(' ').Select(w => char.ToUpper(w[0]) + w.Substring(1)).Aggregate((a, b) => $"{a} {b}")
            };
        }

        private static string BuildWowheadGuideUrl(string className, string specName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(specName))
            {
                return "https://www.wowhead.com/class-guides";
            }

            var query = Uri.EscapeDataString($"{specName} {className} guide");
            return $"https://www.wowhead.com/search?q={query}";
        }

        private static string BuildStatsSummary(Dictionary<string, int> statTotals)
        {
            if (statTotals == null || statTotals.Count == 0)
            {
                return null;
            }

            var statMeta = new (string Key, string Label, string Emoji)[]
            {
                ("INTELLECT", "Int", "🧠"),
                ("STRENGTH", "Str", "💪"),
                ("AGILITY", "Agi", "🦊"),
                ("STAMINA", "Stam", "❤️"),
                ("CRITICAL_STRIKE", "Crit", "🎯"),
                ("HASTE", "Haste", "⚡"),
                ("MASTERY", "Mast", "✨"),
                ("VERSATILITY", "Vers", "🛡️"),
                ("AVOIDANCE", "Avoid", "🌀"),
                ("LEECH", "Leech", "🩸")
            };

            var primary = new List<string>();
            var secondary = new List<string>();

            foreach (var (key, label, emoji) in statMeta)
            {
                if (statTotals.TryGetValue(key, out var value) && value > 0)
                {
                    var entry = $"{emoji} {label} {value}";
                    if (key is "INTELLECT" or "STRENGTH" or "AGILITY" or "STAMINA")
                    {
                        primary.Add(entry);
                    }
                    else
                    {
                        secondary.Add(entry);
                    }
                }
            }

            var lines = new List<string>();
            if (primary.Count > 0)
            {
                lines.Add(string.Join("   ", primary));
            }
            if (secondary.Count > 0)
            {
                lines.Add(string.Join("   ", secondary));
            }

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private static string BuildSetsSection(IEnumerable<(string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)> sets)
        {
            if (sets == null)
            {
                return null;
            }

            var setStrings = new List<string>();
            foreach (var set in sets)
            {
                if (string.IsNullOrEmpty(set.Name))
                {
                    continue;
                }

                var equippedCount = set.ItemIds.Count;
                var total = set.TotalPieces > 0 ? set.TotalPieces : set.ItemIds.Count;
                var sb = new StringBuilder();
                sb.AppendLine($"🧩 **{set.Name}** ({equippedCount}/{total})");
                foreach (var effect in set.Effects ?? Enumerable.Empty<ArmorySetEffect>())
                {
                    var marker = effect.IsActive ? "✅" : "▫️";
                    var display = effect.DisplayString;
                    if (!string.IsNullOrEmpty(display) && display.Length > 170)
                    {
                        display = display.Substring(0, 167) + "...";
                    }
                    sb.AppendLine($" • {marker} ({effect.RequiredCount}) {display}");
                }
                setStrings.Add(sb.ToString().TrimEnd());
            }

            return setStrings.Count == 0 ? null : string.Join("\n\n", setStrings);
        }

        private static string GetQualityEmoji(int? quality)
        {
            return quality switch
            {
                >= 6 => "🟠", // Artifact/Mythic+
                5 => "🟠",   // Legendary
                4 => "🟣",   // Epic
                3 => "🔵",   // Rare
                2 => "🟢",   // Uncommon
                _ => "⚪"    // Common/poor
            };
        }

        private static string GetQualityEmojiByName(string qualityName)
        {
            return qualityName?.ToLower() switch
            {
                "legendary" => "🟠",
                "artifact" => "🟠",
                "epic" => "🟣",
                "rare" => "🔵",
                "uncommon" => "🟢",
                "common" => "⚪",
                _ => null
            };
        }

        #endregion
    }
}
