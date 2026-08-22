using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class WowCardV2Tests
    {
        [Fact]
        public void FromEmbed_BuildsPolishedContainerAndPreservesControls()
        {
            var embed = new EmbedBuilder()
                .WithTitle("Frost Mage - Testchar")
                .WithDescription("**Item Level:** 305\n**M+ Score:** 2,500")
                .WithColor(new Color(70, 130, 255))
                .WithThumbnailUrl("https://example.com/avatar.png")
                .WithImageUrl("https://example.com/character.png")
                .AddField("Armory", "[Open](https://example.com/armory)", true)
                .AddField("Raider.IO", "[Open](https://example.com/rio)", true)
                .WithFooter("Area 52 (US) | Live character data");

            var legacyControls = new ComponentBuilder()
                .WithButton("Armory", "char_view_gear~1~Testchar~Area 52~us", ButtonStyle.Primary, new Emoji("🛡️"), row: 0)
                .WithButton("Upgrades", "char_view_upgrades~1~Testchar~Area 52~us", ButtonStyle.Success, new Emoji("⬆️"), row: 0)
                .WithSelectMenu(new SelectMenuBuilder()
                    .WithCustomId("char_gear_select~1~Testchar~Area 52~us")
                    .WithPlaceholder("Select an item")
                    .AddOption("Head - ilvl 305", "HEAD:123"), row: 1)
                .Build();

            var built = WowCardV2.FromEmbed(embed, legacyControls).Build();

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            Assert.Equal(new Color(70, 130, 255), container.AccentColor);

            var section = Assert.Single(container.Components.OfType<SectionComponent>());
            Assert.IsType<ThumbnailComponent>(section.Accessory);
            Assert.Contains(section.Components.OfType<TextDisplayComponent>(), text => text.Content.Contains("# Frost Mage - Testchar"));

            var text = string.Join("\n", container.Components.OfType<TextDisplayComponent>().Select(component => component.Content));
            Assert.Contains("Item Level", text);
            Assert.Contains("Armory", text);
            Assert.Contains("Area 52 (US)", text);

            var gallery = Assert.Single(container.Components.OfType<MediaGalleryComponent>());
            Assert.Single(gallery.Items);

            var rows = container.Components.OfType<ActionRowComponent>().ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows[0].Components.OfType<ButtonComponent>().Count());
            Assert.Single(rows[1].Components.OfType<SelectMenuComponent>());
            Assert.True(CountComponents(built.Components) <= 40);
        }

        [Fact]
        public void FromEmbed_WithoutThumbnailUsesTextHeaderAndNoInvalidSection()
        {
            var built = WowCardV2.FromEmbed(new EmbedBuilder()
                    .WithTitle("Realm: Area 52")
                    .WithDescription("🟢 **Up**")
                    .WithColor(Color.Green))
                .Build();

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            Assert.Empty(container.Components.OfType<SectionComponent>());
            Assert.Contains(container.Components.OfType<TextDisplayComponent>(), text => text.Content == "# Realm: Area 52");
        }

        [Fact]
        public void Notice_BuildsCompactStatusCard()
        {
            var built = WowCardV2.Notice(
                    "Character Not Found",
                    "Check the character name and realm.",
                    Color.Red,
                    "❌")
                .Build();

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var text = string.Join("\n", container.Components.OfType<TextDisplayComponent>().Select(component => component.Content));
            Assert.Contains("# ❌ Character Not Found", text);
            Assert.Contains("Check the character name", text);
            Assert.Equal(Color.Red, container.AccentColor);
        }

        [Fact]
        public void FromEmbed_PreservesDisabledButtonsAndSelectDefaults()
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId("char_logs_difficulty~1~Testchar~Area 52~us~53")
                .WithPlaceholder("Filter by difficulty")
                .AddOption("All Difficulties", "0", isDefault: true)
                .AddOption("Mythic", "5");
            var controls = new ComponentBuilder()
                .WithButton("Saved", "char_save~1~Testchar~Area 52~us", ButtonStyle.Secondary, disabled: true, row: 0)
                .WithSelectMenu(menu, row: 1)
                .Build();

            var built = WowCardV2.FromEmbed(new EmbedBuilder().WithTitle("Logs"), controls).Build();
            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var rows = container.Components.OfType<ActionRowComponent>().ToList();
            Assert.True(Assert.Single(rows[0].Components.OfType<ButtonComponent>()).IsDisabled);
            var select = Assert.Single(rows[1].Components.OfType<SelectMenuComponent>());
            Assert.True(select.Options.Single(option => option.Value == "0").IsDefault);
        }

        [Fact]
        public void CharacterManagementSelectedCard_RoutesViewProfileToLiveCharHandler()
        {
            var character = new WowCharAssociation
            {
                Id = 77,
                UserId = 123456789,
                CharName = "Testchar",
                WowRealm = "Area 52",
                LocalRealmSlug = "area-52",
                WowRegion = "us",
                IsMain = true
            };

            var built = CharacterManagementView.BuildSelectedCard(
                    123456789UL,
                    character,
                    "https://example.com/avatar.png")
                .Build();
            Assert.True(CharacterManagementView.TryBuildCharacterInfo(character, out var charInfo));
            Assert.Equal("Testchar", charInfo.Name);
            Assert.Equal("area-52", charInfo.RealmSlug);

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var buttons = container.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .ToList();
            var profile = Assert.Single(buttons, button => button.Label == "View Profile");
            Assert.Equal(
                "char_view_saved~123456789~77",
                profile.CustomId);
            Assert.DoesNotContain(buttons, button => button.CustomId?.StartsWith("char_view_rio~") == true);
            Assert.True(profile.CustomId.Length <= 100);

            var handler = typeof(CharCommands).GetMethod(
                "HandleViewSavedCharacter",
                BindingFlags.Instance | BindingFlags.Public);
            var route = handler?.GetCustomAttribute<ComponentInteractionAttribute>();
            Assert.NotNull(route);
            Assert.Equal("char_view_saved~*~*", route.CustomId);
            Assert.True(CountComponents(built.Components) <= 40);
        }

        [Fact]
        public void CharacterManagementSelectedCard_KeepsCustomIdBoundedForLegacyUnboundedData()
        {
            var character = new WowCharAssociation
            {
                Id = long.MaxValue,
                CharName = new string('x', 5000),
                WowRealm = new string('y', 5000),
                WowRegion = new string('z', 5000)
            };

            var built = CharacterManagementView.BuildSelectedCard(ulong.MaxValue, character).Build();
            Assert.False(CharacterManagementView.TryBuildCharacterInfo(character, out _));
            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var profile = container.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .Single(button => button.Label == "View Profile");

            Assert.Equal($"char_view_saved~{ulong.MaxValue}~{long.MaxValue}", profile.CustomId);
            Assert.True(profile.CustomId.Length <= 100);
        }

        [Fact]
        public void RetailOverviewControls_FitV2LimitAndKeepEveryRoute()
        {
            var character = new CharacterInfo
            {
                Name = "Testchar",
                Realm = "Area 52",
                RealmSlug = "area-52",
                Region = "us"
            };
            var controls = CharOverviewView.BuildComponents(
                    123456789UL,
                    character,
                    hasRioData: true,
                    hasArmoryData: true,
                    isAlreadySaved: true,
                    hasAchievements: true)
                .Build();

            var built = WowCardV2.FromEmbed(
                    new EmbedBuilder()
                        .WithTitle("Testchar — Frost Mage")
                        .WithDescription("Retail character overview")
                        .WithThumbnailUrl("https://example.com/avatar.png"),
                    controls)
                .Build();

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var buttons = container.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .ToList();

            Assert.Equal(11, buttons.Count);
            Assert.Contains(buttons, button => button.CustomId?.StartsWith("char_view_gear~") == true);
            Assert.Contains(buttons, button => button.CustomId?.StartsWith("char_view_logs~") == true);
            Assert.Contains(buttons, button => button.CustomId?.StartsWith("char_view_achievements~") == true);
            Assert.Contains(buttons, button => button.CustomId?.StartsWith("char_manage_ret~") == true);
            Assert.True(CountComponents(built.Components) <= 40);
        }

        [Fact]
        public void SavedCharacterBoundary_KeepsAllProductionNavigationIdsUnderDiscordLimit()
        {
            var character = new WowCharAssociation
            {
                CharName = new string('n', 12),
                WowRealm = new string('r', 32),
                WowRegion = "us"
            };
            Assert.True(CharacterManagementView.TryBuildCharacterInfo(character, out var charInfo));

            var overview = CharOverviewView.BuildComponents(
                    ulong.MaxValue,
                    charInfo,
                    hasRioData: true,
                    hasArmoryData: true,
                    isAlreadySaved: true,
                    hasAchievements: true)
                .Build();
            var overviewIds = overview.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .Where(button => button.CustomId != null)
                .Select(button => button.CustomId)
                .ToList();
            Assert.All(overviewIds, customId => Assert.True(customId.Length <= 100));

            var difficulty = CharLogsView.BuildDifficultySelectMenu(
                    ulong.MaxValue,
                    charInfo,
                    zoneId: 53,
                    currentDifficulty: 5)
                .Build();
            Assert.True(difficulty.CustomId.Length <= 100);

            var rankings = new WclV2ZoneRankingsData
            {
                Rankings = new List<WclV2BossRanking>
                {
                    new()
                    {
                        Encounter = new WclV2EncounterBasic { Id = 1, Name = "Boss" },
                        RankPercent = 90,
                        TotalKills = 1
                    }
                }
            };
            var encounter = CharLogsView.BuildEncounterSelectMenuV2(
                    ulong.MaxValue,
                    charInfo,
                    rankings,
                    zoneId: 53,
                    currentDifficulty: 5)
                .Build();
            Assert.True(encounter.CustomId.Length <= 100);
        }

        [Fact]
        public void FromEmbed_LongUnicodeTextSplitsWithoutBreakingSurrogatePairs()
        {
            var description = "  " + new string('x', 3897) + "🚀" + new string('y', 100) + "  ";
            var built = WowCardV2.FromEmbed(new EmbedBuilder()
                    .WithTitle("Long card")
                    .WithDescription(description))
                .Build();
            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var body = container.Components
                .OfType<TextDisplayComponent>()
                .Where(display => !display.Content.StartsWith("# "))
                .Select(display => display.Content)
                .ToList();

            Assert.Equal(description, string.Concat(body));
            Assert.All(body, chunk =>
            {
                Assert.False(char.IsHighSurrogate(chunk[^1]));
                Assert.False(char.IsLowSurrogate(chunk[0]));
                Assert.True(chunk.Length <= 4000);
            });
        }

        private static int CountComponents(IEnumerable<IMessageComponent> components)
        {
            var count = 0;
            foreach (var component in components)
            {
                count++;
                switch (component)
                {
                    case ContainerComponent container:
                        count += CountComponents(container.Components);
                        break;
                    case SectionComponent section:
                        count += CountComponents(section.Components);
                        count++;
                        break;
                    case ActionRowComponent row:
                        count += CountComponents(row.Components);
                        break;
                }
            }
            return count;
        }
    }
}
