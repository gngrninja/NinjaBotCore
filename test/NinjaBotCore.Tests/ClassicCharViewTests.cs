using System;
using System.Collections.Generic;
using Discord;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class ClassicCharViewTests
    {
        private static ClassicRaiderIOModels.ClassicCharProfile CreateTestProfile(
            string faction = "alliance",
            bool withGear = true,
            bool withRaids = true,
            bool withGuild = true)
        {
            var profile = new ClassicRaiderIOModels.ClassicCharProfile
            {
                Name = "TestChar",
                Race = "Human",
                Class = "Paladin",
                Gender = "Male",
                Faction = faction,
                Level = 85,
                Region = "us",
                Realm = "Whitemane",
                ProfileUrl = new Uri("https://classic.raider.io/characters/us/whitemane/TestChar"),
                ThumbnailUrl = new Uri("https://render.worldofwarcraft.com/classic-us/character/whitemane/1/1-avatar.jpg")
            };

            if (withGuild)
            {
                profile.Guild = new ClassicRaiderIOModels.ClassicGuildRef
                {
                    Name = "Test Guild",
                    Realm = "Whitemane"
                };
            }

            if (withGear)
            {
                profile.Gear = new ClassicRaiderIOModels.ClassicGear
                {
                    ItemLevelEquipped = 245,
                    ItemLevelTotal = 249,
                    Items = new ClassicRaiderIOModels.ClassicGearItem
                    {
                        Head = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Crown of Test", ItemLevel = 251, ItemQuality = 4 },
                        Neck = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Amulet", ItemLevel = 245, ItemQuality = 4 },
                        Shoulder = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Shoulders", ItemLevel = 245, ItemQuality = 4 },
                        Back = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Cloak", ItemLevel = 245, ItemQuality = 4 },
                        Chest = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Chestguard", ItemLevel = 251, ItemQuality = 4 },
                        Waist = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Belt", ItemLevel = 245, ItemQuality = 3 },
                        Wrist = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Bracers", ItemLevel = 232, ItemQuality = 4 },
                        Hands = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Gauntlets", ItemLevel = 245, ItemQuality = 4 },
                        Legs = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Legplates", ItemLevel = 245, ItemQuality = 4 },
                        Feet = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Boots", ItemLevel = 245, ItemQuality = 4 },
                        Finger1 = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Ring", ItemLevel = 245, ItemQuality = 4 },
                        Finger2 = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Band", ItemLevel = 232, ItemQuality = 4 },
                        Trinket1 = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Trinket", ItemLevel = 258, ItemQuality = 4 },
                        Trinket2 = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Idol", ItemLevel = 245, ItemQuality = 4 },
                        Mainhand = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Sword", ItemLevel = 258, ItemQuality = 4 },
                        Offhand = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Shield", ItemLevel = 245, ItemQuality = 4 },
                        Ranged = new ClassicRaiderIOModels.ClassicItemDetail { Name = "Test Libram", ItemLevel = 245, ItemQuality = 4 }
                    }
                };
            }

            if (withRaids)
            {
                profile.RaidProgression = new Dictionary<string, ClassicRaiderIOModels.ClassicRaidProgressionEntry>
                {
                    ["icecrown-citadel"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                    {
                        Summary = "12/12 H25",
                        TotalBosses = 12,
                        Normal25BossesKilled = 12,
                        Heroic25BossesKilled = 12,
                        Normal10BossesKilled = 12,
                        Heroic10BossesKilled = 8
                    },
                    ["trial-of-the-crusader"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                    {
                        Summary = "5/5 N25",
                        TotalBosses = 5,
                        Normal25BossesKilled = 5,
                        Heroic25BossesKilled = 0
                    },
                    ["onyxias-lair"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                    {
                        Summary = "1/1 N10",
                        TotalBosses = 1,
                        Normal10BossesKilled = 1
                    }
                };
            }

            return profile;
        }

        #region Overview View Tests

        [Fact]
        public void OverviewBuild_Title_ContainsClassAndName()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Equal("Paladin - TestChar", embed.Title);
        }

        [Fact]
        public void OverviewBuild_AllianceColor_IsBlue()
        {
            var profile = CreateTestProfile(faction: "alliance");
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Equal(new Color(0, 112, 221), embed.Color.Value);
        }

        [Fact]
        public void OverviewBuild_HordeColor_IsRed()
        {
            var profile = CreateTestProfile(faction: "horde");
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Equal(new Color(200, 35, 35), embed.Color.Value);
        }

        [Fact]
        public void OverviewBuild_Description_ContainsLevel()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Contains("85", embed.Description);
        }

        [Fact]
        public void OverviewBuild_Description_ContainsGuild()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Contains("Test Guild", embed.Description);
        }

        [Fact]
        public void OverviewBuild_Description_ContainsItemLevel()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Contains("245", embed.Description);
        }

        [Fact]
        public void OverviewBuild_Description_ContainsRaidProgression()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Contains("Icecrown Citadel", embed.Description);
        }

        [Fact]
        public void OverviewBuild_Footer_ContainsClassicLabel()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Contains("Classic", embed.Footer.Text);
            Assert.Contains("Whitemane", embed.Footer.Text);
        }

        [Fact]
        public void OverviewBuild_HasThumbnail()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.NotNull(embed.ThumbnailUrl);
        }

        [Fact]
        public void OverviewBuild_NoGuild_SkipsGuildLine()
        {
            var profile = CreateTestProfile(withGuild: false);
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.DoesNotContain("Guild", embed.Description);
        }

        [Fact]
        public void OverviewBuild_NoRaids_SkipsRaidSection()
        {
            var profile = CreateTestProfile(withRaids: false);
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.DoesNotContain("Raid Progression", embed.Description);
        }

        [Fact]
        public void OverviewBuild_ZeroKills_ShowsRaidsWithDelayedNote()
        {
            var profile = CreateTestProfile(withRaids: false);
            // Add raids with 0 kills (like Classic RIO returns before tracking)
            profile.RaidProgression = new Dictionary<string, ClassicRaiderIOModels.ClassicRaidProgressionEntry>
            {
                ["throne-of-thunder"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    TotalBosses = 13
                },
                ["heart-of-fear"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    TotalBosses = 6
                }
            };
            var embed = ClassicCharOverviewView.Build(profile);

            Assert.Contains("Raid Progression", embed.Description);
            Assert.Contains("Throne Of Thunder", embed.Description);
            Assert.Contains("Kill tracking may be delayed", embed.Description);
        }

        [Fact]
        public void OverviewBuildComponents_HasGearAndRaidButtons()
        {
            var profile = CreateTestProfile();
            var components = ClassicCharOverviewView.BuildComponents(123456789UL, profile);
            var built = components.Build();

            Assert.NotNull(built);
            Assert.True(built.Components.Count >= 2); // At least 2 rows
        }

        [Fact]
        public void BuildDetailViewComponents_HighlightsCurrentView()
        {
            var charParam = "TestChar~Whitemane~us";
            var components = ClassicCharOverviewView.BuildDetailViewComponents(123456789UL, charParam, "gear");
            var built = components.Build();

            Assert.NotNull(built);
        }

        #endregion

        #region Gear View Tests

        [Fact]
        public void GearBuild_Title_ContainsCharName()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharGearView.Build(profile);

            Assert.Equal("Gear - TestChar", embed.Title);
        }

        [Fact]
        public void GearBuild_Description_ContainsItemLevel()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharGearView.Build(profile);

            Assert.Contains("245", embed.Description);
        }

        [Fact]
        public void GearBuild_Description_ContainsGearSlots()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharGearView.Build(profile);

            Assert.Contains("Crown of Test", embed.Description);
            Assert.Contains("Test Sword", embed.Description);
        }

        [Fact]
        public void GearBuild_Description_ContainsRangedSlot()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharGearView.Build(profile);

            Assert.Contains("Test Libram", embed.Description);
            Assert.Contains("Ranged", embed.Description);
        }

        [Fact]
        public void GearBuild_NoGear_ShowsMessage()
        {
            var profile = CreateTestProfile(withGear: false);
            var embed = ClassicCharGearView.Build(profile);

            Assert.Contains("No gear data available", embed.Description);
        }

        [Fact]
        public void GearBuild_Footer_ContainsClassicLabel()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharGearView.Build(profile);

            Assert.Contains("Classic", embed.Footer.Text);
        }

        #endregion

        #region Raids View Tests

        [Fact]
        public void RaidsBuild_Title_ContainsCharName()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Equal("Raid Progression - TestChar", embed.Title);
        }

        [Fact]
        public void RaidsBuild_Description_ContainsRaidNames()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("Icecrown Citadel", embed.Description);
            Assert.Contains("Trial Of The Crusader", embed.Description);
        }

        [Fact]
        public void RaidsBuild_Description_ContainsProgressBars()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharRaidsView.Build(profile);

            // Progress bars use block characters
            Assert.Contains("█", embed.Description);
        }

        [Fact]
        public void RaidsBuild_Description_Shows10And25ManProgress()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("H25", embed.Description);
            Assert.Contains("N25", embed.Description);
        }

        [Fact]
        public void RaidsBuild_NoRaidData_ShowsMessage()
        {
            var profile = CreateTestProfile(withRaids: false);
            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("No raid progression data available", embed.Description);
        }

        [Fact]
        public void RaidsBuild_ZeroKills_ShowsAllRaidsWithSummary()
        {
            // Exactly mirrors Nylock's data: raids exist but 0 kills everywhere
            var profile = CreateTestProfile(withRaids: false);
            profile.RaidProgression = new Dictionary<string, ClassicRaiderIOModels.ClassicRaidProgressionEntry>
            {
                ["throne-of-thunder"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    Summary = "",
                    TotalBosses = 13
                },
                ["heart-of-fear"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    Summary = "",
                    TotalBosses = 6
                },
                ["mogushan-vaults"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    Summary = "",
                    TotalBosses = 6
                },
                ["terrace-of-endless-spring"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    Summary = "",
                    TotalBosses = 4
                }
            };

            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("Throne Of Thunder", embed.Description);
            Assert.Contains("Heart Of Fear", embed.Description);
            Assert.Contains("Mogushan Vaults", embed.Description);
            Assert.Contains("Terrace Of Endless Spring", embed.Description);
            Assert.Contains("0/13", embed.Description);
            Assert.Contains("Kill tracking may be delayed", embed.Description);
        }

        [Fact]
        public void RaidsBuild_10ManNormal_ShowsN10Format()
        {
            // Onyxia's Lair uses N10 format
            var profile = CreateTestProfile();
            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("Onyxias Lair", embed.Description);
            Assert.Contains("N10", embed.Description);
        }

        [Fact]
        public void RaidsBuild_Footer_ContainsClassicLabel()
        {
            var profile = CreateTestProfile();
            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("Classic", embed.Footer.Text);
        }

        #endregion

        #region Helper Tests

        [Theory]
        [InlineData("icecrown-citadel", "Icecrown Citadel")]
        [InlineData("trial-of-the-crusader", "Trial Of The Crusader")]
        [InlineData("onyxias-lair", "Onyxias Lair")]
        [InlineData("naxxramas", "Naxxramas")]
        public void FormatRaidName_ConvertsSlugToTitle(string slug, string expected)
        {
            var result = ClassicCharOverviewView.FormatRaidName(slug);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("alliance", 0, 112, 221)]
        [InlineData("horde", 200, 35, 35)]
        [InlineData("neutral", 128, 128, 128)]
        [InlineData(null, 128, 128, 128)]
        public void GetFactionColor_ReturnsCorrectColor(string faction, int r, int g, int b)
        {
            var color = ClassicCharOverviewView.GetFactionColor(faction);
            Assert.Equal(new Color(r, g, b), color);
        }

        [Fact]
        public void FormatClassicProgress_Shows25ManHeroic()
        {
            var entry = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
            {
                TotalBosses = 12,
                Heroic25BossesKilled = 10,
                Normal25BossesKilled = 12
            };

            var result = ClassicCharOverviewView.FormatClassicProgress(entry);

            Assert.Contains("H25", result);
            Assert.Contains("10/12", result);
            Assert.Contains("N25", result);
        }

        [Fact]
        public void FormatClassicProgress_Shows10ManFormat()
        {
            var entry = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
            {
                TotalBosses = 1,
                Normal10BossesKilled = 1
            };

            var result = ClassicCharOverviewView.FormatClassicProgress(entry);

            Assert.Contains("1/1", result);
            Assert.Contains("N10", result);
        }

        [Fact]
        public void HasAnyKills_ReturnsFalse_WhenAllZero()
        {
            var entry = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
            {
                TotalBosses = 13
            };

            Assert.False(ClassicCharOverviewView.HasAnyKills(entry));
        }

        [Fact]
        public void HasAnyKills_ReturnsTrue_WhenAnyKillsExist()
        {
            var entry = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
            {
                TotalBosses = 13,
                Normal10BossesKilled = 1
            };

            Assert.True(ClassicCharOverviewView.HasAnyKills(entry));
        }

        #endregion

        #region Embed Length Protection Tests

        [Fact]
        public void RaidsBuild_ManyRaids_DoesNotExceedDiscordLimit()
        {
            var profile = CreateTestProfile(withRaids: false);
            profile.RaidProgression = new Dictionary<string, ClassicRaiderIOModels.ClassicRaidProgressionEntry>();

            // Add 20 raids with full kill data and progress bars to push description length
            for (int i = 0; i < 20; i++)
            {
                profile.RaidProgression[$"test-raid-tier-{i}-with-a-long-name"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    TotalBosses = 13,
                    Normal10BossesKilled = 10,
                    Normal25BossesKilled = 13,
                    Heroic10BossesKilled = 8,
                    Heroic25BossesKilled = 12
                };
            }

            var embed = ClassicCharRaidsView.Build(profile);

            Assert.True(embed.Description.Length <= 4096,
                $"Embed description is {embed.Description.Length} chars, exceeds Discord's 4096 limit");
        }

        [Fact]
        public void RaidsBuild_ManyRaids_ShowsTruncationMessage()
        {
            var profile = CreateTestProfile(withRaids: false);
            profile.RaidProgression = new Dictionary<string, ClassicRaiderIOModels.ClassicRaidProgressionEntry>();

            // Need enough raids with full progress bars to exceed 3600 chars
            for (int i = 0; i < 40; i++)
            {
                profile.RaidProgression[$"test-raid-tier-{i}-with-a-really-long-name-for-testing"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                {
                    TotalBosses = 13,
                    Normal10BossesKilled = 10,
                    Normal25BossesKilled = 13,
                    Heroic10BossesKilled = 8,
                    Heroic25BossesKilled = 12
                };
            }

            var embed = ClassicCharRaidsView.Build(profile);

            Assert.Contains("...and more", embed.Description);
            Assert.True(embed.Description.Length <= 4096);
        }

        #endregion
    }
}
