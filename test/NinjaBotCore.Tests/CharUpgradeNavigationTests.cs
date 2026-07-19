using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CharUpgradeNavigationTests
    {
        private static readonly CharacterInfo Character = new()
        {
            Name = "Testchar",
            Realm = "Area 52",
            RealmSlug = "area-52",
            Region = "us"
        };

        [Fact]
        public void BuildComponents_IncludesUpgradesViewForArmoryCharacters()
        {
            var built = CharOverviewView.BuildComponents(
                123456789UL,
                Character,
                hasRioData: true,
                hasArmoryData: true).Build();

            var buttons = built.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .ToList();

            var upgrades = Assert.Single(buttons, button => button.Label == "Upgrades");
            Assert.Equal("char_view_upgrades~123456789~Testchar~Area 52~us", upgrades.CustomId);
            Assert.False(upgrades.IsDisabled);
        }

        [Fact]
        public void BuildComponents_DisablesUpgradesWithoutArmoryData()
        {
            var built = CharOverviewView.BuildComponents(
                123456789UL,
                Character,
                hasRioData: true,
                hasArmoryData: false).Build();

            var upgrades = built.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .Single(button => button.Label == "Upgrades");

            Assert.True(upgrades.IsDisabled);
        }

        [Fact]
        public void BuildDetailViewComponents_HighlightsUpgradesView()
        {
            var built = CharOverviewView.BuildDetailViewComponents(
                123456789UL,
                Character,
                "upgrades",
                isAlreadySaved: true).Build();

            var upgrades = built.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .Single(button => button.Label == "Upgrades");

            Assert.Equal(ButtonStyle.Success, upgrades.Style);
        }

        [Fact]
        public void CharCommands_RegistersUpgradesComponentHandler()
        {
            var method = typeof(CharCommands).GetMethod(
                "HandleViewUpgrades",
                BindingFlags.Instance | BindingFlags.Public);

            Assert.NotNull(method);
            var attribute = method.GetCustomAttribute<ComponentInteractionAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal("char_view_upgrades~*~*", attribute.CustomId);
        }

        [Fact]
        public async Task InteractionService_RoutesCompleteUpgradeCustomId()
        {
            using var restClient = new DiscordRestClient();
            var interactionService = new InteractionService(restClient);
            using var services = new ServiceCollection().BuildServiceProvider();
            await interactionService.AddModuleAsync<UpgradeRoutingFixtureModule>(services);

            var data = new Mock<IComponentInteractionData>();
            data.SetupGet(value => value.CustomId)
                .Returns("char_view_upgrades~123456789~Testchar~Area 52~us");
            var interaction = new Mock<IComponentInteraction>();
            interaction.SetupGet(value => value.Data).Returns(data.Object);

            var result = interactionService.SearchComponentCommand(interaction.Object);

            Assert.True(result.IsSuccess, result.ErrorReason);
            Assert.Equal(
                new[] { "123456789", "Testchar~Area 52~us" },
                result.RegexCaptureGroups);
        }

        public sealed class UpgradeRoutingFixtureModule : InteractionModuleBase
        {
            [ComponentInteraction("char_view_upgrades~*~*")]
            public Task Handle(string userId, string character) => Task.CompletedTask;
        }
    }
}
