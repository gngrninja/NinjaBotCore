using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Help;

namespace NinjaBotCore.Modules.Interactions.Misc
{
    public class HelpCommands : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly ILogger<HelpCommands> _logger;
        private readonly IConfigurationRoot _config;
        private static HelpContent _helpContent;
        private static readonly object _lockObject = new object();

        public HelpCommands(ILogger<HelpCommands> logger, IConfigurationRoot config)
        {
            _logger = logger;
            _config = config;

            // Load help content if not already loaded
            if (_helpContent == null)
            {
                lock (_lockObject)
                {
                    if (_helpContent == null)
                    {
                        LoadHelpContent();
                    }
                }
            }
        }

        [SlashCommand("help", "Browse NinjaBot commands by category")]
        public async Task Help()
        {
            try
            {
                if (_helpContent == null)
                {
                    await RespondAsync("Help system is not available. Please contact the bot owner.", ephemeral: true);
                    return;
                }

                var embed = BuildWelcomeEmbed();
                var components = BuildCategorySelectMenu(Context);

                await RespondAsync(embed: embed.Build(), components: components.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Help command");
                await RespondAsync("An error occurred while loading help. Please try again.", ephemeral: true);
            }
        }

        [ComponentInteraction("help_category_select")]
        public async Task HandleCategorySelection(string[] selections)
        {
            try
            {
                await DeferAsync();

                string categoryId = selections[0];

                if (categoryId == "welcome")
                {
                    var welcomeEmbed = BuildWelcomeEmbed();
                    var components = BuildCategorySelectMenu(Context);

                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = welcomeEmbed.Build();
                        msg.Components = components.Build();
                    });
                    return;
                }

                var embed = BuildCategoryEmbed(categoryId, Context);
                var selectMenu = BuildCategorySelectMenu(Context);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = selectMenu.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling category selection");
            }
        }

        private void LoadHelpContent()
        {
            try
            {
                var basePath = Directory.GetCurrentDirectory();
                var helpPath = Path.Combine(basePath, "help-commands.json");

                if (!File.Exists(helpPath))
                {
                    _logger.LogError("help-commands.json not found at {Path}", helpPath);
                    return;
                }

                var json = File.ReadAllText(helpPath);
                _helpContent = JsonSerializer.Deserialize<HelpContent>(json);
                _logger.LogInformation("Loaded {Count} help categories with {Commands} total commands",
                    _helpContent.Categories.Count,
                    _helpContent.Metadata.TotalCommands);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading help-commands.json");
            }
        }

        private EmbedBuilder BuildWelcomeEmbed()
        {
            var embed = new EmbedBuilder();
            embed.Title = "🟣 NinjaBot Help System";
            embed.Description = "Select a category below to view available commands.\n\n" +
                "Commands are filtered based on your permissions.";
            embed.WithColor(new Color(0, 0, 255));
            embed.ThumbnailUrl = Context.Client.CurrentUser.GetAvatarUrl();
            embed.WithFooter($"NinjaBot v{_helpContent?.Metadata?.Version ?? "1.0.0"}");
            embed.WithCurrentTimestamp();

            return embed;
        }

        private EmbedBuilder BuildCategoryEmbed(string categoryId, ShardedInteractionContext context)
        {
            var category = _helpContent.Categories.FirstOrDefault(c => c.Id == categoryId);

            if (category == null)
            {
                return BuildWelcomeEmbed();
            }

            // Filter commands based on user permissions
            var user = context.User as SocketGuildUser;
            var filteredCommands = category.Commands
                .Where(cmd => HasPermissionForCommand(user, cmd.Permission))
                .ToList();

            var embed = new EmbedBuilder();
            embed.Title = $"{category.Emoji} {category.Name}";
            embed.Description = category.Description;

            // Color based on permission level
            embed.WithColor(category.PermissionLevel switch
            {
                "owner" => new Color(255, 0, 0),      // Red
                "moderator" => new Color(255, 165, 0), // Orange
                _ => new Color(0, 0, 255)              // Blue
            });

            // Add commands as fields (max 25)
            int commandCount = 0;
            foreach (var cmd in filteredCommands.Take(25))
            {
                var badge = !string.IsNullOrEmpty(cmd.PermissionBadge) ? $"{cmd.PermissionBadge} " : "";
                var fieldValue = new StringBuilder();
                fieldValue.AppendLine(cmd.Description);
                fieldValue.AppendLine($"**Usage:** `{cmd.Usage}`");

                if (!string.IsNullOrEmpty(cmd.Example))
                {
                    fieldValue.AppendLine($"**Example:** `{cmd.Example}`");
                }

                embed.AddField($"{badge}/{cmd.Name}", fieldValue.ToString(), inline: false);
                commandCount++;
            }

            embed.WithFooter($"{commandCount} command{(commandCount != 1 ? "s" : "")} available");
            embed.WithCurrentTimestamp();

            return embed;
        }

        private ComponentBuilder BuildCategorySelectMenu(ShardedInteractionContext context)
        {
            var builder = new ComponentBuilder();
            var selectMenu = new SelectMenuBuilder()
                .WithPlaceholder("📚 Select a command category...")
                .WithCustomId("help_category_select")
                .WithMinValues(1)
                .WithMaxValues(1);

            // Add welcome option
            selectMenu.AddOption("🏠 Home", "welcome", "Return to help home");

            // Filter categories based on user permissions
            var visibleCategories = FilterCategoriesByPermission(context);

            foreach (var category in visibleCategories)
            {
                var description = category.Description;
                if (description.Length > 100)
                {
                    description = description.Substring(0, 97) + "...";
                }

                selectMenu.AddOption(
                    label: $"{category.Emoji} {category.Name}",
                    value: category.Id,
                    description: description
                );
            }

            builder.WithSelectMenu(selectMenu);
            return builder;
        }

        private List<HelpCategory> FilterCategoriesByPermission(ShardedInteractionContext context)
        {
            var user = context.User as SocketGuildUser;
            var categories = new List<HelpCategory>();

            foreach (var category in _helpContent.Categories)
            {
                // Owner sees everything
                if (IsOwner(context.User.Id))
                {
                    categories.Add(category);
                    continue;
                }

                // Check if user has permission for ANY command in this category
                var hasAccessToAnyCommand = category.Commands.Any(cmd =>
                    HasPermissionForCommand(user, cmd.Permission));

                if (hasAccessToAnyCommand)
                {
                    categories.Add(category);
                }
            }

            return categories;
        }

        private bool HasPermissionForCommand(SocketGuildUser user, string permission)
        {
            if (user == null) return permission == "public";

            return permission switch
            {
                "public" => true,
                "owner" => IsOwner(user.Id),
                "administrator" => user.GuildPermissions.Administrator,
                "moderator" => user.GuildPermissions.KickMembers ||
                               user.GuildPermissions.BanMembers ||
                               user.GuildPermissions.Administrator,
                "manage_messages" => user.GuildPermissions.ManageMessages ||
                                    user.GuildPermissions.Administrator,
                _ => false
            };
        }

        private bool IsOwner(ulong userId)
        {
            var ownerId = _config.GetValue<ulong>("OwnerId");
            return userId == ownerId;
        }

        [SlashCommand("regenerate-help", "Regenerate help-commands.json by scanning all slash commands")]
        [RequireOwner]
        public async Task RegenerateHelp()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var commands = ScanAllSlashCommands();
                var categories = OrganizeCommandsIntoCategories(commands);

                var helpContent = new HelpContent
                {
                    Categories = categories,
                    PermissionBadges = new Dictionary<string, string>
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

                var basePath = Directory.GetCurrentDirectory();
                var helpPath = Path.Combine(basePath, "help-commands.json");

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(helpContent, options);
                await File.WriteAllTextAsync(helpPath, json);

                // Reload the help content
                lock (_lockObject)
                {
                    _helpContent = helpContent;
                }

                _logger.LogInformation("Regenerated help-commands.json with {Count} commands in {Categories} categories",
                    commands.Count, categories.Count);

                await FollowupAsync(
                    $"✅ Successfully regenerated help-commands.json!\n\n" +
                    $"**Commands found:** {commands.Count}\n" +
                    $"**Categories:** {categories.Count}\n" +
                    $"**File location:** `{helpPath}`",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error regenerating help file");
                await FollowupAsync($"❌ Error regenerating help file: {ex.Message}", ephemeral: true);
            }
        }

        private List<CommandInfo> ScanAllSlashCommands()
        {
            var commandInfos = new List<CommandInfo>();
            var assembly = Assembly.GetExecutingAssembly();

            var interactionModules = assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(InteractionModuleBase<ShardedInteractionContext>)))
                .ToList();

            foreach (var moduleType in interactionModules)
            {
                var methods = moduleType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

                foreach (var method in methods)
                {
                    var slashCommandAttr = method.GetCustomAttribute<SlashCommandAttribute>();
                    if (slashCommandAttr == null) continue;

                    var requireOwnerAttr = method.GetCustomAttribute<RequireOwnerAttribute>();
                    var requireUserPermAttr = method.GetCustomAttribute<RequireUserPermissionAttribute>();
                    var requireBotPermAttr = method.GetCustomAttribute<RequireBotPermissionAttribute>();
                    var defaultMemberPermAttr = method.GetCustomAttribute<DefaultMemberPermissionsAttribute>();

                    var permission = "public";
                    var badge = (string)null;

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

                    commandInfos.Add(new CommandInfo
                    {
                        Name = slashCommandAttr.Name,
                        Description = slashCommandAttr.Description,
                        Permission = permission,
                        PermissionBadge = badge,
                        ModuleName = moduleType.Namespace,
                        ModuleTypeName = moduleType.Name
                    });
                }
            }

            return commandInfos;
        }

        private List<HelpCategory> OrganizeCommandsIntoCategories(List<CommandInfo> commands)
        {
            var categories = new List<HelpCategory>();

            // WoW Main commands
            var wowMainCommands = commands
                .Where(c => c.ModuleName.Contains("Wow") &&
                           c.ModuleTypeName == "WowInteract")
                .Select(c => new HelpCommand
                {
                    Name = c.Name,
                    Description = c.Description,
                    Usage = $"/{c.Name}",
                    Example = null,
                    Permission = c.Permission,
                    PermissionBadge = c.PermissionBadge
                })
                .OrderBy(c => c.Name)
                .ToList();

            if (wowMainCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "wow_main",
                    Name = "World of Warcraft - Main",
                    Emoji = "⚔️",
                    Description = "Character lookups, guild info, and Mythic+ tracking",
                    PermissionLevel = "public",
                    Commands = wowMainCommands
                });
            }

            // WoW Classic & Vanilla
            var wowClassicCommands = commands
                .Where(c => c.ModuleName.Contains("Wow") &&
                           (c.ModuleTypeName.Contains("Classic") || c.ModuleTypeName.Contains("Vanilla")))
                .Select(c => new HelpCommand
                {
                    Name = c.Name,
                    Description = c.Description,
                    Usage = $"/{c.Name}",
                    Example = null,
                    Permission = c.Permission,
                    PermissionBadge = c.PermissionBadge
                })
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
                .Select(c => new HelpCommand
                {
                    Name = c.Name,
                    Description = c.Description,
                    Usage = $"/{c.Name}",
                    Example = null,
                    Permission = c.Permission,
                    PermissionBadge = c.PermissionBadge
                })
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
                .Select(c => new HelpCommand
                {
                    Name = c.Name,
                    Description = c.Description,
                    Usage = $"/{c.Name}",
                    Example = null,
                    Permission = c.Permission,
                    PermissionBadge = c.PermissionBadge
                })
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
                           c.Permission == "public")
                .Select(c => new HelpCommand
                {
                    Name = c.Name,
                    Description = c.Description,
                    Usage = $"/{c.Name}",
                    Example = null,
                    Permission = c.Permission,
                    PermissionBadge = c.PermissionBadge
                })
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

            // Owner Only commands
            var ownerCommands = commands
                .Where(c => c.Permission == "owner" && !c.Name.Contains("regenerate-help"))
                .Select(c => new HelpCommand
                {
                    Name = c.Name,
                    Description = c.Description,
                    Usage = $"/{c.Name}",
                    Example = null,
                    Permission = c.Permission,
                    PermissionBadge = c.PermissionBadge
                })
                .OrderBy(c => c.Name)
                .ToList();

            if (ownerCommands.Any())
            {
                categories.Add(new HelpCategory
                {
                    Id = "owner_only",
                    Name = "Owner Only",
                    Emoji = "🔒",
                    Description = "Bot owner administrative commands",
                    PermissionLevel = "owner",
                    Commands = ownerCommands
                });
            }

            return categories;
        }

        private class CommandInfo
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string Permission { get; set; }
            public string PermissionBadge { get; set; }
            public string ModuleName { get; set; }
            public string ModuleTypeName { get; set; }
        }
    }
}
