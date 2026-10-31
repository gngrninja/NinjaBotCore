using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests;

// /forever registration, help and views. Every guild, user and character is SYNTHETIC.
public class ForeverCommandsTests
{
    private static readonly Regex OptionName = new(@"^[-_\p{Ll}\p{N}]{1,32}$");

    private static async Task<InteractionService> RegisterAsync()
    {
        var client = new DiscordRestClient();
        var service = new InteractionService(client);
        var deps = new ServiceCollection()
            .AddSingleton<ILogger<ForeverService>>(NullLogger<ForeverService>.Instance)
            .AddSingleton<ForeverService>()
            .BuildServiceProvider();
        await service.AddModuleAsync<ForeverCommands>(deps);
        await service.AddModuleAsync<ForeverComponentHandlers>(deps);
        return service;
    }

    // Enum choices are written by the type converter at registration, not stored on the parameter.
    private static List<string> Choices(SlashCommandParameterInfo parameter)
    {
        var props = new ApplicationCommandOptionProperties();
        parameter.TypeConverter.Write(props, parameter);
        return props.Choices.Select(c => c.Name).ToList();
    }

    private static string FullName(SlashCommandInfo c)
    {
        var parts = new List<string> { c.Name };
        for (var m = c.Module; m != null; m = m.Parent)
            if (!string.IsNullOrEmpty(m.SlashGroupName)) parts.Insert(0, m.SlashGroupName);
        return string.Join(" ", parts);
    }

    [Fact]
    public async Task Forever_registers_nested_groups_with_valid_discord_option_shapes()
    {
        using var service = await RegisterAsync();
        var commands = service.SlashCommands.Where(c => FullName(c).StartsWith("forever ")).ToList();

        Assert.Equal(new[]
        {
            "forever char add", "forever char edit", "forever char list", "forever char main", "forever char remove",
            "forever guild roster", "forever guild set", "forever guild unset", "forever guild view",
            "forever roster share", "forever roster unshare", "forever roster view"
        }, commands.Select(FullName).OrderBy(n => n));

        foreach (var command in commands)
        {
            Assert.InRange(command.Description.Length, 1, 100);
            Assert.InRange(command.Parameters.Count, 0, 25);
            foreach (var p in command.Parameters)
            {
                Assert.Matches(OptionName, p.Name);
                Assert.InRange(p.Description.Length, 1, 100);
            }
            // Required options must come before optional ones.
            Assert.True(command.Parameters.SkipWhile(p => p.IsRequired).All(p => !p.IsRequired), FullName(command));
        }

        var add = commands.Single(c => FullName(c) == "forever char add");
        Assert.Equal(new[] { "first-name", "surname", "class", "region", "ruleset", "faction" }, add.Parameters.Select(p => p.Name));
        Assert.Equal(new[] { "US", "EU", "KR", "TW" }, Choices(add.Parameters.Single(p => p.Name == "region")));
        Assert.Contains("Roleplay PvP", Choices(add.Parameters.Single(p => p.Name == "ruleset")));
        Assert.Equal(9, Choices(add.Parameters.Single(p => p.Name == "class")).Count);
    }

    [Fact]
    public async Task Only_guild_management_needs_kick_members()
    {
        using var service = await RegisterAsync();
        var managed = service.SlashCommands
            .Where(c => FullName(c).StartsWith("forever "))
            .Where(c => c.Preconditions.OfType<RequireUserPermissionAttribute>().Any(p => p.GuildPermission == GuildPermission.KickMembers))
            .Select(FullName)
            .OrderBy(n => n);

        Assert.Equal(new[] { "forever guild roster", "forever guild set", "forever guild unset" }, managed);
    }

    [Fact]
    public async Task Buttons_route_to_the_component_handlers()
    {
        using var service = await RegisterAsync();
        var ids = service.ComponentCommands.Select(c => c.Name).ToList();

        Assert.Contains($"{ForeverViews.MainButtonPrefix}~*~*", ids);
        Assert.Contains($"{ForeverViews.ShareButtonPrefix}~*~*", ids);
        Assert.Contains($"{ForeverViews.RosterPagePrefix}~*~*", ids);
    }

    [Fact]
    public void Help_lists_forever_in_its_own_category_with_real_option_names()
    {
        using var help = new HelpContentProvider(NullLogger<HelpContentProvider>.Instance, new ConfigurationBuilder().Build());
        help.RegenerateHelpContent();
        var content = help.GetHelpContent()!;

        var forever = content.Categories.Single(c => c.Id == "wow_forever");
        Assert.Contains(forever.Commands, c => c.Name == "forever guild set" && c.Permission == "moderator");
        var add = forever.Commands.Single(c => c.Name == "forever char add");
        Assert.Equal("first-name", add.Parameters[0].Name);
        Assert.Contains("US", add.Parameters.Single(p => p.Name == "region").Choices!);
        Assert.DoesNotContain(content.Categories.Where(c => c.Id != "wow_forever").SelectMany(c => c.Commands),
            c => c.Name.StartsWith("forever "));
    }

    private static ForeverCharacter Character(long id, long user, string first) => new()
    {
        Id = id, UserId = user, FirstName = first, LastName = "Brightvale",
        Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, Class = ForeverClass.Priest
    };

    [Fact]
    public void Roster_pages_never_overflow_an_embed_and_keep_everyone()
    {
        var guild = new ForeverGuild { Name = "*Synthetic* Guild", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, RosterEnabled = true };
        var members = Enumerable.Range(1, 60)
            .Select(u => new ForeverRosterMember(u,
                Enumerable.Range(0, u % 7 == 0 ? 25 : 2).Select(i => Character(u * 100 + i, u, "Member" + new string('x', i % 10))).ToList(),
                u * 100))
            .ToList();
        var roster = new ForeverRoster(guild, members);

        var pages = ForeverViews.RosterPages(roster, id => id == 3 ? null : $"User {id:D2}");

        Assert.True(pages.Count > 1);
        Assert.Equal(60, pages.Sum(p => p.Count));
        Assert.Contains(pages.SelectMany(p => p), e => e.Name == "Unknown member");
        for (var i = 0; i < pages.Count; i++)
        {
            var embed = ForeverViews.Roster(guild, pages, i);
            Assert.True(embed.Description.Length <= 4096);
            Assert.Contains($"Page {i + 1}/{pages.Count}", embed.Footer!.Value.Text);
        }
        Assert.Contains(@"\*Synthetic\*", ForeverViews.Roster(guild, pages, 0).Title);
    }

    [Fact]
    public void Pager_and_actions_only_offer_what_applies()
    {
        Assert.Null(ForeverViews.RosterPager(7, 0, 1));
        var pager = ForeverViews.RosterPager(7, 0, 3)!;
        var buttons = pager.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();
        Assert.True(buttons[0].IsDisabled);
        Assert.False(buttons[1].IsDisabled);
        Assert.Equal($"{ForeverViews.RosterPagePrefix}~7~1", buttons[1].CustomId);

        var c = Character(42, 7, "Asha");
        Assert.Null(ForeverViews.CharacterActions(7, c, isMain: true, canShare: false));
        var actions = ForeverViews.CharacterActions(7, c, isMain: false, canShare: true)!
            .Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId);
        Assert.Equal(new[] { $"{ForeverViews.MainButtonPrefix}~7~42", $"{ForeverViews.ShareButtonPrefix}~7~42" }, actions);
    }

    [Fact]
    public void Empty_states_tell_people_what_to_do()
    {
        var guild = new ForeverGuild { Name = "Synthetic", RosterEnabled = true };
        Assert.Contains("/forever roster share", ForeverViews.Roster(guild, new(), 0).Description);
        Assert.Contains("/forever char add", ForeverViews.CharacterList(Array.Empty<ForeverCharacter>(), null, new HashSet<long>()).Description);
        Assert.Contains("/forever guild set", ForeverViews.NoGuild("Server").Description);
    }
}
