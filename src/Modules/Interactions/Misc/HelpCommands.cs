using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Help;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Misc
{
    public class HelpCommands : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly ILogger<HelpCommands> _logger;
        private readonly IConfigurationRoot _config;
        private readonly HelpContentProvider _helpProvider;

        public HelpCommands(
            ILogger<HelpCommands> logger,
            IConfigurationRoot config,
            HelpContentProvider helpProvider)
        {
            _logger = logger;
            _config = config;
            _helpProvider = helpProvider;
        }

        private HelpContent? HelpContent => _helpProvider.GetHelpContent();

        [SlashCommand("help", "Browse NinjaBot commands by category")]
        public async Task Help()
        {
            try
            {
                if (HelpContent == null)
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

        private EmbedBuilder BuildWelcomeEmbed()
        {
            var embed = new EmbedBuilder();
            embed.Title = "🟣 NinjaBot Help System";
            embed.Description = "Select a category below to view available commands.\n\n" +
                "Commands are filtered based on your permissions.";
            embed.WithColor(new Color(0, 0, 255));
            embed.ThumbnailUrl = Context.Client.CurrentUser.GetAvatarUrl();
            embed.WithFooter($"NinjaBot v{HelpContent?.Metadata?.Version ?? "1.0.0"}");
            embed.WithCurrentTimestamp();

            return embed;
        }

        private EmbedBuilder BuildCategoryEmbed(string categoryId, ShardedInteractionContext context)
        {
            var category = HelpContent.Categories.FirstOrDefault(c => c.Id == categoryId);

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

            foreach (var category in HelpContent.Categories)
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
                _helpProvider.RegenerateHelpContent();

                var content = HelpContent;
                var commandCount = content?.Metadata?.TotalCommands ?? 0;
                var categoryCount = content?.Categories?.Count ?? 0;

                await FollowupAsync(
                    $"✅ Successfully regenerated help-commands.json!\n\n" +
                    $"**Commands found:** {commandCount}\n" +
                    $"**Categories:** {categoryCount}\n" +
                    $"**Last updated:** {content?.Metadata?.LastUpdated ?? "unknown"}",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error regenerating help file");
                await FollowupAsync($"❌ Error regenerating help file: {ex.Message}", ephemeral: true);
            }
        }

    }
}
