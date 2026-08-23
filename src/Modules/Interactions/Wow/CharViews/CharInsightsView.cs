using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    public static class CharInsightsView
    {
        private const string HeatmapsUrl = "https://raider.io/stats/mythic-plus-heatmaps";
        private const string RecruitmentUrl = "https://raider.io/recruitment";

        public static string GetCurrentSeasonSlug(RaiderIOModels.RioMythicPlusChar rio) =>
            rio?.MythicPlusScores?
                .Select(score => score?.Season?.Trim())
                .FirstOrDefault(season => !string.IsNullOrWhiteSpace(season));

        public static EmbedBuilder BuildCoach(
            CharacterInfo character,
            RaiderIOModels.RioMythicPlusChar rio)
        {
            var score = rio?.MythicPlusScores?.FirstOrDefault()?.Scores?.All ?? 0;
            var bestRuns = rio?.MythicPlusBestRuns ?? Array.Empty<RaiderIOModels.MythicPlusRun>();
            var counts = rio?.MythicPlusDungeonRunCounts ?? Array.Empty<RaiderIOModels.MythicPlusDungeonRunCount>();
            var bestByZone = bestRuns
                .Where(run => run.ZoneId > 0)
                .GroupBy(run => run.ZoneId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(run => run.Score).First());
            var alternateByZone = (rio?.MythicPlusAlternateRuns ?? Array.Empty<RaiderIOModels.MythicPlusRun>())
                .Where(run => run.ZoneId > 0)
                .GroupBy(run => run.ZoneId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(run => run.Score).First());

            var opportunities = counts
                .Select(count => new
                {
                    Count = count,
                    Best = bestByZone.GetValueOrDefault(count.ZoneId),
                    Alternate = alternateByZone.GetValueOrDefault(count.ZoneId)
                })
                .OrderBy(value => value.Best?.Score ?? 0)
                .ThenBy(value => value.Count.ShortName)
                .ToList();

            var text = new StringBuilder();
            text.AppendLine($"**Current score:** {score:N1}  •  **{rio?.ActiveSpecName ?? "Unknown spec"} {rio?.Class ?? string.Empty}**");
            if (rio?.LastCrawledAt is { } crawled)
            {
                text.AppendLine($"Raider.IO snapshot <t:{crawled.ToUnixTimeSeconds()}:R>");
            }

            text.AppendLine();
            text.AppendLine("## Best opportunities");
            if (opportunities.Count == 0)
            {
                text.AppendLine("*Dungeon activity is not available yet. Refresh the character on Raider.IO and try again.*");
            }
            else
            {
                foreach (var item in opportunities.Take(8))
                {
                    var shortName = item.Count.ShortName ?? item.Count.Dungeon ?? "Dungeon";
                    var best = item.Best == null
                        ? "no scored run"
                        : $"+{item.Best.MythicLevel} · {item.Best.Score:N1} score";
                    var alternate = item.Alternate == null
                        ? string.Empty
                        : $" · alternate {item.Alternate.Score:N1}";
                    text.AppendLine($"`{shortName,-4}` {best}{alternate} · {item.Count.SeasonRunsTimed}/{item.Count.SeasonRunsTotal} timed");
                }
            }

            var recent = rio?.MythicPlusRecentRuns ?? Array.Empty<RaiderIOModels.MythicPlusRun>();
            text.AppendLine();
            text.AppendLine("## Recent runs");
            if (recent.Length == 0)
            {
                text.AppendLine("*No recent runs are available.*");
            }
            else
            {
                foreach (var run in recent.Take(5))
                {
                    var timing = run.ParTimeMs > 0 && run.ClearTimeMs > run.ParTimeMs ? "depleted" : "timed";
                    text.AppendLine($"**{run.ShortName ?? run.Dungeon} +{run.MythicLevel}** · {run.Score:N1} · {timing}");
                }
                text.AppendLine("-# Use Review a recent run below for Keystone Pace and comparison data.");
            }

            text.AppendLine();
            text.AppendLine($"[Talent Builds]({BuildTalentBuildsUrl(rio)}) • [Dungeon Heatmaps]({HeatmapsUrl}) • [Find Groups]({RecruitmentUrl}) • [Warband Rankings]({BuildWarbandUrl(rio)})");

            return new EmbedBuilder()
                .WithTitle($"M+ Coach — {character.Name}")
                .WithDescription(text.ToString())
                .WithThumbnailUrl(rio?.ThumbnailUrl?.AbsoluteUri)
                .WithColor(new Color(91, 66, 243))
                .WithFooter("Character and run data from Raider.IO");
        }

        public static ComponentBuilder BuildInsightComponents(
            ulong userId,
            CharacterInfo character,
            string currentInsight,
            bool isAlreadySaved = false,
            IEnumerable<RaiderIOModels.MythicPlusRun> reviewRuns = null,
            string rivalsScope = "region")
        {
            var builder = CharOverviewView.BuildDetailViewComponents(
                userId,
                character,
                currentInsight,
                isAlreadySaved);
            var charParam = $"{character.Name}~{character.Realm}~{character.Region}";
            var insightMenu = new SelectMenuBuilder()
                .WithCustomId($"{ModalConstants.CharInsightsSelect}~{userId}~{charParam}")
                .WithPlaceholder("Character insights...")
                .AddOption("M+ Coach", "coach", "Dungeon opportunities and recent runs", isDefault: currentInsight == "coach")
                .AddOption("Talents", "talents", "Active personal loadout", isDefault: currentInsight == "talents")
                .AddOption("Rivals", "rivals", "Nearby score targets", isDefault: currentInsight == "rivals")
                .AddOption("Score Goals", "cutoffs", "Season achievements and leaderboard capacity", isDefault: currentInsight == "cutoffs");
            builder.WithSelectMenu(insightMenu, row: 3);

            var eligibleRuns = (reviewRuns ?? Array.Empty<RaiderIOModels.MythicPlusRun>())
                .Where(run => run.KeystoneRunId > 0)
                .Take(10)
                .ToList();
            if (currentInsight == "coach" && eligibleRuns.Count > 0)
            {
                var reviewMenu = new SelectMenuBuilder()
                    .WithCustomId($"{ModalConstants.CharRunReviewSelect}~{userId}~{charParam}")
                    .WithPlaceholder("Review a recent run...");
                foreach (var run in eligibleRuns)
                {
                    var label = $"{run.ShortName ?? run.Dungeon} +{run.MythicLevel} · {run.Score:N1}";
                    if (label.Length > 100) label = label[..100];
                    reviewMenu.AddOption(label, run.KeystoneRunId.ToString(CultureInfo.InvariantCulture));
                }
                builder.WithSelectMenu(reviewMenu, row: 4);
            }
            else if (currentInsight == "rivals")
            {
                var scopeMenu = new SelectMenuBuilder()
                    .WithCustomId($"{ModalConstants.CharRivalsScopeSelect}~{userId}~{charParam}")
                    .WithPlaceholder("Leaderboard scope...")
                    .AddOption("Realm", "realm", "Nearby players on this realm", isDefault: rivalsScope == "realm")
                    .AddOption("Region", "region", "Nearby players in this region", isDefault: rivalsScope == "region")
                    .AddOption("World", "world", "Nearby players worldwide", isDefault: rivalsScope == "world");
                builder.WithSelectMenu(scopeMenu, row: 4);
            }

            return builder;
        }

        public static EmbedBuilder BuildTalents(
            CharacterInfo character,
            RaiderIOModels.RioMythicPlusChar rio)
        {
            var loadout = rio?.TalentLoadout;
            var text = new StringBuilder();
            text.AppendLine($"**{rio?.ActiveSpecName ?? "Unknown spec"} {rio?.Class ?? string.Empty}**");
            if (!string.IsNullOrWhiteSpace(loadout?.ActiveHeroTree?.Name))
            {
                text.AppendLine($"**Hero talents:** {loadout.ActiveHeroTree.Name}");
            }

            AppendTalentGroup(text, "Class highlights", loadout?.ClassTalents);
            AppendTalentGroup(text, "Spec highlights", loadout?.SpecTalents);
            AppendTalentGroup(text, "Hero highlights", loadout?.HeroTalents);

            if (!string.IsNullOrWhiteSpace(loadout?.LoadoutText))
            {
                text.AppendLine();
                text.AppendLine("## Import code");
                text.AppendLine($"```\n{loadout.LoadoutText}\n```");
            }
            else
            {
                text.AppendLine();
                text.AppendLine("*No active loadout was returned. Refresh the character on Raider.IO after logging out of WoW.*");
            }

            text.AppendLine();
            text.AppendLine($"[Browse top-performing {rio?.ActiveSpecName ?? "spec"} builds]({BuildTalentBuildsUrl(rio)})");

            return new EmbedBuilder()
                .WithTitle($"Talents — {character.Name}")
                .WithDescription(text.ToString())
                .WithThumbnailUrl(loadout?.ActiveHeroTree?.IconUrl ?? rio?.ThumbnailUrl?.AbsoluteUri)
                .WithColor(new Color(0, 200, 150))
                .WithFooter("Personal loadout from Raider.IO • Aggregate builds open on Raider.IO");
        }

        public static EmbedBuilder BuildRivals(
            CharacterInfo character,
            RaiderIOModels.CharacterRivalsResponse response,
            string requestedScope = null)
        {
            var rivals = response?.Rivals;
            var resolvedScope = string.IsNullOrWhiteSpace(rivals?.Scope)
                ? requestedScope
                : rivals.Scope;
            var scope = string.IsNullOrWhiteSpace(resolvedScope)
                ? "Region"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(resolvedScope);
            var text = new StringBuilder();
            if (rivals?.Entries == null || rivals.Entries.Length == 0)
            {
                text.AppendLine("*No nearby leaderboard entries are available yet.*");
            }
            else
            {
                foreach (var entry in rivals.Entries)
                {
                    var marker = entry.IsSelf ? " ← You" : string.Empty;
                    text.AppendLine($"**#{entry.Rank:N0}** · **{entry.Name}**-{entry.Realm} · {entry.Score:N1}{marker}");
                }
            }

            if (!string.IsNullOrWhiteSpace(rivals?.FullRankingPath))
            {
                text.AppendLine();
                text.AppendLine($"[Open full leaderboard]({RaiderIoLinks.FromRelativePath(rivals.FullRankingPath)})");
            }

            return new EmbedBuilder()
                .WithTitle($"{scope} Rivals — {character.Name}")
                .WithDescription(text.ToString())
                .WithColor(new Color(249, 117, 60))
                .WithFooter("Current character leaderboard window from Raider.IO");
        }

        public static EmbedBuilder BuildCutoffs(
            CharacterInfo character,
            RaiderIOModels.RioMythicPlusChar rio,
            RaiderIOModels.SeasonCutoffsResponse cutoffsResponse,
            RaiderIOModels.LeaderboardCapacityResponse capacityResponse)
        {
            var score = rio?.MythicPlusScores?.FirstOrDefault()?.Scores?.All ?? 0;
            var cutoffs = cutoffsResponse?.Cutoffs;
            var text = new StringBuilder();
            text.AppendLine($"**Current score:** {score:N1} · **Region:** {cutoffs?.Region?.ShortName ?? character.Region.ToUpperInvariant()}");
            text.AppendLine();
            text.AppendLine("## Season achievements");
            AppendGoal(text, "Explorer", cutoffs?.KeystoneExplorer?.Score, score);
            AppendGoal(text, "Conqueror", cutoffs?.KeystoneConqueror?.Score, score);
            AppendGoal(text, "Master", cutoffs?.KeystoneMaster?.Score, score);
            AppendGoal(text, "Hero", cutoffs?.KeystoneHero?.Score, score);
            AppendGoal(text, "Legend", cutoffs?.KeystoneLegend?.Score, score);
            AppendGoal(text, "Myth", cutoffs?.KeystoneMyth?.Score, score);
            if (cutoffs?.P999?.All != null)
            {
                text.AppendLine($"**Top 0.1% snapshot:** {cutoffs.P999.All.QuantileMinValue:N1}");
            }

            text.AppendLine();
            text.AppendLine("## Realm leaderboard capacity");
            var capacity = capacityResponse?.RealmListing?.Realms?
                .SelectMany(realm => realm.Dungeons ?? Array.Empty<RaiderIOModels.LeaderboardCapacityDungeon>())
                .Where(dungeon => dungeon.Lowest != null)
                .Take(8)
                .ToList() ?? new List<RaiderIOModels.LeaderboardCapacityDungeon>();
            if (capacity.Count == 0)
            {
                text.AppendLine("*No qualifying floor is published yet for this realm/week.*");
            }
            else
            {
                foreach (var dungeon in capacity)
                {
                    text.AppendLine($"**{dungeon.Dungeon?.ShortName ?? dungeon.Dungeon?.Name} +{dungeon.Lowest.MythicLevel}** · {FormatDuration(dungeon.Lowest.TimeInMilliseconds)}");
                }
            }

            text.AppendLine();
            text.AppendLine($"[Season cutoffs](https://raider.io/mythic-plus/cutoffs) • [Warband rankings]({BuildWarbandUrl(rio)})");
            if (!string.IsNullOrWhiteSpace(cutoffs?.UpdatedAt))
            {
                text.AppendLine($"-# Raider.IO cutoff snapshot: {cutoffs.UpdatedAt}");
            }

            return new EmbedBuilder()
                .WithTitle($"Score Goals — {character.Name}")
                .WithDescription(text.ToString())
                .WithColor(new Color(163, 53, 238))
                .WithFooter("Season threshold and realm-capacity snapshot from Raider.IO");
        }

        public static EmbedBuilder BuildRunReview(
            CharacterInfo character,
            RaiderIOModels.MythicPlusRun run,
            RaiderIOModels.RunReviewResponse review)
        {
            var pace = review?.KeystonePace;
            var text = new StringBuilder();
            text.AppendLine($"**{run.ShortName ?? run.Dungeon} +{run.MythicLevel}** · {review?.RunScore ?? run.Score:N1} score · {FormatDuration(run.ClearTimeMs)}");
            text.AppendLine();
            text.AppendLine("## Keystone Pace");
            AppendPace(text, "Current", pace?.Current);
            AppendPace(text, "Historical", pace?.Historical);
            if (pace?.ItemLevel?.Current != null)
            {
                text.AppendLine($"**Item-level pace:** {pace.ItemLevel.Current.Percentile:N1} percentile · {pace.ItemLevel.Current.PopulationCount:N0} runs near {pace.ItemLevel.Average:N1} ilvl");
            }

            text.AppendLine();
            text.AppendLine("## Previous attempts");
            var previous = (review?.PastRuns ?? Array.Empty<RaiderIOModels.PastDungeonRun>()).Skip(1).Take(5).ToList();
            if (previous.Count == 0)
            {
                text.AppendLine("*No prior attempts were returned for this dungeon.*");
            }
            else
            {
                foreach (var past in previous)
                {
                    var score = past.Score.HasValue ? past.Score.Value.ToString("N1") : "—";
                    text.AppendLine($"**+{past.KeyLevel}** · {score} · {FormatDuration(past.ClearTimeMs)} · {(past.Timed ? "timed" : "depleted")}");
                }
            }

            if (review?.Rivals?.SelfRank is { } rank)
            {
                text.AppendLine();
                text.AppendLine($"**Current spec rank:** #{rank:N0}");
            }
            text.AppendLine();
            text.AppendLine($"[Dungeon heatmaps]({HeatmapsUrl}) • [Open run on Raider.IO]({RaiderIoLinks.FromAbsolute(run.Url)})");

            return new EmbedBuilder()
                .WithTitle($"Run Review — {character.Name}")
                .WithDescription(text.ToString())
                .WithThumbnailUrl(run.IconUrl)
                .WithColor(new Color(70, 130, 255))
                .WithFooter("Keystone Pace and comparison data from Raider.IO");
        }

        private static void AppendTalentGroup(
            StringBuilder text,
            string title,
            IEnumerable<RaiderIOModels.TalentSelection> selections)
        {
            var talents = (selections ?? Array.Empty<RaiderIOModels.TalentSelection>())
                .Where(selection => selection.IncludeInSummary)
                .Select(GetTalentLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
            if (talents.Count == 0) return;
            text.AppendLine();
            text.AppendLine($"## {title}");
            text.AppendLine(string.Join(" • ", talents));
        }

        private static string GetTalentLabel(RaiderIOModels.TalentSelection selection)
        {
            var entries = selection?.Node?.Entries;
            if (entries == null || entries.Length == 0) return null;
            var index = Math.Clamp(selection.EntryIndex, 0, entries.Length - 1);
            var name = entries[index]?.Spell?.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;
            return selection.Rank > 1 ? $"{name} {selection.Rank}" : name;
        }

        private static void AppendGoal(StringBuilder text, string name, double? target, double score)
        {
            if (!target.HasValue) return;
            if (score >= target.Value)
            {
                text.AppendLine($"✅ **{name}:** {target.Value:N0} · reached");
            }
            else
            {
                text.AppendLine($"▫️ **{name}:** {target.Value:N0} · {(target.Value - score):N1} remaining");
            }
        }

        private static void AppendPace(StringBuilder text, string label, RaiderIOModels.PaceSnapshot pace)
        {
            if (pace?.Percentile == null)
            {
                text.AppendLine($"**{label}:** insufficient sample");
                return;
            }
            text.AppendLine($"**{label}:** {pace.Percentile:N1} percentile · {pace.PopulationCount:N0} runs");
        }

        private static string BuildTalentBuildsUrl(RaiderIOModels.RioMythicPlusChar rio)
        {
            var spec = Slug(rio?.ActiveSpecName);
            var characterClass = Slug(rio?.Class);
            return string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(characterClass)
                ? "https://raider.io/specs"
                : $"https://raider.io/specs/{spec}-{characterClass}/talents";
        }

        private static string BuildWarbandUrl(RaiderIOModels.RioMythicPlusChar rio)
        {
            var season = rio?.MythicPlusScores?.FirstOrDefault()?.Season;
            if (string.IsNullOrWhiteSpace(season)
                || season.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
            {
                return "https://raider.io/mythic-plus-rankings";
            }

            return $"https://raider.io/mythic-plus-rankings/{season}/all/world/leaderboards?leaderboards=warband";
        }

        private static string Slug(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : string.Join("-", value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static string FormatDuration(long milliseconds)
        {
            if (milliseconds <= 0) return "—";
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }
    }
}
