using System.Collections.Generic;
using System.Linq;
using Discord.Interactions;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Discord allows 100 top-level slash commands globally, and 100 per server. A group like
    /// /keys counts once however many subcommands it has. Going over makes Discord reject the
    /// whole update, so every command stays stale.
    /// </summary>
    public static class SlashCommandLimits
    {
        public const int MaxTopLevelCommands = 100;

        /// <summary>Top-level modules split into the global set and the owner's-server set ([OwnerGuildOnly]).</summary>
        public static (ModuleInfo[] Global, ModuleInfo[] OwnerGuild) Split(IEnumerable<ModuleInfo> modules)
        {
            var topLevel = modules.Where(m => m.Parent == null).ToArray();
            return (topLevel.Where(m => !m.DontAutoRegister).ToArray(), topLevel.Where(m => m.DontAutoRegister).ToArray());
        }

        public static int TopLevelCount(IEnumerable<ModuleInfo> modules) => modules.Sum(Count);

        private static int Count(ModuleInfo module)
            => module.IsSlashGroup ? 1 : module.SlashCommands.Count + module.SubModules.Sum(Count);
    }
}
