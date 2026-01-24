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
        /// Build the gear view embed from Armory data (concise version with gear audit)
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

            // Item Level - prefer summary endpoint as equipment endpoint doesn't always include these fields
            var equippedIlvl = armorySummary?.EquippedItemLevel ?? armoryEquipment?.EquippedItemLevel ?? 0;
            var averageIlvl = armorySummary?.AverageItemLevel ?? armoryEquipment?.AverageItemLevel ?? equippedIlvl;

            descriptionBuilder.AppendLine($"**Item Level:** {equippedIlvl} / {averageIlvl}");

            // Process gear for stats, sets, and audit
            var statTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var setProgress = new Dictionary<int, (string Name, HashSet<int> ItemIds, List<ArmorySetEffect> Effects, int TotalPieces)>();
            var missingEnchants = new List<string>();
            var emptySockets = new List<string>();
            var lowestItems = new List<(string Slot, int Ilvl)>();

            if (armoryEquipment?.EquippedItems != null)
            {
                foreach (var slot in SlotOrder)
                {
                    var item = armoryEquipment.EquippedItems.FirstOrDefault(i =>
                        string.Equals(i.Slot?.Type, slot, StringComparison.OrdinalIgnoreCase));

                    if (item == null) continue;

                    var slotLabel = CharViewHelpers.NormalizeSlot(slot);
                    var itemLevel = item.Level?.Value ?? 0;

                    // Track item levels for "lowest" display
                    if (itemLevel > 0)
                    {
                        lowestItems.Add((slotLabel, itemLevel));
                    }

                    // Check enchants on enchantable slots
                    if (item.Enchantments == null || item.Enchantments.Count == 0)
                    {
                        if (slot is "BACK" or "CHEST" or "WRIST" or "LEGS" or "FEET" or "FINGER_1" or "FINGER_2" or "MAIN_HAND")
                        {
                            missingEnchants.Add(slotLabel);
                        }
                    }

                    // Check for empty sockets
                    if (item.Sockets != null && item.Sockets.Count > 0)
                    {
                        var empty = item.Sockets.Count(s => s.Item == null);
                        if (empty > 0)
                        {
                            emptySockets.Add($"{slotLabel} ({empty})");
                        }
                    }

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

            // Gear Audit section
            descriptionBuilder.AppendLine();
            descriptionBuilder.AppendLine("**Gear Audit**");

            if (missingEnchants.Count == 0)
            {
                descriptionBuilder.AppendLine("✅ All enchants applied");
            }
            else
            {
                descriptionBuilder.AppendLine($"⚠️ Missing enchants: {string.Join(", ", missingEnchants)}");
            }

            if (emptySockets.Count > 0)
            {
                descriptionBuilder.AppendLine($"⚠️ Empty sockets: {string.Join(", ", emptySockets)}");
            }

            // Show lowest ilvl pieces (bottom 3)
            if (lowestItems.Count > 0)
            {
                var lowest = lowestItems.OrderBy(x => x.Ilvl).Take(3).ToList();
                var lowestStr = string.Join(", ", lowest.Select(x => $"{x.Slot} ({x.Ilvl})"));
                descriptionBuilder.AppendLine($"📉 Lowest: {lowestStr}");
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

            // Compact fields row
            embed.AddField("Class / Spec", $"{className} — {specLabel}", true);

            var guideUrl = BuildWowheadGuideUrl(className, specLabel);
            embed.AddField("Wowhead Guide", $"[Open]({guideUrl})", true);

            embed.AddField("Armory", $"[View]({charInfo.ArmoryUrl})", true);

            // Images
            embed.ThumbnailUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            embed.ImageUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "main-raw")?.Value;

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{charInfo.Realm} ({charInfo.Region.ToUpper()}) | Select gear below for details"
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
                    var slotLabel = CharViewHelpers.NormalizeSlot(slot);
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

            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";
            return new SelectMenuBuilder()
                .WithCustomId($"char_gear_select~{userId}~{charParam}")
                .WithPlaceholder("Select an item for details")
                .WithMinValues(1)
                .WithMaxValues(1)
                .WithOptions(options);
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

        /// <summary>
        /// Build components for item detail view (back button + item select)
        /// </summary>
        public static ComponentBuilder BuildItemDetailComponents(
            ulong userId,
            CharacterInfo charInfo,
            ArmoryEquipment armoryEquipment)
        {
            var builder = new ComponentBuilder();
            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";

            // Row 0: Back button
            builder.WithButton(
                label: "Back to Gear",
                customId: $"char_view_gear~{userId}~{charParam}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("↩️"),
                row: 0);

            // Row 1: Item select dropdown (so user can select another item)
            var selectMenu = BuildItemSelectMenu(userId, charInfo, armoryEquipment);
            if (selectMenu != null)
            {
                builder.WithSelectMenu(selectMenu, 1);
            }

            return builder;
        }

        /// <summary>
        /// Build a detailed embed for a single equipped item
        /// </summary>
        public static EmbedBuilder BuildItemDetail(ArmoryEquippedItem item, CharacterInfo charInfo, ArmoryItemMedia itemMedia = null)
        {
            var embed = new EmbedBuilder();
            var slotLabel = CharViewHelpers.NormalizeSlot(item.Slot?.Type ?? "Unknown");
            var qualityEmoji = CharViewHelpers.GetQualityEmoji(item.Quality?.Name);
            var wowheadUrl = item.Item?.Id > 0 ? $"https://www.wowhead.com/item={item.Item.Id}" : null;
            var itemLevel = item.Level?.Value ?? 0;

            embed.Title = $"{slotLabel} - {item.Name}";
            embed.WithColor(new Color(0, 200, 150));
            embed.Description = $"{qualityEmoji} {(wowheadUrl != null ? $"[{item.Name}]({wowheadUrl})" : item.Name)}\n`ilvl {itemLevel}`";

            // Set item icon as thumbnail if available
            var iconUrl = itemMedia?.Assets?.FirstOrDefault(a => a.Key == "icon")?.Value;
            if (!string.IsNullOrEmpty(iconUrl))
            {
                embed.ThumbnailUrl = iconUrl;
            }

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
    }
}
