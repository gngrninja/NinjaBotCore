using System;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class TimeParserTests
    {
        private static void AssertCloseTo(DateTime expectedUtc, DateTime? actual, int toleranceSeconds = 5)
        {
            Assert.NotNull(actual);
            var delta = Math.Abs((actual!.Value - expectedUtc).TotalSeconds);
            Assert.True(delta <= toleranceSeconds, $"Expected ~{expectedUtc:O}, got {actual:O} (off by {delta}s)");
        }

        // --- relative forms ---------------------------------------------------

        [Theory]
        [InlineData("in 30", 30)]
        [InlineData("in 30m", 30)]
        [InlineData("in 30 min", 30)]
        [InlineData("in 45 minutes", 45)]
        [InlineData("IN 90M", 90)]
        [InlineData("in 999", 999)]
        public void Relative_minutes(string input, int minutes)
        {
            AssertCloseTo(DateTime.UtcNow.AddMinutes(minutes), TimeParser.TryParse(input));
        }

        [Theory]
        [InlineData("in 2h", 120)]
        [InlineData("in 2 hours", 120)]
        [InlineData("in 1hr", 60)]
        [InlineData("in 1h30m", 90)]
        [InlineData("in 1h 30", 90)]
        [InlineData("in 2h15min", 135)]
        public void Relative_hours_and_combined(string input, int minutes)
        {
            AssertCloseTo(DateTime.UtcNow.AddMinutes(minutes), TimeParser.TryParse(input));
        }

        // --- Discord timestamp -------------------------------------------------

        [Fact]
        public void Discord_timestamp_is_exact()
        {
            // 2100-01-01T00:00:00Z — far future so the past-instant guard never trips.
            var result = TimeParser.TryParse("<t:4102444800:F>");
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800).UtcDateTime, result);
        }

        [Fact]
        public void Past_instants_are_rejected()
        {
            // 2020-01-01T00:00:00Z as a Discord timestamp, and a past calendar date via the
            // DateTime.TryParse fallback — both name explicit instants in the past.
            Assert.Null(TimeParser.TryParse("<t:1577836800:F>"));
            Assert.Null(TimeParser.TryParse("2020-01-01"));
        }

        // --- absolute forms (UTC default) ---------------------------------------

        [Theory]
        [InlineData("20:00", 20, 0)]
        [InlineData("8pm", 20, 0)]
        [InlineData("8 PM", 20, 0)]
        [InlineData("8:30pm", 20, 30)]
        [InlineData("12am", 0, 0)]
        [InlineData("12pm", 12, 0)]
        [InlineData("8pm utc", 20, 0)]
        [InlineData("20:00 UTC", 20, 0)]
        public void Absolute_defaults_to_utc(string input, int hour, int minute)
        {
            var result = TimeParser.TryParse(input);
            Assert.NotNull(result);
            Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
            Assert.Equal(new TimeSpan(hour, minute, 0), result.Value.TimeOfDay);
            var untilStart = result.Value - DateTime.UtcNow;
            Assert.True(untilStart > TimeSpan.Zero && untilStart <= TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(2)),
                $"Expected next occurrence within 24h, got {untilStart}");
        }

        // --- absolute forms with explicit UTC offset ----------------------------

        [Theory]
        [InlineData("8pm -5", -5, 0, 20, 0)]
        [InlineData("8pm-5", -5, 0, 20, 0)]
        [InlineData("20:00+2", 2, 0, 20, 0)]
        [InlineData("20:00 UTC+2", 2, 0, 20, 0)]
        [InlineData("18:30 utc+5:30", 5, 30, 18, 30)]
        [InlineData("8:15pm -4", -4, 0, 20, 15)]
        public void Absolute_with_offset_is_wall_clock_in_that_offset(string input, int offH, int offM, int hour, int minute)
        {
            var result = TimeParser.TryParse(input);
            Assert.NotNull(result);
            Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);

            var offset = new TimeSpan(Math.Abs(offH), offM, 0);
            if (offH < 0) offset = offset.Negate();
            var localWallClock = result.Value + offset;
            Assert.Equal(new TimeSpan(hour, minute, 0), localWallClock.TimeOfDay);

            var untilStart = result.Value - DateTime.UtcNow;
            Assert.True(untilStart > TimeSpan.Zero && untilStart <= TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(2)),
                $"Expected next occurrence within 24h, got {untilStart}");
        }

        // --- rejects -------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("in")]
        [InlineData("in h")]
        [InlineData("25:00")]
        [InlineData("8:60pm")]
        [InlineData("20:00+15")]
        [InlineData("in 100h")]
        public void Unparseable_returns_null(string input)
        {
            Assert.Null(TimeParser.TryParse(input));
        }
    }
}
