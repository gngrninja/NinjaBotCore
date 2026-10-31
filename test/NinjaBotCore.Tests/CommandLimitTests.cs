using System;
using System.Linq;
using System.Reflection;
using Discord.Interactions;
using Xunit;
using Xunit.Abstractions;

namespace NinjaBotCore.Tests;

public class CommandLimitTests
{
    private readonly ITestOutputHelper _output;

    public CommandLimitTests(ITestOutputHelper output) => _output = output;

    private static bool IsModule(Type t) => !t.IsAbstract && typeof(IInteractionModuleBase).IsAssignableFrom(t);

    // Discord rejects the whole global bulk overwrite past 100 top-level slash commands,
    // which would leave every command stale. A group (e.g. /keys, /forever) counts once.
    [Fact]
    public void Top_level_slash_commands_fit_discords_global_limit()
    {
        var roots = typeof(NinjaBot).Assembly.GetTypes()
            .Where(t => IsModule(t) && (t.DeclaringType == null || !IsModule(t.DeclaringType)))
            .Where(t => t.GetCustomAttribute<DontAutoRegisterAttribute>() == null)
            .ToList();

        var count = roots.Sum(t => t.GetCustomAttribute<GroupAttribute>() != null
            ? 1
            : t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Count(m => m.GetCustomAttribute<SlashCommandAttribute>() != null));
        _output.WriteLine($"Top-level slash commands: {count}/100");

        Assert.InRange(count, 1, 100);
    }
}
