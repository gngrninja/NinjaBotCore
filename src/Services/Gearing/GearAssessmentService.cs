using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Services.Gearing
{
    public sealed class GearAssessmentService
    {
        private static readonly string[] RequiredCombatSlots =
        {
            "HEAD", "NECK", "SHOULDER", "BACK", "CHEST", "WRIST", "HANDS", "WAIST",
            "LEGS", "FEET", "FINGER_1", "FINGER_2", "TRINKET_1", "TRINKET_2", "MAIN_HAND"
        };

        private static readonly HashSet<string> CombatSlots = new(
            RequiredCombatSlots.Append("OFF_HAND"),
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> EnchantableSlots = new(StringComparer.OrdinalIgnoreCase)
        {
            "BACK", "CHEST", "WRIST", "LEGS", "FEET", "FINGER_1", "FINGER_2", "MAIN_HAND"
        };

        public GearAssessment Analyze(ArmorySummary summary, ArmoryEquipment equipment)
        {
            var equippedItemLevel = summary?.EquippedItemLevel > 0
                ? summary.EquippedItemLevel
                : equipment?.EquippedItemLevel ?? 0;
            var equipmentAverageItemLevel = equipment?.AverageItemLevel ?? 0;
            var averageItemLevel = summary?.AverageItemLevel > 0
                ? summary.AverageItemLevel
                : equipmentAverageItemLevel > 0
                    ? equipmentAverageItemLevel
                    : equippedItemLevel;

            var missingEnchants = new List<string>();
            var emptySockets = new List<string>();
            var slots = new List<GearSlotAssessment>();
            var presentCombatSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var equippedItems = equipment?.EquippedItems;

            if (equippedItems == null || equippedItems.Count == 0)
            {
                return new GearAssessment
                {
                    EquippedItemLevel = equippedItemLevel,
                    AverageItemLevel = averageItemLevel
                };
            }

            foreach (var item in equippedItems)
            {
                var slotType = item?.Slot?.Type;
                if (string.IsNullOrWhiteSpace(slotType) || !CombatSlots.Contains(slotType))
                {
                    continue;
                }

                presentCombatSlots.Add(slotType);
                var itemLevel = item?.Level?.Value ?? 0;
                if (itemLevel <= 0)
                {
                    continue;
                }

                var slotLabel = NormalizeSlot(slotType);
                var isSetPiece = item.Set?.ItemSet?.Id > 0;
                var hasActiveSetBonus = item.Set?.Effects?.Any(effect => effect?.IsActive == true) == true;
                slots.Add(new GearSlotAssessment
                {
                    SlotType = slotType,
                    SlotLabel = slotLabel,
                    ItemId = item.Item?.Id ?? 0,
                    ItemName = item.Name ?? "Unknown Item",
                    ItemLevel = itemLevel,
                    IsSetPiece = isSetPiece,
                    HasActiveSetBonus = hasActiveSetBonus
                });

                var shouldHaveEnchant = EnchantableSlots.Contains(slotType)
                    || (string.Equals(slotType, "OFF_HAND", StringComparison.OrdinalIgnoreCase) && item.Weapon != null);
                if (shouldHaveEnchant && (item.Enchantments == null || item.Enchantments.Count == 0))
                {
                    missingEnchants.Add(slotLabel);
                }

                var emptySocketCount = item.Sockets?.Count(socket => socket != null && socket.Item == null) ?? 0;
                if (emptySocketCount > 0)
                {
                    emptySockets.Add($"{slotLabel} ({emptySocketCount})");
                }
            }

            foreach (var requiredSlot in RequiredCombatSlots)
            {
                if (presentCombatSlots.Contains(requiredSlot))
                {
                    continue;
                }

                slots.Add(new GearSlotAssessment
                {
                    SlotType = requiredSlot,
                    SlotLabel = NormalizeSlot(requiredSlot),
                    ItemName = "Empty slot",
                    ItemLevel = 0,
                    IsMissing = true
                });
            }

            var priorities = slots
                .OrderBy(slot => slot.ItemLevel)
                .ThenBy(slot => slot.SlotLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            GearRecommendation recommendation = null;
            if (priorities.Count > 0)
            {
                var weakest = priorities[0];
                recommendation = new GearRecommendation
                {
                    SlotType = weakest.SlotType,
                    SlotLabel = weakest.SlotLabel,
                    CurrentItemId = weakest.ItemId,
                    CurrentItemName = weakest.ItemName,
                    CurrentItemLevel = weakest.ItemLevel,
                    IsMissing = weakest.IsMissing,
                    ItemLevelGap = Math.Max(0, equippedItemLevel - weakest.ItemLevel),
                    NextAction = "Target this slot through the Great Vault, current weekly caches, or content that awards a higher upgrade track.",
                    Caution = weakest.HasActiveSetBonus
                        ? "This item contributes to an active set bonus. Preserve the bonus when replacing it."
                        : string.Empty
                };
            }

            return new GearAssessment
            {
                EquippedItemLevel = equippedItemLevel,
                AverageItemLevel = averageItemLevel,
                PrioritySlots = priorities,
                MissingEnchantSlots = missingEnchants,
                EmptySocketSlots = emptySockets,
                OverallRecommendation = recommendation
            };
        }

        private static string NormalizeSlot(string slot) => slot?.ToUpperInvariant() switch
        {
            "HEAD" => "Head",
            "NECK" => "Neck",
            "SHOULDER" => "Shoulder",
            "BACK" => "Back",
            "CHEST" => "Chest",
            "WRIST" => "Wrist",
            "HANDS" => "Hands",
            "WAIST" => "Waist",
            "LEGS" => "Legs",
            "FEET" => "Feet",
            "FINGER_1" => "Ring 1",
            "FINGER_2" => "Ring 2",
            "TRINKET_1" => "Trinket 1",
            "TRINKET_2" => "Trinket 2",
            "MAIN_HAND" => "Main Hand",
            "OFF_HAND" => "Off Hand",
            _ => slot?.Replace('_', ' ') ?? "Unknown"
        };
    }

    public sealed class GearAssessment
    {
        public int EquippedItemLevel { get; set; }
        public int AverageItemLevel { get; set; }
        public IReadOnlyList<GearSlotAssessment> PrioritySlots { get; set; } = Array.Empty<GearSlotAssessment>();
        public IReadOnlyList<string> MissingEnchantSlots { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> EmptySocketSlots { get; set; } = Array.Empty<string>();
        public GearRecommendation OverallRecommendation { get; set; }
    }

    public sealed class GearSlotAssessment
    {
        public string SlotType { get; set; }
        public string SlotLabel { get; set; }
        public int ItemId { get; set; }
        public string ItemName { get; set; }
        public int ItemLevel { get; set; }
        public bool IsMissing { get; set; }
        public bool IsSetPiece { get; set; }
        public bool HasActiveSetBonus { get; set; }
    }

    public sealed class GearRecommendation
    {
        public string SlotType { get; set; }
        public string SlotLabel { get; set; }
        public int CurrentItemId { get; set; }
        public string CurrentItemName { get; set; }
        public int CurrentItemLevel { get; set; }
        public bool IsMissing { get; set; }
        public int ItemLevelGap { get; set; }
        public string NextAction { get; set; }
        public string Caution { get; set; }
    }
}
