using Discord;
using NinjaBotCore.Models.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    public static class RosterView
    {
        public static EmbedBuilder Build(
            ArmoryGuildInfo guild,
            List<RosterMemberData> members,
            int page,
            int pageSize,
            RosterSortOption sort,
            bool hasMPlusData = false)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            var guildName = guild?.Name ?? "Unknown Guild";
            var realmName = guild?.Realm?.Name ?? "Unknown Realm";
            var faction = guild?.Faction?.Name ?? "Unknown";

            embed.Title = $"{guildName} - {realmName}";
            embed.WithColor(faction?.ToLower() == "horde" ? new Color(139, 0, 0) : new Color(0, 71, 171));

            var totalPages = (members.Count + pageSize - 1) / pageSize;
            var pageMembers = members.Skip(page * pageSize).Take(pageSize).ToList();

            sb.AppendLine($"**Members:** {members.Count} | **Sort:** {GetSortLabel(sort)}");
            if (!hasMPlusData)
            {
                sb.AppendLine("*M+ scores not loaded - click \"Refresh M+\" to load*");
            }
            sb.AppendLine();

            // Header
            sb.AppendLine("```");
            sb.AppendLine($"{"Rank",-6} {"Name",-16} {"Class",-10} {"M+",6}");
            sb.AppendLine(new string('-', 42));

            foreach (var member in pageMembers)
            {
                var className = GetClassShortName(member.ClassId);
                var rankStr = GetRankName(member.Rank);
                var nameStr = member.Name.Length > 15 ? member.Name.Substring(0, 15) : member.Name;
                var mplusStr = member.MythicPlusScore > 0 ? member.MythicPlusScore.ToString("F0") : "-";

                sb.AppendLine($"{rankStr,-6} {nameStr,-16} {className,-10} {mplusStr,6}");
            }

            sb.AppendLine("```");

            // Stats summary
            var avgMplus = members.Where(m => m.MythicPlusScore > 0).Select(m => m.MythicPlusScore).DefaultIfEmpty(0).Average();
            var highestMplus = members.MaxBy(m => m.MythicPlusScore);

            sb.AppendLine();
            if (avgMplus > 0)
            {
                sb.AppendLine($"**Avg M+:** {avgMplus:F0}");
            }
            if (highestMplus != null && highestMplus.MythicPlusScore > 0)
            {
                sb.AppendLine($"**Top M+:** {highestMplus.Name} ({highestMplus.MythicPlusScore:F0})");
            }

            embed.Description = sb.ToString();

            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"Page {page + 1} of {totalPages} | Data cached for 60 min"
            };

            return embed;
        }

        public static ComponentBuilder BuildComponents(
            ulong userId,
            string guildParam,
            int currentPage,
            int totalPages,
            RosterSortOption currentSort,
            bool hasMPlusData = false)
        {
            var builder = new ComponentBuilder();

            // Row 0: Pagination buttons
            builder.WithButton(
                label: "◀ Prev",
                customId: $"roster_page~{userId}~{guildParam}~{currentPage - 1}~{currentSort}",
                style: ButtonStyle.Secondary,
                disabled: currentPage <= 0,
                row: 0);

            builder.WithButton(
                label: $"Page {currentPage + 1}/{totalPages}",
                customId: "roster_page_info",
                style: ButtonStyle.Secondary,
                disabled: true,
                row: 0);

            builder.WithButton(
                label: "Next ▶",
                customId: $"roster_page~{userId}~{guildParam}~{currentPage + 1}~{currentSort}",
                style: ButtonStyle.Secondary,
                disabled: currentPage >= totalPages - 1,
                row: 0);

            // Refresh M+ button if no M+ data
            if (!hasMPlusData)
            {
                builder.WithButton(
                    label: "🔄 Refresh M+",
                    customId: $"roster_refresh_mplus~{userId}~{guildParam}",
                    style: ButtonStyle.Primary,
                    row: 0);
            }

            // Row 1: Sort dropdown
            var sortOptions = new List<SelectMenuOptionBuilder>
            {
                new SelectMenuOptionBuilder()
                    .WithLabel("M+ Score (High to Low)")
                    .WithValue("MythicPlus")
                    .WithDescription("Sort by Mythic+ rating")
                    .WithDefault(currentSort == RosterSortOption.MythicPlus),
                new SelectMenuOptionBuilder()
                    .WithLabel("Name (A-Z)")
                    .WithValue("Name")
                    .WithDescription("Sort alphabetically by name")
                    .WithDefault(currentSort == RosterSortOption.Name),
                new SelectMenuOptionBuilder()
                    .WithLabel("Rank")
                    .WithValue("Rank")
                    .WithDescription("Sort by guild rank")
                    .WithDefault(currentSort == RosterSortOption.Rank)
            };

            var sortMenu = new SelectMenuBuilder()
                .WithCustomId($"roster_sort~{userId}~{guildParam}")
                .WithPlaceholder("Sort by...")
                .WithOptions(sortOptions)
                .WithMinValues(1)
                .WithMaxValues(1);

            builder.WithSelectMenu(sortMenu, 1);

            return builder;
        }

        private static string GetSortLabel(RosterSortOption sort)
        {
            return sort switch
            {
                RosterSortOption.MythicPlus => "M+ Score",
                RosterSortOption.ItemLevel => "Item Level",
                RosterSortOption.Name => "Name",
                RosterSortOption.Rank => "Rank",
                _ => "Unknown"
            };
        }

        private static string GetClassShortName(int classId)
        {
            return classId switch
            {
                1 => "Warrior",
                2 => "Paladin",
                3 => "Hunter",
                4 => "Rogue",
                5 => "Priest",
                6 => "DK",
                7 => "Shaman",
                8 => "Mage",
                9 => "Warlock",
                10 => "Monk",
                11 => "Druid",
                12 => "DH",
                13 => "Evoker",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Returns display name for guild rank.
        /// Note: Blizzard API only returns rank numbers, not custom names.
        /// "The guild roster endpoint will return the rank as an integer field only.
        /// It is not currently possible to get the same names you see in game."
        /// </summary>
        private static string GetRankName(int rank)
        {
            return rank == 0 ? "GM" : rank.ToString();
        }
    }
}
