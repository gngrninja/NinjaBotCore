using System;
using System.Linq;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Pins the Raider.IO /mythic-plus/static-data model to the real API shape and verifies that
    /// season selection picks the canonical main season, never an event variant. The fixture below
    /// is a verbatim (trimmed) slice of a real expansion_id=10 response — a real main season
    /// ("season-tww-3") plus a real event variant ("season-tww-3-break-the-meta") that starts later,
    /// including the extra fields the model intentionally ignores (blizzard_season_id, seasonal_affix,
    /// dungeon id/icon_url, top-level dungeons).
    /// </summary>
    public class RaiderIOStaticDataModelTests
    {
        private const string RealStaticData = """
            {
              "dungeons": [
                {
                  "id": 15093,
                  "challenge_mode_id": 503,
                  "slug": "arakara-city-of-echoes",
                  "name": "Ara-Kara, City of Echoes",
                  "short_name": "ARAK",
                  "keystone_timer_seconds": 1800,
                  "icon_url": "https://cdn.raiderio.net/images/wow/icons/large/inv_achievement_dungeon_arak-ara.jpg",
                  "background_image_url": "https://cdn.raiderio.net/images/dungeons/expansion10/base/arakara-city-of-echoes.jpg"
                }
              ],
              "seasons": [
                {
                  "slug": "season-tww-3",
                  "name": "TWW Season 3 • With Pre Patch",
                  "blizzard_season_id": 15,
                  "is_main_season": true,
                  "short_name": "TWW3 (Full)",
                  "seasonal_affix": null,
                  "starts": {
                    "us": "2025-08-12T15:00:00Z",
                    "eu": "2025-08-13T04:00:00Z",
                    "tw": "2025-08-13T23:00:00Z",
                    "kr": "2025-08-13T23:00:00Z",
                    "cn": "2025-08-13T23:00:00Z"
                  },
                  "ends": {
                    "us": "2026-03-02T22:00:00Z",
                    "eu": "2026-03-02T22:00:00Z",
                    "tw": "2026-03-02T22:00:00Z",
                    "kr": "2026-03-02T22:00:00Z",
                    "cn": "2026-03-02T22:00:00Z"
                  },
                  "dungeons": [
                    {
                      "id": 15093,
                      "challenge_mode_id": 503,
                      "slug": "arakara-city-of-echoes",
                      "name": "Ara-Kara, City of Echoes",
                      "short_name": "ARAK",
                      "keystone_timer_seconds": 1800,
                      "icon_url": "https://cdn.raiderio.net/images/wow/icons/large/inv_achievement_dungeon_arak-ara.jpg",
                      "background_image_url": "https://cdn.raiderio.net/images/dungeons/expansion10/base/arakara-city-of-echoes.jpg"
                    },
                    {
                      "id": 16104,
                      "challenge_mode_id": 542,
                      "slug": "ecodome-aldani",
                      "name": "Eco-Dome Al'dani",
                      "short_name": "EDA",
                      "keystone_timer_seconds": 1860,
                      "icon_url": "https://cdn.raiderio.net/images/wow/icons/large/inv_112_achievement_dungeon_ecodome.jpg",
                      "background_image_url": "https://cdn.raiderio.net/images/dungeons/expansion10/base/ecodome-aldani.jpg"
                    }
                  ]
                },
                {
                  "slug": "season-tww-3-break-the-meta",
                  "name": "TWW Season 3 • Break the Meta",
                  "blizzard_season_id": 15,
                  "is_main_season": false,
                  "short_name": "BTM TWW3",
                  "seasonal_affix": null,
                  "starts": {
                    "us": "2025-11-18T15:00:00Z",
                    "eu": "2025-11-19T04:00:00Z",
                    "tw": "2025-11-19T23:00:00Z",
                    "kr": "2025-11-19T23:00:00Z",
                    "cn": "2025-11-19T23:00:00Z"
                  },
                  "ends": {
                    "us": "2025-11-25T15:00:00Z",
                    "eu": "2025-11-26T04:00:00Z",
                    "tw": "2025-11-26T23:00:00Z",
                    "kr": "2025-11-26T23:00:00Z",
                    "cn": "2025-11-26T23:00:00Z"
                  },
                  "dungeons": [
                    {
                      "id": 15093,
                      "challenge_mode_id": 503,
                      "slug": "arakara-city-of-echoes",
                      "name": "Ara-Kara, City of Echoes",
                      "short_name": "ARAK",
                      "keystone_timer_seconds": 1800,
                      "icon_url": "https://cdn.raiderio.net/images/wow/icons/large/inv_achievement_dungeon_arak-ara.jpg",
                      "background_image_url": "https://cdn.raiderio.net/images/dungeons/expansion10/base/arakara-city-of-echoes.jpg"
                    }
                  ]
                }
              ]
            }
            """;

        private static RaiderIOModels.MythicPlusStaticData Parse() =>
            JsonConvert.DeserializeObject<RaiderIOModels.MythicPlusStaticData>(RealStaticData);

        [Fact]
        public void Deserializes_RealResponse_PopulatesEveryUsedField()
        {
            var data = Parse();

            Assert.NotNull(data);
            Assert.NotNull(data.Seasons);
            Assert.Equal(2, data.Seasons.Count);

            var main = data.Seasons.Single(s => s.Slug == "season-tww-3");
            Assert.Equal("TWW Season 3 • With Pre Patch", main.Name);
            Assert.Equal("TWW3 (Full)", main.ShortName);
            Assert.True(main.IsMainSeason);
            Assert.Equal(DateTimeOffset.Parse("2025-08-12T15:00:00Z"), main.Starts["us"]);
            Assert.Equal(DateTimeOffset.Parse("2026-03-02T22:00:00Z"), main.Ends["us"]);

            Assert.Equal(2, main.Dungeons.Count);
            var arak = main.Dungeons.Single(d => d.Slug == "arakara-city-of-echoes");
            Assert.Equal("Ara-Kara, City of Echoes", arak.Name);
            Assert.Equal("ARAK", arak.ShortName);

            var variant = data.Seasons.Single(s => s.Slug == "season-tww-3-break-the-meta");
            Assert.False(variant.IsMainSeason);
        }

        [Fact]
        public void SelectActiveSeason_PrefersMainSeason_WhileVariantIsLive()
        {
            var data = Parse();
            // 2025-11-20 sits inside BOTH season-tww-3 (the real season) and the break-the-meta
            // event window (2025-11-18 → 2025-11-25). "Latest started" would wrongly pick the variant.
            var now = DateTimeOffset.Parse("2025-11-20T00:00:00Z");

            var active = MythicPlusDungeonService.SelectActiveSeason(data.Seasons, now);

            Assert.NotNull(active);
            Assert.Equal("season-tww-3", active.Slug);
            Assert.True(active.IsMainSeason);
        }

        [Fact]
        public void SelectActiveSeason_NeverReturnsVariant_OutsideMainWindow()
        {
            var data = Parse();
            // After the main season ended; the only main season in this slice is season-tww-3,
            // so it falls back to that — and must never return the non-main variant.
            var after = DateTimeOffset.Parse("2026-06-01T00:00:00Z");

            var active = MythicPlusDungeonService.SelectActiveSeason(data.Seasons, after);

            Assert.NotNull(active);
            Assert.True(active.IsMainSeason);
            Assert.Equal("season-tww-3", active.Slug);
        }

        [Fact]
        public void SelectActiveSeason_EmptyOrNull_ReturnsNull()
        {
            Assert.Null(MythicPlusDungeonService.SelectActiveSeason(Array.Empty<RaiderIOModels.MythicPlusSeason>(), DateTimeOffset.UtcNow));
            Assert.Null(MythicPlusDungeonService.SelectActiveSeason(null, DateTimeOffset.UtcNow));
        }
    }
}
