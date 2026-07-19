using System;
using System.Linq;
using NinjaBotCore.Common;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class MythicPlusWeeklyTests
    {
        // --- WeekStartUtc -------------------------------------------------------

        [Theory]
        // US reset: Tuesday 15:00 UTC. 2026-06-30 was a Tuesday.
        [InlineData("us", "2026-06-30T14:59:00Z", "2026-06-23T15:00:00Z")] // just before reset → previous window
        [InlineData("us", "2026-06-30T15:00:00Z", "2026-06-30T15:00:00Z")] // exactly at reset → new window
        [InlineData("us", "2026-07-03T09:00:00Z", "2026-06-30T15:00:00Z")] // mid-week
        [InlineData("US", "2026-07-06T23:59:00Z", "2026-06-30T15:00:00Z")] // case-insensitive
        [InlineData(null, "2026-07-03T09:00:00Z", "2026-06-30T15:00:00Z")] // default region = us
        // EU reset: Wednesday 07:00 UTC. 2026-07-01 was a Wednesday.
        [InlineData("eu", "2026-07-01T06:59:00Z", "2026-06-24T07:00:00Z")]
        [InlineData("eu", "2026-07-01T07:00:00Z", "2026-07-01T07:00:00Z")]
        [InlineData("eu", "2026-07-06T23:00:00Z", "2026-07-01T07:00:00Z")]
        public void WeekStartUtc_matches_region_reset(string region, string nowIso, string expectedIso)
        {
            var now = DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var expected = DateTime.Parse(expectedIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

            var result = MythicPlusWeekly.WeekStartUtc(region, now);

            Assert.Equal(expected, result);
            Assert.Equal(DateTimeKind.Utc, result.Kind);
        }

        [Fact]
        public void WeekStartUtc_is_never_in_the_future_and_at_most_7_days_back()
        {
            var now = DateTime.UtcNow;
            foreach (var region in new[] { "us", "eu", "kr", null })
            {
                var start = MythicPlusWeekly.WeekStartUtc(region, now);
                Assert.True(start <= now);
                Assert.True(now - start < TimeSpan.FromDays(7));
            }
        }

        // --- Vault math -----------------------------------------------------------

        [Fact]
        public void Vault_zero_runs_all_locked()
        {
            var p = MythicPlusWeekly.VaultFromRunLevels(Array.Empty<int>());
            Assert.Equal(0, p.SlotsUnlocked);
            Assert.Equal(1, p.RunsToNextSlot);
            Assert.Null(p.Slot1Level);
        }

        [Fact]
        public void Vault_one_run_unlocks_slot1_with_that_level()
        {
            var p = MythicPlusWeekly.VaultFromRunLevels(new[] { 14 });
            Assert.Equal(1, p.SlotsUnlocked);
            Assert.Equal(14, p.Slot1Level);
            Assert.Null(p.Slot2Level);
            Assert.Equal(3, p.RunsToNextSlot); // 4 - 1
        }

        [Fact]
        public void Vault_levels_come_from_sorted_runs_not_input_order()
        {
            // 5 runs: slot1 = highest (16), slot2 = 4th-highest (10).
            var p = MythicPlusWeekly.VaultFromRunLevels(new[] { 10, 16, 12, 9, 14 });
            Assert.Equal(2, p.SlotsUnlocked);
            Assert.Equal(16, p.Slot1Level);
            Assert.Equal(10, p.Slot2Level);
            Assert.Null(p.Slot3Level);
            Assert.Equal(3, p.RunsToNextSlot); // 8 - 5
        }

        [Fact]
        public void Vault_eight_runs_unlocks_everything()
        {
            var levels = new[] { 18, 17, 16, 15, 14, 13, 12, 11 };
            var p = MythicPlusWeekly.VaultFromRunLevels(levels);
            Assert.Equal(3, p.SlotsUnlocked);
            Assert.Equal(18, p.Slot1Level);
            Assert.Equal(15, p.Slot2Level);
            Assert.Equal(11, p.Slot3Level);
            Assert.Equal(0, p.RunsToNextSlot);
        }

        [Fact]
        public void Vault_more_than_eight_runs_uses_the_best_eight()
        {
            var levels = Enumerable.Range(1, 10).ToArray(); // 1..10
            var p = MythicPlusWeekly.VaultFromRunLevels(levels);
            Assert.Equal(10, p.RunCount);
            Assert.Equal(10, p.Slot1Level);
            Assert.Equal(7, p.Slot2Level);  // 4th highest of 10,9,8,7,...
            Assert.Equal(3, p.Slot3Level);  // 8th highest
        }
    }
}
