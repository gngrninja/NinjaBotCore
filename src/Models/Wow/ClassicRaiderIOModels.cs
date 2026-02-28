using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ClassicRaiderIOModels
    {
        public class ClassicCharProfile
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("race")]
            public string Race { get; set; }

            [JsonProperty("class")]
            public string Class { get; set; }

            [JsonProperty("gender")]
            public string Gender { get; set; }

            [JsonProperty("faction")]
            public string Faction { get; set; }

            [JsonProperty("level")]
            public int Level { get; set; }

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

            [JsonProperty("gear")]
            public ClassicGear Gear { get; set; }

            [JsonProperty("talents")]
            public ClassicTalents Talents { get; set; }

            [JsonProperty("guild")]
            public ClassicGuildRef Guild { get; set; }

            [JsonProperty("raid_progression")]
            public Dictionary<string, ClassicRaidProgressionEntry> RaidProgression { get; set; }
        }

        public class ClassicGear
        {
            [JsonProperty("item_level_equipped")]
            public long ItemLevelEquipped { get; set; }

            [JsonProperty("item_level_total")]
            public long ItemLevelTotal { get; set; }

            [JsonProperty("items")]
            public ClassicGearItem Items { get; set; }
        }

        public class ClassicGearItem
        {
            [JsonProperty("head")]
            public ClassicItemDetail Head { get; set; }

            [JsonProperty("neck")]
            public ClassicItemDetail Neck { get; set; }

            [JsonProperty("shoulder")]
            public ClassicItemDetail Shoulder { get; set; }

            [JsonProperty("back")]
            public ClassicItemDetail Back { get; set; }

            [JsonProperty("chest")]
            public ClassicItemDetail Chest { get; set; }

            [JsonProperty("waist")]
            public ClassicItemDetail Waist { get; set; }

            [JsonProperty("wrist")]
            public ClassicItemDetail Wrist { get; set; }

            [JsonProperty("hands")]
            public ClassicItemDetail Hands { get; set; }

            [JsonProperty("legs")]
            public ClassicItemDetail Legs { get; set; }

            [JsonProperty("feet")]
            public ClassicItemDetail Feet { get; set; }

            [JsonProperty("finger1")]
            public ClassicItemDetail Finger1 { get; set; }

            [JsonProperty("finger2")]
            public ClassicItemDetail Finger2 { get; set; }

            [JsonProperty("trinket1")]
            public ClassicItemDetail Trinket1 { get; set; }

            [JsonProperty("trinket2")]
            public ClassicItemDetail Trinket2 { get; set; }

            [JsonProperty("mainhand")]
            public ClassicItemDetail Mainhand { get; set; }

            [JsonProperty("offhand")]
            public ClassicItemDetail Offhand { get; set; }

            [JsonProperty("ranged")]
            public ClassicItemDetail Ranged { get; set; }
        }

        public class ClassicItemDetail
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
        }

        public class ClassicRaidProgressionEntry
        {
            [JsonProperty("summary")]
            public string Summary { get; set; }

            [JsonProperty("total_bosses")]
            public long TotalBosses { get; set; }

            // Classic MoP API uses "normal10" not "normal_10" (no underscore before number)
            [JsonProperty("normal10_bosses_killed")]
            public long Normal10BossesKilled { get; set; }

            [JsonProperty("normal25_bosses_killed")]
            public long Normal25BossesKilled { get; set; }

            [JsonProperty("heroic10_bosses_killed")]
            public long Heroic10BossesKilled { get; set; }

            [JsonProperty("heroic25_bosses_killed")]
            public long Heroic25BossesKilled { get; set; }
        }

        public class ClassicTalents
        {
            [JsonProperty("spec_name")]
            public string SpecName { get; set; }

            [JsonProperty("spec_role")]
            public string SpecRole { get; set; }

            [JsonProperty("trees")]
            public List<ClassicTalentTree> Trees { get; set; }
        }

        public class ClassicTalentTree
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("points")]
            public int Points { get; set; }
        }

        public class ClassicGuildRef
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("realm")]
            public string Realm { get; set; }
        }

        public class ClassicGuildProfile
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

            [JsonProperty("raid_progression")]
            public Dictionary<string, ClassicRaidProgressionEntry> RaidProgression { get; set; }
        }
    }
}
