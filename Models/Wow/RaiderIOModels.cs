using System;
using NinjaBotCore.Models.Wow;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class RaiderIOModels 
    {
        public partial class Affix
        {
            [JsonProperty("region")]
            public string Region { get; set; }

            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("leaderboard_url")]
            public Uri LeaderboardUrl { get; set; }

            [JsonProperty("affix_details")]
            public AffixDetail[] AffixDetails { get; set; }
        }

        public partial class AffixDetail
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("wowhead_url")]
            public Uri WowheadUrl { get; set; }
        }


        public partial class RioGuildInfo
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("faction")]
            public string Faction { get; set; }

            [JsonProperty("region")]
            public string Region { get; set; }

            [JsonProperty("realm")]
            public string Realm { get; set; }

            [JsonProperty("profile_url")]
            public Uri ProfileUrl { get; set; }

            [JsonProperty("raid_rankings")]
            public RaidRankings RaidRankings { get; set; }

            [JsonProperty("raid_progression")]
            public RaidProgression RaidProgression { get; set; }
        }

        public partial class RaidProgression
        {
            [JsonProperty("antorus-the-burning-throne")]
            public RaidProgressionAntorusTheBurningThrone AntorusTheBurningThrone { get; set; }

            [JsonProperty("the-emerald-nightmare")]
            public RaidProgressionAntorusTheBurningThrone TheEmeraldNightmare { get; set; }

            [JsonProperty("the-nighthold")]
            public RaidProgressionAntorusTheBurningThrone TheNighthold { get; set; }

            [JsonProperty("tomb-of-sargeras")]
            public RaidProgressionAntorusTheBurningThrone TombOfSargeras { get; set; }

            [JsonProperty("trial-of-valor")]
            public RaidProgressionAntorusTheBurningThrone TrialOfValor { get; set; }

            [JsonProperty("uldir")]
            public RaidProgressionAntorusTheBurningThrone Uldir { get; set; }

            [JsonProperty("battle-of-dazaralor")]
            public RaidProgressionAntorusTheBurningThrone BattleOfDazaralor { get; set; }
        }

        public partial class RaidProgressionAntorusTheBurningThrone
        {
            [JsonProperty("summary")]
            public string Summary { get; set; }

            [JsonProperty("total_bosses")]
            public long TotalBosses { get; set; }

            [JsonProperty("normal_bosses_killed")]
            public long NormalBossesKilled { get; set; }

            [JsonProperty("heroic_bosses_killed")]
            public long HeroicBossesKilled { get; set; }

            [JsonProperty("mythic_bosses_killed")]
            public long MythicBossesKilled { get; set; }
        }

        public partial class RaidRankings
        {
            [JsonProperty("antorus-the-burning-throne")]
            public RaidRankingsAntorusTheBurningThrone AntorusTheBurningThrone { get; set; }

            [JsonProperty("the-emerald-nightmare")]
            public RaidRankingsAntorusTheBurningThrone TheEmeraldNightmare { get; set; }

            [JsonProperty("the-nighthold")]
            public RaidRankingsAntorusTheBurningThrone TheNighthold { get; set; }

            [JsonProperty("tomb-of-sargeras")]
            public RaidRankingsAntorusTheBurningThrone TombOfSargeras { get; set; }

            [JsonProperty("trial-of-valor")]
            public RaidRankingsAntorusTheBurningThrone TrialOfValor { get; set; }

            [JsonProperty("uldir")]
            public RaidRankingsAntorusTheBurningThrone Uldir { get; set; }

            [JsonProperty("battle-of-dazaralor")]
            public RaidRankingsAntorusTheBurningThrone BattleOfDazaralor { get; set; }
        }

        public partial class RaidRankingsAntorusTheBurningThrone
        {
            [JsonProperty("normal")]
            public Heroic Normal { get; set; }

            [JsonProperty("heroic")]
            public Heroic Heroic { get; set; }

            [JsonProperty("mythic")]
            public Heroic Mythic { get; set; }
        }

        public partial class Heroic
        {
            [JsonProperty("world")]
            public long World { get; set; }

            [JsonProperty("region")]
            public long Region { get; set; }

            [JsonProperty("realm")]
            public long Realm { get; set; }
        }
        
        public partial class Mythic
        {
            [JsonProperty("world")]
            public long World { get; set; }

            [JsonProperty("region")]
            public long Region { get; set; }

            [JsonProperty("realm")]
            public long Realm { get; set; }
        }

        public partial class Normal
        {
            [JsonProperty("world")]
            public long World { get; set; }

            [JsonProperty("region")]
            public long Region { get; set; }

            [JsonProperty("realm")]
            public long Realm { get; set; }
        }

        public partial class RioMythicPlusChar
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("race")]
            public string Race { get; set; }

            [JsonProperty("class")]
            public string Class { get; set; }

            [JsonProperty("active_spec_name")]
            public string ActiveSpecName { get; set; }

            [JsonProperty("active_spec_role")]
            public string ActiveSpecRole { get; set; }

            [JsonProperty("gender")]
            public string Gender { get; set; }

            [JsonProperty("faction")]
            public string Faction { get; set; }

            [JsonProperty("achievement_points")]
            public long AchievementPoints { get; set; }

            [JsonProperty("honorable_kills")]
            public long HonorableKills { get; set; }

            [JsonProperty("thumbnail_url")]
            public Uri ThumbnailUrl { get; set; }

            [JsonProperty("region")]
            public string Region { get; set; }

            [JsonProperty("realm")]
            public string Realm { get; set; }

            [JsonProperty("profile_url")]
            public Uri ProfileUrl { get; set; }

            [JsonProperty("mythic_plus_scores")]
            public MythicPlusScores MythicPlusScores { get; set; }

            [JsonProperty("mythic_plus_ranks")]
            public MythicPlusRanks MythicPlusRanks { get; set; }

            [JsonProperty("mythic_plus_recent_runs")]
            public MythicPlusRun[] MythicPlusRecentRuns { get; set; }

            [JsonProperty("mythic_plus_best_runs")]
            public MythicPlusRun[] MythicPlusBestRuns { get; set; }

            [JsonProperty("mythic_plus_highest_level_runs")]
            public MythicPlusRun[] MythicPlusHighestLevelRuns { get; set; }

            [JsonProperty("raid_progression")]
            public RaidProgression RaidProgression { get; set; }
        }

        public partial class MythicPlusRun
        {
            [JsonProperty("dungeon")]
            public string Dungeon { get; set; }

            [JsonProperty("short_name")]
            public string ShortName { get; set; }

            [JsonProperty("mythic_level")]
            public long MythicLevel { get; set; }

            [JsonProperty("completed_at")]
            public DateTimeOffset CompletedAt { get; set; }

            [JsonProperty("clear_time_ms")]
            public long ClearTimeMs { get; set; }

            [JsonProperty("num_keystone_upgrades")]
            public long NumKeystoneUpgrades { get; set; }

            [JsonProperty("map_challenge_mode_id")]
            public long MapChallengeModeId { get; set; }

            [JsonProperty("score")]
            public double Score { get; set; }

            [JsonProperty("affixes")]
            public AffixInfo[] Affixes { get; set; }

            [JsonProperty("url")]
            public Uri Url { get; set; }
        }

        public partial class AffixInfo
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("wowhead_url")]
            public Uri WowheadUrl { get; set; }
        }

        public partial class MythicPlusRanks
        {
            [JsonProperty("overall")]
            public Class Overall { get; set; }

            [JsonProperty("dps")]
            public Class Dps { get; set; }

            [JsonProperty("healer")]
            public Class Healer { get; set; }

            [JsonProperty("tank")]
            public Class Tank { get; set; }

            [JsonProperty("class")]
            public Class Class { get; set; }

            [JsonProperty("class_dps")]
            public Class ClassDps { get; set; }

            [JsonProperty("class_healer")]
            public Class ClassHealer { get; set; }

            [JsonProperty("class_tank")]
            public Class ClassTank { get; set; }
        }

        public partial class Class
        {
            [JsonProperty("world")]
            public long World { get; set; }

            [JsonProperty("region")]
            public long Region { get; set; }

            [JsonProperty("realm")]
            public long Realm { get; set; }
        }

        public partial class MythicPlusScores
        {
            [JsonProperty("all")]
            public double All { get; set; }

            [JsonProperty("dps")]
            public double Dps { get; set; }

            [JsonProperty("healer")]
            public long Healer { get; set; }

            [JsonProperty("tank")]
            public long Tank { get; set; }
        }
    }
}
