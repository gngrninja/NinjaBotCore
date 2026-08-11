using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Services.Gearing
{
    public sealed class GearAssessmentService
    {
        // Midnight Season 2 client build 12.1.0.69214. The season marker and rank bonus IDs
        // are Blizzard client-data identifiers; matching the IDs avoids ambiguous item-level inference.
        private const int CurrentSeasonMarkerBonusId = 13662;
        private const int PreviousSeasonMarkerBonusId = 13577;

        private static readonly IReadOnlyDictionary<string, int[]> CurrentSeasonTracks =
            new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Adventurer"] = new[] { 266, 269, 272, 276, 279, 282 },
                ["Veteran"] = new[] { 279, 282, 285, 289, 292, 295 },
                ["Champion"] = new[] { 292, 295, 298, 302, 305, 308 },
                ["Hero"] = new[] { 305, 308, 311, 315, 318, 321 },
                ["Myth"] = new[] { 318, 321, 324, 328, 331, 334 }
            };

        private static readonly IReadOnlyDictionary<int, TrackBonus> CurrentSeasonTrackBonuses =
            BuildCurrentSeasonTrackBonuses();

        private static readonly IReadOnlyDictionary<int, TrackBonus> ExceptionalTrackBonuses =
            BuildExceptionalTrackBonuses();

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
                var slotAssessment = new GearSlotAssessment
                {
                    SlotType = slotType,
                    SlotLabel = slotLabel,
                    ItemId = item.Item?.Id ?? 0,
                    ItemName = item.Name ?? "Unknown Item",
                    ItemLevel = itemLevel,
                    IsSetPiece = isSetPiece,
                    HasActiveSetBonus = hasActiveSetBonus
                };
                ApplyTrack(item.BonusList, slotAssessment);
                slots.Add(slotAssessment);

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
                .OrderBy(slot => slot.IsMissing ? 0 : slot.TrackCeilingItemLevel ?? slot.ItemLevel)
                .ThenBy(slot => slot.SlotLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var upgradeInPlace = slots
                .Where(slot => slot.IsCurrentSeasonTrack && slot.TrackRank < slot.TrackMaxRank)
                .OrderByDescending(slot => slot.UpgradeItemLevelsRemaining)
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
                    TrackLabel = weakest.TrackLabel,
                    ItemLevelGap = Math.Max(0, equippedItemLevel - weakest.ItemLevel),
                    NextAction = BuildNextAction(weakest),
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
                UpgradeInPlaceSlots = upgradeInPlace,
                MissingEnchantSlots = missingEnchants,
                EmptySocketSlots = emptySockets,
                OverallRecommendation = recommendation
            };
        }

        private static void ApplyTrack(IReadOnlyCollection<int> bonusList, GearSlotAssessment slot)
        {
            if (bonusList == null || bonusList.Count == 0)
            {
                return;
            }

            var hasCurrentSeasonMarker = bonusList.Contains(CurrentSeasonMarkerBonusId);
            var hasPreviousSeasonMarker = bonusList.Contains(PreviousSeasonMarkerBonusId);
            if (hasPreviousSeasonMarker)
            {
                slot.TrackStatus = hasCurrentSeasonMarker
                    ? "Conflicting season markers"
                    : "Legacy or special track";
                return;
            }

            if (!hasCurrentSeasonMarker)
            {
                return;
            }

            var exceptionalMatches = bonusList
                .Where(ExceptionalTrackBonuses.ContainsKey)
                .Select(bonusId => ExceptionalTrackBonuses[bonusId])
                .ToList();
            var matches = bonusList
                .Where(CurrentSeasonTrackBonuses.ContainsKey)
                .Select(bonusId => CurrentSeasonTrackBonuses[bonusId])
                .ToList();
            if (exceptionalMatches.Count > 0)
            {
                if (exceptionalMatches.Count == 1
                    && matches.Count == 0
                    && exceptionalMatches[0].ItemLevel == slot.ItemLevel)
                {
                    var exceptional = exceptionalMatches[0];
                    slot.TrackName = exceptional.TrackName;
                    slot.TrackLabel = $"{exceptional.TrackName}-equivalent {exceptional.ItemLevel}";
                    slot.TrackStatus = "Special item level";
                }
                else
                {
                    slot.TrackStatus = "Track unresolved";
                }

                return;
            }

            if (matches.Count != 1 || matches[0].ItemLevel != slot.ItemLevel)
            {
                slot.TrackStatus = "Track unresolved";
                return;
            }

            var track = matches[0];
            var itemLevels = CurrentSeasonTracks[track.TrackName];
            slot.TrackName = track.TrackName;
            slot.TrackRank = track.Rank;
            slot.TrackMaxRank = track.MaxRank;
            slot.TrackLabel = $"{track.TrackName} {track.Rank}/{track.MaxRank}";
            slot.IsCurrentSeasonTrack = true;
            slot.TrackStatus = "Current season · exact bonus match";
            slot.TrackCeilingItemLevel = itemLevels[^1];
            slot.UpgradeItemLevelsRemaining = itemLevels[^1] - slot.ItemLevel;
            if (track.Rank < track.MaxRank)
            {
                slot.UpgradeAction = $"Upgrade {slot.TrackLabel} toward {track.TrackName} {track.MaxRank}/{track.MaxRank} ({itemLevels[^1]}).";
            }
        }

        private static IReadOnlyDictionary<int, TrackBonus> BuildCurrentSeasonTrackBonuses()
        {
            var result = new Dictionary<int, TrackBonus>();
            AddTrack(result, "Adventurer", new[] { 12817, 12818, 12819, 12820, 12821, 12822 });
            AddTrack(result, "Veteran", new[] { 12825, 12826, 12827, 12828, 12829, 12830 });
            AddTrack(result, "Champion", new[] { 12833, 12834, 12835, 12836, 12837, 12838 });
            AddTrack(result, "Hero", new[] { 12841, 12842, 12843, 12844, 12845, 12846 });
            AddTrack(result, "Myth", new[] { 12849, 12850, 12851, 12852, 12853, 12854 });
            return result;
        }

        private static void AddTrack(IDictionary<int, TrackBonus> result, string trackName, IReadOnlyList<int> bonusIds)
        {
            var itemLevels = CurrentSeasonTracks[trackName];
            for (var index = 0; index < bonusIds.Count; index++)
            {
                result[bonusIds[index]] = new TrackBonus(trackName, index + 1, bonusIds.Count, itemLevels[index]);
            }
        }

        private static IReadOnlyDictionary<int, TrackBonus> BuildExceptionalTrackBonuses() =>
            new Dictionary<int, TrackBonus>
            {
                [12823] = new TrackBonus("Adventurer", 0, 0, 285),
                [12824] = new TrackBonus("Adventurer", 0, 0, 289),
                [12831] = new TrackBonus("Veteran", 0, 0, 298),
                [12832] = new TrackBonus("Veteran", 0, 0, 302),
                [12839] = new TrackBonus("Champion", 0, 0, 311),
                [12840] = new TrackBonus("Champion", 0, 0, 315),
                [12847] = new TrackBonus("Hero", 0, 0, 324),
                [12848] = new TrackBonus("Hero", 0, 0, 328),
                [12855] = new TrackBonus("Myth", 0, 0, 337),
                [12856] = new TrackBonus("Myth", 0, 0, 340),
                [13848] = new TrackBonus("Myth", 0, 0, 344)
            };

        private sealed class TrackBonus
        {
            public TrackBonus(string trackName, int rank, int maxRank, int itemLevel)
            {
                TrackName = trackName;
                Rank = rank;
                MaxRank = maxRank;
                ItemLevel = itemLevel;
            }

            public string TrackName { get; }
            public int Rank { get; }
            public int MaxRank { get; }
            public int ItemLevel { get; }
        }

        private static string BuildNextAction(GearSlotAssessment slot)
        {
            if (slot.IsMissing)
            {
                return "Equip an item in this slot before optimizing other gear.";
            }

            if (slot.IsCurrentSeasonTrack && slot.TrackRank < slot.TrackMaxRank)
            {
                return slot.UpgradeAction;
            }

            if (slot.IsCurrentSeasonTrack)
            {
                return $"Target a higher track replacement; this item is capped at {slot.TrackLabel} ({slot.TrackCeilingItemLevel}).";
            }

            return "Target this slot from current content or the next eligible Great Vault; confirm the reward track in game.";
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
        public IReadOnlyList<GearSlotAssessment> UpgradeInPlaceSlots { get; set; } = Array.Empty<GearSlotAssessment>();
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
        public string TrackName { get; set; }
        public int TrackRank { get; set; }
        public int TrackMaxRank { get; set; }
        public string TrackLabel { get; set; }
        public string TrackStatus { get; set; }
        public bool IsCurrentSeasonTrack { get; set; }
        public int? TrackCeilingItemLevel { get; set; }
        public int UpgradeItemLevelsRemaining { get; set; }
        public string UpgradeAction { get; set; }
    }

    public sealed class GearRecommendation
    {
        public string SlotType { get; set; }
        public string SlotLabel { get; set; }
        public int CurrentItemId { get; set; }
        public string CurrentItemName { get; set; }
        public int CurrentItemLevel { get; set; }
        public bool IsMissing { get; set; }
        public string TrackLabel { get; set; }
        public int ItemLevelGap { get; set; }
        public string NextAction { get; set; }
        public string Caution { get; set; }
    }
}
