using Newtonsoft.Json;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RaiderIOInsightEndpointModelTests
    {
        [Fact]
        public void SeasonCutoffContractRequiresExplicitSeason()
        {
            var method = typeof(IRaiderIOApi).GetMethod(nameof(IRaiderIOApi.GetSeasonCutoffsAsync));

            Assert.False(method.GetParameters()[1].IsOptional);
        }

        [Fact]
        public void InsightResponses_DeserializeDiscordFacingFields()
        {
            const string rivalsJson = """
            { "rivals": { "scope": "region", "specId": 71, "selfRank": 4,
              "fullRankingPath": "/mythic-plus-spec-rankings/season-mn-2/us/warrior/arms",
              "entries": [
                { "rank": 3, "name": "Ahead", "realm": "Stormrage", "realmSlug": "stormrage", "regionSlug": "us", "score": 3436.1, "isSelf": false, "classId": 1, "specId": 71 },
                { "rank": 4, "name": "Odib", "realm": "Tichondrius", "realmSlug": "tichondrius", "regionSlug": "us", "score": 3427.1, "isSelf": true, "classId": 1, "specId": 71 }
              ] } }
            """;
            const string reviewJson = """
            { "percentile": 78.1, "historicalPercentile": 76.4, "historicalLocked": false,
              "runScore": 444.1,
              "keystonePace": { "current": { "percentile": 78.1, "populationCount": 33, "generatedAt": "2026-08-23T14:26:17Z" },
                "itemLevel": { "average": 275.4, "min": 273, "maxExclusive": 278,
                  "current": { "percentile": 91.2, "populationCount": 12, "generatedAt": "2026-08-23T14:26:17Z" } } },
              "pastRuns": [{ "completedAt": "2026-08-23T05:32:10Z", "keyLevel": 17, "clearTimeMs": 1709162, "timed": true, "score": 444.1 }],
              "rivals": { "scope": "region", "specId": 71, "selfRank": 4, "entries": [] } }
            """;
            const string cutoffsJson = """
            { "cutoffs": { "updatedAt": "Sun Aug 23 2026 14:04:47 GMT+0000", "region": { "name": "United States & Oceania", "slug": "us", "short_name": "US" },
              "keystoneExplorer": { "score": 1000 }, "keystoneConqueror": { "score": 1500 },
              "keystoneMaster": { "score": 2000 }, "keystoneHero": { "score": 2500 },
              "keystoneLegend": { "score": 3000 }, "keystoneMyth": { "score": 3400 },
              "p999": { "all": { "quantileMinValue": 3236.69, "totalPopulationCount": 287259 } } } }
            """;
            const string capacityJson = """
            { "realmListing": { "region": { "name": "United States & Oceania", "slug": "us", "short_name": "US" },
              "realms": [{ "id": 236, "connectedRealms": [{ "name": "Tichondrius", "slug": "tichondrius" }],
                "dungeons": [{ "dungeon": { "id": 16368, "name": "Den of Nalorakk", "short_name": "DON", "slug": "den-of-nalorakk" },
                  "lowest": { "rank": 500, "mythicLevel": 12, "timeInMilliseconds": 1850000 } }] }] } }
            """;

            var rivals = JsonConvert.DeserializeObject<RaiderIOModels.CharacterRivalsResponse>(rivalsJson);
            var review = JsonConvert.DeserializeObject<RaiderIOModels.RunReviewResponse>(reviewJson);
            var cutoffs = JsonConvert.DeserializeObject<RaiderIOModels.SeasonCutoffsResponse>(cutoffsJson);
            var capacity = JsonConvert.DeserializeObject<RaiderIOModels.LeaderboardCapacityResponse>(capacityJson);

            Assert.Equal(4, rivals.Rivals.SelfRank);
            Assert.Equal(3436.1, rivals.Rivals.Entries[0].Score);
            Assert.Equal(78.1, review.KeystonePace.Current.Percentile);
            Assert.Equal(91.2, review.KeystonePace.ItemLevel.Current.Percentile);
            Assert.Equal(17, review.PastRuns[0].KeyLevel);
            Assert.Equal(3000, cutoffs.Cutoffs.KeystoneLegend.Score);
            Assert.Equal(3236.69, cutoffs.Cutoffs.P999.All.QuantileMinValue);
            var lowest = capacity.RealmListing.Realms[0].Dungeons[0].Lowest;
            Assert.Equal(500, lowest.Rank);
            Assert.Equal(12, lowest.MythicLevel);
            Assert.Equal(1850000, lowest.TimeInMilliseconds);
        }
    }
}
