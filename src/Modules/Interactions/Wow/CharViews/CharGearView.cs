using Discord;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the Gear view embed for character profiles (from Blizzard Armory API)
    /// </summary>
    public static class CharGearView
    {
        private static readonly string[] SlotOrder = new[]
        {
            "HEAD", "NECK", "SHOULDER", "BACK", "CHEST", "WRIST",
            "HANDS", "WAIST", "LEGS", "FEET",
            "FINGER_1", "FINGER_2", "TRINKET_1", "TRINKET_2",
            "MAIN_HAND", "OFF_HAND"
        };

        /// <summary>
        /// Build the gear view embed from Armory data
        /// </summary>
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            ArmorySummary armorySummary,
            ArmoryEquipment armoryEquipment,
            ArmoryMedia armoryMedia)
        {
            var embed = new EmbedBuilder();
            var descriptionBuilder = new StringBuilder();

            // Title
            var specLabel = armorySummary?.ActiveSpec?.Name ?? "";
            var className = armorySummary?.CharacterClass?.Name ?? "Unknown Class";
            var charName = armorySummary?.Name ?? charInfo.Name;

            embed.Title = !string.IsNullOrEmpty(specLabel)
                ? $"{specLabel} {className} - {charName}"
                : $"{className} - {charName}";

            embed.WithColor(new Color(0, 200, 150));

            // Item Level
            var equippedIlvl = armoryEquipment?.EquippedItemLevel ?? armorySummary?.EquippedItemLevel ?? 0;
            var averageIlvl = armoryEquipment?.AverageItemLevel ?? armorySummary?.AverageItemLevel ?? equippedIlvl;

            descriptionBuilder.AppendLine($"**Item Level:** {equippedIlvl} (equipped) / {averageIlvl} (max)");

            // Stats summary
            var statTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var setProgress = new Dictionary<int, (string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)>();

            // Class / Spec field
            embed.AddField("Class / Spec", $"{className} — {specLabel}", true);

            // Wowhead Guide
            var guideUrl = BuildWowheadGuideUrl(className, specLabel);
            embed.AddField("Wowhead Guide", $"[Open guide]({guideUrl})", true);

            // Process gear slots
            if (armoryEquipment?.EquippedItems != null)
            {
                foreach (var slot in SlotOrder)
                {
                    var slotLabel = NormalizeSlot(slot);
                    var item = armoryEquipment.EquippedItems.FirstOrDefault(i =>
                        string.Equals(i.Slot?.Type, slot, StringComparison.OrdinalIgnoreCase));

                    if (item == null)
                    {
                        embed.AddField(slotLabel, "_empty_", true);
                        continue;
                    }

                    var fieldValue = BuildItemFieldValue(item);
                    embed.AddField(slotLabel, fieldValue, true);

                    // Collect stats
                    if (item.Stats != null)
                    {
                        foreach (var stat in item.Stats)
                        {
                            if (string.IsNullOrEmpty(stat?.Type?.Type)) continue;
                            if (statTotals.ContainsKey(stat.Type.Type))
                                statTotals[stat.Type.Type] += stat.Value;
                            else
                                statTotals[stat.Type.Type] = stat.Value;
                        }
                    }

                    // Track set progress
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
                            progress.ItemIds.Add(item.Item.Id);
                        setProgress[item.Set.ItemSet.Id] = progress;
                    }
                }
            }

            // Stats section
            var statsSummary = BuildStatsSummary(statTotals);
            if (!string.IsNullOrEmpty(statsSummary))
            {
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("**Stats**");
                descriptionBuilder.AppendLine(statsSummary);
            }

            // Sets section
            var setsSection = BuildSetsSection(setProgress.Values);
            if (!string.IsNullOrEmpty(setsSection))
            {
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("**Sets**");
                descriptionBuilder.AppendLine(setsSection);
            }

            embed.Description = descriptionBuilder.ToString();

            // Armory link
            embed.AddField("Armory", $"[View on Battle.net]({charInfo.ArmoryUrl})", true);

            // Images
            embed.ThumbnailUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            embed.ImageUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "main-raw")?.Value;

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()})"
            };

            return embed;
        }

        /// <summary>
        /// Build gear select menu for item details
        /// </summary>
        public static SelectMenuBuilder BuildItemSelectMenu(
            ulong userId,
            CharacterInfo charInfo,
            ArmoryEquipment armoryEquipment)
        {
            var options = new List<SelectMenuOptionBuilder>();

            foreach (var slot in SlotOrder)
            {
                var item = armoryEquipment?.EquippedItems?.FirstOrDefault(i =>
                    string.Equals(i.Slot?.Type, slot, StringComparison.OrdinalIgnoreCase));

                if (item?.Item?.Id > 0)
                {
                    var slotLabel = NormalizeSlot(slot);
                    var itemLevel = item.Level?.Value ?? 0;

                    var optionLabel = $"{slotLabel} - ilvl {itemLevel}";
                    if (optionLabel.Length > 100)
                        optionLabel = optionLabel.Substring(0, 100);

                    var optionDescription = item.Name?.Length > 100
                        ? item.Name.Substring(0, 97) + "..."
                        : item.Name ?? "Unknown";

                    options.Add(new SelectMenuOptionBuilder
                    {
                        Label = optionLabel,
                        Value = $"{slot}:{item.Item.Id}",
                        Description = optionDescription
                    });
                }
            }

            if (options.Count == 0) return null;

            var charParam = $"{charInfo.Name}~{charInfo.RealmSlug}~{charInfo.Region}";
            return new SelectMenuBuilder()
                .WithCustomId($"char_gear_select~{userId}~{charParam}")
                .WithPlaceholder("Select an item for details")
                .WithMinValues(1)
                .WithMaxValues(1)
                .WithOptions(options);
        }

        private static string BuildItemFieldValue(ArmoryEquippedItem item)
        {
            var sb = new StringBuilder();
            var qualityEmoji = CharViewHelpers.GetQualityEmoji(item.Quality?.Name);
            var wowheadUrl = item.Item?.Id > 0 ? $"https://www.wowhead.com/item={item.Item.Id}" : null;
            var itemLevel = item.Level?.Value ?? 0;

            if (!string.IsNullOrEmpty(qualityEmoji))
                sb.Append($"{qualityEmoji} ");

            sb.Append(!string.IsNullOrEmpty(wowheadUrl)
                ? $"[{item.Name}]({wowheadUrl})"
                : item.Name);

            sb.Append($"\n`ilvl {itemLevel}`");

            // Warnings
            var notes = new List<string>();
            if (item.Enchantments == null || item.Enchantments.Count == 0)
            {
                // Only warn on enchantable slots
                var slot = item.Slot?.Type?.ToUpper();
                if (slot is "BACK" or "CHEST" or "WRIST" or "LEGS" or "FEET" or "FINGER_1" or "FINGER_2" or "MAIN_HAND")
                {
                    notes.Add("no enchant");
                }
            }

            if (item.Sockets != null && item.Sockets.Count > 0)
            {
                var emptySockets = item.Sockets.Count(s => s.Item == null);
                if (emptySockets > 0)
                    notes.Add($"{emptySockets} empty socket(s)");
            }

            if (notes.Count > 0)
                sb.Append($"\n⚠️ {string.Join(", ", notes)}");

            return sb.ToString();
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

        private static string BuildWowheadGuideUrl(string className, string specName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(specName))
                return "https://www.wowhead.com/class-guides";

            var query = Uri.EscapeDataString($"{specName} {className} guide");
            return $"https://www.wowhead.com/search?q={query}";
        }

        private static string BuildStatsSummary(Dictionary<string, int> statTotals)
        {
            if (statTotals == null || statTotals.Count == 0)
                return null;

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
                        primary.Add(entry);
                    else
                        secondary.Add(entry);
                }
            }

            var lines = new List<string>();
            if (primary.Count > 0)
                lines.Add(string.Join("   ", primary));
            if (secondary.Count > 0)
                lines.Add(string.Join("   ", secondary));

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private static string BuildSetsSection(IEnumerable<(string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)> sets)
        {
            if (sets == null) return null;

            var setStrings = new List<string>();
            foreach (var set in sets)
            {
                if (string.IsNullOrEmpty(set.Name)) continue;

                var equippedCount = set.ItemIds.Count;
                var total = set.TotalPieces > 0 ? set.TotalPieces : equippedCount;
                var sb = new StringBuilder();
                sb.Append($"🧩 **{set.Name}** ({equippedCount}/{total})");

                foreach (var effect in set.Effects ?? Enumerable.Empty<ArmorySetEffect>())
                {
                    var marker = effect.IsActive ? "✅" : "▫️";
                    var display = effect.DisplayString;
                    if (!string.IsNullOrEmpty(display) && display.Length > 50)
                        display = display.Substring(0, 47) + "...";
                    sb.Append($"\n • {marker} ({effect.RequiredCount}) {display}");
                }

                setStrings.Add(sb.ToString());
            }

            return setStrings.Count == 0 ? null : string.Join("\n\n", setStrings);
        }
    }
}
