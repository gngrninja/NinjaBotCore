using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Help;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Provides centralized access to help command content.
    /// Used by both Discord /help command and the Commands API.
    /// Supports auto-regeneration at configurable intervals.
    /// </summary>
    public class HelpContentProvider : IDisposable
    {
        private readonly ILogger<HelpContentProvider> _logger;
        private readonly IConfigurationRoot _config;
        private HelpContent? _helpContent;
        private readonly object _lock = new();
        private DateTime _lastLoaded = DateTime.MinValue;
        private Timer? _autoRefreshTimer;
        private bool _disposed;

        public HelpContentProvider(ILogger<HelpContentProvider> logger, IConfigurationRoot config)
        {
            _logger = logger;
            _config = config;

            // Always regenerate from actual slash commands after startup
            // No file persistence - in-memory only, regenerated each startup
            var intervalHours = _config.GetValue<int>("CommandsApi:AutoRegenerateHours", 0);
            var interval = intervalHours > 0
                ? TimeSpan.FromHours(intervalHours)
                : Timeout.InfiniteTimeSpan; // No recurring refresh if not configured

            _logger.LogInformation("Help content will regenerate from slash commands after startup" +
                (intervalHours > 0 ? $", then every {intervalHours} hours" : ""));

            // Initial regeneration after 30 seconds (give bot time to register commands), then at interval
            _autoRefreshTimer = new Timer(
                _ => RegenerateHelpContent(),
                null,
                TimeSpan.FromSeconds(30),
                interval);
        }

        /// <summary>
        /// Gets the current help content. Thread-safe.
        /// </summary>
        public HelpContent? GetHelpContent()
        {
            lock (_lock)
            {
                return _helpContent;
            }
        }

        /// <summary>
        /// Gets when the help content was last loaded/refreshed.
        /// </summary>
        public DateTime LastLoaded
        {
            get
            {
                lock (_lock)
                {
                    return _lastLoaded;
                }
            }
        }

        /// <summary>
        /// Refreshes the help content with newly generated data.
        /// Called by /regenerate-help command.
        /// </summary>
        public void RefreshHelpContent(HelpContent content)
        {
            lock (_lock)
            {
                _helpContent = content;
                _lastLoaded = DateTime.UtcNow;
                _logger.LogInformation("Help content refreshed with {Count} commands in {Categories} categories",
                    content.Metadata?.TotalCommands ?? 0,
                    content.Categories?.Count ?? 0);
            }
        }
        /// <summary>
        /// Regenerates help content by scanning all slash commands via reflection.
        /// Content is stored in-memory only (no file persistence).
        /// </summary>
        public void RegenerateHelpContent()
        {
            try
            {
                _logger.LogInformation("Regenerating help content from slash commands...");

                var commands = ScanAllSlashCommands();
                var categories = OrganizeCommandsIntoCategories(commands);

                var helpContent = new HelpContent
                {
                    Categories = categories,
                    PermissionBadges = new Dictionary<string, string?>
                    {
                        { "public", null },
                        { "moderator", "🛡️" },
                        { "administrator", "👑" },
                        { "manage_messages", "🛡️" },
                        { "owner", "🔒" }
                    },
                    Metadata = new HelpMetadata
                    {
                        Version = "1.0.0",
                        LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                        TotalCommands = commands.Count
                    }
                };

                // Update in-memory content only (no file persistence)
                RefreshHelpContent(helpContent);

                _logger.LogInformation("Help content regenerated: {Count} commands in {Categories} categories",
                    commands.Count, categories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error regenerating help content");
            }
        }

        private List<ScannedCommand> ScanAllSlashCommands()
        {
            var commandInfos = new List<ScannedCommand>();
            var assembly = Assembly.GetExecutingAssembly();

            var interactionModules = assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(InteractionModuleBase<ShardedInteractionContext>)))
                .ToList();

            foreach (var moduleType in interactionModules)
            {
                // Compose the group prefix from the module AND any declaring modules —
                // nested [Group] classes (e.g. keys → board) must produce "keys board",
                // not just "board".
                var groupParts = new List<string>();
                for (var t = moduleType; t != null; t = t.DeclaringType)
                {
                    var ga = t.GetCustomAttribute<GroupAttribute>();
                    if (!string.IsNullOrEmpty(ga?.Name)) groupParts.Insert(0, ga!.Name);
                }
                var groupPrefix = groupParts.Count > 0 ? string.Join(" ", groupParts) : null;

                var methods = moduleType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

                foreach (var method in methods)
                {
                    var slashCommandAttr = method.GetCustomAttribute<SlashCommandAttribute>();
                    if (slashCommandAttr == null) continue;

                    var requireOwnerAttr = method.GetCustomAttribute<RequireOwnerAttribute>();
                    var requireUserPermAttr = method.GetCustomAttribute<RequireUserPermissionAttribute>();
                    var requireBotPermAttr = method.GetCustomAttribute<RequireBotPermissionAttribute>();

                    var permission = "public";
                    string? badge = null;

                    if (requireOwnerAttr != null)
                    {
                        permission = "owner";
                        badge = "🔒";
                    }
                    else if (requireUserPermAttr != null)
                    {
                        if (requireUserPermAttr.GuildPermission.HasValue)
                        {
                            var perm = requireUserPermAttr.GuildPermission.Value;
                            if (perm == GuildPermission.Administrator)
                            {
                                permission = "administrator";
                                badge = "👑";
                            }
                            else if (perm == GuildPermission.KickMembers || perm == GuildPermission.BanMembers)
                            {
                                permission = "moderator";
                                badge = "🛡️";
                            }
                            else if (perm == GuildPermission.ManageMessages)
                            {
                                permission = "manage_messages";
                                badge = "🛡️";
                            }
                        }
                    }
                    else if (requireBotPermAttr != null && requireBotPermAttr.GuildPermission.HasValue)
                    {
                        var perm = requireBotPermAttr.GuildPermission.Value;
                        if (perm == GuildPermission.ManageMessages)
                        {
                            permission = "manage_messages";
                            badge = "🛡️";
                        }
                    }

                    // Scan method parameters
                    var parameters = new List<ScannedParameter>();
                    foreach (var param in method.GetParameters())
                    {
                        // Skip special Discord.NET injected parameters
                        if (param.ParameterType == typeof(ShardedInteractionContext)) continue;

                        var summaryAttr = param.GetCustomAttribute<SummaryAttribute>();

                        var paramType = GetFriendlyTypeName(param.ParameterType);
                        var isRequired = !param.HasDefaultValue;

                        // Get choices if any
                        var choices = new List<string>();
                        if (param.ParameterType.IsEnum)
                        {
                            choices = Enum.GetNames(param.ParameterType).ToList();
                        }

                        parameters.Add(new ScannedParameter
                        {
                            Name = param.Name ?? "param",
                            Description = summaryAttr?.Description ?? "",
                            Type = paramType,
                            Required = isRequired,
                            Choices = choices
                        });
                    }

                    // Build full command name with group prefix if present
                    var commandName = string.IsNullOrEmpty(groupPrefix)
                        ? slashCommandAttr.Name
                        : $"{groupPrefix} {slashCommandAttr.Name}";

                    commandInfos.Add(new ScannedCommand
                    {
                        Name = commandName,
                        Description = slashCommandAttr.Description,
                        Permission = permission,
                        PermissionBadge = badge,
                        ModuleName = moduleType.Namespace ?? "",
                        ModuleTypeName = moduleType.Name,
                        Parameters = parameters
                    });
                }
            }

            return commandInfos;
        }

        private static string GetFriendlyTypeName(Type type)
        {
            // Handle nullable types
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) type = underlying;

            return type.Name switch
            {
                "String" => "text",
                "Int32" or "Int64" => "number",
                "Double" or "Single" or "Decimal" => "number",
                "Boolean" => "true/false",
                "IUser" or "SocketUser" or "IGuildUser" or "SocketGuildUser" => "user",
                "IChannel" or "SocketChannel" or "ITextChannel" or "SocketTextChannel" => "channel",
                "IRole" or "SocketRole" => "role",
                _ when type.IsEnum => "choice",
                _ => type.Name.ToLower()
            };
        }

        private List<HelpCategory> OrganizeCommandsIntoCategories(List<ScannedCommand> commands)
        {
            var categories = new List<HelpCategory>();

            // WoW Main commands - all Wow modules except Classic/Vanilla/Admin (listed first)
            var classicModules = new[] { "WowClassicInteract", "WowVanillaInteract", "CharClassicCommands" };
            var nonRetailModules = new[] { "WowAdminInteract", "CharacterResolver" };
            var wowMainCommands = commands
                .Where(c => c.ModuleName.Contains("Wow") &&
                           !classicModules.Contains(c.ModuleTypeName) &&
                           !nonRetailModules.Contains(c.ModuleTypeName) &&
                           c.Permission != "owner")
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (wowMainCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "wow_main",
                    Name = "World of Warcraft",
                    Emoji = "⚔️",
                    Description = "Character lookups, guild info, Mythic+, mounts, PvP, and more",
                    PermissionLevel = "public",
                    Commands = wowMainCommands
                });
            }

            // WoW Classic & Vanilla
            var wowClassicCommands = commands
                .Where(c => c.ModuleName.Contains("Wow") &&
                           classicModules.Contains(c.ModuleTypeName))
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (wowClassicCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "wow_classic",
                    Name = "WoW Classic & Vanilla",
                    Emoji = "🏛️",
                    Description = "Classic and Vanilla WoW guild management and logs",
                    PermissionLevel = "public",
                    Commands = wowClassicCommands
                });
            }

            // Moderation commands
            var moderationCommands = commands
                .Where(c => c.ModuleName.Contains("Admin") &&
                           (c.ModuleTypeName == "Admin" || c.ModuleTypeName == "DiscordHelpers") &&
                           c.Permission != "owner")
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (moderationCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "moderation",
                    Name = "Moderation & Admin",
                    Emoji = "🛡️",
                    Description = "Server moderation and administrative tools",
                    PermissionLevel = "moderator",
                    Commands = moderationCommands
                });
            }

            // Away System
            var awayCommands = commands
                .Where(c => c.ModuleName.Contains("Away"))
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (awayCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "away_system",
                    Name = "Away System",
                    Emoji = "💤",
                    Description = "Set yourself as away and auto-respond to mentions",
                    PermissionLevel = "public",
                    Commands = awayCommands
                });
            }

            // Fun & Utility
            var funCommands = commands
                .Where(c => (c.ModuleName.Contains("Fun") || c.ModuleName.Contains("YouTube") ||
                            c.ModuleName.Contains("Misc")) &&
                           !c.Name.Contains("help") &&
                           !c.Name.Contains("regenerate") &&
                           c.Permission == "public")
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (funCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "fun_utility",
                    Name = "Fun & Utility",
                    Emoji = "🎮",
                    Description = "Fun commands and bot utilities",
                    PermissionLevel = "public",
                    Commands = funCommands
                });
            }

            // Crafting
            var craftingCommands = commands
                .Where(c => c.ModuleName.Contains("Crafting") &&
                           c.ModuleTypeName == "CraftCommands")
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (craftingCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "crafting",
                    Name = "Crafting",
                    Emoji = "\u2692\uFE0F",
                    Description = "Request crafted items and manage crafting orders",
                    PermissionLevel = "public",
                    Commands = craftingCommands
                });
            }

            // Owner Only commands are intentionally excluded from help
            // They should not be visible to regular users

            // Polls
            var pollCommands = commands
                .Where(c => c.ModuleName.Contains("Poll"))
                .Select(ToHelpCommand)
                .OrderBy(c => c.Name)
                .ToList();

            if (pollCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "polls",
                    Name = "Polls",
                    Emoji = "📊",
                    Description = "Create and manage polls",
                    PermissionLevel = "public",
                    Commands = pollCommands
                });
            }

            return categories;
        }

        private static HelpCommand ToHelpCommand(ScannedCommand c)
        {
            // Build usage string with parameters
            var usage = $"/{c.Name}";
            if (c.Parameters.Any())
            {
                var paramParts = c.Parameters.Select(p =>
                    p.Required ? $"<{p.Name}>" : $"[{p.Name}]");
                usage = $"/{c.Name} {string.Join(" ", paramParts)}";
            }

            return new HelpCommand
            {
                Name = c.Name,
                Description = c.Description,
                Usage = usage,
                Example = null,
                Permission = c.Permission,
                PermissionBadge = c.PermissionBadge,
                Parameters = c.Parameters.Select(p => new HelpParameter
                {
                    Name = p.Name,
                    Description = p.Description,
                    Type = p.Type,
                    Required = p.Required,
                    Choices = p.Choices.Any() ? p.Choices : null
                }).ToList()
            };
        }

        private class ScannedCommand
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Permission { get; set; } = "public";
            public string? PermissionBadge { get; set; }
            public string ModuleName { get; set; } = "";
            public string ModuleTypeName { get; set; } = "";
            public List<ScannedParameter> Parameters { get; set; } = new();
        }

        private class ScannedParameter
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Type { get; set; } = "text";
            public bool Required { get; set; }
            public List<string> Choices { get; set; } = new();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _autoRefreshTimer?.Dispose();
        }
    }
}
