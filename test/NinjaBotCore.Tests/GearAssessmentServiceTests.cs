using System.Collections.Generic;
using System.Linq;
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
