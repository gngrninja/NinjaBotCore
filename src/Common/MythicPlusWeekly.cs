#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Weekly-reset and Great Vault math for Mythic+. Pure functions — unit-tested.
    /// </summary>
    public static class MythicPlusWeekly
    {
        /// <summary>
        /// Start of the current weekly reset window in UTC for a region.
        /// us (and default): Tuesday 15:00 UTC · eu: Wednesday 07:00 UTC.
        /// </summary>
        public static DateTime WeekStartUtc(string? region, DateTime nowUtc)
        {
            var (day, hour) = (region?.ToLowerInvariant()) switch
            {
                "eu" => (DayOfWeek.Wednesday, 7),
                _ => (DayOfWeek.Tuesday, 15),
            };

            var candidate = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, 0, 0, DateTimeKind.Utc);
            var daysBack = ((int)nowUtc.DayOfWeek - (int)day + 7) % 7;
            candidate = candidate.AddDays(-daysBack);
            if (candidate > nowUtc) candidate = candidate.AddDays(-7);
            return candidate;
        }

        /// <summary>
        /// Floor for "rows belonging to the current reset window": every region's reset is
        /// weekly, so anything younger than 7 days is current in its own region. All week-window
        /// queries use this one helper so a future tightening to true per-region resets
        /// (WeekStartUtc above) happens in exactly one place.
        /// </summary>
        public static DateTime CurrentWeekFloorUtc(DateTime nowUtc) => nowUtc.AddDays(-7);

        /// <summary>Great Vault M+ row thresholds.</summary>
        public const int VaultSlot1Runs = 1;
        public const int VaultSlot2Runs = 4;
        public const int VaultSlot3Runs = 8;

        /// <param name="RunCount">Completed M+ runs this week (top-10 window).</param>
        /// <param name="Slot1Level">Key level backing vault slot 1 (highest run), null if locked.</param>
        /// <param name="Slot2Level">Key level backing vault slot 2 (4th-highest run), null if locked.</param>
        /// <param name="Slot3Level">Key level backing vault slot 3 (8th-highest run), null if locked.</param>
        public record VaultProgress(int RunCount, int? Slot1Level, int? Slot2Level, int? Slot3Level)
        {
            public int SlotsUnlocked =>
                (Slot1Level.HasValue ? 1 : 0) + (Slot2Level.HasValue ? 1 : 0) + (Slot3Level.HasValue ? 1 : 0);

            /// <summary>Runs still needed to unlock the next slot; 0 when all three are open.</summary>
            public int RunsToNextSlot => RunCount switch
            {
                < VaultSlot1Runs => VaultSlot1Runs - RunCount,
                < VaultSlot2Runs => VaultSlot2Runs - RunCount,
                < VaultSlot3Runs => VaultSlot3Runs - RunCount,
                _ => 0,
            };
        }

        /// <summary>Vault progress from this week's run key levels (order irrelevant).</summary>
        public static VaultProgress VaultFromRunLevels(IEnumerable<int> runLevels)
        {
            var sorted = runLevels.OrderByDescending(x => x).ToList();
            int? At(int n) => sorted.Count >= n ? sorted[n - 1] : null;
            return new VaultProgress(sorted.Count, At(VaultSlot1Runs), At(VaultSlot2Runs), At(VaultSlot3Runs));
        }
    }
}
