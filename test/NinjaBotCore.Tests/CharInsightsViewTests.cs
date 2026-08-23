using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CharInsightsViewTests
    {
        private static readonly CharacterInfo Character = new()
        {
            Name = "Odib",
            Realm = "Tichondrius",
            RealmSlug = "tichondrius",
            Region = "us"
        };

        [Fact]
        public void Coach_ShowsScoreOpportunitiesActivityAndReviewRoute()
        {
            var rio = BuildRio();

            var embed = CharInsightsView.BuildCoach(Character, rio).Build();
            var components = CharInsightsView.BuildInsightComponents(
                    123,
                    Character,
                    "coach",
                    reviewRuns: rio.MythicPlusRecentRuns)
                .Build();

            Assert.Contains("M+ Coach", embed.Title);
            Assert.Contains("3,427.1", embed.Description);
            Assert.Contains("Best opportunities", embed.Description);
            Assert.Contains("DON", embed.Description);
            Assert.Contains("7/8 timed", embed.Description);
            Assert.Contains("alternate 427.2", embed.Description);
            var menus = components.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<SelectMenuComponent>()
                .ToList();
            Assert.Contains(menus, menu => menu.CustomId.StartsWith("char_insights~123~"));
            var review = Assert.Single(menus, menu => menu.CustomId.StartsWith("char_run_review~123~"));
            Assert.Contains(review.Options, option => option.Value == "2447383");
        }

        [Fact]
        public void Coach_WarbandLinkFollowsCharacterSeasonInsteadOfHardCoding()
        {
            var rio = BuildRio();
            rio.MythicPlusScores[0].Season = "season-future-3";

            var embed = CharInsightsView.BuildCoach(Character, rio).Build();

            Assert.Contains("/season-future-3/", embed.Description);
            Assert.DoesNotContain("/season-mn-2/", embed.Description);
        }

        [Fact]
        public void CurrentSeasonSlugComesFromCharacterPayload()
        {
            var rio = BuildRio();
            rio.MythicPlusScores = new[]
            {
                new RaiderIOModels.MythicPlusScores { Season = "season-future-3" }
            };

            Assert.Equal("season-future-3", CharInsightsView.GetCurrentSeasonSlug(rio));
        }

        [Fact]
        public void Talents_ShowsHeroTreeSummaryAndImportCode()
        {
            var embed = CharInsightsView.BuildTalents(Character, BuildRio()).Build();

            Assert.Contains("Talents", embed.Title);
            Assert.Contains("Slayer", embed.Description);
            Assert.Contains("Rallying Cry", embed.Description);
            Assert.Contains("IMPORT-CODE", embed.Description);
            Assert.Contains("raider.io/specs/arms-warrior/talents", embed.Description);
        }

        [Fact]
        public void Rivals_ShowsSelfAndNearbyTargets()
        {
            var response = new RaiderIOModels.CharacterRivalsResponse
            {
                Rivals = new RaiderIOModels.RivalWindow
                {
                    Scope = "region",
                    SelfRank = 4,
                    FullRankingPath = "/mythic-plus-spec-rankings/season-mn-2/us/warrior/arms",
                    Entries = new[]
                    {
                        new RaiderIOModels.RivalEntry { Rank = 3, Name = "Ahead", Realm = "Stormrage", Score = 3436.1 },
                        new RaiderIOModels.RivalEntry { Rank = 4, Name = "Odib", Realm = "Tichondrius", Score = 3427.1, IsSelf = true },
                        new RaiderIOModels.RivalEntry { Rank = 5, Name = "Behind", Realm = "Area 52", Score = 3399.2 }
                    }
                }
            };

            var embed = CharInsightsView.BuildRivals(Character, response).Build();
            var components = CharInsightsView.BuildInsightComponents(
                    123,
                    Character,
                    "rivals",
                    rivalsScope: "region")
                .Build();

            Assert.Contains("Region Rivals", embed.Title);
            Assert.Contains("#3", embed.Description);
            Assert.Contains("Ahead", embed.Description);
            Assert.Contains("← You", embed.Description);
            Assert.Contains("raider.io/mythic-plus-spec-rankings", embed.Description);
            var scopeMenu = components.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<SelectMenuComponent>()
                .Single(menu => menu.CustomId.StartsWith("char_rivals_scope~123~"));
            Assert.Equal(3, scopeMenu.Options.Count);
            Assert.True(scopeMenu.Options.Single(option => option.Value == "region").IsDefault);
        }

        [Fact]
        public void ProviderLinksRejectOffOriginUrls()
        {
            var rivals = new RaiderIOModels.CharacterRivalsResponse
            {
                Rivals = new RaiderIOModels.RivalWindow
                {
                    Scope = "world",
                    FullRankingPath = "//evil.example/steal",
                    Entries = Array.Empty<RaiderIOModels.RivalEntry>()
                }
            };
            var run = BuildRio().MythicPlusRecentRuns[0];
            run.Url = new Uri("https://evil.example/steal");
            var review = new RaiderIOModels.RunReviewResponse
            {
                KeystonePace = new RaiderIOModels.KeystonePace(),
                PastRuns = Array.Empty<RaiderIOModels.PastDungeonRun>()
            };

            var rivalsEmbed = CharInsightsView.BuildRivals(Character, rivals).Build();
            var reviewEmbed = CharInsightsView.BuildRunReview(Character, run, review).Build();

            Assert.DoesNotContain("evil.example", rivalsEmbed.Description);
            Assert.DoesNotContain("evil.example", reviewEmbed.Description);
        }

        [Fact]
        public void Cutoffs_ShowsProgressAndRealmCapacity()
        {
            var cutoffs = new RaiderIOModels.SeasonCutoffsResponse
            {
                Cutoffs = new RaiderIOModels.SeasonCutoffs
                {
                    Region = new RaiderIOModels.RegionSummary { ShortName = "US" },
                    KeystoneExplorer = Threshold(1000),
                    KeystoneConqueror = Threshold(1500),
                    KeystoneMaster = Threshold(2000),
                    KeystoneHero = Threshold(2500),
                    KeystoneLegend = Threshold(3000),
                    KeystoneMyth = Threshold(3400),
                    P999 = new RaiderIOModels.CutoffThreshold
                    {
                        All = new RaiderIOModels.CutoffPopulation { QuantileMinValue = 3236.69 }
                    }
                }
            };
            var capacity = new RaiderIOModels.LeaderboardCapacityResponse
            {
                RealmListing = new RaiderIOModels.LeaderboardCapacityListing
                {
                    Realms = new[]
                    {
                        new RaiderIOModels.LeaderboardCapacityRealm
                        {
                            Dungeons = new[]
                            {
                                new RaiderIOModels.LeaderboardCapacityDungeon
                                {
                                    Dungeon = new RaiderIOModels.CapacityDungeonSummary { ShortName = "DON" },
                                    Lowest = new RaiderIOModels.CapacityLowestRun { MythicLevel = 12, TimeInMilliseconds = 1850000 }
                                }
                            }
                        }
                    }
                }
            };

            var embed = CharInsightsView.BuildCutoffs(Character, BuildRio(), cutoffs, capacity).Build();

            Assert.Contains("Score Goals", embed.Title);
            Assert.Contains("Legend", embed.Description);
            Assert.Contains("reached", embed.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DON +12", embed.Description);
        }

        [Fact]
        public void RunReview_ShowsPaceHistoryAndRivals()
        {
            var run = BuildRio().MythicPlusRecentRuns[0];
            var review = new RaiderIOModels.RunReviewResponse
            {
                RunScore = 444.1,
                KeystonePace = new RaiderIOModels.KeystonePace
                {
                    Current = new RaiderIOModels.PaceSnapshot { Percentile = 78.1, PopulationCount = 33 },
                    Historical = new RaiderIOModels.PaceSnapshot { Percentile = 76.4, PopulationCount = 30 },
                    ItemLevel = new RaiderIOModels.ItemLevelPace
                    {
                        Average = 275.4,
                        Current = new RaiderIOModels.PaceSnapshot { Percentile = 91.2, PopulationCount = 12 }
                    }
                },
                PastRuns = new[]
                {
                    new RaiderIOModels.PastDungeonRun { KeyLevel = 17, ClearTimeMs = 1709162, Timed = true, Score = 444.1 },
                    new RaiderIOModels.PastDungeonRun { KeyLevel = 16, ClearTimeMs = 1810509, Timed = true, Score = 427.2 }
                },
                Rivals = new RaiderIOModels.RivalWindow { SelfRank = 4 }
            };

            var embed = CharInsightsView.BuildRunReview(Character, run, review).Build();

            Assert.Contains("Run Review", embed.Title);
            Assert.Contains("78.1 percentile", embed.Description);
            Assert.Contains("91.2 percentile", embed.Description);
            Assert.Contains("Previous attempts", embed.Description);
            Assert.Contains("+16", embed.Description);
        }

        [Fact]
        public void KeysHubOffersCharacterInsightsWithoutReplacingTheSharedHub()
        {
            var built = PushGroupStatsCards.BuildHub(
                    1,
                    Array.Empty<PushGroupStatsCards.OpenGroupRow>(),
                    Array.Empty<PushGroupStatsCards.KeystoneRow>(),
                    Array.Empty<PushGroupStatsCards.LeaderboardRow>())
                .Build();
            var container = Assert.IsType<ContainerComponent>(Assert.Single(built.Components));
            var text = string.Join("\n", container.Components
                .OfType<TextDisplayComponent>()
                .Select(component => component.Content));
            Assert.Contains("[Raider.IO](https://raider.io)", text);

            var buttons = container.Components
                .OfType<ActionRowComponent>()
                .SelectMany(row => row.Components)
                .OfType<ButtonComponent>()
                .ToList();

            Assert.Contains(buttons, button => button.CustomId == "pushgroup_hubnew");
            Assert.Contains(buttons, button => button.CustomId == "pushgroup_hubinsights");
        }

        [Fact]
        public void InsightHandlersExposeStableComponentRoutes()
        {
            AssertRoute(nameof(CharCommands.HandleOpenMainInsights), "pushgroup_hubinsights");
            AssertRoute(nameof(CharCommands.HandleViewInsights), "char_view_insights~*~*");
            AssertRoute(nameof(CharCommands.HandleInsightSelect), "char_insights~*~*");
            AssertRoute(nameof(CharCommands.HandleRivalsScopeSelect), "char_rivals_scope~*~*");
            AssertRoute(nameof(CharCommands.HandleRunReviewSelect), "char_run_review~*~*");
            AssertRoute(nameof(CharCommands.HandleManageCharactersPage), "char_mpage~*~*");
            AssertRoute(nameof(CharCommands.HandleManageCharactersPageWithReturn), "char_mpage_ret~*~*~*~*~*");
        }

        private static void AssertRoute(string methodName, string expected)
        {
            var method = typeof(CharCommands).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            var route = method?.GetCustomAttribute<ComponentInteractionAttribute>();
            Assert.NotNull(route);
            Assert.Equal(expected, route.CustomId);
        }

        private static RaiderIOModels.RioMythicPlusChar BuildRio() => new()
        {
            Name = "Odib",
            Class = "Warrior",
            ActiveSpecName = "Arms",
            LastCrawledAt = DateTimeOffset.Parse("2026-08-23T05:52:31Z"),
            MythicPlusScores = new[]
            {
                new RaiderIOModels.MythicPlusScores
                {
                    Season = "season-mn-2",
                    Scores = new RaiderIOModels.MythicPlusScoreBreakout { All = 3427.1 }
                }
            },
            MythicPlusBestRuns = new[]
            {
                new RaiderIOModels.MythicPlusRun { Dungeon = "Den of Nalorakk", ShortName = "DON", MythicLevel = 17, Score = 444.1, ZoneId = 16368 },
                new RaiderIOModels.MythicPlusRun { Dungeon = "Murder Row", ShortName = "MR", MythicLevel = 12, Score = 350.2, ZoneId = 16091 }
            },
            MythicPlusAlternateRuns = new[]
            {
                new RaiderIOModels.MythicPlusRun { Dungeon = "Den of Nalorakk", ShortName = "DON", MythicLevel = 16, Score = 427.2, ZoneId = 16368 }
            },
            MythicPlusDungeonRunCounts = new[]
            {
                new RaiderIOModels.MythicPlusDungeonRunCount { Dungeon = "Den of Nalorakk", ShortName = "DON", ZoneId = 16368, SeasonRunsTotal = 8, SeasonRunsTimed = 7 },
                new RaiderIOModels.MythicPlusDungeonRunCount { Dungeon = "Murder Row", ShortName = "MR", ZoneId = 16091, SeasonRunsTotal = 3, SeasonRunsTimed = 1 }
            },
            MythicPlusRecentRuns = new[]
            {
                new RaiderIOModels.MythicPlusRun
                {
                    KeystoneRunId = 2447383,
                    Dungeon = "Den of Nalorakk",
                    ShortName = "DON",
                    MythicLevel = 17,
                    Score = 444.1,
                    ZoneId = 16368,
                    ClearTimeMs = 1709162,
                    CompletedAt = DateTimeOffset.Parse("2026-08-23T05:32:10Z")
                }
            },
            TalentLoadout = new RaiderIOModels.TalentLoadout
            {
                LoadoutSpecId = 71,
                LoadoutText = "IMPORT-CODE",
                ActiveHeroTree = new RaiderIOModels.HeroTalentTree { Name = "Slayer", Slug = "slayer" },
                ClassTalents = new[] { SummaryTalent("Rallying Cry") },
                SpecTalents = new[] { SummaryTalent("Bladestorm") },
                HeroTalents = Array.Empty<RaiderIOModels.TalentSelection>()
            }
        };

        private static RaiderIOModels.TalentSelection SummaryTalent(string name) => new()
        {
            IncludeInSummary = true,
            Rank = 1,
            EntryIndex = 0,
            Node = new RaiderIOModels.TalentNode
            {
                Entries = new[]
                {
                    new RaiderIOModels.TalentEntry { Spell = new RaiderIOModels.TalentSpell { Name = name } }
                }
            }
        };

        private static RaiderIOModels.CutoffThreshold Threshold(double score) => new() { Score = score };
    }
}
