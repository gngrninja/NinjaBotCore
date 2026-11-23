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
using static NinjaBotCore.Tests.ForeverCardReader;

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
            .AddSingleton<ILogger<ForeverCrafters>>(NullLogger<ForeverCrafters>.Instance)
            .AddSingleton<IForeverArmory>(new FakeForeverArmory())
            .AddSingleton<IForeverCatalog>(new FakeForeverCatalog())
            .AddSingleton<IForeverPrices>(new FakeForeverPrices())
            .AddSingleton<IForeverCrafters, ForeverCrafters>()
            .AddMemoryCache()
            .AddSingleton<ForeverService>()
            .AddSingleton<ForeverWorkshop>()
            .AddSingleton<ForeverCamps>()
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
    public async Task Forever_registers_its_commands_with_valid_discord_option_shapes()
    {
        using var service = await RegisterAsync();
        var commands = service.SlashCommands.Where(c => FullName(c).StartsWith("forever ")).ToList();

        Assert.Equal(new[]
        {
            "forever camp",
            "forever char add", "forever char list", "forever char view",
            "forever craft",
            "forever guild set", "forever guild unset", "forever guild view",
            "forever help", "forever price", "forever roster"
        }, commands.Select(FullName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(commands.Single(c => FullName(c) == "forever help").Parameters);

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

        (string, bool)[] Options(string name) => commands.Single(c => FullName(c) == name).Parameters.Select(p => (p.Name, p.IsRequired)).ToArray();
        IAutocompleteHandler Suggests(string name) => commands.Single(c => FullName(c) == name).Parameters[0].AutocompleteHandler;

        // The armory fills in everything else, so a name and a region are all anyone is asked for.
        Assert.Equal(new[] { ("character", true), ("region", false) }, Options("forever char add"));
        Assert.Equal(new[] { ("name", true), ("region", true) }, Options("forever guild set"));
        Assert.Equal(new[] { ("character", true), ("region", false) }, Options("forever char view"));
        Assert.Equal(new[] { ("member", false) }, Options("forever roster"));
        Assert.Equal(new[] { ("recipe", true) }, Options("forever craft"));
        Assert.Equal(new[] { ("item", true), ("region", false) }, Options("forever price"));
        Assert.Equal(new[] { ("note", false) }, Options("forever camp"));

        // Blizzard has no search, so names are suggested from the guild's roster and by asking the armory about what's typed.
        Assert.IsType<ForeverLookupAutocomplete>(Suggests("forever char view"));
        Assert.IsType<ForeverAddAutocomplete>(Suggests("forever char add"));
        Assert.IsType<ForeverGuildNameAutocomplete>(Suggests("forever guild set"));
        Assert.IsType<ForeverRecipeAutocomplete>(Suggests("forever craft"));
        Assert.IsType<ForeverItemAutocomplete>(Suggests("forever price"));
        Assert.Equal(new[] { "US", "EU", "KR", "TW" },
            Choices(commands.Single(c => FullName(c) == "forever char add").Parameters.Single(p => p.Name == "region")));
    }

    [Fact]
    public async Task Only_setting_and_removing_the_guild_need_kick_members()
    {
        using var service = await RegisterAsync();
        var managed = service.SlashCommands
            .Where(c => FullName(c).StartsWith("forever "))
            .Where(c => c.Preconditions.OfType<RequireUserPermissionAttribute>().Any(p => p.GuildPermission == GuildPermission.KickMembers))
            .Select(FullName)
            .OrderBy(n => n);

        Assert.Equal(new[] { "forever guild set", "forever guild unset" }, managed);
    }

    [Fact]
    public async Task Buttons_and_menus_route_to_the_component_handlers()
    {
        using var service = await RegisterAsync();
        var ids = service.ComponentCommands.Select(c => c.Name).ToList();

        Assert.Contains($"{ForeverViews.MainButtonPrefix}~*~*", ids);
        Assert.Contains($"{ForeverViews.ShareButtonPrefix}~*~*", ids);
        Assert.Contains($"{ForeverViews.HideButtonPrefix}~*~*", ids);
        Assert.Contains($"{ForeverViews.RemoveButtonPrefix}~*~*~*", ids);
        Assert.Contains($"{ForeverViews.ManagePrefix}~*", ids);
        Assert.Contains($"{ForeverViews.ListButtonPrefix}~*", ids);
        Assert.Contains($"{ForeverViews.RosterPagePrefix}~*~*", ids);
        Assert.Contains($"{ForeverViews.CardButtonPrefix}~*~*~*~*~*", ids);
        Assert.Contains($"{ForeverViews.CardMenuPrefix}~*~*~*~*~*", ids);
        Assert.Contains($"{ForeverViews.RosterSortPrefix}~*", ids);
        Assert.Contains($"{ForeverViews.RosterPickPrefix}~*", ids);
        Assert.Contains($"{ForeverViews.CampButtonPrefix}~*", ids);
        // Lists sent before characters could be managed from them still open an armory card.
        Assert.Contains($"{ForeverViews.OpenCardPrefix}~*", ids);
        // No two handlers answer the same id.
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Help_lists_forever_in_its_own_category_with_real_option_names()
    {
        using var help = new HelpContentProvider(NullLogger<HelpContentProvider>.Instance, new ConfigurationBuilder().Build());
        help.RegenerateHelpContent();
        var content = help.GetHelpContent()!;

        var forever = content.Categories.Single(c => c.Id == "wow_forever");
        Assert.Contains(forever.Commands, c => c.Name == "forever guild set" && c.Permission == "moderator");
        Assert.Contains(forever.Commands, c => c.Name == "forever help" && c.Permission == "public");
        var add = forever.Commands.Single(c => c.Name == "forever char add");
        Assert.Equal("character", add.Parameters[0].Name);
        Assert.Contains("US", add.Parameters.Single(p => p.Name == "region").Choices!);
        Assert.DoesNotContain(content.Categories.Where(c => c.Id != "wow_forever").SelectMany(c => c.Commands),
            c => c.Name.StartsWith("forever "));
    }

    // A command as the help card writes it: in backticks, in full or shortened to "group command".
    private const string Command = @"`(?:/forever )?((?:guild|char) [a-z]+|roster|craft|price|camp)`";

    // The help card is written by hand, so this keeps it honest when a command is added, renamed or removed.
    [Fact]
    public async Task Forever_help_names_every_forever_command_and_only_real_ones()
    {
        using var service = await RegisterAsync();
        var registered = service.SlashCommands.Select(FullName).Where(n => n.StartsWith("forever ")).ToHashSet();
        var matched = new ForeverGuild { Name = "Synthetic", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, RosterEnabled = true };
        var setups = new[]
        {
            new ForeverSetup(null, 0, null, 0, IsOfficer: false),
            new ForeverSetup(null, 0, null, 0, IsOfficer: true),
            new ForeverSetup(matched, 3, "Asha Brightvale", 2, IsOfficer: true)
        };

        foreach (var setup in setups)
        {
            var text = Text(ForeverViews.Help("Server", null, 7, setup));
            // Commands are written in backticks, in full or shortened to "group command".
            var named = Regex.Matches(text, Command).Select(m => "forever " + m.Groups[1].Value).ToHashSet();

            Assert.Empty(named.Except(registered));
            Assert.Empty(registered.Except(named).Except(new[] { "forever help" }));
            Assert.Equal(Regex.Matches(text, "`").Count / 2, Regex.Matches(text, Command).Count);
        }
    }

    private static ForeverCharacter Character(long id, long user, string first) => new()
    {
        Id = id, UserId = user, FirstName = first, LastName = "Brightvale",
        Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, Class = ForeverClass.Priest
    };

    [Fact]
    public void Roster_pages_never_overflow_a_card_and_keep_everyone()
    {
        var guild = new ForeverGuild { Name = "*Synthetic* Guild", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, RosterEnabled = true };
        var members = Enumerable.Range(1, 60)
            .Select(u => new ForeverRosterMember(u,
                Enumerable.Range(0, u % 7 == 0 ? 25 : 2).Select(i => Character(u * 100 + i, u, "Member" + new string('x', i % 10))).ToList(),
                u * 100))
            .ToList();
        var roster = new ForeverRoster(guild, members);
        string Name(long id) => id == 3 ? null : $"User {id:D2}";

        var pages = ForeverViews.RosterPages(roster, Name);

        Assert.True(pages.Count > 1);
        Assert.Equal(60, pages.Sum(p => p.Count));
        Assert.Contains(pages.SelectMany(p => p), e => e.Name == "Unknown member");
        for (var i = 0; i < pages.Count; i++)
        {
            var page = ForeverViews.RosterPage(roster, Name, i, 7);
            Assert.Equal((i, pages.Count), (page.Page, page.PageCount));
            AssertFits(page.Card);
            Assert.Contains(Buttons(page.Card), b => b.Label == $"Page {i + 1}/{pages.Count}" && b.IsDisabled);
        }
        Assert.StartsWith(@"# 👥 \*Synthetic\* Guild roster", Text(ForeverViews.RosterPage(roster, Name, 0, 7).Card));
    }

    [Fact]
    public void Pager_and_actions_only_offer_what_applies()
    {
        var guild = new ForeverGuild { Name = "Synthetic", Region = ForeverRegion.US, Ruleset = ForeverRuleset.PvP, Faction = ForeverFaction.Horde, RosterEnabled = true };
        ForeverRoster Roster(int count) => new(guild, Enumerable.Range(1, count)
            .Select(u => new ForeverRosterMember(u, new[] { Character(u, u, "Asha") }, null)).ToList());

        Assert.Empty(Buttons(ForeverViews.RosterPage(Roster(3), _ => "Member", 0, 7).Card));
        var buttons = Buttons(ForeverViews.RosterPage(Roster(40), _ => "Member", 0, 7).Card);
        Assert.True(buttons[0].IsDisabled);
        Assert.False(buttons[2].IsDisabled);
        Assert.Equal($"{ForeverViews.RosterPagePrefix}~7~1r", buttons[2].CustomId);
    }

    [Fact]
    public void A_saved_characters_card_offers_what_can_be_done_to_it()
    {
        var onArmory = new ForeverArmoryCharacter(1, "Asha", "Brightvale", "asha-brightvale", ForeverRegion.US, 34, "Orc", ForeverClass.Priest, "Priest",
            ForeverFaction.Horde, ForeverRuleset.PvP, "PvP", "Synthetic", "synthetic", 20, null);
        var saved = new ForeverSavedCharacter(Character(42, 7, "Asha"), onArmory, ForeverLookupStatus.Found);
        string[] Ids(MessageComponent card) => Buttons(card).Select(b => b.CustomId).ToArray();
        const string remove = "forever_remove~7~42~n", list = "forever_list~7", armoryCard = "forever_card~7~o~0~Asha~Brightvale";

        // A main that is already on the roster: hide it, open it, remove it, go back.
        var shown = ForeverViews.CharacterSaved(saved, "added", isMain: true, isShared: true, 7, canShare: true);
        Assert.Equal(new[] { "forever_hide~7~42", armoryCard, remove, list }, Ids(shown));
        Assert.Contains("Asha Brightvale · added ✅", Text(shown));
        Assert.Contains("level 34 Orc, in Synthetic", Text(shown));
        Assert.Contains("★ Your main in this server.", Text(shown));
        AssertFits(shown);

        // Not the main and not on the roster: both are one press away, five buttons in all.
        var fresh = ForeverViews.CharacterSaved(saved, null, isMain: false, isShared: false, 7, canShare: true);
        Assert.Equal(new[] { "forever_main~7~42", "forever_share~7~42", armoryCard, remove, list }, Ids(fresh));
        Assert.DoesNotContain("✅", Text(fresh));
        Assert.Contains("only you can see that it's yours", Text(fresh));

        // A character that doesn't fit this server's guild can't go on its roster, and the card says why.
        var elsewhere = ForeverViews.CharacterSaved(saved, null, isMain: false, isShared: false, 7, canShare: false);
        Assert.DoesNotContain(Ids(elsewhere), id => id.StartsWith("forever_share"));
        Assert.Contains("roster is for its own guild's region, ruleset and faction", Text(elsewhere));

        // Remove asks first, and only the second press carries the "y" that deletes.
        var asking = ForeverViews.CharacterSaved(saved, null, isMain: true, isShared: false, 7, canShare: true, confirmRemove: true);
        var confirm = Assert.Single(Buttons(asking), b => b.CustomId.StartsWith("forever_remove"));
        Assert.Equal(("forever_remove~7~42~y", "Really remove it", ButtonStyle.Danger), (confirm.CustomId, confirm.Label, confirm.Style));
        Assert.Contains("from every server's roster", Text(asking));

        // Off the armory there is no armory card to open; everything else still works.
        var young = new ForeverSavedCharacter(Character(42, 7, "Asha"), null, ForeverLookupStatus.NotFound);
        var youngCard = ForeverViews.CharacterSaved(young, "added", isMain: true, isShared: false, 7, canShare: true);
        Assert.Equal(new[] { "forever_share~7~42", remove, list }, Ids(youngCard));
        Assert.Contains("Not on the armory yet", Text(youngCard));
    }

    [Fact]
    public void The_character_list_opens_a_character_to_manage_it()
    {
        var characters = new[] { Character(1, 7, "Asha"), Character(2, 7, "Thalen") };
        var card = ForeverViews.CharacterList(characters, 2, new HashSet<long> { 1 }, 7, "🗑️ Removed **Mira Brightvale**.");

        var menu = Menu(card, ForeverViews.ManagePrefix);
        Assert.Equal("forever_manage~7", menu.CustomId);
        Assert.Equal(new[] { "1", "2" }, menu.Options.Select(o => o.Value));
        var lines = Lines(card);
        Assert.Contains("🗑️ Removed **Mira Brightvale**.", lines);
        Assert.Contains(lines, l => l.StartsWith("👥 ") && l.Contains("**Asha Brightvale**"));
        Assert.Contains(lines, l => l.StartsWith("★ ") && l.Contains("**Thalen Brightvale**"));
        AssertFits(card);
    }

    [Fact]
    public void Empty_states_tell_people_what_to_do()
    {
        var guild = new ForeverGuild { Name = "Synthetic", RosterEnabled = true };
        var empty = new ForeverRoster(guild, Array.Empty<ForeverRosterMember>());
        Assert.Contains("/forever char list", Text(ForeverViews.RosterPage(empty, _ => null, 0, 7).Card));
        Assert.Contains("/forever char add", Text(ForeverViews.CharacterList(Array.Empty<ForeverCharacter>(), null, new HashSet<long>(), 7)));
        Assert.Contains("/forever guild set", Text(ForeverViews.NoGuild("Server", null)));
    }
}
