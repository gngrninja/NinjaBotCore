using Discord;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RosterViewTests
    {
        private static ArmoryGuildInfo CreateGuildInfo(string name = "Test Guild", string realm = "Test Realm", string faction = "Alliance")
        {
            return new ArmoryGuildInfo
            {
                Name = name,
                Realm = new ArmoryRealm { Name = realm, Slug = realm.ToLower().Replace(" ", "-") },
                Faction = new ArmoryType { Name = faction }
            };
        }

        private static List<RosterMemberData> CreateTestMembers(int count, bool withMPlusData = true)
        {
            var members = new List<RosterMemberData>();
            for (int i = 0; i < count; i++)
            {
                members.Add(new RosterMemberData
                {
                    Name = $"Player{i + 1}",
                    Realm = "test-realm",
                    Rank = i % 10,
                    Level = 70,
                    ClassId = (i % 13) + 1,
                    ItemLevel = 400 + i,
                    MythicPlusScore = withMPlusData ? 2000 - (i * 50) : 0
                });
            }
            return members;
        }

        [Fact]
        public void Build_GeneratesCorrectTitle_WithGuildAndRealm()
        {
            var guild = CreateGuildInfo("Awesome Guild", "Stormrage");
            var members = CreateTestMembers(5);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            Assert.Equal("Awesome Guild - Stormrage", embed.Title);
        }

        [Fact]
        public void Build_SetsColorRed_ForHordeFaction()
        {
            var guild = CreateGuildInfo(faction: "Horde");
            var members = CreateTestMembers(5);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            // Horde color is dark red (139, 0, 0)
            Assert.Equal(new Color(139, 0, 0), embed.Color);
        }

        [Fact]
        public void Build_SetsColorBlue_ForAllianceFaction()
        {
            var guild = CreateGuildInfo(faction: "Alliance");
            var members = CreateTestMembers(5);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            // Alliance color is dark blue (0, 71, 171)
            Assert.Equal(new Color(0, 71, 171), embed.Color);
        }

        [Fact]
        public void Build_PaginatesMembers_CorrectlyPerPage()
        {
            var guild = CreateGuildInfo();
            var members = CreateTestMembers(30); // 2 pages with pageSize 15

            // First page
            var embed1 = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);
            Assert.Contains("Page 1 of 2", embed1.Footer?.Text);

            // Second page
            var embed2 = RosterView.Build(guild, members, 1, 15, RosterSortOption.MythicPlus, true);
            Assert.Contains("Page 2 of 2", embed2.Footer?.Text);
        }

        [Fact]
        public void Build_IncludesAverageMplus_WhenAvailable()
        {
            var guild = CreateGuildInfo();
            var members = CreateTestMembers(5, withMPlusData: true);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            Assert.Contains("Avg M+", embed.Description);
        }

        [Fact]
        public void Build_IncludesTopMplus_Member()
        {
            var guild = CreateGuildInfo();
            var members = CreateTestMembers(5, withMPlusData: true);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            Assert.Contains("Top M+", embed.Description);
            Assert.Contains("Player1", embed.Description); // First player has highest score
        }

        [Fact]
        public void Build_ShowsMPlusNotLoadedMessage_WhenNoData()
        {
            var guild = CreateGuildInfo();
            var members = CreateTestMembers(5, withMPlusData: true);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, hasMPlusData: false);

            Assert.Contains("M+ scores not loaded", embed.Description);
        }

        [Fact]
        public void Build_ShowsMemberCount_InDescription()
        {
            var guild = CreateGuildInfo();
            var members = CreateTestMembers(25);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            Assert.Contains("Members:** 25", embed.Description);
        }

        [Fact]
        public void Build_ShowsSortOption_InDescription()
        {
            var guild = CreateGuildInfo();
            var members = CreateTestMembers(5);

            var embedMplus = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);
            Assert.Contains("Sort:** M+ Score", embedMplus.Description);

            var embedName = RosterView.Build(guild, members, 0, 15, RosterSortOption.Name, true);
            Assert.Contains("Sort:** Name", embedName.Description);

            var embedRank = RosterView.Build(guild, members, 0, 15, RosterSortOption.Rank, true);
            Assert.Contains("Sort:** Rank", embedRank.Description);
        }

        [Fact]
        public void BuildComponents_DisablesPrevButton_OnFirstPage()
        {
            var components = RosterView.BuildComponents(123456789, "guild|realm|us", 0, 5, RosterSortOption.MythicPlus);
            var built = components.Build();

            var actionRow = built.Components.First() as ActionRowComponent;
            Assert.NotNull(actionRow);
            var prevButton = actionRow.Components.First() as ButtonComponent;

            Assert.NotNull(prevButton);
            Assert.True(prevButton.IsDisabled);
        }

        [Fact]
        public void BuildComponents_DisablesNextButton_OnLastPage()
        {
            var components = RosterView.BuildComponents(123456789, "guild|realm|us", 4, 5, RosterSortOption.MythicPlus);
            var built = components.Build();

            var actionRow = built.Components.First() as ActionRowComponent;
            Assert.NotNull(actionRow);
            var nextButton = actionRow.Components.ToList()[2] as ButtonComponent;

            Assert.NotNull(nextButton);
            Assert.True(nextButton.IsDisabled);
        }

        [Fact]
        public void BuildComponents_ShowsRefreshMplusButton_WhenNoData()
        {
            var components = RosterView.BuildComponents(123456789, "guild|realm|us", 0, 5, RosterSortOption.MythicPlus, hasMPlusData: false);
            var built = components.Build();

            var actionRow = built.Components.First() as ActionRowComponent;
            Assert.NotNull(actionRow);
            var hasRefreshButton = actionRow.Components.Count > 3;

            Assert.True(hasRefreshButton);
        }

        [Fact]
        public void BuildComponents_HidesRefreshMplusButton_WhenDataLoaded()
        {
            var components = RosterView.BuildComponents(123456789, "guild|realm|us", 0, 5, RosterSortOption.MythicPlus, hasMPlusData: true);
            var built = components.Build();

            var actionRow = built.Components.First() as ActionRowComponent;
            Assert.NotNull(actionRow);
            // Should only have 3 buttons: Prev, Page info, Next
            Assert.Equal(3, actionRow.Components.Count);
        }

        [Fact]
        public void BuildComponents_IncludesSortDropdown_InSecondRow()
        {
            var components = RosterView.BuildComponents(123456789, "guild|realm|us", 0, 5, RosterSortOption.MythicPlus);
            var built = components.Build();

            Assert.Equal(2, built.Components.Count);
            var secondRow = built.Components.ToList()[1] as ActionRowComponent;
            Assert.NotNull(secondRow);
            var selectMenu = secondRow.Components.First() as SelectMenuComponent;
            Assert.NotNull(selectMenu);
            Assert.Contains("roster_sort", selectMenu.CustomId);
        }

        [Fact]
        public void BuildComponents_EncodesUserIdInCustomId()
        {
            ulong userId = 123456789012345678;
            var components = RosterView.BuildComponents(userId, "guild|realm|us", 0, 5, RosterSortOption.MythicPlus);
            var built = components.Build();

            var actionRow = built.Components.First() as ActionRowComponent;
            Assert.NotNull(actionRow);
            var prevButton = actionRow.Components.First() as ButtonComponent;

            Assert.NotNull(prevButton);
            Assert.Contains(userId.ToString(), prevButton.CustomId);
        }

        [Fact]
        public void Build_HandlesNullGuildInfo_Gracefully()
        {
            var guild = new ArmoryGuildInfo(); // All nulls
            var members = CreateTestMembers(5);

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            Assert.Equal("Unknown Guild - Unknown Realm", embed.Title);
        }

        [Fact]
        public void Build_TruncatesLongNames_InTableRow()
        {
            var guild = CreateGuildInfo();
            var members = new List<RosterMemberData>
            {
                new RosterMemberData
                {
                    Name = "VeryLongCharacterNameThatExceedsFifteenChars",
                    Realm = "test-realm",
                    Rank = 0,
                    Level = 70,
                    ClassId = 1,
                    MythicPlusScore = 2500
                }
            };

            var embed = RosterView.Build(guild, members, 0, 15, RosterSortOption.MythicPlus, true);

            // The table row (inside code block) should have truncated name
            // Extract the code block portion to check
            var description = embed.Description;
            var codeBlockStart = description.IndexOf("```");
            var codeBlockEnd = description.LastIndexOf("```");
            var tableContent = description.Substring(codeBlockStart, codeBlockEnd - codeBlockStart);

            // Table should contain truncated name (15 chars max)
            Assert.Contains("VeryLongCharact", tableContent);
            // Full name shouldn't appear in the table section
            Assert.DoesNotContain("VeryLongCharacterNameThatExceedsFifteenChars", tableContent);
        }
    }
}
