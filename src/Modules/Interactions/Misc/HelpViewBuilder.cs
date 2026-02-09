using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NinjaBotCore.Models.Help;

namespace NinjaBotCore.Modules.Interactions.Misc
{
    public static class HelpViewBuilder
    {
        public const int CommandsPerPage = 5;

        public static EmbedBuilder BuildWelcomeEmbed(string? avatarUrl, string? version)
        {
            var embed = new EmbedBuilder();
            embed.Title = "🟣 NinjaBot Help System";
            embed.Description = "Select a category below to view available commands.\n\n" +
                "Commands are filtered based on your permissions.";
            embed.WithColor(new Color(0, 0, 255));

            if (!string.IsNullOrEmpty(avatarUrl))
                embed.ThumbnailUrl = avatarUrl;

            embed.WithFooter($"NinjaBot v{version ?? "1.0.0"}");
            embed.WithCurrentTimestamp();

            return embed;
        }

        public static EmbedBuilder BuildCategoryEmbed(
            HelpContent? content, string categoryId, int currentPage,
            string? avatarUrl, string? version,
            Func<string, bool> hasPermission)
        {
            if (content?.Categories == null)
                return BuildWelcomeEmbed(avatarUrl, version);

            var category = content.Categories.FirstOrDefault(c => c.Id == categoryId);

            if (category == null)
                return BuildWelcomeEmbed(avatarUrl, version);

            var filteredCommands = category.Commands
                .Where(cmd => hasPermission(cmd.Permission))
                .ToList();

            var totalPages = Math.Max(1, (int)Math.Ceiling(filteredCommands.Count / (double)CommandsPerPage));
            currentPage = Math.Clamp(currentPage, 0, totalPages - 1);

            var pageCommands = filteredCommands
                .Skip(currentPage * CommandsPerPage)
                .Take(CommandsPerPage)
                .ToList();

            var embed = new EmbedBuilder();
            embed.Title = $"{category.Emoji} {category.Name}";
            embed.Description = category.Description;

            embed.WithColor(category.PermissionLevel switch
            {
                "owner" => new Color(255, 0, 0),
                "moderator" => new Color(255, 165, 0),
                _ => new Color(0, 0, 255)
            });

            foreach (var cmd in pageCommands)
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
            }

            embed.WithFooter($"Page {currentPage + 1}/{totalPages} - {filteredCommands.Count} command{(filteredCommands.Count != 1 ? "s" : "")} total");
            embed.WithCurrentTimestamp();

            return embed;
        }

        public static ComponentBuilder BuildCategoryComponents(
            HelpContent? content, string categoryId, int currentPage,
            ulong userId, bool isOwner, Func<string, bool> hasPermission)
        {
            var builder = BuildCategorySelectMenu(content, isOwner, hasPermission);

            if (content?.Categories == null)
                return builder;

            var category = content.Categories.FirstOrDefault(c => c.Id == categoryId);
            if (category == null)
                return builder;

            var filteredCount = category.Commands.Count(cmd => hasPermission(cmd.Permission));
            var totalPages = (int)Math.Ceiling(filteredCount / (double)CommandsPerPage);

            if (totalPages > 1)
            {
                currentPage = Math.Clamp(currentPage, 0, totalPages - 1);

                builder.WithButton("First", $"help_first~{userId}~{categoryId}~{currentPage}", ButtonStyle.Secondary, disabled: currentPage == 0, row: 1);
                builder.WithButton("Prev", $"help_prev~{userId}~{categoryId}~{currentPage}", ButtonStyle.Primary, disabled: currentPage == 0, row: 1);
                builder.WithButton($"Page {currentPage + 1}/{totalPages}", "help_page_info", ButtonStyle.Secondary, disabled: true, row: 1);
                builder.WithButton("Next", $"help_next~{userId}~{categoryId}~{currentPage}", ButtonStyle.Primary, disabled: currentPage >= totalPages - 1, row: 1);
                builder.WithButton("Last", $"help_last~{userId}~{categoryId}~{currentPage}~{totalPages}", ButtonStyle.Secondary, disabled: currentPage >= totalPages - 1, row: 1);
            }

            return builder;
        }

        public static ComponentBuilder BuildCategorySelectMenu(
            HelpContent? content, bool isOwner, Func<string, bool> hasPermission)
        {
            var builder = new ComponentBuilder();
            var selectMenu = new SelectMenuBuilder()
                .WithPlaceholder("📚 Select a command category...")
                .WithCustomId("help_category_select")
                .WithMinValues(1)
                .WithMaxValues(1);

            selectMenu.AddOption("🏠 Home", "welcome", "Return to help home");

            var visibleCategories = FilterCategoriesByPermission(content, isOwner, hasPermission);

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

        public static List<HelpCategory> FilterCategoriesByPermission(
            HelpContent? content, bool isOwner, Func<string, bool> hasPermission)
        {
            if (content?.Categories == null)
                return new List<HelpCategory>();

            var categories = new List<HelpCategory>();

            foreach (var category in content.Categories)
            {
                if (isOwner)
                {
                    categories.Add(category);
                    continue;
                }

                var hasAccessToAnyCommand = category.Commands.Any(cmd =>
                    hasPermission(cmd.Permission));

                if (hasAccessToAnyCommand)
                {
                    categories.Add(category);
                }
            }

            return categories;
        }
    }
}
