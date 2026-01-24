using Discord;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the achievements embed for character profiles
    /// </summary>
    public static class CharAchievementsView
    {
        private const int AchievementsPerPage = 10;

        /// <summary>
        /// Build the achievements embed with paginated list
        /// </summary>
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            ArmoryAchievementsSummary achievements,
            ArmoryMedia armoryMedia,
            int page = 0)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = $"🏆 Achievements - {charInfo.Name}";
            embed.WithColor(new Color(255, 215, 0)); // Gold color for achievements

            if (achievements == null || achievements.RecentEvents == null || !achievements.RecentEvents.Any())
            {
                sb.AppendLine("No recent achievements found.");
                embed.Description = sb.ToString();
                return embed;
            }

            // Summary stats
            sb.AppendLine($"**Total Points:** {achievements.TotalPoints:N0}");
            sb.AppendLine($"**Total Achievements:** {achievements.TotalQuantity:N0}");
            sb.AppendLine();

            // Recent achievements (paginated)
            var recentAchievements = achievements.RecentEvents
                .OrderByDescending(a => a.Timestamp)
                .ToList();

            var totalPages = (recentAchievements.Count + AchievementsPerPage - 1) / AchievementsPerPage;
            page = Math.Clamp(page, 0, Math.Max(0, totalPages - 1));

            var pageAchievements = recentAchievements
                .Skip(page * AchievementsPerPage)
                .Take(AchievementsPerPage)
                .ToList();

            sb.AppendLine("**Recent Achievements**");
            sb.AppendLine("```");

            foreach (var ach in pageAchievements)
            {
                var date = DateTimeOffset.FromUnixTimeMilliseconds(ach.Timestamp);
                var dateStr = date.ToString("MMM dd");
                var name = ach.Achievement?.Name ?? "Unknown";

                // Truncate name if too long
                if (name.Length > 40)
                {
                    name = name.Substring(0, 37) + "...";
                }

                sb.AppendLine($"{dateStr}  {name}");
            }

            sb.AppendLine("```");

            // Category progress (if available)
            if (achievements.CategoryProgress?.Any() == true)
            {
                sb.AppendLine();
                sb.AppendLine("**Category Progress**");

                var topCategories = achievements.CategoryProgress
                    .Where(c => c.Category?.Name != null)
                    .OrderByDescending(c => c.Points)
                    .Take(5)
                    .ToList();

                foreach (var cat in topCategories)
                {
                    sb.AppendLine($"• {cat.Category.Name}: {cat.Quantity} ({cat.Points:N0} pts)");
                }
            }

            embed.Description = sb.ToString();

            // Thumbnail
            var thumbnailUrl = armoryMedia?.Assets?.FirstOrDefault(a => a.Key == "avatar")?.Value;
            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                embed.ThumbnailUrl = thumbnailUrl;
            }

            // Footer with pagination info
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"Page {page + 1} of {totalPages} | {charInfo.Realm} ({charInfo.Region.ToUpper()})"
            };

            return embed;
        }

        /// <summary>
        /// Build the component buttons for the achievements view
        /// </summary>
        public static ComponentBuilder BuildComponents(
            ulong userId,
            CharacterInfo charInfo,
            int currentPage,
            int totalPages,
            bool isAlreadySaved = false)
        {
            var charParam = $"{charInfo.Name}~{charInfo.Realm}~{charInfo.Region}";

            // Use detail view components but add pagination
            var builder = CharOverviewView.BuildDetailViewComponents(userId, charInfo, "achievements", isAlreadySaved);

            // Add pagination buttons if needed (row 2 may already have action buttons, so use row 3)
            if (totalPages > 1)
            {
                builder.WithButton(
                    label: "◀ Prev",
                    customId: $"char_achievements_page~{userId}~{charParam}~{currentPage - 1}",
                    style: ButtonStyle.Secondary,
                    disabled: currentPage <= 0,
                    row: 2);

                builder.WithButton(
                    label: $"Page {currentPage + 1}/{totalPages}",
                    customId: "char_achievements_page_info",
                    style: ButtonStyle.Secondary,
                    disabled: true,
                    row: 2);

                builder.WithButton(
                    label: "Next ▶",
                    customId: $"char_achievements_page~{userId}~{charParam}~{currentPage + 1}",
                    style: ButtonStyle.Secondary,
                    disabled: currentPage >= totalPages - 1,
                    row: 2);
            }

            return builder;
        }

        /// <summary>
        /// Calculate total pages for achievements
        /// </summary>
        public static int GetTotalPages(ArmoryAchievementsSummary achievements)
        {
            if (achievements?.RecentEvents == null || !achievements.RecentEvents.Any())
                return 1;

            return (achievements.RecentEvents.Count + AchievementsPerPage - 1) / AchievementsPerPage;
        }
    }
}
