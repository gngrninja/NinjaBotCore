using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Services.Gearing;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class GearAssessmentServiceTests
    {
        private readonly GearAssessmentService _service = new();

        [Fact]
        public void Analyze_RanksLowestEquippedSlotsAndBuildsOverallRecommendation()
        {
            var equipment = CompleteEquipment(
                Item("HEAD", "Tier Helm", 272, itemId: 1001, isTier: true),
                Item("WRIST", "Weathered Bracers", 263, itemId: 1002),
                Item("WAIST", "Veteran Belt", 266, itemId: 1003));
            var summary = Summary(equipped: 274, average: 278);

            var result = _service.Analyze(summary, equipment);

            Assert.Equal(274, result.EquippedItemLevel);
            Assert.Collection(
                result.PrioritySlots.Take(3),
                wrist => Assert.Equal("Wrist", wrist.SlotLabel),
                waist => Assert.Equal("Waist", waist.SlotLabel),
                head => Assert.Equal("Head", head.SlotLabel));
            Assert.Equal("Wrist", result.OverallRecommendation.SlotLabel);
            Assert.Equal("Weathered Bracers", result.OverallRecommendation.CurrentItemName);
            Assert.Equal(11, result.OverallRecommendation.ItemLevelGap);
            Assert.Contains("Great Vault", result.OverallRecommendation.NextAction);
            Assert.DoesNotContain("current weekly caches", result.OverallRecommendation.NextAction);
        }

        [Fact]
        public void Analyze_ResolvesCurrentSeasonTrackFromOfficialBonusList()
        {
            var heroChest = Item("CHEST", "Season Chest", 308, itemId: 1101, enchanted: true);
            heroChest.BonusList = new List<int> { 13662, 12842 };

            var result = _service.Analyze(Summary(308, 308), CompleteEquipment(heroChest));

            var chest = Assert.Single(result.PrioritySlots, slot => slot.SlotType == "CHEST");
            Assert.Equal("Hero", chest.TrackName);
            Assert.Equal(2, chest.TrackRank);
            Assert.Equal(6, chest.TrackMaxRank);
            Assert.Equal(321, chest.TrackCeilingItemLevel);
            Assert.Equal(13, chest.UpgradeItemLevelsRemaining);
            Assert.True(chest.IsCurrentSeasonTrack);
            Assert.Equal("Hero 2/6", chest.TrackLabel);
        }

        [Fact]
        public void Analyze_PrioritizesLowerTrackCeilingOverLowerCurrentItemLevel()
        {
            var championChest = Item("CHEST", "Capped Champion Chest", 308, itemId: 1201, enchanted: true);
            championChest.BonusList = new List<int> { 13662, 12838 };
            var heroWrist = Item("WRIST", "Upgradeable Hero Wrist", 305, itemId: 1202, enchanted: true);
            heroWrist.BonusList = new List<int> { 13662, 12841 };
            var equipment = CompleteEquipment(championChest, heroWrist);
            foreach (var item in equipment.EquippedItems.Where(item => item != championChest && item != heroWrist))
            {
                item.Level.Value = 334;
            }

            var result = _service.Analyze(
                Summary(308, 308),
                equipment);

            Assert.Equal("Chest", result.OverallRecommendation.SlotLabel);
            Assert.Contains("higher track", result.OverallRecommendation.NextAction.ToLowerInvariant());
            Assert.Equal("Wrist", result.UpgradeInPlaceSlots[0].SlotLabel);
            Assert.Contains("Hero 6/6", result.UpgradeInPlaceSlots[0].UpgradeAction);
        }

        [Fact]
        public void Analyze_DoesNotApplyCurrentSeasonCeilingToLegacyTrackItem()
        {
            var legacyChest = Item("CHEST", "Legacy Hero Chest", 619, itemId: 1301, enchanted: true);
            legacyChest.BonusList = new List<int> { 13577, 12806 };

            var result = _service.Analyze(Summary(619, 619), CompleteEquipment(legacyChest));

            var chest = Assert.Single(result.PrioritySlots, slot => slot.SlotType == "CHEST");
            Assert.Null(chest.TrackLabel);
            Assert.False(chest.IsCurrentSeasonTrack);
            Assert.Null(chest.TrackCeilingItemLevel);
            Assert.Equal("Legacy or special track", chest.TrackStatus);
        }

        [Fact]
        public void Analyze_WithExceptionalCurrentSeasonBonus_DoesNotInventAStandardTrackRank()
        {
            var exceptional = Item("CHEST", "Exceptional Raid Chest", 337, itemId: 1302, enchanted: true);
            exceptional.BonusList = new List<int> { 13662, 12855 };

            var result = _service.Analyze(Summary(337, 337), CompleteEquipment(exceptional));

            var chest = Assert.Single(result.PrioritySlots, slot => slot.SlotType == "CHEST");
            Assert.Equal("Myth", chest.TrackName);
            Assert.Equal("Myth-equivalent 337", chest.TrackLabel);
            Assert.Equal("Special item level", chest.TrackStatus);
            Assert.False(chest.IsCurrentSeasonTrack);
            Assert.Null(chest.TrackCeilingItemLevel);
        }

        [Fact]
        public void Analyze_WithCurrentAndLegacySeasonMarkers_FailsClosed()
        {
            var item = Item("CHEST", "Conflicting Season Chest", 308, itemId: 1303, enchanted: true);
            item.BonusList = new List<int> { 13662, 13577, 12842 };

            var result = _service.Analyze(Summary(308, 308), CompleteEquipment(item));

            AssertUnresolvedTrack(result, "CHEST", "Conflicting season markers");
        }

        [Fact]
        public void Analyze_WithMultipleExceptionalAndStandardBonuses_FailsClosed()
        {
            var item = Item("CHEST", "Conflicting Exceptional Chest", 308, itemId: 1304, enchanted: true);
            item.BonusList = new List<int> { 13662, 12855, 12856, 12842 };

            var result = _service.Analyze(Summary(308, 308), CompleteEquipment(item));

            AssertUnresolvedTrack(result, "CHEST", "Track unresolved");
        }

        [Fact]
        public void Analyze_WithExceptionalAndStandardBonus_FailsClosed()
        {
            var item = Item("CHEST", "Mixed Exceptional Chest", 308, itemId: 1305, enchanted: true);
            item.BonusList = new List<int> { 13662, 12855, 12842 };

            var result = _service.Analyze(Summary(308, 308), CompleteEquipment(item));

            AssertUnresolvedTrack(result, "CHEST", "Track unresolved");
        }

        [Fact]
        public void Analyze_WithExceptionalBonusItemLevelMismatch_FailsClosed()
        {
            var item = Item("CHEST", "Mismatched Exceptional Chest", 340, itemId: 1306, enchanted: true);
            item.BonusList = new List<int> { 13662, 12855 };

            var result = _service.Analyze(Summary(340, 340), CompleteEquipment(item));

            AssertUnresolvedTrack(result, "CHEST", "Track unresolved");
        }

        [Fact]
        public void Analyze_WithDuplicateRankBonus_FailsClosed()
        {
            var item = Item("CHEST", "Duplicate Rank Chest", 308, itemId: 1307, enchanted: true);
            item.BonusList = new List<int> { 13662, 12842, 12842 };

            var result = _service.Analyze(Summary(308, 308), CompleteEquipment(item));

            AssertUnresolvedTrack(result, "CHEST", "Track unresolved");
        }

        [Fact]
        public void ArmoryEquippedItem_DeserializesOfficialTrackEvidence()
        {
            const string json = """
                {
                  "name": "Season Chest",
                  "bonus_list": [13662, 12842],
                  "context": 6,
                  "name_description": {
                    "display_string": "Mythic+",
                    "color": { "r": 163, "g": 53, "b": 238, "a": 255 }
                  }
                }
                """;

            var item = JsonConvert.DeserializeObject<ArmoryEquippedItem>(json);

            Assert.Equal(new[] { 13662, 12842 }, item.BonusList);
            Assert.Equal(6, item.Context);
            Assert.Equal("Mythic+", item.NameDescription.DisplayString);
        }

        [Fact]
        public void Analyze_ReportsMissingEnchantsEmptySocketsAndTierProtection()
        {
            var tierWrist = Item("WRIST", "Tier Bracers", 263, itemId: 2001, isTier: true);
            tierWrist.Sockets = new List<ArmorySocket>
            {
                new() { SocketType = new ArmoryType { Type = "PRISMATIC", Name = "Prismatic" } }
            };

            var result = _service.Analyze(Summary(274, 278), CompleteEquipment(tierWrist));

            Assert.Contains("Wrist", result.MissingEnchantSlots);
            Assert.Contains("Wrist (1)", result.EmptySocketSlots);
            Assert.True(result.PrioritySlots[0].IsSetPiece);
            Assert.Contains("set bonus", result.OverallRecommendation.Caution.ToLowerInvariant());
            Assert.DoesNotContain("tier", result.OverallRecommendation.Caution.ToLowerInvariant());
        }

        [Fact]
        public void Analyze_IgnoresCosmeticShirtAndTabardSlots()
        {
            var equipment = CompleteEquipment(
                Item("SHIRT", "Old Shirt", 1, itemId: 3001),
                Item("TABARD", "Guild Tabard", 1, itemId: 3002));

            var result = _service.Analyze(Summary(280, 280), equipment);

            Assert.DoesNotContain(result.PrioritySlots, slot => slot.SlotType == "SHIRT");
            Assert.DoesNotContain(result.PrioritySlots, slot => slot.SlotType == "TABARD");
            Assert.NotEqual("Shirt", result.OverallRecommendation.SlotLabel);
            Assert.NotEqual("Tabard", result.OverallRecommendation.SlotLabel);
        }

        [Fact]
        public void Analyze_TreatsMissingRequiredCombatSlotAsHighestPriority()
        {
            var equipment = CompleteEquipment();
            equipment.EquippedItems.RemoveAll(item => item.Slot.Type == "WRIST");

            var result = _service.Analyze(Summary(280, 280), equipment);

            Assert.Equal("Wrist", result.OverallRecommendation.SlotLabel);
            Assert.True(result.PrioritySlots[0].IsMissing);
            Assert.Equal(0, result.PrioritySlots[0].ItemLevel);
        }

        [Fact]
        public void Analyze_DoesNotTreatAbsentOffHandAsMissing()
        {
            var result = _service.Analyze(Summary(280, 280), CompleteEquipment());

            Assert.DoesNotContain(result.PrioritySlots, slot => slot.SlotType == "OFF_HAND");
        }

        [Fact]
        public void Analyze_ReportsMissingEnchantForWeaponOffHand()
        {
            var offHandWeapon = Item("OFF_HAND", "Off-hand Sword", 270, itemId: 4001, weapon: true);

            var result = _service.Analyze(Summary(280, 280), CompleteEquipment(offHandWeapon));

            Assert.Contains("Off Hand", result.MissingEnchantSlots);
        }

        [Fact]
        public void Analyze_DoesNotReportMissingEnchantForNonWeaponOffHand()
        {
            var shield = Item("OFF_HAND", "Defender Shield", 270, itemId: 4002);

            var result = _service.Analyze(Summary(280, 280), CompleteEquipment(shield));

            Assert.DoesNotContain("Off Hand", result.MissingEnchantSlots);
        }

        [Fact]
        public void Analyze_FallsBackToEquippedItemLevelWhenAverageItemLevelIsUnavailable()
        {
            var equipment = CompleteEquipment();
            equipment.AverageItemLevel = 0;

            var result = _service.Analyze(Summary(280, 0), equipment);

            Assert.Equal(280, result.AverageItemLevel);
        }

        [Fact]
        public void Analyze_DoesNotReportOccupiedSlotWithUnknownItemLevelAsEmpty()
        {
            var equipment = CompleteEquipment();
            var wrist = equipment.EquippedItems.Single(item => item.Slot.Type == "WRIST");
            wrist.Level = null;

            var result = _service.Analyze(Summary(280, 280), equipment);

            Assert.DoesNotContain(result.PrioritySlots, slot => slot.SlotType == "WRIST" && slot.IsMissing);
        }

        [Fact]
        public void Analyze_WithNoEquipment_ReturnsEmptyAssessment()
        {
            var result = _service.Analyze(Summary(0, 0), new ArmoryEquipment());

            Assert.Empty(result.PrioritySlots);
            Assert.Null(result.OverallRecommendation);
            Assert.Empty(result.MissingEnchantSlots);
            Assert.Empty(result.EmptySocketSlots);
        }

        private static ArmorySummary Summary(int equipped, int average) => new()
        {
            EquippedItemLevel = equipped,
            AverageItemLevel = average,
            Name = "Testchar",
            CharacterClass = new ArmoryType { Name = "Warrior", Type = "WARRIOR" },
            ActiveSpec = new ArmoryType { Name = "Arms", Type = "ARMS" }
        };

        private static void AssertUnresolvedTrack(GearAssessment assessment, string slotType, string expectedStatus)
        {
            var slot = Assert.Single(assessment.PrioritySlots, candidate => candidate.SlotType == slotType);
            Assert.False(slot.IsCurrentSeasonTrack);
            Assert.Null(slot.TrackLabel);
            Assert.Null(slot.TrackCeilingItemLevel);
            Assert.Null(slot.UpgradeAction);
            Assert.Equal(expectedStatus, slot.TrackStatus);
        }

        private static ArmoryEquipment Equipment(params ArmoryEquippedItem[] items) => new()
        {
            EquippedItems = new List<ArmoryEquippedItem>(items)
        };

        private static ArmoryEquipment CompleteEquipment(params ArmoryEquippedItem[] overrides)
        {
            var requiredSlots = new[]
            {
                "HEAD", "NECK", "SHOULDER", "BACK", "CHEST", "WRIST", "HANDS", "WAIST",
                "LEGS", "FEET", "FINGER_1", "FINGER_2", "TRINKET_1", "TRINKET_2", "MAIN_HAND"
            };
            var items = new List<ArmoryEquippedItem>();
            for (var index = 0; index < requiredSlots.Length; index++)
            {
                var slot = requiredSlots[index];
                items.Add(Item(slot, $"Baseline {slot}", 280, 5000 + index, enchanted: true));
            }

            foreach (var replacement in overrides)
            {
                items.RemoveAll(item => item.Slot.Type == replacement.Slot.Type);
                items.Add(replacement);
            }

            return Equipment(items.ToArray());
        }

        private static ArmoryEquippedItem Item(
            string slot,
            string name,
            int itemLevel,
            int itemId,
            bool isTier = false,
            bool enchanted = false,
            bool weapon = false) => new()
        {
            Name = name,
            Item = new ArmoryItemRef { Id = itemId },
            Slot = new ArmoryType { Type = slot, Name = slot },
            Level = new ArmoryValue { Value = itemLevel },
            Enchantments = enchanted
                ? new List<ArmoryEnchantment> { new() { EnchantmentId = 1 } }
                : new List<ArmoryEnchantment>(),
            Sockets = new List<ArmorySocket>(),
            Weapon = weapon ? new ArmoryWeapon() : null,
            Set = isTier
                ? new ArmorySet
                {
                    ItemSet = new ArmoryItemSet { Id = 99, Name = "Test Set" },
                    Items = new List<ArmorySetItem>(),
                    Effects = new List<ArmorySetEffect>
                    {
                        new() { RequiredCount = 2, IsActive = true, DisplayString = "2 Set" }
                    }
                }
                : null
        };
    }
}
