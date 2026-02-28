using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class ClassicRaiderIOApiTests
    {
        [Fact]
        public void ClassicRaiderIOApi_Implements_IClassicRaiderIOApi()
        {
            Assert.True(typeof(IClassicRaiderIOApi).IsAssignableFrom(typeof(ClassicRaiderIOApi)));
        }

        [Fact]
        public void Interface_Has_GetCharacterProfileAsync_Method()
        {
            var method = typeof(IClassicRaiderIOApi).GetMethod("GetCharacterProfileAsync");

            Assert.NotNull(method);
            Assert.True(typeof(Task<ClassicRaiderIOModels.ClassicCharProfile>).IsAssignableFrom(method.ReturnType));
        }

        [Fact]
        public void Interface_Has_GetGuildProfileAsync_Method()
        {
            var method = typeof(IClassicRaiderIOApi).GetMethod("GetGuildProfileAsync");

            Assert.NotNull(method);
            Assert.True(typeof(Task<ClassicRaiderIOModels.ClassicGuildProfile>).IsAssignableFrom(method.ReturnType));
        }

        [Fact]
        public void ClassicCharProfile_Has_Level_Property()
        {
            var profile = new ClassicRaiderIOModels.ClassicCharProfile { Level = 85 };
            Assert.Equal(85, profile.Level);
        }

        [Fact]
        public void ClassicCharProfile_Does_Not_Have_ActiveSpecName()
        {
            var type = typeof(ClassicRaiderIOModels.ClassicCharProfile);
            var prop = type.GetProperty("ActiveSpecName");
            Assert.Null(prop);
        }

        [Fact]
        public void ClassicGearItem_Has_Ranged_Slot()
        {
            var item = new ClassicRaiderIOModels.ClassicGearItem
            {
                Ranged = new ClassicRaiderIOModels.ClassicItemDetail
                {
                    Name = "Test Ranged",
                    ItemLevel = 200
                }
            };

            Assert.NotNull(item.Ranged);
            Assert.Equal("Test Ranged", item.Ranged.Name);
            Assert.Equal(200, item.Ranged.ItemLevel);
        }

        [Fact]
        public void ClassicRaidProgressionEntry_Has_10_25_Man_Fields()
        {
            var entry = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
            {
                TotalBosses = 12,
                Normal10BossesKilled = 10,
                Normal25BossesKilled = 12,
                Heroic10BossesKilled = 6,
                Heroic25BossesKilled = 8
            };

            Assert.Equal(12, entry.TotalBosses);
            Assert.Equal(10, entry.Normal10BossesKilled);
            Assert.Equal(12, entry.Normal25BossesKilled);
            Assert.Equal(6, entry.Heroic10BossesKilled);
            Assert.Equal(8, entry.Heroic25BossesKilled);
        }

        [Fact]
        public void ClassicRaidProgression_Uses_Dictionary()
        {
            var profile = new ClassicRaiderIOModels.ClassicCharProfile
            {
                RaidProgression = new System.Collections.Generic.Dictionary<string, ClassicRaiderIOModels.ClassicRaidProgressionEntry>
                {
                    ["icecrown-citadel"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                    {
                        TotalBosses = 12,
                        Heroic25BossesKilled = 12
                    },
                    ["trial-of-the-crusader"] = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
                    {
                        TotalBosses = 5,
                        Normal25BossesKilled = 5
                    }
                }
            };

            Assert.Equal(2, profile.RaidProgression.Count);
            Assert.True(profile.RaidProgression.ContainsKey("icecrown-citadel"));
            Assert.Equal(12, profile.RaidProgression["icecrown-citadel"].Heroic25BossesKilled);
        }

        [Fact]
        public void ClassicTalents_Has_Trees()
        {
            var talents = new ClassicRaiderIOModels.ClassicTalents
            {
                SpecName = "Holy",
                SpecRole = "HEALING",
                Trees = new System.Collections.Generic.List<ClassicRaiderIOModels.ClassicTalentTree>
                {
                    new() { Name = "Holy", Points = 51 },
                    new() { Name = "Discipline", Points = 20 },
                    new() { Name = "Shadow", Points = 0 }
                }
            };

            Assert.Equal("Holy", talents.SpecName);
            Assert.Equal(3, talents.Trees.Count);
            Assert.Equal(51, talents.Trees[0].Points);
        }

        [Fact]
        public void ModalConstants_Has_Classic_Component_IDs()
        {
            Assert.Equal("charclassic_view_overview", ModalConstants.ClassicCharOverview);
            Assert.Equal("charclassic_view_gear", ModalConstants.ClassicCharGear);
            Assert.Equal("charclassic_view_raids", ModalConstants.ClassicCharRaids);
            Assert.Equal("charclassic_refresh", ModalConstants.ClassicCharRefresh);
            Assert.Equal("charclassic_share", ModalConstants.ClassicCharShare);
        }

        [Fact]
        public void WowRealms_Has_GameVersion_Property()
        {
            var realm = new NinjaBotCore.Database.WowRealms
            {
                GameVersion = "Classic"
            };

            Assert.Equal("Classic", realm.GameVersion);
        }

        [Fact]
        public void WowRealms_GameVersion_Null_Is_Retail()
        {
            var realm = new NinjaBotCore.Database.WowRealms();

            Assert.Null(realm.GameVersion);
        }

        [Fact]
        public void ClassicCharProfile_RaidProgression_EmptySummary_IsNotNull()
        {
            // Classic RIO returns empty string summaries, not null
            var entry = new ClassicRaiderIOModels.ClassicRaidProgressionEntry
            {
                Summary = "",
                TotalBosses = 13
            };

            Assert.NotNull(entry.Summary);
            Assert.True(string.IsNullOrWhiteSpace(entry.Summary));
        }

        #region RioSearchHistory GameVersion Tests

        [Fact]
        public void RioSearchHistory_GameVersion_Classic_SetCorrectly()
        {
            var entry = new RioSearchHistory
            {
                DiscordUserId = 12345,
                CharacterName = "Nylock",
                RealmName = "Garalon",
                Region = "eu",
                GameVersion = "Classic",
                SearchCount = 1,
                LastSearched = DateTime.UtcNow
            };

            Assert.Equal("Classic", entry.GameVersion);
        }

        [Fact]
        public void RioSearchHistory_GameVersion_Null_Is_Retail()
        {
            var entry = new RioSearchHistory
            {
                DiscordUserId = 12345,
                CharacterName = "Retailchar",
                RealmName = "Proudmoore",
                Region = "us",
                SearchCount = 1,
                LastSearched = DateTime.UtcNow
            };

            Assert.Null(entry.GameVersion);
        }

        [Fact]
        public void RioSearchHistory_GameVersion_HasMaxLength20()
        {
            var prop = typeof(RioSearchHistory).GetProperty(nameof(RioSearchHistory.GameVersion));
            var maxLengthAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), false)
                .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()
                .FirstOrDefault();

            Assert.NotNull(maxLengthAttr);
            Assert.Equal(20, maxLengthAttr.Length);
        }

        [Fact]
        public void RioSearchHistory_ClassicAndRetail_CanCoexist()
        {
            var entries = new List<RioSearchHistory>
            {
                new() { DiscordUserId = 100, CharacterName = "RetailChar", RealmName = "Proudmoore", Region = "us", GameVersion = null },
                new() { DiscordUserId = 100, CharacterName = "ClassicChar", RealmName = "Whitemane", Region = "us", GameVersion = "Classic" },
                new() { DiscordUserId = 100, CharacterName = "SharedName", RealmName = "Proudmoore", Region = "us", GameVersion = null },
                new() { DiscordUserId = 100, CharacterName = "SharedName", RealmName = "Whitemane", Region = "us", GameVersion = "Classic" },
            };

            var retailOnly = entries.Where(e => e.GameVersion == null).ToList();
            var classicOnly = entries.Where(e => e.GameVersion == "Classic").ToList();

            Assert.Equal(2, retailOnly.Count);
            Assert.Equal(2, classicOnly.Count);
            Assert.DoesNotContain(retailOnly, e => e.GameVersion == "Classic");
            Assert.DoesNotContain(classicOnly, e => e.GameVersion == null);
        }

        #endregion

        #region Autocomplete Value Format Tests

        [Fact]
        public void AutocompleteValue_TildeDelimited_ParsesCorrectly()
        {
            // ClassicCharAutocomplete produces values like "CharName~RealmName~Region"
            var value = "Nylock~Garalon~eu";
            var parts = value.Split('~', 3);

            Assert.Equal(3, parts.Length);
            Assert.Equal("Nylock", parts[0]);
            Assert.Equal("Garalon", parts[1]);
            Assert.Equal("eu", parts[2]);
        }

        [Fact]
        public void AutocompleteValue_RealmWithSpaces_ParsesCorrectly()
        {
            var value = "Testchar~Sisters of Elune~us";
            var parts = value.Split('~', 3);

            Assert.Equal(3, parts.Length);
            Assert.Equal("Testchar", parts[0]);
            Assert.Equal("Sisters of Elune", parts[1]);
            Assert.Equal("us", parts[2]);
        }

        [Fact]
        public void AutocompleteValue_NoTilde_IsManualEntry()
        {
            // When user types manually instead of selecting from autocomplete
            var value = "Nylock";

            Assert.False(value.Contains('~'));
        }

        [Fact]
        public void AutocompleteValue_TildeParseOverridesRealmParam()
        {
            // Simulates what CharClassicCommands does when parsing autocomplete value
            var character = "Nylock~Garalon~eu";
            string realm = null;
            string region = "us";

            if (character.Contains('~'))
            {
                var parts = character.Split('~', 3);
                character = parts[0];
                if (parts.Length > 1 && string.IsNullOrWhiteSpace(realm))
                    realm = parts[1];
                if (parts.Length > 2)
                    region = parts[2];
            }

            Assert.Equal("Nylock", character);
            Assert.Equal("Garalon", realm);
            Assert.Equal("eu", region);
        }

        [Fact]
        public void AutocompleteValue_TildeParseDoesNotOverrideExplicitRealm()
        {
            // If user provides both autocomplete value AND typed realm, keep the typed realm
            var character = "Nylock~Garalon~eu";
            string realm = "Whitemane"; // Explicitly typed
            string region = "us";

            if (character.Contains('~'))
            {
                var parts = character.Split('~', 3);
                character = parts[0];
                if (parts.Length > 1 && string.IsNullOrWhiteSpace(realm))
                    realm = parts[1]; // Won't override because realm is not empty
                if (parts.Length > 2)
                    region = parts[2];
            }

            Assert.Equal("Nylock", character);
            Assert.Equal("Whitemane", realm); // Kept explicit realm
            Assert.Equal("eu", region); // Region still updated from autocomplete
        }

        [Fact]
        public void AutocompleteValue_ManualEntry_RequiresRealm()
        {
            // When user types manually without autocomplete, realm stays null
            var character = "Nylock";
            string realm = null;

            Assert.False(character.Contains('~'));
            Assert.True(string.IsNullOrWhiteSpace(realm));
        }

        #endregion

        #region ClassicCharAutocomplete Handler Tests

        [Fact]
        public void ClassicCharAutocomplete_InheritsAutocompleteHandler()
        {
            Assert.True(typeof(Discord.Interactions.AutocompleteHandler)
                .IsAssignableFrom(typeof(ClassicCharAutocomplete)));
        }

        [Fact]
        public void ClassicCharAutocomplete_HasGenerateSuggestionsAsync()
        {
            var method = typeof(ClassicCharAutocomplete).GetMethod("GenerateSuggestionsAsync");
            Assert.NotNull(method);
        }

        [Fact]
        public void ClassicRealmAutocomplete_InheritsAutocompleteHandler()
        {
            Assert.True(typeof(Discord.Interactions.AutocompleteHandler)
                .IsAssignableFrom(typeof(ClassicRealmAutocomplete)));
        }

        #endregion

        #region CharClassicCommands Autocomplete Attribute Tests

        [Fact]
        public void CharClassicCommands_Character_HasAutocompleteAttribute()
        {
            var method = typeof(NinjaBotCore.Modules.Interactions.Wow.CharClassicCommands)
                .GetMethod("GetClassicCharacterProfile");
            Assert.NotNull(method);

            var charParam = method.GetParameters().FirstOrDefault(p => p.Name == "character");
            Assert.NotNull(charParam);

            var autocompleteAttr = charParam.GetCustomAttributes(typeof(Discord.Interactions.AutocompleteAttribute), false)
                .FirstOrDefault();
            Assert.NotNull(autocompleteAttr);
        }

        [Fact]
        public void CharClassicCommands_Realm_IsOptional()
        {
            var method = typeof(NinjaBotCore.Modules.Interactions.Wow.CharClassicCommands)
                .GetMethod("GetClassicCharacterProfile");
            Assert.NotNull(method);

            var realmParam = method.GetParameters().FirstOrDefault(p => p.Name == "realm");
            Assert.NotNull(realmParam);
            Assert.True(realmParam.HasDefaultValue);
            Assert.Null(realmParam.DefaultValue);
        }

        [Fact]
        public void CharClassicCommands_Realm_HasAutocompleteAttribute()
        {
            var method = typeof(NinjaBotCore.Modules.Interactions.Wow.CharClassicCommands)
                .GetMethod("GetClassicCharacterProfile");
            Assert.NotNull(method);

            var realmParam = method.GetParameters().FirstOrDefault(p => p.Name == "realm");
            Assert.NotNull(realmParam);

            var autocompleteAttr = realmParam.GetCustomAttributes(typeof(Discord.Interactions.AutocompleteAttribute), false)
                .FirstOrDefault();
            Assert.NotNull(autocompleteAttr);
        }

        [Fact]
        public void CharClassicCommands_HasWowCacheServiceDependency()
        {
            var ctor = typeof(NinjaBotCore.Modules.Interactions.Wow.CharClassicCommands)
                .GetConstructors()
                .First();

            var parameters = ctor.GetParameters();
            Assert.Contains(parameters, p => p.ParameterType == typeof(WowCacheService));
        }

        #endregion
    }
}
