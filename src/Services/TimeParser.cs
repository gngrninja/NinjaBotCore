#nullable enable

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Parses lightweight human-friendly time strings for /keys scheduling.
    /// Relative forms are timezone-proof: "in 30", "in 90m", "in 2h", "in 1h30m".
    /// Absolute forms ("20:00", "8pm", "8:30pm") are interpreted as UTC unless a UTC offset
    /// is appended ("8pm -5", "20:00+2", "18:30 utc+5:30"); a bare trailing "utc" is accepted.
    /// Also accepts a Discord timestamp "&lt;t:1234567890:...&gt;".
    /// Returns UTC DateTime, or null if unparseable. Inputs that name an explicit instant
    /// (Discord timestamps, full dates) are rejected when that instant is in the past —
    /// this parser exists to schedule things, and "starts 4 hours ago" is never intended.
    /// </summary>
    public static class TimeParser
    {
        private static readonly Regex Relative = new(
            @"^in\s+(?:(\d{1,2})\s*h(?:(?:ou)?rs?)?)?\s*(?:(\d{1,3})\s*(?:m(?:in(?:ute)?s?)?)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DiscordTs = new(@"<t:(\d+)(?::[a-zA-Z])?>", RegexOptions.Compiled);
        private static readonly Regex Hour24 = new(@"^(\d{1,2}):(\d{2})$", RegexOptions.Compiled);
        private static readonly Regex Hour12 = new(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OffsetSuffix = new(@"(?:\s*utc)?\s*([+-])(\d{1,2})(?::(\d{2}))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UtcSuffix = new(@"\s+utc\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static DateTime? TryParse(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = input.Trim();
            var now = DateTime.UtcNow;

            var m = DiscordTs.Match(s);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var unix))
            {
                return FutureOrNull(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, now);
            }

            m = Relative.Match(s);
            if (m.Success && (m.Groups[1].Success || m.Groups[2].Success))
            {
                var hrs = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                var mins = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                return now.AddHours(hrs).AddMinutes(mins);
            }

            // Absolute forms: strip an optional UTC-offset suffix first ("8pm -5", "20:00+5:30",
            // "18:00 utc+2"), or a bare "utc" marker. Times without an offset are UTC.
            var abs = s;
            var offset = TimeSpan.Zero;
            var offsetInvalid = false;
            var om = OffsetSuffix.Match(abs);
            if (om.Success)
            {
                var oh = int.Parse(om.Groups[2].Value, CultureInfo.InvariantCulture);
                var omin = om.Groups[3].Success ? int.Parse(om.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                if (oh <= 14 && omin < 60)
                {
                    offset = new TimeSpan(oh, omin, 0);
                    if (om.Groups[1].Value == "-") offset = offset.Negate();
                    abs = abs[..om.Index];
                }
                else
                {
                    offsetInvalid = true;
                }
            }
            else
            {
                var um = UtcSuffix.Match(abs);
                if (um.Success) abs = abs[..um.Index];
            }

            if (!offsetInvalid)
            {
                m = Hour24.Match(abs);
                if (m.Success
                    && int.TryParse(m.Groups[1].Value, out var h24)
                    && int.TryParse(m.Groups[2].Value, out var min24)
                    && h24 is >= 0 and < 24 && min24 is >= 0 and < 60)
                {
                    return NextOccurrenceOfTime(now, h24, min24, offset);
                }

                m = Hour12.Match(abs);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var h12) && h12 is >= 1 and <= 12)
                {
                    var min12 = 0;
                    if (m.Groups[2].Success && (!int.TryParse(m.Groups[2].Value, out min12) || min12 is < 0 or >= 60))
                    {
                        return null;
                    }
                    var hour = m.Groups[3].Value.ToLowerInvariant() == "pm"
                        ? (h12 == 12 ? 12 : h12 + 12)
                        : (h12 == 12 ? 0 : h12);
                    return NextOccurrenceOfTime(now, hour, min12, offset);
                }
            }

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return FutureOrNull(parsed, now);
            }

            return null;
        }

        private static DateTime? FutureOrNull(DateTime candidateUtc, DateTime nowUtc) =>
            candidateUtc > nowUtc ? candidateUtc : null;

        /// <summary>
        /// Next occurrence of hour:minute in the wall-clock frame at the given UTC offset,
        /// converted back to UTC.
        /// </summary>
        private static DateTime NextOccurrenceOfTime(DateTime baseUtc, int hour, int minute, TimeSpan offset)
        {
            var baseLocal = baseUtc + offset;
            var candidate = new DateTime(baseLocal.Year, baseLocal.Month, baseLocal.Day, hour, minute, 0, DateTimeKind.Unspecified);
            if (candidate <= baseLocal.AddMinutes(1)) candidate = candidate.AddDays(1);
            return DateTime.SpecifyKind(candidate - offset, DateTimeKind.Utc);
        }
    }
}
