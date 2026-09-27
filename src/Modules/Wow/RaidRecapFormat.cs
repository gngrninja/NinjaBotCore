using System;
using System.Globalization;

namespace NinjaBotCore.Modules.Wow;

/// <summary>
/// Display formatting shared by the raid recap views. Matches the conventions of the
/// /char WarcraftLogs cards: compact numbers, m:ss durations and short difficulty labels.
/// Untrusted provider numbers are bounded before formatting.
/// </summary>
public static class RaidRecapFormat
{
    public const string Unknown = "—";
    public const string Source = "WarcraftLogs";

    private static readonly double MaxMs = TimeSpan.FromDays(365).TotalMilliseconds;

    /// <summary>1234567 becomes 1.23M, 45678 becomes 45.7K.</summary>
    public static string Compact(double? value)
    {
        if (value is not >= 0 || !double.IsFinite(value.Value))
        {
            return Unknown;
        }

        var v = value.Value;
        var culture = CultureInfo.InvariantCulture;
        return v switch
        {
            >= 1e15 => v.ToString("0.##E+0", culture),
            >= 1e12 => (v / 1e12).ToString("0.00", culture) + "T",
            >= 1e9 => (v / 1e9).ToString("0.00", culture) + "B",
            >= 1e6 => (v / 1e6).ToString("0.00", culture) + "M",
            >= 1e3 => (v / 1e3).ToString("0.0", culture) + "K",
            _ => v.ToString("0", culture)
        };
    }

    /// <summary>Whole counts such as deaths or interrupts.</summary>
    public static string Count(double? value)
    {
        if (value is not >= 0 || !double.IsFinite(value.Value))
        {
            return Unknown;
        }

        return value.Value >= 1e15
            ? value.Value.ToString("0.##E+0", CultureInfo.InvariantCulture)
            : value.Value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>A positive duration as m:ss, or h:mm:ss from one hour.</summary>
    public static string Clock(double? ms)
    {
        if (ms is not > 0)
        {
            return Unknown;
        }

        return Elapsed(ms.Value);
    }

    /// <summary>A timestamp inside a pull as m:ss. Zero is a valid value.</summary>
    public static string Elapsed(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0 || ms > MaxMs)
        {
            return Unknown;
        }

        var span = TimeSpan.FromMilliseconds(ms);
        var days = span.Days > 0 ? $"{span.Days}d " : "";
        var clock = span.Days > 0 || span.Hours > 0
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        return days + clock;
    }

    /// <summary>A long span such as a raid night: 3h 12m or 45m.</summary>
    public static string Span(double? ms)
    {
        if (ms is not > 0 || !double.IsFinite(ms.Value) || ms > MaxMs)
        {
            return Unknown;
        }

        var span = TimeSpan.FromMilliseconds(ms.Value);
        var hours = (int)span.TotalHours;
        if (hours > 0)
        {
            return $"{hours}h {span.Minutes:00}m";
        }

        return span.Minutes > 0 ? $"{span.Minutes}m" : $"{span.Seconds}s";
    }

    public static string Percent(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
        : Unknown;

    public static string Difficulty(int? id) => id switch
    {
        1 => "LFR",
        3 => "Normal",
        4 => "Heroic",
        5 => "Mythic",
        null => "Unknown difficulty",
        _ => $"Difficulty {id}"
    };

    public static string DifficultyShort(int? id) => id switch
    {
        1 => "LFR",
        3 => "N",
        4 => "H",
        5 => "M",
        _ => "?"
    };

    public static string OutcomeEmoji(RaidRecapFight fight) =>
        fight.IsKill ? "✅" : fight.IsWipe ? "💀" : "⏳";

    public static string Outcome(RaidRecapFight fight) =>
        fight.IsKill ? "Kill" : fight.IsWipe ? "Wipe" : "In progress";

    public static string Plural(int count, string singular, string plural = null) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");

    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13
            ? "th"
            : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    public static string RoleEmoji(string role) => role switch
    {
        "tanks" => "🛡️",
        "healers" => "💚",
        "dps" => "⚔️",
        _ => "👤"
    };

    /// <summary>Provider class identifiers are PascalCase without spaces, e.g. DeathKnight.</summary>
    public static string ClassName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var result = new System.Text.StringBuilder(value.Length + 2);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && char.IsLower(value[i - 1]))
            {
                result.Append(' ');
            }

            result.Append(value[i]);
        }

        return RaidRecapRules.Text(result.ToString(), 30);
    }
}
