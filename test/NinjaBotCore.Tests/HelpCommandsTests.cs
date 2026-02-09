using Discord;
using NinjaBotCore.Models.Help;
using NinjaBotCore.Modules.Interactions.Misc;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class HelpCommandsTests
    {
        // Permission functions for testing
        private static readonly Func<string, bool> PublicOnly = perm => perm == "public";
        private static readonly Func<string, bool> AllPermissions = _ => true;
        private static readonly Func<string, bool> ModeratorAndBelow = perm =>
            perm == "public" || perm == "moderator" || perm == "manage_messages";

        private static HelpContent CreateTestContent(int commandCount = 12, string categoryId = "wow_main")
        {
            var commands = new List<HelpCommand>();
            for (int i = 0; i < commandCount; i++)
            {
                commands.Add(new HelpCommand
                {
                    Name = $"command-{i + 1}",
                    Description = $"Description for command {i + 1}",
                    Usage = $"/command-{i + 1}",
                    Permission = "public",
                    Parameters = new List<HelpParameter>()
                });
            }

            return new HelpContent
            {
                Categories = new List<HelpCategory>
                {
                    new HelpCategory
                    {
                        Id = categoryId,
                        Name = "World of Warcraft",
                        Emoji = "⚔️",
                        Description = "WoW commands for character lookups and guild info",
                        PermissionLevel = "public",
                        Commands = commands
                    }
                },
                Metadata = new HelpMetadata
                {
                    Version = "2.0.0",
                    LastUpdated = "2026-01-01",
                    TotalCommands = commandCount
                }
            };
        }

        private static HelpContent CreateMultiCategoryContent()
        {
            return new HelpContent
            {
                Categories = new List<HelpCategory>
                {
                    new HelpCategory
                    {
                        Id = "wow_main",
                        Name = "World of Warcraft",
                        Emoji = "⚔️",
                        Description = "WoW commands",
                        PermissionLevel = "public",
                        Commands = new List<HelpCommand>
                        {
                            new HelpCommand { Name = "lookup", Description = "Look up a char", Usage = "/lookup", Permission = "public", Parameters = new() },
                            new HelpCommand { Name = "guild", Description = "Guild info", Usage = "/guild", Permission = "public", Parameters = new() }
                        }
                    },
                    new HelpCategory
                    {
                        Id = "moderation",
                        Name = "Moderation & Admin",
                        Emoji = "🛡️",
                        Description = "Server moderation and administrative tools",
                        PermissionLevel = "moderator",
                        Commands = new List<HelpCommand>
                        {
                            new HelpCommand { Name = "kick", Description = "Kick user", Usage = "/kick", Permission = "moderator", PermissionBadge = "🛡️", Parameters = new() },
                            new HelpCommand { Name = "ban", Description = "Ban user", Usage = "/ban", Permission = "administrator", PermissionBadge = "👑", Parameters = new() }
                        }
                    },
                    new HelpCategory
                    {
                        Id = "owner_only",
                        Name = "Owner Only",
                        Emoji = "🔒",
                        Description = "Bot owner commands",
                        PermissionLevel = "owner",
                        Commands = new List<HelpCommand>
                        {
                            new HelpCommand { Name = "shutdown", Description = "Shut down bot", Usage = "/shutdown", Permission = "owner", PermissionBadge = "🔒", Parameters = new() }
                        }
                    },
                    new HelpCategory
                    {
                        Id = "polls",
                        Name = "Polls",
                        Emoji = "📊",
                        Description = "Create and manage polls",
                        PermissionLevel = "public",
                        Commands = new List<HelpCommand>
                        {
                            new HelpCommand { Name = "poll create", Description = "Create poll", Usage = "/poll create", Permission = "public", Parameters = new() },
                            new HelpCommand { Name = "poll close", Description = "Close poll", Usage = "/poll close", Permission = "manage_messages", PermissionBadge = "🛡️", Parameters = new() }
                        }
                    }
                },
                Metadata = new HelpMetadata { Version = "2.0.0", LastUpdated = "2026-01-01", TotalCommands = 7 }
            };
        }

        private static List<ButtonComponent> GetButtons(MessageComponent built)
        {
            var rows = built.Components.ToList();
            if (rows.Count < 2) return new List<ButtonComponent>();
            var buttonRow = rows[1] as ActionRowComponent;
            return buttonRow?.Components.OfType<ButtonComponent>().ToList() ?? new List<ButtonComponent>();
        }

        private static SelectMenuComponent GetSelectMenu(MessageComponent built)
        {
            var firstRow = built.Components.First() as ActionRowComponent;
            return firstRow?.Components.First() as SelectMenuComponent;
        }

        // ========== Welcome Embed Tests ==========

        [Fact]
        public void BuildWelcomeEmbed_SetsCorrectTitle()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, "2.0.0");
            Assert.Equal("🟣 NinjaBot Help System", embed.Title);
        }

        [Fact]
        public void BuildWelcomeEmbed_SetsBlueColor()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, null);
            Assert.Equal(new Color(0, 0, 255), embed.Color);
        }

        [Fact]
        public void BuildWelcomeEmbed_IncludesVersionInFooter()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, "3.5.0");
            Assert.Contains("v3.5.0", embed.Footer?.Text);
        }

        [Fact]
        public void BuildWelcomeEmbed_UsesDefaultVersion_WhenNull()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, null);
            Assert.Contains("v1.0.0", embed.Footer?.Text);
        }

        [Fact]
        public void BuildWelcomeEmbed_SetsThumbnail_WhenAvatarProvided()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed("https://example.com/avatar.png", "1.0.0");
            Assert.Equal("https://example.com/avatar.png", embed.ThumbnailUrl);
        }

        [Fact]
        public void BuildWelcomeEmbed_NoThumbnail_WhenAvatarNull()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, "1.0.0");
            Assert.Null(embed.ThumbnailUrl);
        }

        [Fact]
        public void BuildWelcomeEmbed_HasDescription()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, null);
            Assert.Contains("Select a category below", embed.Description);
        }

        [Fact]
        public void BuildWelcomeEmbed_HasTimestamp()
        {
            var embed = HelpViewBuilder.BuildWelcomeEmbed(null, null);
            Assert.NotNull(embed.Timestamp);
        }

        // ========== Category Embed - Basic Tests ==========

        [Fact]
        public void BuildCategoryEmbed_SetsCorrectTitle()
        {
            var content = CreateTestContent(3);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal("⚔️ World of Warcraft", embed.Title);
        }

        [Fact]
        public void BuildCategoryEmbed_SetsDescription()
        {
            var content = CreateTestContent(3);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal("WoW commands for character lookups and guild info", embed.Description);
        }

        [Fact]
        public void BuildCategoryEmbed_ReturnsWelcomeEmbed_ForUnknownCategory()
        {
            var content = CreateTestContent(3);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "nonexistent", 0, null, "2.0.0", PublicOnly);
            Assert.Equal("🟣 NinjaBot Help System", embed.Title);
        }

        [Fact]
        public void BuildCategoryEmbed_ReturnsWelcomeEmbed_WhenContentNull()
        {
            var embed = HelpViewBuilder.BuildCategoryEmbed(null, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal("🟣 NinjaBot Help System", embed.Title);
        }

        [Fact]
        public void BuildCategoryEmbed_ReturnsWelcomeEmbed_WhenCategoriesNull()
        {
            var content = new HelpContent { Categories = null };
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal("🟣 NinjaBot Help System", embed.Title);
        }

        // ========== Category Embed - Color Tests ==========

        [Fact]
        public void BuildCategoryEmbed_SetsBlueColor_ForPublicCategory()
        {
            var content = CreateTestContent(3);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal(new Color(0, 0, 255), embed.Color);
        }

        [Fact]
        public void BuildCategoryEmbed_SetsOrangeColor_ForModeratorCategory()
        {
            var content = CreateMultiCategoryContent();
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "moderation", 0, null, null, AllPermissions);
            Assert.Equal(new Color(255, 165, 0), embed.Color);
        }

        [Fact]
        public void BuildCategoryEmbed_SetsRedColor_ForOwnerCategory()
        {
            var content = CreateMultiCategoryContent();
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "owner_only", 0, null, null, AllPermissions);
            Assert.Equal(new Color(255, 0, 0), embed.Color);
        }

        // ========== Category Embed - Pagination Tests ==========

        [Fact]
        public void BuildCategoryEmbed_ShowsFirstFiveCommands_OnPage0()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.Equal(5, embed.Fields.Count);
            Assert.Equal("/command-1", embed.Fields[0].Name);
            Assert.Equal("/command-5", embed.Fields[4].Name);
        }

        [Fact]
        public void BuildCategoryEmbed_ShowsNextFiveCommands_OnPage1()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 1, null, null, PublicOnly);

            Assert.Equal(5, embed.Fields.Count);
            Assert.Equal("/command-6", embed.Fields[0].Name);
            Assert.Equal("/command-10", embed.Fields[4].Name);
        }

        [Fact]
        public void BuildCategoryEmbed_ShowsRemainingCommands_OnLastPage()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 2, null, null, PublicOnly);

            Assert.Equal(2, embed.Fields.Count);
            Assert.Equal("/command-11", embed.Fields[0].Name);
            Assert.Equal("/command-12", embed.Fields[1].Name);
        }

        [Fact]
        public void BuildCategoryEmbed_FooterShowsPageInfo()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Contains("Page 1/3", embed.Footer?.Text);
            Assert.Contains("12 commands total", embed.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_FooterShowsCorrectPage_OnPage2()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 1, null, null, PublicOnly);
            Assert.Contains("Page 2/3", embed.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_FooterSingular_ForOneCommand()
        {
            var content = CreateTestContent(1);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Contains("1 command total", embed.Footer?.Text);
            Assert.DoesNotContain("commands", embed.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_ClampsNegativePage_ToZero()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", -5, null, null, PublicOnly);

            Assert.Equal(5, embed.Fields.Count);
            Assert.Equal("/command-1", embed.Fields[0].Name);
            Assert.Contains("Page 1/3", embed.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_ClampsExcessivePage_ToLastPage()
        {
            var content = CreateTestContent(12);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 99, null, null, PublicOnly);

            Assert.Equal(2, embed.Fields.Count);
            Assert.Contains("Page 3/3", embed.Footer?.Text);
        }

        // ========== Category Embed - Edge Case Page Sizes ==========

        [Fact]
        public void BuildCategoryEmbed_ExactlyFiveCommands_OnePage()
        {
            var content = CreateTestContent(5);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.Equal(5, embed.Fields.Count);
            Assert.Contains("Page 1/1", embed.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_SixCommands_TwoPages()
        {
            var content = CreateTestContent(6);

            var page0 = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal(5, page0.Fields.Count);
            Assert.Contains("Page 1/2", page0.Footer?.Text);

            var page1 = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 1, null, null, PublicOnly);
            Assert.Equal(1, page1.Fields.Count);
            Assert.Contains("Page 2/2", page1.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_ZeroCommands_ShowsOnePage()
        {
            var content = CreateTestContent(0);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.Empty(embed.Fields);
            Assert.Contains("Page 1/1", embed.Footer?.Text);
            Assert.Contains("0 commands total", embed.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_TenCommands_TwoPages()
        {
            var content = CreateTestContent(10);

            var page0 = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal(5, page0.Fields.Count);
            Assert.Contains("Page 1/2", page0.Footer?.Text);

            var page1 = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 1, null, null, PublicOnly);
            Assert.Equal(5, page1.Fields.Count);
            Assert.Contains("Page 2/2", page1.Footer?.Text);
        }

        [Fact]
        public void BuildCategoryEmbed_ElevenCommands_ThreePages()
        {
            var content = CreateTestContent(11);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 2, null, null, PublicOnly);

            Assert.Single(embed.Fields);
            Assert.Contains("Page 3/3", embed.Footer?.Text);
        }

        // ========== Category Embed - Command Field Content ==========

        [Fact]
        public void BuildCategoryEmbed_FieldIncludesDescription()
        {
            var content = CreateTestContent(1);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.Contains("Description for command 1", embed.Fields[0].Value.ToString());
        }

        [Fact]
        public void BuildCategoryEmbed_FieldIncludesUsage()
        {
            var content = CreateTestContent(1);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.Contains("**Usage:** `/command-1`", embed.Fields[0].Value.ToString());
        }

        [Fact]
        public void BuildCategoryEmbed_FieldIncludesExample_WhenPresent()
        {
            var content = CreateTestContent(1);
            content.Categories[0].Commands[0].Example = "/command-1 test";
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.Contains("**Example:** `/command-1 test`", embed.Fields[0].Value.ToString());
        }

        [Fact]
        public void BuildCategoryEmbed_FieldOmitsExample_WhenNull()
        {
            var content = CreateTestContent(1);
            content.Categories[0].Commands[0].Example = null;
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.DoesNotContain("Example", embed.Fields[0].Value.ToString());
        }

        [Fact]
        public void BuildCategoryEmbed_FieldIncludesBadge_WhenPresent()
        {
            var content = CreateMultiCategoryContent();
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "moderation", 0, null, null, AllPermissions);

            var kickField = embed.Fields.First(f => f.Name.Contains("kick"));
            Assert.StartsWith("🛡️", kickField.Name);
        }

        [Fact]
        public void BuildCategoryEmbed_FieldsAreNotInline()
        {
            var content = CreateTestContent(3);
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);

            Assert.All(embed.Fields, f => Assert.False(f.IsInline));
        }

        // ========== Category Embed - Permission Filtering ==========

        [Fact]
        public void BuildCategoryEmbed_FiltersCommands_ByPermission()
        {
            var content = CreateMultiCategoryContent();
            // PublicOnly should only see "poll create", not "poll close" (manage_messages)
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "polls", 0, null, null, PublicOnly);

            Assert.Single(embed.Fields);
            Assert.Contains("poll create", embed.Fields[0].Name);
        }

        [Fact]
        public void BuildCategoryEmbed_ShowsAllCommands_WhenAllPermissions()
        {
            var content = CreateMultiCategoryContent();
            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "polls", 0, null, null, AllPermissions);

            Assert.Equal(2, embed.Fields.Count);
        }

        [Fact]
        public void BuildCategoryEmbed_PaginatesFilteredCommands()
        {
            // 12 commands total, but only 7 are public
            var content = CreateTestContent(12);
            for (int i = 0; i < 5; i++)
            {
                content.Categories[0].Commands[i].Permission = "administrator";
            }

            var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 0, null, null, PublicOnly);
            Assert.Equal(5, embed.Fields.Count);
            Assert.Contains("7 commands total", embed.Footer?.Text);

            var page1 = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", 1, null, null, PublicOnly);
            Assert.Equal(2, page1.Fields.Count);
            Assert.Contains("Page 2/2", page1.Footer?.Text);
        }

        // ========== Category Components - Select Menu Tests ==========

        [Fact]
        public void BuildCategoryComponents_HasSelectMenuInFirstRow()
        {
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            Assert.NotNull(selectMenu);
            Assert.Equal("help_category_select", selectMenu.CustomId);
        }

        [Fact]
        public void BuildCategoryComponents_SelectMenuIncludesHomeOption()
        {
            var content = CreateTestContent(3);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            Assert.Contains(selectMenu.Options, o => o.Value == "welcome");
        }

        [Fact]
        public void BuildCategoryComponents_SelectMenuIncludesCategories()
        {
            var content = CreateMultiCategoryContent();
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, AllPermissions).Build();

            var selectMenu = GetSelectMenu(built);
            Assert.Contains(selectMenu.Options, o => o.Value == "wow_main");
            Assert.Contains(selectMenu.Options, o => o.Value == "moderation");
            Assert.Contains(selectMenu.Options, o => o.Value == "polls");
        }

        // ========== Category Components - Pagination Button Tests ==========

        [Fact]
        public void BuildCategoryComponents_NoPaginationButtons_WhenFiveOrFewerCommands()
        {
            var content = CreateTestContent(5);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();

            Assert.Single(built.Components); // Only the select menu row
        }

        [Fact]
        public void BuildCategoryComponents_NoPaginationButtons_WhenThreeCommands()
        {
            var content = CreateTestContent(3);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();

            Assert.Single(built.Components);
        }

        [Fact]
        public void BuildCategoryComponents_HasPaginationButtons_WhenSixCommands()
        {
            var content = CreateTestContent(6);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();

            Assert.Equal(2, built.Components.Count); // Select menu + buttons
            var buttons = GetButtons(built);
            Assert.Equal(5, buttons.Count); // First, Prev, Page info, Next, Last
        }

        [Fact]
        public void BuildCategoryComponents_FirstAndPrevDisabled_OnPage0()
        {
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            var firstBtn = buttons[0];
            var prevBtn = buttons[1];
            var nextBtn = buttons[3];
            var lastBtn = buttons[4];

            Assert.True(firstBtn.IsDisabled);
            Assert.True(prevBtn.IsDisabled);
            Assert.False(nextBtn.IsDisabled);
            Assert.False(lastBtn.IsDisabled);
        }

        [Fact]
        public void BuildCategoryComponents_NextAndLastDisabled_OnLastPage()
        {
            var content = CreateTestContent(12); // 3 pages
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 2, 123, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            var firstBtn = buttons[0];
            var prevBtn = buttons[1];
            var nextBtn = buttons[3];
            var lastBtn = buttons[4];

            Assert.False(firstBtn.IsDisabled);
            Assert.False(prevBtn.IsDisabled);
            Assert.True(nextBtn.IsDisabled);
            Assert.True(lastBtn.IsDisabled);
        }

        [Fact]
        public void BuildCategoryComponents_AllNavEnabled_OnMiddlePage()
        {
            var content = CreateTestContent(12); // 3 pages
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, 123, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            Assert.False(buttons[0].IsDisabled); // First
            Assert.False(buttons[1].IsDisabled); // Prev
            Assert.True(buttons[2].IsDisabled);  // Page info always disabled
            Assert.False(buttons[3].IsDisabled); // Next
            Assert.False(buttons[4].IsDisabled); // Last
        }

        [Fact]
        public void BuildCategoryComponents_PageInfoButton_ShowsCorrectText()
        {
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, 123, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            var pageInfo = buttons[2];
            Assert.Equal("Page 2/3", pageInfo.Label);
            Assert.True(pageInfo.IsDisabled);
        }

        [Fact]
        public void BuildCategoryComponents_PageInfoButton_AlwaysDisabled()
        {
            var content = CreateTestContent(12);

            for (int page = 0; page < 3; page++)
            {
                var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", page, 123, false, PublicOnly).Build();
                var buttons = GetButtons(built);
                Assert.True(buttons[2].IsDisabled);
            }
        }

        // ========== Category Components - Custom ID Format Tests ==========

        [Fact]
        public void BuildCategoryComponents_FirstButton_HasCorrectCustomId()
        {
            ulong userId = 123456789012345678;
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, userId, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            Assert.Equal($"help_first~{userId}~wow_main~1", buttons[0].CustomId);
        }

        [Fact]
        public void BuildCategoryComponents_PrevButton_HasCorrectCustomId()
        {
            ulong userId = 123456789012345678;
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, userId, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            Assert.Equal($"help_prev~{userId}~wow_main~1", buttons[1].CustomId);
        }

        [Fact]
        public void BuildCategoryComponents_NextButton_HasCorrectCustomId()
        {
            ulong userId = 123456789012345678;
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, userId, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            Assert.Equal($"help_next~{userId}~wow_main~1", buttons[3].CustomId);
        }

        [Fact]
        public void BuildCategoryComponents_LastButton_HasTotalPages_InCustomId()
        {
            ulong userId = 123456789012345678;
            var content = CreateTestContent(12); // 3 pages
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, userId, false, PublicOnly).Build();

            var buttons = GetButtons(built);
            Assert.Equal($"help_last~{userId}~wow_main~1~3", buttons[4].CustomId);
        }

        [Fact]
        public void BuildCategoryComponents_ButtonCustomIds_UpdateWithCurrentPage()
        {
            ulong userId = 999;
            var content = CreateTestContent(12);

            var builtP0 = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, userId, false, PublicOnly).Build();
            var builtP2 = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 2, userId, false, PublicOnly).Build();

            var buttonsP0 = GetButtons(builtP0);
            var buttonsP2 = GetButtons(builtP2);

            Assert.Contains("~0", buttonsP0[3].CustomId); // next on page 0
            Assert.Contains("~2", buttonsP2[1].CustomId); // prev on page 2
        }

        // ========== Category Components - Button Styles ==========

        [Fact]
        public void BuildCategoryComponents_FirstAndLastButtons_AreSecondary()
        {
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, 123, false, PublicOnly).Build();
            var buttons = GetButtons(built);

            Assert.Equal(ButtonStyle.Secondary, buttons[0].Style); // First
            Assert.Equal(ButtonStyle.Secondary, buttons[4].Style); // Last
        }

        [Fact]
        public void BuildCategoryComponents_PrevAndNextButtons_ArePrimary()
        {
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, 123, false, PublicOnly).Build();
            var buttons = GetButtons(built);

            Assert.Equal(ButtonStyle.Primary, buttons[1].Style); // Prev
            Assert.Equal(ButtonStyle.Primary, buttons[3].Style); // Next
        }

        // ========== Category Components - Null Safety ==========

        [Fact]
        public void BuildCategoryComponents_HandlesNullContent()
        {
            var built = HelpViewBuilder.BuildCategoryComponents(null, "wow_main", 0, 123, false, PublicOnly).Build();

            Assert.Single(built.Components); // Just select menu, no buttons
        }

        [Fact]
        public void BuildCategoryComponents_HandlesUnknownCategory()
        {
            var content = CreateTestContent(12);
            var built = HelpViewBuilder.BuildCategoryComponents(content, "nonexistent", 0, 123, false, PublicOnly).Build();

            Assert.Single(built.Components); // Just select menu, no buttons
        }

        // ========== Category Components - Two-Page Edge Case ==========

        [Fact]
        public void BuildCategoryComponents_TwoPages_FirstAndLastPage()
        {
            var content = CreateTestContent(6); // Exactly 2 pages

            // Page 0: First/Prev disabled, Next/Last enabled
            var builtP0 = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 0, 123, false, PublicOnly).Build();
            var btnsP0 = GetButtons(builtP0);
            Assert.True(btnsP0[0].IsDisabled);  // First
            Assert.True(btnsP0[1].IsDisabled);  // Prev
            Assert.False(btnsP0[3].IsDisabled); // Next
            Assert.False(btnsP0[4].IsDisabled); // Last

            // Page 1: First/Prev enabled, Next/Last disabled
            var builtP1 = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 1, 123, false, PublicOnly).Build();
            var btnsP1 = GetButtons(builtP1);
            Assert.False(btnsP1[0].IsDisabled); // First
            Assert.False(btnsP1[1].IsDisabled); // Prev
            Assert.True(btnsP1[3].IsDisabled);  // Next
            Assert.True(btnsP1[4].IsDisabled);  // Last
        }

        // ========== Select Menu Tests ==========

        [Fact]
        public void BuildCategorySelectMenu_HasHomeOption()
        {
            var content = CreateTestContent(3);
            var built = HelpViewBuilder.BuildCategorySelectMenu(content, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            Assert.Contains(selectMenu.Options, o => o.Value == "welcome" && o.Label == "🏠 Home");
        }

        [Fact]
        public void BuildCategorySelectMenu_HasCorrectCustomId()
        {
            var content = CreateTestContent(3);
            var built = HelpViewBuilder.BuildCategorySelectMenu(content, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            Assert.Equal("help_category_select", selectMenu.CustomId);
        }

        [Fact]
        public void BuildCategorySelectMenu_TruncatesLongDescriptions()
        {
            var content = CreateTestContent(3);
            content.Categories[0].Description = new string('x', 150);
            var built = HelpViewBuilder.BuildCategorySelectMenu(content, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            var option = selectMenu.Options.First(o => o.Value == "wow_main");
            Assert.Equal(100, option.Description.Length);
            Assert.EndsWith("...", option.Description);
        }

        [Fact]
        public void BuildCategorySelectMenu_KeepsShortDescriptions()
        {
            var content = CreateTestContent(3);
            content.Categories[0].Description = "Short desc";
            var built = HelpViewBuilder.BuildCategorySelectMenu(content, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            var option = selectMenu.Options.First(o => o.Value == "wow_main");
            Assert.Equal("Short desc", option.Description);
        }

        [Fact]
        public void BuildCategorySelectMenu_IncludesEmojiInLabel()
        {
            var content = CreateTestContent(3);
            var built = HelpViewBuilder.BuildCategorySelectMenu(content, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            var option = selectMenu.Options.First(o => o.Value == "wow_main");
            Assert.Equal("⚔️ World of Warcraft", option.Label);
        }

        [Fact]
        public void BuildCategorySelectMenu_HandlesNullContent()
        {
            var built = HelpViewBuilder.BuildCategorySelectMenu(null, false, PublicOnly).Build();

            var selectMenu = GetSelectMenu(built);
            // Should only have Home option
            Assert.Single(selectMenu.Options);
            Assert.Equal("welcome", selectMenu.Options.First().Value);
        }

        // ========== FilterCategoriesByPermission Tests ==========

        [Fact]
        public void FilterCategories_OwnerSeesAll()
        {
            var content = CreateMultiCategoryContent();
            var result = HelpViewBuilder.FilterCategoriesByPermission(content, isOwner: true, PublicOnly);

            Assert.Equal(4, result.Count);
        }

        [Fact]
        public void FilterCategories_PublicUserSeesPublicCategories()
        {
            var content = CreateMultiCategoryContent();
            var result = HelpViewBuilder.FilterCategoriesByPermission(content, isOwner: false, PublicOnly);

            // wow_main (has public commands), polls (has public "poll create")
            // NOT moderation (only moderator+admin commands), NOT owner_only
            Assert.Equal(2, result.Count);
            Assert.Contains(result, c => c.Id == "wow_main");
            Assert.Contains(result, c => c.Id == "polls");
        }

        [Fact]
        public void FilterCategories_ModeratorSeesModeratorCategories()
        {
            var content = CreateMultiCategoryContent();
            var result = HelpViewBuilder.FilterCategoriesByPermission(content, isOwner: false, ModeratorAndBelow);

            // wow_main, moderation (has "kick" which is moderator), polls (both commands visible)
            // NOT owner_only
            Assert.Equal(3, result.Count);
            Assert.Contains(result, c => c.Id == "wow_main");
            Assert.Contains(result, c => c.Id == "moderation");
            Assert.Contains(result, c => c.Id == "polls");
        }

        [Fact]
        public void FilterCategories_AllPermissions_SeesAllCategories()
        {
            var content = CreateMultiCategoryContent();
            var result = HelpViewBuilder.FilterCategoriesByPermission(content, isOwner: false, AllPermissions);

            Assert.Equal(4, result.Count);
        }

        [Fact]
        public void FilterCategories_ReturnsEmpty_WhenContentNull()
        {
            var result = HelpViewBuilder.FilterCategoriesByPermission(null, isOwner: false, PublicOnly);
            Assert.Empty(result);
        }

        [Fact]
        public void FilterCategories_ReturnsEmpty_WhenCategoriesNull()
        {
            var content = new HelpContent { Categories = null };
            var result = HelpViewBuilder.FilterCategoriesByPermission(content, isOwner: false, PublicOnly);
            Assert.Empty(result);
        }

        [Fact]
        public void FilterCategories_OwnerSeesCategoriesEvenWithRestrictedPermissionFunc()
        {
            var content = CreateMultiCategoryContent();
            // Even with a function that rejects everything, owner sees all
            Func<string, bool> rejectAll = _ => false;
            var result = HelpViewBuilder.FilterCategoriesByPermission(content, isOwner: true, rejectAll);

            Assert.Equal(4, result.Count);
        }

        // ========== Integration: Components Match Embed Pagination ==========

        [Fact]
        public void EmbedAndComponents_AreConsistent_OnSamePage()
        {
            var content = CreateTestContent(12);
            ulong userId = 555;

            for (int page = 0; page < 3; page++)
            {
                var embed = HelpViewBuilder.BuildCategoryEmbed(content, "wow_main", page, null, null, PublicOnly);
                var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", page, userId, false, PublicOnly).Build();

                var buttons = GetButtons(built);
                var pageInfo = buttons[2];

                // Page info button should match embed footer
                Assert.Contains($"Page {page + 1}/3", embed.Footer?.Text);
                Assert.Equal($"Page {page + 1}/3", pageInfo.Label);
            }
        }

        [Fact]
        public void NoPaginationButtons_WhenAllCommandsFilteredOut()
        {
            var content = CreateMultiCategoryContent();
            // owner_only has 1 command with "owner" permission, PublicOnly rejects it
            var built = HelpViewBuilder.BuildCategoryComponents(content, "owner_only", 0, 123, false, PublicOnly).Build();

            Assert.Single(built.Components); // Just select menu
        }

        [Fact]
        public void BuildCategoryComponents_ClampsPage_WhenExceedsTotalPages()
        {
            var content = CreateTestContent(12); // 3 pages
            ulong userId = 123;

            var built = HelpViewBuilder.BuildCategoryComponents(content, "wow_main", 99, userId, false, PublicOnly).Build();
            var buttons = GetButtons(built);

            // Should be on last page (2), so Next/Last disabled
            Assert.True(buttons[3].IsDisabled);  // Next
            Assert.True(buttons[4].IsDisabled);  // Last
            Assert.Equal("Page 3/3", buttons[2].Label);
        }

        // ========== Constants ==========

        [Fact]
        public void CommandsPerPage_IsFive()
        {
            Assert.Equal(5, HelpViewBuilder.CommandsPerPage);
        }
    }
}
