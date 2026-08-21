using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RaiderIOModelTests
    {
        #region Dictionary Deserialization

        [Fact]
        public void RaidProgression_DeserializesToDictionary()
        {
            var json = @"{
                ""name"": ""Test Guild"",
                ""faction"": ""alliance"",
                ""region"": ""us"",
                ""realm"": ""illidan"",
                ""raid_progression"": {
                    ""nerubar-palace"": {
                        ""summary"": ""8/8 H"",
                        ""total_bosses"": 8,
                        ""normal_bosses_killed"": 8,
                        ""heroic_bosses_killed"": 8,
                        ""mythic_bosses_killed"": 3
                    },
                    ""manaforge-omega"": {
                        ""summary"": ""6/8 N"",
                        ""total_bosses"": 8,
                        ""normal_bosses_killed"": 6,
                        ""heroic_bosses_killed"": 0,
                        ""mythic_bosses_killed"": 0
                    }
                },
                ""raid_rankings"": {
                    ""nerubar-palace"": {
                        ""normal"": { ""world"": 1000, ""region"": 500, ""realm"": 10 },
                        ""heroic"": { ""world"": 2000, ""region"": 800, ""realm"": 20 },
                        ""mythic"": { ""world"": 5000, ""region"": 1500, ""realm"": 50 }
                    }
                }
            }";

            var result = JsonConvert.DeserializeObject<RaiderIOModels.RioGuildInfo>(json);

            Assert.NotNull(result.RaidProgression);
            Assert.Equal(2, result.RaidProgression.Count);
            Assert.True(result.RaidProgression.ContainsKey("nerubar-palace"));
            Assert.True(result.RaidProgression.ContainsKey("manaforge-omega"));
            Assert.Equal(8, result.RaidProgression["nerubar-palace"].HeroicBossesKilled);
            Assert.Equal(6, result.RaidProgression["manaforge-omega"].NormalBossesKilled);

            Assert.NotNull(result.RaidRankings);
            Assert.Single(result.RaidRankings);
            Assert.Equal(10, result.RaidRankings["nerubar-palace"].Normal.Realm);
        }

        [Fact]
        public void CharRaidProgression_DeserializesToDictionary()
        {
            var json = @"{
                ""name"": ""Testchar"",
                ""race"": ""Human"",
                ""class"": ""Warrior"",
                ""active_spec_name"": ""Arms"",
                ""active_spec_role"": ""DPS"",
                ""gender"": ""male"",
                ""faction"": ""alliance"",
                ""achievement_points"": 1000,
                ""honorable_kills"": 0,
                ""region"": ""us"",
                ""realm"": ""Illidan"",
                ""raid_progression"": {
                    ""uldir"": {
                        ""summary"": ""8/8 M"",
                        ""total_bosses"": 8,
                        ""normal_bosses_killed"": 8,
                        ""heroic_bosses_killed"": 8,
                        ""mythic_bosses_killed"": 8
                    },
                    ""manaforge-omega"": {
                        ""summary"": ""5/8 H"",
                        ""total_bosses"": 8,
                        ""normal_bosses_killed"": 8,
                        ""heroic_bosses_killed"": 5,
                        ""mythic_bosses_killed"": 0
                    }
                },
                ""raid_achievement_meta"": {
                    ""manaforge-omega"": { ""aotc"": false, ""cutting_edge"": false }
                },
                ""raid_achievement_curve"": {
                    ""manaforge-omega"": { ""aotc"": true, ""cutting_edge"": false }
                }
            }";

            var result = JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(json);

            Assert.NotNull(result.RaidProgression);
            Assert.Equal(2, result.RaidProgression.Count);
            Assert.Equal(5, result.RaidProgression["manaforge-omega"].HeroicBossesKilled);

            Assert.NotNull(result.RaidAchievementMeta);
            Assert.False(result.RaidAchievementMeta["manaforge-omega"].Aotc);

            Assert.NotNull(result.RaidAchievementCurve);
            Assert.True(result.RaidAchievementCurve["manaforge-omega"].Aotc);
        }

        #endregion

        #region DictionaryOrEmptyArrayConverter

        [Fact]
        public void DictionaryOrEmptyArrayConverter_EmptyArray_ReturnsNull()
        {
            var json = @"{
                ""name"": ""Test"",
                ""race"": ""Human"",
                ""class"": ""Warrior"",
                ""active_spec_name"": ""Arms"",
                ""active_spec_role"": ""DPS"",
                ""gender"": ""male"",
                ""faction"": ""alliance"",
                ""achievement_points"": 0,
                ""honorable_kills"": 0,
                ""region"": ""us"",
                ""realm"": ""Test"",
                ""raid_achievement_meta"": [],
                ""raid_achievement_curve"": []
            }";

            var result = JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(json);

            Assert.Null(result.RaidAchievementMeta);
            Assert.Null(result.RaidAchievementCurve);
        }

        [Fact]
        public void DictionaryOrEmptyArrayConverter_Object_ReturnsDictionary()
        {
            var json = @"{
                ""name"": ""Test"",
                ""race"": ""Human"",
                ""class"": ""Warrior"",
                ""active_spec_name"": ""Arms"",
                ""active_spec_role"": ""DPS"",
                ""gender"": ""male"",
                ""faction"": ""alliance"",
                ""achievement_points"": 0,
                ""honorable_kills"": 0,
                ""region"": ""us"",
                ""realm"": ""Test"",
                ""raid_achievement_meta"": {
                    ""nerubar-palace"": { ""aotc"": true, ""cutting_edge"": false }
                },
                ""raid_achievement_curve"": {
                    ""nerubar-palace"": { ""aotc"": true, ""cutting_edge"": true }
                }
            }";

            var result = JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(json);

            Assert.NotNull(result.RaidAchievementMeta);
            Assert.True(result.RaidAchievementMeta["nerubar-palace"].Aotc);

            Assert.NotNull(result.RaidAchievementCurve);
            Assert.True(result.RaidAchievementCurve["nerubar-palace"].CuttingEdge);
        }

        #endregion

        #region GetCurrentRaid

        [Fact]
        public void GetCurrentRaid_ReturnsLastEntry()
        {
            var dict = new Dictionary<string, RaiderIOModels.RaidProgressionEntry>
            {
                ["uldir"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 8 },
                ["nerubar-palace"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 8 },
                ["manaforge-omega"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 8, NormalBossesKilled = 5 }
            };

            var result = CharViewHelpers.GetCurrentRaid(dict);

            Assert.NotNull(result);
            Assert.Equal("manaforge-omega", result.Value.Key);
            Assert.Equal(5, result.Value.Value.NormalBossesKilled);
        }

        [Fact]
        public void GetCurrentRaid_PrefersNamedTier_WhenCharacterPayloadOrderIsNotChronological()
        {
            var dict = new Dictionary<string, RaiderIOModels.RaidProgressionEntry>
            {
                ["the-tidebound-grotto"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 1 },
                ["the-venomous-abyss"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 8, HeroicBossesKilled = 3 },
                ["sporefall"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 1 },
                ["tier-mn-1"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 9, MythicBossesKilled = 9 }
            };

            var result = CharViewHelpers.GetCurrentRaid(dict, "The Venomous Abyss");

            Assert.NotNull(result);
            Assert.Equal("the-venomous-abyss", result.Value.Key);
            Assert.Equal(3, result.Value.Value.HeroicBossesKilled);
        }

        [Fact]
        public void GetCurrentRaid_PrefersNamedTier_ForRankingsToo()
        {
            var dict = new Dictionary<string, RaiderIOModels.RaidRankingsEntry>
            {
                ["the-venomous-abyss"] = new RaiderIOModels.RaidRankingsEntry
                {
                    Heroic = new RaiderIOModels.Heroic { World = 25 }
                },
                ["tier-mn-1"] = new RaiderIOModels.RaidRankingsEntry
                {
                    Heroic = new RaiderIOModels.Heroic { World = 1 }
                }
            };

            var result = CharViewHelpers.GetCurrentRaid(dict, "The Venomous Abyss");

            Assert.NotNull(result);
            Assert.Equal("the-venomous-abyss", result.Value.Key);
            Assert.Equal(25, result.Value.Value.Heroic.World);
        }

        [Fact]
        public void GetCurrentRaid_FallsBackToLastEntry_WhenConfiguredTierIsUnavailable()
        {
            var dict = new Dictionary<string, RaiderIOModels.RaidProgressionEntry>
            {
                ["older-raid"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 8 },
                ["available-raid"] = new RaiderIOModels.RaidProgressionEntry { TotalBosses = 9 }
            };

            var result = CharViewHelpers.GetCurrentRaid(dict, "Not Yet Returned By Raider IO");

            Assert.NotNull(result);
            Assert.Equal("available-raid", result.Value.Key);
        }

        [Fact]
        public void CharacterViews_RenderNamedCurrentTier_WhenRaiderIoOrderIsStale()
        {
            var character = new CharacterInfo
            {
                Name = "Testchar",
                Realm = "Area 52",
                RealmSlug = "area-52",
                Region = "us"
            };
            var rio = new RaiderIOModels.RioMythicPlusChar
            {
                Name = "Testchar",
                Class = "Warrior",
                ActiveSpecName = "Arms",
                RaidProgression = new Dictionary<string, RaiderIOModels.RaidProgressionEntry>
                {
                    ["the-venomous-abyss"] = new RaiderIOModels.RaidProgressionEntry
                    {
                        TotalBosses = 8,
                        HeroicBossesKilled = 3
                    },
                    ["tier-mn-1"] = new RaiderIOModels.RaidProgressionEntry
                    {
                        TotalBosses = 9,
                        MythicBossesKilled = 9
                    }
                }
            };

            var overview = CharOverviewView.Build(
                character,
                rio,
                armoryEquipment: null,
                armorySummary: null,
                armoryMedia: null,
                currentRaidName: "The Venomous Abyss");
            var mythicPlus = CharMythicPlusView.Build(
                character,
                rio,
                wowUtils: null,
                currentRaidName: "The Venomous Abyss");

            Assert.Contains("3/8H The Venomous Abyss", overview.Description);
            Assert.DoesNotContain("Midnight Season 1", overview.Description);
            Assert.Contains("The Venomous Abyss", mythicPlus.Description);
            Assert.DoesNotContain("Midnight Season 1", mythicPlus.Description);
        }

        [Fact]
        public void GetCurrentRaid_NullDictionary_ReturnsNull()
        {
            Dictionary<string, RaiderIOModels.RaidProgressionEntry> dict = null;

            var result = CharViewHelpers.GetCurrentRaid(dict);

            Assert.Null(result);
        }

        [Fact]
        public void GetCurrentRaid_EmptyDictionary_ReturnsNull()
        {
            var dict = new Dictionary<string, RaiderIOModels.RaidProgressionEntry>();

            var result = CharViewHelpers.GetCurrentRaid(dict);

            Assert.Null(result);
        }

        [Fact]
        public void GetCurrentRaid_WorksWithRankings()
        {
            var dict = new Dictionary<string, RaiderIOModels.RaidRankingsEntry>
            {
                ["nerubar-palace"] = new RaiderIOModels.RaidRankingsEntry
                {
                    Normal = new RaiderIOModels.Heroic { Realm = 10 }
                },
                ["manaforge-omega"] = new RaiderIOModels.RaidRankingsEntry
                {
                    Normal = new RaiderIOModels.Heroic { Realm = 5 }
                }
            };

            var result = CharViewHelpers.GetCurrentRaid(dict);

            Assert.NotNull(result);
            Assert.Equal("manaforge-omega", result.Value.Key);
            Assert.Equal(5, result.Value.Value.Normal.Realm);
        }

        #endregion

        #region FormatRaidName

        [Theory]
        [InlineData("uldir", "Uldir")]
        [InlineData("the-eternal-palace", "The Eternal Palace")]
        [InlineData("amirdrassil-the-dreams-hope", "Amirdrassil The Dreams Hope")]
        [InlineData("manaforge-omega", "Manaforge Omega")]
        [InlineData("nerubar-palace", "Nerubar Palace")]
        [InlineData("castle-nathria", "Castle Nathria")]
        [InlineData("vault-of-the-incarnates", "Vault Of The Incarnates")]
        public void FormatRaidName_ConvertsSlugToDisplayName(string slug, string expected)
        {
            var result = CharViewHelpers.FormatRaidName(slug);
            Assert.Equal(expected, result);
        }

        #endregion

        #region Full Deserialization Round-Trip

        [Fact]
        public void GuildInfo_WithManyRaids_LastRaidIsCurrentTier()
        {
            var json = @"{
                ""name"": ""Test Guild"",
                ""faction"": ""alliance"",
                ""region"": ""us"",
                ""realm"": ""illidan"",
                ""raid_progression"": {
                    ""antorus-the-burning-throne"": { ""summary"": ""11/11 M"", ""total_bosses"": 11, ""normal_bosses_killed"": 11, ""heroic_bosses_killed"": 11, ""mythic_bosses_killed"": 11 },
                    ""uldir"": { ""summary"": ""8/8 M"", ""total_bosses"": 8, ""normal_bosses_killed"": 8, ""heroic_bosses_killed"": 8, ""mythic_bosses_killed"": 8 },
                    ""nerubar-palace"": { ""summary"": ""8/8 H"", ""total_bosses"": 8, ""normal_bosses_killed"": 8, ""heroic_bosses_killed"": 8, ""mythic_bosses_killed"": 0 },
                    ""manaforge-omega"": { ""summary"": ""3/8 N"", ""total_bosses"": 8, ""normal_bosses_killed"": 3, ""heroic_bosses_killed"": 0, ""mythic_bosses_killed"": 0 }
                }
            }";

            var guild = JsonConvert.DeserializeObject<RaiderIOModels.RioGuildInfo>(json);
            var currentRaid = CharViewHelpers.GetCurrentRaid(guild.RaidProgression);

            Assert.NotNull(currentRaid);
            Assert.Equal("manaforge-omega", currentRaid.Value.Key);
            Assert.Equal("Manaforge Omega", CharViewHelpers.FormatRaidName(currentRaid.Value.Key));
            Assert.Equal(3, currentRaid.Value.Value.NormalBossesKilled);
        }

        #endregion
    }
}
