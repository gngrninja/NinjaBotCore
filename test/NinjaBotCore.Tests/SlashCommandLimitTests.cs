#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NinjaBotCore.Attributes;
using NinjaBotCore.Services;
using Xunit;
using Xunit.Abstractions;

namespace NinjaBotCore.Tests;

// Discord's application command limits, checked against every module as the bot registers it.
// Going over any of them makes Discord reject the whole update, so every command stays stale.
public class SlashCommandLimitTests
{
    private const int MaxChildren = 25;       // subcommands + groups per command, subcommands per group
    private const int MaxOptions = 25;
    private const int MaxChoices = 25;
    private const int MaxCommandSize = 8000;  // names, descriptions and choice values summed over one command
    private static readonly Regex Name = new(@"^[-_\p{Ll}\p{N}]{1,32}$");

    // Owner commands that act on the server they're run in, so they stay global for now
    private static readonly string[] GlobalOwnerCommands = { "top10-refresh", "migrate-watchers" };

    private readonly ITestOutputHelper _output;

    public SlashCommandLimitTests(ITestOutputHelper output) => _output = output;

    // Modules are constructed once while they're built, so any stand-in dependency will do.
    // Discord.Net builds each one inside a service scope, so this is its own scope too.
    private sealed class AnyServices : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        public IServiceProvider ServiceProvider => this;
        public IServiceScope CreateScope() => this;
        public void Dispose() { }

        public object GetService(Type type)
        {
            if (type == typeof(IServiceProvider) || type == typeof(IServiceScopeFactory)) return this;
            if (type.IsInterface || type.IsAbstract)
                return ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!).Object;
            return RuntimeHelpers.GetUninitializedObject(type);
        }
    }

    private static readonly Lazy<Task<InteractionService>> Registered = new(async () =>
    {
        var service = new InteractionService(new DiscordRestClient());
        await service.AddModulesAsync(typeof(NinjaBot).Assembly, new AnyServices());
        return service;
    });

    private static IEnumerable<SlashCommandInfo> Commands(ModuleInfo m)
        => m.SlashCommands.Concat(m.SubModules.Where(s => !s.IsSlashGroup).SelectMany(Commands));

    private static IEnumerable<ModuleInfo> Groups(ModuleInfo m)
        => m.SubModules.Where(s => s.IsSlashGroup).Concat(m.SubModules.Where(s => !s.IsSlashGroup).SelectMany(Groups));

    // What Discord sees at the top level: loose commands and groups
    private static List<(string Name, SlashCommandInfo? Command, ModuleInfo? Group)> TopLevel(IEnumerable<ModuleInfo> modules)
        => modules.SelectMany(m => m.IsSlashGroup
                ? new[] { (m.SlashGroupName, (SlashCommandInfo?)null, (ModuleInfo?)m) }
                : Commands(m).Select(c => (c.Name, (SlashCommandInfo?)c, (ModuleInfo?)null))
                    .Concat(Groups(m).Select(g => (g.SlashGroupName, (SlashCommandInfo?)null, (ModuleInfo?)g))))
            .ToList();

    private static List<(string Name, string Value)> Choices(SlashCommandParameterInfo p)
    {
        if (p.Choices.Count > 0)
            return p.Choices.Select(c => (c.Name, c.Value?.ToString() ?? "")).ToList();

        // Enum choices are written by the type converter at registration
        var props = new ApplicationCommandOptionProperties();
        p.TypeConverter?.Write(props, p);
        return props.Choices?.Select(c => (c.Name, c.Value?.ToString() ?? "")).ToList() ?? new();
    }

    private static void CheckText(string? name, string? description, string path, List<string> problems)
    {
        if (name == null || !Name.IsMatch(name))
            problems.Add($"/{path}: name '{name}' must be 1-32 lowercase letters, digits, - or _");
        if (string.IsNullOrEmpty(description) || description.Length > 100)
            problems.Add($"/{path}: description must be 1-100 characters (has {description?.Length ?? 0})");
    }

    private static int CheckCommand(SlashCommandInfo c, string path, List<string> problems)
    {
        CheckText(c.Name, c.Description, path, problems);
        var options = c.FlattenedParameters.ToList();
        if (options.Count > MaxOptions)
            problems.Add($"/{path}: {options.Count} options (max {MaxOptions})");
        if (options.SkipWhile(p => p.IsRequired).Any(p => p.IsRequired))
            problems.Add($"/{path}: a required option comes after an optional one");
        foreach (var name in options.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key))
            problems.Add($"/{path}: option '{name}' is used twice");

        var size = c.Name.Length + (c.Description?.Length ?? 0);
        foreach (var p in options)
        {
            CheckText(p.Name, p.Description, $"{path} {p.Name}:", problems);
            size += p.Name.Length + (p.Description?.Length ?? 0);

            var choices = Choices(p);
            if (choices.Count > MaxChoices)
                problems.Add($"/{path} {p.Name}: {choices.Count} choices (max {MaxChoices})");
            foreach (var (name, value) in choices)
            {
                if (name.Length is < 1 or > 100)
                    problems.Add($"/{path} {p.Name}: choice '{name}' must be 1-100 characters");
                if (value.Length > 100)
                    problems.Add($"/{path} {p.Name}: choice value '{value}' is over 100 characters");
                size += name.Length + value.Length;
            }
        }
        return size;
    }

    private static int CheckGroup(ModuleInfo g, string path, List<string> problems, int depth)
    {
        CheckText(g.SlashGroupName, g.Description, path, problems);
        var commands = Commands(g).ToList();
        var groups = Groups(g).ToList();
        var children = commands.Count + groups.Count;
        if (children is 0 or > MaxChildren)
            problems.Add($"/{path}: {children} subcommands and groups (must be 1-{MaxChildren})");
        if (depth > 1 && groups.Count > 0)
            problems.Add($"/{path}: groups can only nest one level deep");
        foreach (var name in commands.Select(c => c.Name).Concat(groups.Select(s => s.SlashGroupName))
                     .GroupBy(n => n).Where(n => n.Count() > 1).Select(n => n.Key))
            problems.Add($"/{path}: '{name}' is used twice");

        return g.SlashGroupName.Length + (g.Description?.Length ?? 0)
            + commands.Sum(c => CheckCommand(c, $"{path} {c.Name}", problems))
            + groups.Sum(s => CheckGroup(s, $"{path} {s.SlashGroupName}", problems, depth + 1));
    }

    private List<string> CheckScope(string scope, ModuleInfo[] modules)
    {
        var problems = new List<string>();
        var entries = TopLevel(modules);
        Assert.Equal(entries.Count, SlashCommandLimits.TopLevelCount(modules));
        _output.WriteLine($"{scope}: {entries.Count}/{SlashCommandLimits.MaxTopLevelCommands} top-level slash commands");

        if (entries.Count > SlashCommandLimits.MaxTopLevelCommands)
            problems.Add($"{scope}: {entries.Count} top-level commands (max {SlashCommandLimits.MaxTopLevelCommands})");
        foreach (var name in entries.GroupBy(e => e.Name).Where(g => g.Count() > 1).Select(g => g.Key))
            problems.Add($"{scope}: /{name} is registered twice");

        foreach (var (name, command, group) in entries)
        {
            var size = command != null ? CheckCommand(command, name, problems) : CheckGroup(group!, name, problems, 1);
            if (size > MaxCommandSize)
                problems.Add($"/{name}: {size} characters of names, descriptions and choices (max {MaxCommandSize})");
        }
        return problems;
    }

    private static string FullName(SlashCommandInfo c)
    {
        var parts = new List<string> { c.Name };
        for (var m = c.Module; m != null; m = m.Parent)
            if (m.IsSlashGroup) parts.Insert(0, m.SlashGroupName);
        return string.Join(" ", parts);
    }

    private static bool RequiresOwner(SlashCommandInfo c)
    {
        static bool Owner(IEnumerable<PreconditionAttribute> p)
            => p.Any(a => a is Discord.Interactions.RequireOwnerAttribute or NinjaBotCore.Attributes.RequireOwnerAttribute);
        if (Owner(c.Preconditions)) return true;
        for (var m = c.Module; m != null; m = m.Parent)
            if (Owner(m.Preconditions)) return true;
        return false;
    }

    [Fact]
    public async Task Global_and_owner_server_commands_fit_every_discord_limit()
    {
        var (global, owner) = SlashCommandLimits.Split((await Registered.Value).Modules);

        var problems = CheckScope("Global", global).Concat(CheckScope("Owner server", owner)).ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Owner_commands_are_only_registered_to_the_owner_server()
    {
        var (global, owner) = SlashCommandLimits.Split((await Registered.Value).Modules);
        var ownerCommands = owner.SelectMany(m => Commands(m).Concat(Groups(m).SelectMany(Commands))).ToList();
        var globalCommands = global.SelectMany(m => Commands(m).Concat(Groups(m).SelectMany(Commands))).ToList();

        Assert.Equal(new[] { "OwnerCommands", "RaidRecapRolloutCommands", "SyncCommands", "WowAdminInteract" },
            owner.Select(m => m.Name).OrderBy(n => n));
        Assert.All(ownerCommands, c => Assert.True(RequiresOwner(c), $"/{c.Name} is in the owner server but anyone there could run it"));
        Assert.Equal(GlobalOwnerCommands.OrderBy(n => n), globalCommands.Where(RequiresOwner).Select(c => c.Name).OrderBy(n => n));

        // These act on every server's guild links
        foreach (var name in new[] { "wow-list-duplicates", "wow-cleanup-duplicates", "wow-cleanup-null-slugs" })
            Assert.Contains(ownerCommands, c => c.Name == name);
    }

    [Fact]
    public void Only_owner_tools_skip_global_registration()
    {
        var skipped = typeof(NinjaBot).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<DontAutoRegisterAttribute>() != null);

        Assert.All(skipped, t => Assert.NotNull(t.GetCustomAttribute<OwnerGuildOnlyAttribute>()));
    }

    [Fact]
    public async Task Help_leaves_out_owner_server_commands()
    {
        var (_, owner) = SlashCommandLimits.Split((await Registered.Value).Modules);
        var ownerNames = owner.SelectMany(m => Commands(m).Concat(Groups(m).SelectMany(Commands))).Select(FullName).ToHashSet();
        using var help = new HelpContentProvider(NullLogger<HelpContentProvider>.Instance, new ConfigurationBuilder().Build());
        help.RegenerateHelpContent();

        var listed = help.GetHelpContent()!.Categories.SelectMany(c => c.Commands).Select(c => c.Name);

        Assert.DoesNotContain(listed, ownerNames.Contains);
    }
}
