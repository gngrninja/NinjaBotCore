#nullable enable

using System.Globalization;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Models.Wow.Forever
{
    /// <summary>
    /// Forever character names are two parts (first + surname), unique per region with no realm.
    /// Blizzard hasn't published length/charset rules, so these bounds are the bot's own sanity limits.
    /// </summary>
    public static class ForeverNames
    {
        public const int MinLength = 2;
        public const int MaxLength = 24;
        public const int MaxStoredLength = 64;
        public const int MaxGuildNameLength = 64;

        /// <summary>Cleans one name part for display: trimmed, NFC, first letter capitalised, otherwise as typed.</summary>
        public static bool TryCleanPart(string? input, string label, out string display, out string error)
        {
            display = string.Empty;
            error = string.Empty;

            var value = (input ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
            if (value.Length == 0)
            {
                error = $"{label} is required.";
                return false;
            }

            // Text elements for the visible length; the char cap keeps stacked accents inside the column.
            var length = new StringInfo(value).LengthInTextElements;
            if (length < MinLength || length > MaxLength || value.Length > MaxStoredLength)
            {
                error = $"{label} must be {MinLength}–{MaxLength} characters.";
                return false;
            }

            if (!value.All(IsNameChar))
            {
                error = $"{label} can only use letters, apostrophes and hyphens.";
                return false;
            }

            if (!char.IsLetter(value[0]))
            {
                error = $"{label} must start with a letter.";
                return false;
            }

            display = char.ToUpperInvariant(value[0]) + value[1..];
            return true;
        }

        /// <summary>Comparison key: same NFC form, case-insensitive. Accents and punctuation still count.</summary>
        public static string Normalize(string display)
            => display.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();

        public static string FullName(string first, string last) => $"{first} {last}";

        public static bool TryCleanGuildName(string? input, out string name, out string error)
        {
            name = (input ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
            error = string.Empty;

            if (name.Length == 0)
            {
                error = "Guild name is required.";
                return false;
            }

            if (name.Length > MaxGuildNameLength)
            {
                error = $"Guild name must be {MaxGuildNameLength} characters or fewer.";
                return false;
            }

            if (name.Any(char.IsControl))
            {
                error = "Guild name can't contain line breaks or control characters.";
                return false;
            }

            return true;
        }

        private static bool IsNameChar(char c)
        {
            if (char.IsLetter(c)) return true;
            if (c == '\'' || c == '’' || c == '-') return true;
            var category = char.GetUnicodeCategory(c);
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark;
        }
    }
}
