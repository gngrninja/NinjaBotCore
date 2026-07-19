#nullable enable

using System.Linq;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// The one realm/dungeon-name → raider.io slug normalizer. If the input already looks
    /// like a slug (lowercase/dashes/digits) it passes through; otherwise lowercase,
    /// spaces → dashes, apostrophes dropped.
    /// </summary>
    public static class WowRealmSlug
    {
        public static string From(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var trimmed = name.Trim();
            if (trimmed.All(ch => char.IsLower(ch) || ch == '-' || char.IsDigit(ch))) return trimmed;
            return new string(trimmed.ToLowerInvariant()
                .Select(ch => ch switch { ' ' => '-', '\'' => '\0', _ => ch })
                .Where(ch => ch != '\0').ToArray());
        }
    }
}
