using System;
using System.Collections.Generic;
using System.Linq;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Crafting;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CraftCardV2Tests
    {
        [Fact]
        public void CraftItemNameValidationBoundsFreeformDiscordInput()
        {
            Assert.True(CraftEmbedBuilder.IsValidItemName(new string('x', 256)));
            Assert.False(CraftEmbedBuilder.IsValidItemName(new string('x', 257)));
            Assert.False(CraftEmbedBuilder.IsValidItemName("   "));
        }

        [Fact]
        public void BuildTicketCard_UsesV2ContainerAndPreservesLifecycleControls()
        {
            var ticket = new CraftTicket
            {
                Id = 42,
                ItemName = "Consecrated Alloy",
                Profession = "Blacksmithing",
                Status = "Open",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24),
                RequesterId = 123456789,
                RequesterName = "Requester",
                RequesterRealm = "Area 52",
                ConnectedRealms = "Area 52, Illidan",
                QualityDesired = "Rank 5",
                MaterialsStatus = "Have all materials",
                Commission = "10,000 gold",
                Note = "Need this before raid.",
                ItemIconUrl = "https://example.com/item.png",
                BlizzardItemId = 210221
            };

            var built = CraftEmbedBuilder.BuildTicketCard(ticket).Build();

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            Assert.Equal(Color.Blue, container.AccentColor);
            var section = Assert.Single(container.Components.OfType<SectionComponent>());
            Assert.IsType<ThumbnailComponent>(section.Accessory);

            var text = string.Join("\n", FlattenText(container.Components));
            Assert.Contains("Consecrated Alloy", text);
            Assert.Contains("Open", text);
            Assert.Contains("Blacksmithing", text);
            Assert.Contains("Rank 5", text);
            Assert.Contains("Have all materials", text);
            Assert.Contains("10,000 gold", text);
            Assert.Contains("Need this before raid.", text);
            Assert.Contains("Area 52, Illidan", text);
            Assert.Contains("Ticket #42", text);

            var buttons = container.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .ToList();
            Assert.Contains(buttons, button => button.CustomId == "craft_claim~42");
            Assert.Contains(buttons, button => button.CustomId == "craft_gotit~42");
            Assert.Contains(buttons, button => button.CustomId == "craft_cancel~42");
            Assert.Contains(buttons, button => button.Style == ButtonStyle.Link);
            Assert.True(CountComponents(built.Components) <= 40);
        }

        [Fact]
        public void BuildTicketListCard_RendersRowsAndKeepsFilterMenu()
        {
            var tickets = new List<CraftTicket>
            {
                new()
                {
                    Id = 7,
                    ItemName = "Algari Competitor's Plate Gauntlets",
                    Status = "Claimed",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-20),
                    RequesterId = 111,
                    CrafterId = 222
                },
                new()
                {
                    Id = 8,
                    ItemName = "Vicious Flask",
                    Status = "Open",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                    RequesterId = 333
                }
            };
            var controls = new ComponentBuilder()
                .WithSelectMenu(
                    "craft_list_filter~111~mine",
                    new List<SelectMenuOptionBuilder>
                    {
                        new("All Professions", "all", isDefault: true),
                        new("Alchemy", "Alchemy")
                    },
                    "Filter by profession...")
                .Build();

            var built = CraftEmbedBuilder.BuildTicketListCard(
                    tickets,
                    "Your Crafting Requests",
                    controls)
                .Build();

            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var text = string.Join("\n", FlattenText(container.Components));
            Assert.Contains("Your Crafting Requests", text);
            Assert.Contains("#7", text);
            Assert.Contains("Algari Competitor's Plate Gauntlets", text);
            Assert.Contains("Claimed", text);
            Assert.Contains("#8", text);
            Assert.Contains("Vicious Flask", text);
            Assert.Single(container.Components.OfType<ActionRowComponent>());
            Assert.True(CountComponents(built.Components) <= 40);
        }

        [Fact]
        public void BuildTicketCard_BoundsLegacyItemNamesWithoutBreakingUnicode()
        {
            var ticket = new CraftTicket
            {
                Id = long.MaxValue,
                ItemName = new string('x', 254) + "🚀" + new string('y', 5000),
                Status = "Open",
                CreatedAt = DateTime.UtcNow,
                RequesterId = 123
            };

            var built = CraftEmbedBuilder.BuildTicketCard(ticket).Build();
            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var heading = FlattenText(container.Components).Single(text => text.StartsWith("# "));

            Assert.True(heading.Length <= 258);
            Assert.Contains("…", heading);
            Assert.False(char.IsHighSurrogate(heading[^1]));
            Assert.False(char.IsLowSurrogate(heading[^1]));
            var buttons = container.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .ToList();
            Assert.All(buttons.Where(button => button.CustomId != null),
                button => Assert.True(button.CustomId.Length <= 100));
            var link = Assert.Single(buttons, button => button.Style == ButtonStyle.Link);
            Assert.True(link.Url.Length <= 512);
        }

        [Fact]
        public void BuildTicketListCard_MaxRowsFitsAggregateTextBudget()
        {
            var tickets = Enumerable.Range(1, 24)
                .Select(index => new CraftTicket
                {
                    Id = index,
                    ItemName = new string((char)('a' + index % 20), 200),
                    Status = "Open",
                    CreatedAt = DateTime.UtcNow,
                    RequesterId = 1000 + index
                })
                .ToList();

            var built = CraftEmbedBuilder.BuildTicketListCard(tickets, "Open Crafting Requests").Build();
            var renderedText = FlattenText(built.Components).ToList();

            Assert.True(renderedText.Sum(text => text.Length) <= 4000);
            Assert.All(renderedText, text => Assert.True(IsWellFormedUtf16(text)));
            Assert.Contains("Showing 10 of 24", string.Join("\n", renderedText));
        }

        [Fact]
        public void BuildThreadPreface_PreservesRequesterAndCrafterContextAcrossUpdates()
        {
            var ticket = new CraftTicket
            {
                ItemName = "Consecrated Alloy",
                Status = "Claimed",
                RequesterId = 123,
                CrafterId = 456
            };

            var preface = CraftEmbedBuilder.BuildThreadPreface(ticket);

            Assert.Contains("<@123>", preface);
            Assert.Contains("<@456>", preface);
            Assert.Contains("In progress", preface);
        }

        [Fact]
        public void BuildTicketListCard_DoesNotSplitUnicodeInLongItemNames()
        {
            var ticket = new CraftTicket
            {
                Id = 7,
                ItemName = string.Concat(Enumerable.Repeat("🚀", 300)),
                Status = "Open",
                CreatedAt = DateTime.UtcNow,
                RequesterId = 123
            };

            var built = CraftEmbedBuilder.BuildTicketListCard(
                    new List<CraftTicket> { ticket },
                    "Crafting Requests")
                .Build();
            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));

            Assert.All(FlattenText(container.Components), text =>
                Assert.True(IsWellFormedUtf16(text), "Rendered V2 text contains an unpaired UTF-16 surrogate."));
        }

        private static bool IsWellFormedUtf16(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<string> FlattenText(IEnumerable<IMessageComponent> components)
        {
            foreach (var component in components)
            {
                switch (component)
                {
                    case TextDisplayComponent text:
                        yield return text.Content;
                        break;
                    case ContainerComponent container:
                        foreach (var value in FlattenText(container.Components)) yield return value;
                        break;
                    case SectionComponent section:
                        foreach (var value in FlattenText(section.Components)) yield return value;
                        break;
                }
            }
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
