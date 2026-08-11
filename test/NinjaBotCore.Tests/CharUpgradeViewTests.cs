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
                "https://www.raidbots.com/simbot/topgear?region=us&realm=area-52&name=Test%20Char",
                embed.Description);
        }

        [Fact]
        public void Build_ShowsTrackDetailsUpgradeActionsAndSpecResources()
        {
            var assessment = new GearAssessment
            {
                EquippedItemLevel = 308,
                AverageItemLevel = 308,
                PrioritySlots = new List<GearSlotAssessment>
                {
                    new()
                    {
                        SlotType = "CHEST",
                        SlotLabel = "Chest",
                        ItemName = "Capped Champion Chest",
                        ItemLevel = 308,
                        TrackLabel = "Champion 6/6",
                        TrackCeilingItemLevel = 308,
                        IsCurrentSeasonTrack = true
                    }
                },
                UpgradeInPlaceSlots = new List<GearSlotAssessment>
                {
                    new()
                    {
                        SlotType = "WRIST",
                        SlotLabel = "Wrist",
                        ItemName = "Upgradeable Hero Wrist",
                        ItemLevel = 305,
                        TrackLabel = "Hero 1/6",
                        TrackCeilingItemLevel = 321,
                        IsCurrentSeasonTrack = true,
                        UpgradeAction = "Upgrade Hero 1/6 toward Hero 6/6 (321)."
                    }
                },
                OverallRecommendation = new GearRecommendation
                {
                    SlotLabel = "Chest",
                    CurrentItemName = "Capped Champion Chest",
                    CurrentItemLevel = 308,
                    TrackLabel = "Champion 6/6",
                    NextAction = "Target a higher track replacement."
                }
            };

            var embed = CharUpgradeView.Build(Character, Summary(), new ArmoryMedia(), assessment);

            Assert.Contains("Champion 6/6", embed.Description);
            Assert.Contains("Upgrade in Place", embed.Description);
            Assert.Contains("Hero 1/6", embed.Description);
            Assert.Contains("Hero 6/6", embed.Description);
            Assert.Contains("https://www.wowhead.com/guide/classes/warrior/arms/overview-pve-dps", embed.Description);
            Assert.Contains("https://www.wowhead.com/guide/classes/warrior/arms/bis-gear", embed.Description);
            Assert.Contains("https://www.archon.gg/wow/builds/arms/warrior/mythic-plus/overview/10/all-dungeons/this-week", embed.Description);
            Assert.Contains("Raidbots Top Gear", embed.Description);
            Assert.Contains("12.1.0.69214", embed.Footer.Text);
            Assert.Contains("Exact only on bonus match", embed.Footer.Text);
        }

        [Fact]
        public void BuildWowheadOverviewUrl_UsesStableClassSpecTypesAndRole()
        {
            var summary = new ArmorySummary
            {
                CharacterClass = new ArmoryType { Type = "DEATH_KNIGHT", Name = "Death Knight" },
                ActiveSpec = new ArmoryType { Type = "BLOOD", Name = "Blood" }
            };

            Assert.Equal(
                "https://www.wowhead.com/guide/classes/death-knight/blood/overview-pve-tank",
                CharUpgradeView.BuildWowheadOverviewUrl(summary));
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
            CharacterClass = new ArmoryType { Name = "Warrior", Type = "WARRIOR" },
            ActiveSpec = new ArmoryType { Name = "Arms", Type = "ARMS" }
        };
    }
}
