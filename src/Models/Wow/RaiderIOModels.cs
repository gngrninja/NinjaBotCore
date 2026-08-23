using System;
using System.Collections.Generic;
using NinjaBotCore.Models.Wow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Models.Wow
{
    public class RaiderIOModels
    {
        // --- Mythic+ static-data (dungeon rotation), from /mythic-plus/static-data ---
        public class MythicPlusStaticData
        {
            [JsonProperty("seasons")]
            public List<MythicPlusSeason> Seasons { get; set; }
        }

        public class MythicPlusSeason
        {
            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("short_name")]
            public string ShortName { get; set; }

            // true = canonical numbered season; false = event variant (break-the-meta, cutoffs, etc.)
            [JsonProperty("is_main_season")]
            public bool IsMainSeason { get; set; }

            [JsonProperty("starts")]
            public Dictionary<string, DateTimeOffset?> Starts { get; set; }

            [JsonProperty("ends")]
            public Dictionary<string, DateTimeOffset?> Ends { get; set; }

            [JsonProperty("dungeons")]
            public List<MythicPlusStaticDungeon> Dungeons { get; set; }
        }

        public class MythicPlusStaticDungeon
        {
            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("short_name")]
            public string ShortName { get; set; }
        }

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
            public Dictionary<string, RaidRankingsEntry> RaidRankings { get; set; }

            [JsonProperty("raid_progression")]
            public Dictionary<string, RaidProgressionEntry> RaidProgression { get; set; }
        }

        public partial class RaidProgressionEntry
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

        public partial class RaidRankingsEntry
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

            [JsonProperty("last_crawled_at")]
            public DateTimeOffset? LastCrawledAt { get; set; }

            [JsonProperty("gear")]
            public Gear Gear { get; set; }

            [JsonProperty("mythic_plus_scores_by_season")]
            public MythicPlusScores[] MythicPlusScores { get; set; }

            [JsonProperty("mythic_plus_ranks")]
            public MythicPlusRanks MythicPlusRanks { get; set; }

            [JsonProperty("mythic_plus_recent_runs")]
            public MythicPlusRun[] MythicPlusRecentRuns { get; set; }

            [JsonProperty("mythic_plus_best_runs")]
            public MythicPlusRun[] MythicPlusBestRuns { get; set; }

            [JsonProperty("mythic_plus_alternate_runs")]
            public MythicPlusRun[] MythicPlusAlternateRuns { get; set; }

            [JsonProperty("mythic_plus_dungeon_run_counts")]
            public MythicPlusDungeonRunCount[] MythicPlusDungeonRunCounts { get; set; }

            [JsonProperty("talentLoadout")]
            public TalentLoadout TalentLoadout { get; set; }

            [JsonProperty("mythic_plus_highest_level_runs")]
            public MythicPlusRun[] MythicPlusHighestLevelRuns { get; set; }

            [JsonProperty("mythic_plus_weekly_highest_level_runs")]
            public MythicPlusRun[] MythicPlusWeeklyHighestLevelRuns { get; set; }

            [JsonProperty("mythic_plus_previous_weekly_highest_level_runs")]
            public MythicPlusRun[] MythicPlusPreviousWeeklyHighestLevelRuns { get; set; }

            [JsonProperty("raid_progression")]
            public Dictionary<string, RaidProgressionEntry> RaidProgression { get; set; }

            [JsonProperty("raid_achievement_meta")]
            [JsonConverter(typeof(DictionaryOrEmptyArrayConverter<RaidAchievement>))]
            public Dictionary<string, RaidAchievement> RaidAchievementMeta { get; set; }

            [JsonProperty("raid_achievement_curve")]
            [JsonConverter(typeof(DictionaryOrEmptyArrayConverter<RaidAchievement>))]
            public Dictionary<string, RaidAchievement> RaidAchievementCurve { get; set; }
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

            [JsonProperty("keystone_run_id")]
            public long KeystoneRunId { get; set; }

            [JsonProperty("par_time_ms")]
            public long ParTimeMs { get; set; }

            [JsonProperty("num_keystone_upgrades")]
            public long NumKeystoneUpgrades { get; set; }

            [JsonProperty("map_challenge_mode_id")]
            public long MapChallengeModeId { get; set; }

            [JsonProperty("zone_id")]
            public long ZoneId { get; set; }

            [JsonProperty("icon_url")]
            public string IconUrl { get; set; }

            [JsonProperty("background_image_url")]
            public string BackgroundImageUrl { get; set; }

            [JsonProperty("spec")]
            public MythicPlusSpec Spec { get; set; }

            [JsonProperty("role")]
            public string Role { get; set; }

            [JsonProperty("score")]
            public double Score { get; set; }

            [JsonProperty("affixes")]
            public AffixInfo[] Affixes { get; set; }

            [JsonProperty("url")]
            public Uri Url { get; set; }
        }

        public class MythicPlusDungeonRunCount
        {
            [JsonProperty("zone_id")]
            public long ZoneId { get; set; }

            [JsonProperty("dungeon")]
            public string Dungeon { get; set; }

            [JsonProperty("short_name")]
            public string ShortName { get; set; }

            [JsonProperty("season_runs_total")]
            public int SeasonRunsTotal { get; set; }

            [JsonProperty("season_runs_timed")]
            public int SeasonRunsTimed { get; set; }
        }

        public class MythicPlusSpec
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("class_id")]
            public long ClassId { get; set; }

            [JsonProperty("role")]
            public string Role { get; set; }
        }

        public class TalentLoadout
        {
            [JsonProperty("loadout_spec_id")]
            public long LoadoutSpecId { get; set; }

            [JsonProperty("loadout_text")]
            public string LoadoutText { get; set; }

            [JsonProperty("class_talents")]
            public TalentSelection[] ClassTalents { get; set; }

            [JsonProperty("spec_talents")]
            public TalentSelection[] SpecTalents { get; set; }

            [JsonProperty("hero_talents")]
            public TalentSelection[] HeroTalents { get; set; }

            [JsonProperty("active_hero_tree")]
            public HeroTalentTree ActiveHeroTree { get; set; }
        }

        public class TalentSelection
        {
            [JsonProperty("node")]
            public TalentNode Node { get; set; }

            [JsonProperty("entryIndex")]
            public int EntryIndex { get; set; }

            [JsonProperty("rank")]
            public int Rank { get; set; }

            [JsonProperty("grantedNode")]
            public bool GrantedNode { get; set; }

            [JsonProperty("includeInSummary")]
            public bool IncludeInSummary { get; set; }
        }

        public class TalentNode
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("important")]
            public bool Important { get; set; }

            [JsonProperty("entries")]
            public TalentEntry[] Entries { get; set; }
        }

        public class TalentEntry
        {
            [JsonProperty("spell")]
            public TalentSpell Spell { get; set; }
        }

        public class TalentSpell
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("icon")]
            public string Icon { get; set; }
        }

        public class HeroTalentTree
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("iconUrl")]
            public string IconUrl { get; set; }
        }

        public class CharacterRivalsResponse
        {
            [JsonProperty("rivals")]
            public RivalWindow Rivals { get; set; }
        }

        public class RivalWindow
        {
            [JsonProperty("scope")]
            public string Scope { get; set; }

            [JsonProperty("specId")]
            public long? SpecId { get; set; }

            [JsonProperty("selfRank")]
            public long? SelfRank { get; set; }

            [JsonProperty("fullRankingPath")]
            public string FullRankingPath { get; set; }

            [JsonProperty("entries")]
            public RivalEntry[] Entries { get; set; }
        }

        public class RivalEntry
        {
            [JsonProperty("rank")]
            public long Rank { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("realm")]
            public string Realm { get; set; }

            [JsonProperty("realmSlug")]
            public string RealmSlug { get; set; }

            [JsonProperty("regionSlug")]
            public string RegionSlug { get; set; }

            [JsonProperty("score")]
            public double Score { get; set; }

            [JsonProperty("scoreColor")]
            public string ScoreColor { get; set; }

            [JsonProperty("isSelf")]
            public bool IsSelf { get; set; }

            [JsonProperty("classId")]
            public long? ClassId { get; set; }

            [JsonProperty("specId")]
            public long? SpecId { get; set; }
        }

        public class RunReviewResponse
        {
            [JsonProperty("percentile")]
            public double? Percentile { get; set; }

            [JsonProperty("historicalPercentile")]
            public double? HistoricalPercentile { get; set; }

            [JsonProperty("historicalLocked")]
            public bool HistoricalLocked { get; set; }

            [JsonProperty("runScore")]
            public double? RunScore { get; set; }

            [JsonProperty("keystonePace")]
            public KeystonePace KeystonePace { get; set; }

            [JsonProperty("pastRuns")]
            public PastDungeonRun[] PastRuns { get; set; }

            [JsonProperty("rivals")]
            public RivalWindow Rivals { get; set; }
        }

        public class KeystonePace
        {
            [JsonProperty("current")]
            public PaceSnapshot Current { get; set; }

            [JsonProperty("historical")]
            public PaceSnapshot Historical { get; set; }

            [JsonProperty("itemLevel")]
            public ItemLevelPace ItemLevel { get; set; }
        }

        public class PaceSnapshot
        {
            [JsonProperty("percentile")]
            public double? Percentile { get; set; }

            [JsonProperty("populationCount")]
            public long PopulationCount { get; set; }

            [JsonProperty("generatedAt")]
            public DateTimeOffset? GeneratedAt { get; set; }

            [JsonProperty("period")]
            public long? Period { get; set; }

            [JsonProperty("locked")]
            public bool Locked { get; set; }
        }

        public class ItemLevelPace
        {
            [JsonProperty("average")]
            public double Average { get; set; }

            [JsonProperty("min")]
            public double Min { get; set; }

            [JsonProperty("maxExclusive")]
            public double MaxExclusive { get; set; }

            [JsonProperty("current")]
            public PaceSnapshot Current { get; set; }

            [JsonProperty("historical")]
            public PaceSnapshot Historical { get; set; }
        }

        public class PastDungeonRun
        {
            [JsonProperty("completedAt")]
            public DateTimeOffset CompletedAt { get; set; }

            [JsonProperty("keyLevel")]
            public int KeyLevel { get; set; }

            [JsonProperty("clearTimeMs")]
            public long ClearTimeMs { get; set; }

            [JsonProperty("timed")]
            public bool Timed { get; set; }

            [JsonProperty("score")]
            public double? Score { get; set; }
        }

        public class SeasonCutoffsResponse
        {
            [JsonProperty("cutoffs")]
            public SeasonCutoffs Cutoffs { get; set; }
        }

        public class SeasonCutoffs
        {
            [JsonProperty("updatedAt")]
            public string UpdatedAt { get; set; }

            [JsonProperty("region")]
            public RegionSummary Region { get; set; }

            [JsonProperty("keystoneExplorer")]
            public CutoffThreshold KeystoneExplorer { get; set; }

            [JsonProperty("keystoneConqueror")]
            public CutoffThreshold KeystoneConqueror { get; set; }

            [JsonProperty("keystoneMaster")]
            public CutoffThreshold KeystoneMaster { get; set; }

            [JsonProperty("keystoneHero")]
            public CutoffThreshold KeystoneHero { get; set; }

            [JsonProperty("keystoneLegend")]
            public CutoffThreshold KeystoneLegend { get; set; }

            [JsonProperty("keystoneMyth")]
            public CutoffThreshold KeystoneMyth { get; set; }

            [JsonProperty("p999")]
            public CutoffThreshold P999 { get; set; }

            [JsonProperty("p990")]
            public CutoffThreshold P990 { get; set; }

            [JsonProperty("p900")]
            public CutoffThreshold P900 { get; set; }
        }

        public class CutoffThreshold
        {
            [JsonProperty("score")]
            public double? Score { get; set; }

            [JsonProperty("all")]
            public CutoffPopulation All { get; set; }
        }

        public class CutoffPopulation
        {
            [JsonProperty("quantile")]
            public double Quantile { get; set; }

            [JsonProperty("quantileMinValue")]
            public double QuantileMinValue { get; set; }

            [JsonProperty("quantilePopulationCount")]
            public long QuantilePopulationCount { get; set; }

            [JsonProperty("totalPopulationCount")]
            public long TotalPopulationCount { get; set; }
        }

        public class RegionSummary
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("short_name")]
            public string ShortName { get; set; }
        }

        public class LeaderboardCapacityResponse
        {
            [JsonProperty("realmListing")]
            public LeaderboardCapacityListing RealmListing { get; set; }
        }

        public class LeaderboardCapacityListing
        {
            [JsonProperty("region")]
            public RegionSummary Region { get; set; }

            [JsonProperty("realms")]
            public LeaderboardCapacityRealm[] Realms { get; set; }
        }

        public class LeaderboardCapacityRealm
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("connectedRealms")]
            public CapacityRealmSummary[] ConnectedRealms { get; set; }

            [JsonProperty("dungeons")]
            public LeaderboardCapacityDungeon[] Dungeons { get; set; }
        }

        public class CapacityRealmSummary
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }
        }

        public class LeaderboardCapacityDungeon
        {
            [JsonProperty("dungeon")]
            public CapacityDungeonSummary Dungeon { get; set; }

            [JsonProperty("lowest")]
            public CapacityLowestRun Lowest { get; set; }
        }

        public class CapacityDungeonSummary
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("short_name")]
            public string ShortName { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }
        }

        public class CapacityLowestRun
        {
            [JsonProperty("rank")]
            public int Rank { get; set; }

            [JsonProperty("mythicLevel")]
            public int MythicLevel { get; set; }

            [JsonProperty("timeInMilliseconds")]
            public long TimeInMilliseconds { get; set; }
        }

        public class GuildLiveRaidResponse
        {
            [JsonProperty("guild")]
            public LiveGuildSummary Guild { get; set; }

            [JsonProperty("guildPrivacy")]
            public LiveRaidPrivacy GuildPrivacy { get; set; }

            [JsonProperty("raid")]
            public LiveRaidSummary Raid { get; set; }

            [JsonProperty("bosses")]
            public LiveRaidBoss[] Bosses { get; set; }
        }

        public class LiveGuildSummary
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("path")]
            public string Path { get; set; }
        }

        public class LiveRaidPrivacy
        {
            [JsonProperty("raidPulls")]
            public bool RaidPulls { get; set; }

            [JsonProperty("raidPercents")]
            public bool RaidPercents { get; set; }

            [JsonProperty("wereRaidPullsRestricted")]
            public bool WereRaidPullsRestricted { get; set; }

            [JsonProperty("wereRaidPercentsRestricted")]
            public bool WereRaidPercentsRestricted { get; set; }
        }

        public class LiveRaidSummary
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }
        }

        public class LiveRaidBoss
        {
            [JsonProperty("boss")]
            public LiveRaidEncounter Boss { get; set; }

            [JsonProperty("bestPercent")]
            public double? BestPercent { get; set; }

            [JsonProperty("pullCount")]
            public int? PullCount { get; set; }

            [JsonProperty("pullEndedAt")]
            public DateTimeOffset? PullEndedAt { get; set; }

            [JsonProperty("isDefeated")]
            public bool IsDefeated { get; set; }
        }

        public class LiveRaidEncounter
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("slug")]
            public string Slug { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("ordinal")]
            public int Ordinal { get; set; }
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

        public partial class MythicPlusScoreBreakout
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

        public partial class MythicPlusScores
        {
            [JsonProperty("season")]
            public string Season { get; set; }

            [JsonProperty("scores")]
            public MythicPlusScoreBreakout Scores { get; set; }
        }

        public partial class Gear
        {
            [JsonProperty("item_level_equipped")]
            public long ItemLevelEquipped { get; set; }

            [JsonProperty("item_level_total")]
            public long ItemLevelTotal { get; set; }

            [JsonProperty("artifact_traits")]
            public long ArtifactTraits { get; set; }

            [JsonProperty("corruption")]
            public Corruption Corruption { get; set; }

            [JsonProperty("items")]
            [JsonConverter(typeof(GearItemConverter))]
            public GearItem? Items { get; set; }
        }

        public partial class Corruption
        {
            [JsonProperty("added")]
            public long Added { get; set; }

            [JsonProperty("resisted")]
            public long Resisted { get; set; }

            [JsonProperty("total")]
            public long Total { get; set; }

            [JsonProperty("cloakRank")]
            public long CloakRank { get; set; }

            [JsonProperty("spells")]
            public object[] Spells { get; set; }
        }

        public partial class GearItem
        {
            [JsonProperty("head")]
            public ItemDetail Head { get; set; }

            [JsonProperty("neck")]
            public ItemDetail Neck { get; set; }

            [JsonProperty("shoulder")]
            public ItemDetail Shoulder { get; set; }

            [JsonProperty("back")]
            public ItemDetail Back { get; set; }

            [JsonProperty("chest")]
            public ItemDetail Chest { get; set; }

            [JsonProperty("waist")]
            public ItemDetail Waist { get; set; }

            [JsonProperty("wrist")]
            public ItemDetail Wrist { get; set; }

            [JsonProperty("hands")]
            public ItemDetail Hands { get; set; }

            [JsonProperty("legs")]
            public ItemDetail Legs { get; set; }

            [JsonProperty("feet")]
            public ItemDetail Feet { get; set; }

            [JsonProperty("finger1")]
            public ItemDetail Finger1 { get; set; }

            [JsonProperty("finger2")]
            public ItemDetail Finger2 { get; set; }

            [JsonProperty("trinket1")]
            public ItemDetail Trinket1 { get; set; }

            [JsonProperty("trinket2")]
            public ItemDetail Trinket2 { get; set; }

            [JsonProperty("mainhand")]
            public ItemDetail Mainhand { get; set; }

            [JsonProperty("offhand")]
            public ItemDetail Offhand { get; set; }
        }

        public partial class ItemDetail
        {
            [JsonProperty("item_id")]
            public long ItemId { get; set; }

            [JsonProperty("item_level")]
            public long ItemLevel { get; set; }

            [JsonProperty("icon")]
            public string Icon { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("item_quality")]
            public long ItemQuality { get; set; }

            [JsonProperty("is_legendary")]
            public bool IsLegendary { get; set; }

            [JsonProperty("is_azerite_armor")]
            public bool IsAzeriteArmor { get; set; }

            [JsonProperty("azerite_powers")]
            public object[] AzeritePowers { get; set; }

            [JsonProperty("corruption")]
            public Corruption Corruption { get; set; }

            [JsonProperty("domination_shards")]
            public object[] DominationShards { get; set; }

            [JsonProperty("tier_set")]
            public object TierSet { get; set; }

            [JsonProperty("enchant")]
            public long? Enchant { get; set; }

            [JsonProperty("bonuses")]
            public long[] Bonuses { get; set; }

            [JsonProperty("gems")]
            public long[] Gems { get; set; }
        }

        public partial class RaidAchievement
        {
            [JsonProperty("aotc")]
            public bool Aotc { get; set; }

            [JsonProperty("cutting_edge")]
            public bool CuttingEdge { get; set; }
        }
    }

    /// <summary>
    /// Custom converter to handle RaiderIO API inconsistency where 'items' can be either an object or an empty array
    /// </summary>
    public class GearItemConverter : JsonConverter<RaiderIOModels.GearItem>
    {
        public override RaiderIOModels.GearItem ReadJson(JsonReader reader, Type objectType, RaiderIOModels.GearItem existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            // Load the JSON token
            var token = JToken.Load(reader);

            // If it's an array (empty or otherwise), return null
            // This handles the edge case where RaiderIO returns "items": [] instead of an object
            if (token.Type == JTokenType.Array)
            {
                return null;
            }

            // If it's an object, deserialize normally
            if (token.Type == JTokenType.Object)
            {
                return token.ToObject<RaiderIOModels.GearItem>(serializer);
            }

            // If it's null or any other type, return null
            return null;
        }

        public override void WriteJson(JsonWriter writer, RaiderIOModels.GearItem value, JsonSerializer serializer)
        {
            // We don't serialize back to RaiderIO, so just use default serialization
            serializer.Serialize(writer, value);
        }
    }

    /// <summary>
    /// Generic converter to handle RaiderIO API inconsistency where dictionary fields can be either an object or an empty array.
    /// Used for raid_achievement_meta and raid_achievement_curve which return [] instead of {} when empty.
    /// </summary>
    public class DictionaryOrEmptyArrayConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
    {
        public override Dictionary<string, TValue> ReadJson(JsonReader reader, Type objectType, Dictionary<string, TValue> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);

            if (token.Type == JTokenType.Array)
            {
                return null;
            }

            if (token.Type == JTokenType.Object)
            {
                return token.ToObject<Dictionary<string, TValue>>(serializer);
            }

            return null;
        }

        public override void WriteJson(JsonWriter writer, Dictionary<string, TValue> value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }
    }
}
