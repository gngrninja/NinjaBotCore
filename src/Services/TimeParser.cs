#nullable enable

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Parses lightweight human-friendly time strings for /pushgroup scheduling.
    /// Accepts: "in 30", "in 1h", "20:00", "8pm", or a Discord timestamp "<t:1234567890:...>".
    /// Returns UTC DateTime, or null if unparseable.
    /// </summary>
    public static class TimeParser
    {
        private static readonly Regex InMinutes = new(@"^in\s+(\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InHours = new(@"^in\s+(\d{1,2})\s*h(?:ours?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DiscordTs = new(@"<t:(\d+)(?::[a-zA-Z])?>", RegexOptions.Compiled);
        private static readonly Regex Hour24 = new(@"^(\d{1,2}):(\d{2})$", RegexOptions.Compiled);
        private static readonly Regex Hour12 = new(@"^(\d{1,2})\s*(am|pm)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static DateTime? TryParse(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = input.Trim();
            var now = DateTime.UtcNow;

            var m = DiscordTs.Match(s);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var unix))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            m = InMinutes.Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var mins))
            {
                return now.AddMinutes(mins);
            }

            m = InHours.Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var hrs))
            {
                return now.AddHours(hrs);
            }

            m = Hour24.Match(s);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out var h24)
                && int.TryParse(m.Groups[2].Value, out var min24)
                && h24 is >= 0 and < 24 && min24 is >= 0 and < 60)
            {
                return NextOccurrenceOfTime(now, h24, min24);
            }

            m = Hour12.Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var h12) && h12 is >= 1 and <= 12)
            {
                var hour = m.Groups[2].Value.ToLowerInvariant() == "pm"
                    ? (h12 == 12 ? 12 : h12 + 12)
                    : (h12 == 12 ? 0 : h12);
                return NextOccurrenceOfTime(now, hour, 0);
            }

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static DateTime NextOccurrenceOfTime(DateTime baseUtc, int hour, int minute)
        {
            var candidate = new DateTime(baseUtc.Year, baseUtc.Month, baseUtc.Day, hour, minute, 0, DateTimeKind.Utc);
            if (candidate <= baseUtc.AddMinutes(1)) candidate = candidate.AddDays(1);
            return candidate;
        }
    }
}
