using System.Collections.Generic;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Services.Gearing;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CharUpgradeViewTests
    {
        private static readonly CharacterInfo Character = new()
        {
            Name = "Test Char",
            Realm = "Area 52",
            RealmSlug = "area-52",
            Region = "us"
        };

        [Fact]
        public void Build_ShowsOverallPrioritySlotAndImmediateFixes()
        {
            var assessment = new GearAssessment
            {
                EquippedItemLevel = 274,
                AverageItemLevel = 278,
                PrioritySlots = new List<GearSlotAssessment>
                {
                    new() { SlotLabel = "Wrist", ItemName = "Weathered Bracers", ItemLevel = 263 },
                    new() { SlotLabel = "Waist", ItemName = "Veteran Belt", ItemLevel = 266 }
                },
                MissingEnchantSlots = new List<string> { "Wrist" },
                EmptySocketSlots = new List<string> { "Waist (1)" },
                OverallRecommendation = new GearRecommendation
                {
                    SlotLabel = "Wrist",
                    CurrentItemName = "Weathered Bracers",
                    CurrentItemLevel = 263,
                    ItemLevelGap = 11,
                    NextAction = "Target this slot through the Great Vault.",
                    Caution = string.Empty
                }
            };

            var embed = CharUpgradeView.Build(
                Character,
                Summary(),
                new ArmoryMedia(),
                assessment);

            Assert.Contains("Upgrade Analysis", embed.Title);
            Assert.Contains("Wrist", embed.Description);
            Assert.Contains("Weathered Bracers", embed.Description);
            Assert.Contains("11 item levels", embed.Description);
            Assert.Contains("Missing enchants: Wrist", embed.Description);
            Assert.Contains("Empty sockets: Waist (1)", embed.Description);
            Assert.Contains("Waist", embed.Description);
        }

        [Fact]
        public void Build_IncludesCharacterSpecificRaidbotsLink()
        {
            var embed = CharUpgradeView.Build(
                Character,
                Summary(),
                new ArmoryMedia(),
                new GearAssessment());

            Assert.Contains(
                "https://www.raidbots.com/simbot/quick?region=us&realm=area-52&name=Test%20Char",
                embed.Description);
        }

        [Fact]
        public void Build_WithNoGear_ShowsUnavailableMessage()
        {
            var embed = CharUpgradeView.Build(
                Character,
                Summary(),
                new ArmoryMedia(),
                new GearAssessment());

            Assert.Contains("No equipped gear data", embed.Description);
        }

        [Fact]
        public void Build_WithMissingPrioritySlot_UsesEmptySlotGuidanceInsteadOfItemLevelGap()
        {
            var assessment = new GearAssessment
            {
                EquippedItemLevel = 280,
                AverageItemLevel = 280,
                PrioritySlots = new List<GearSlotAssessment>
                {
                    new() { SlotLabel = "Wrist", ItemName = "Empty slot", ItemLevel = 0, IsMissing = true }
                },
                OverallRecommendation = new GearRecommendation
                {
                    SlotLabel = "Wrist",
                    CurrentItemName = "Empty slot",
                    CurrentItemLevel = 0,
                    IsMissing = true,
                    ItemLevelGap = 280,
                    NextAction = "Target this slot through the Great Vault."
                }
            };

            var embed = CharUpgradeView.Build(Character, Summary(), new ArmoryMedia(), assessment);

            Assert.Contains("Equip an item in this slot first", embed.Description);
            Assert.DoesNotContain("280 item levels", embed.Description);
        }

        private static ArmorySummary Summary() => new()
        {
            Name = "Test Char",
            EquippedItemLevel = 274,
            AverageItemLevel = 278,
            CharacterClass = new ArmoryType { Name = "Warrior" },
            ActiveSpec = new ArmoryType { Name = "Arms" }
        };
    }
}
