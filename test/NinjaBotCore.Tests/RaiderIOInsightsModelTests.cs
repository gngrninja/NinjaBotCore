using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RaiderIOInsightsModelTests
    {
        [Fact]
        public void CharacterProfile_DeserializesCoachRunsCountsAndCategorizedTalents()
        {
            const string json = """
            {
              "name": "Odib",
              "last_crawled_at": "2026-08-23T05:52:31.000Z",
              "mythic_plus_best_runs": [{
                "dungeon": "Den of Nalorakk",
                "short_name": "DON",
                "mythic_level": 17,
                "completed_at": "2026-08-23T05:32:10.000Z",
                "clear_time_ms": 1709162,
                "keystone_run_id": 2447383,
                "par_time_ms": 1920999,
                "map_challenge_mode_id": 586,
                "zone_id": 16368,
                "score": 444.1,
                "spec": { "id": 71, "name": "Arms", "slug": "arms", "role": "dps" }
              }],
              "mythic_plus_alternate_runs": [{
                "dungeon": "Den of Nalorakk",
                "mythic_level": 16,
                "score": 427.2
              }],
              "mythic_plus_dungeon_run_counts": [{
                "zone_id": 16368,
                "dungeon": "Den of Nalorakk",
                "short_name": "DON",
                "season_runs_total": 8,
                "season_runs_timed": 7
              }],
              "talentLoadout": {
                "loadout_spec_id": 71,
                "loadout_text": "IMPORT-CODE",
                "class_talents": [{
                  "node": {
                    "important": true,
                    "entries": [{ "spell": { "id": 97462, "name": "Rallying Cry", "icon": "ability_warrior_rallyingcry" } }]
                  },
                  "entryIndex": 0,
                  "rank": 1,
                  "includeInSummary": true
                }],
                "spec_talents": [],
                "hero_talents": [],
                "active_hero_tree": { "id": 60, "name": "Slayer", "slug": "slayer" }
              }
            }
            """;

            var result = JsonConvert.DeserializeObject<RaiderIOModels.RioMythicPlusChar>(json);

            Assert.Equal(2026, result.LastCrawledAt?.Year);
            var best = Assert.Single(result.MythicPlusBestRuns);
            Assert.Equal(2447383, best.KeystoneRunId);
            Assert.Equal(16368, best.ZoneId);
            Assert.Equal(71, best.Spec.Id);
            Assert.Single(result.MythicPlusAlternateRuns);
            var count = Assert.Single(result.MythicPlusDungeonRunCounts);
            Assert.Equal(8, count.SeasonRunsTotal);
            Assert.Equal(7, count.SeasonRunsTimed);
            Assert.Equal("IMPORT-CODE", result.TalentLoadout.LoadoutText);
            Assert.Equal("Slayer", result.TalentLoadout.ActiveHeroTree.Name);
            var summaryTalent = Assert.Single(result.TalentLoadout.ClassTalents);
            Assert.True(summaryTalent.IncludeInSummary);
            Assert.Equal("Rallying Cry", summaryTalent.Node.Entries[0].Spell.Name);
        }
    }
}
