using System;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Xunit;

namespace NinjaBotCore.Tests;

/// <summary>Reads a Components V2 card the way a test wants to: all of its text, buttons and menus, wherever they are nested.</summary>
internal static class ForeverCardReader
{
    public static IEnumerable<IMessageComponent> Flatten(IEnumerable<IMessageComponent> components)
    {
        foreach (var component in components)
        {
            yield return component;
            var children = component switch
            {
                ContainerComponent container => container.Components,
                SectionComponent section => section.Components.Append(section.Accessory),
                ActionRowComponent row => row.Components,
                _ => Enumerable.Empty<IMessageComponent>()
            };
            foreach (var child in Flatten(children.Where(c => c != null))) yield return child;
        }
    }

    public static List<T> All<T>(MessageComponent card) => Flatten(card.Components).OfType<T>().ToList();

    // Line-ending agnostic: AppVeyor runs these on Windows.
    public static string Text(MessageComponent card)
        => string.Join("\n", All<TextDisplayComponent>(card).Select(t => t.Content)).Replace("\r\n", "\n");

    public static string[] Lines(MessageComponent card) => Text(card).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static List<ButtonComponent> Buttons(MessageComponent card) => All<ButtonComponent>(card);

    public static SelectMenuComponent Menu(MessageComponent card, string prefix)
        => All<SelectMenuComponent>(card).SingleOrDefault(m => m.CustomId.StartsWith(prefix + "~"));

    public static Color Accent(MessageComponent card) => Assert.IsType<ContainerComponent>(Assert.Single(card.Components)).AccentColor!.Value;

    /// <summary>Discord's limits for one Components V2 message.</summary>
    public static void AssertFits(MessageComponent card)
    {
        Assert.InRange(All<TextDisplayComponent>(card).Sum(t => t.Content.Length), 1, 4000);
        Assert.InRange(Flatten(card.Components).Count(), 1, 40);
        Assert.All(All<ButtonComponent>(card).Where(b => b.Style != ButtonStyle.Link), b => Assert.InRange(b.CustomId.Length, 1, 100));
        foreach (var menu in All<SelectMenuComponent>(card))
        {
            Assert.InRange(menu.Options.Count, 1, 25);
            Assert.Equal(menu.Options.Count, menu.Options.Select(o => o.Value).Distinct().Count());
            Assert.All(menu.Options, o =>
            {
                Assert.InRange(o.Label.Length, 1, 100);
                Assert.InRange(o.Value.Length, 1, 100);
                Assert.InRange((o.Description ?? "x").Length, 1, 100);
            });
        }
    }
}
