namespace NinjaBotCore.Common
{
    /// <summary>
    /// Helper methods for WoW realm name/slug handling.
    /// </summary>
    public static class RealmHelper
    {
        /// <summary>
        /// Converts a realm name to its URL-safe slug format.
        /// Examples:
        ///   "Sisters of Elune" → "sisters-of-elune"
        ///   "Kul'Tiras" → "kultiras"
        ///   "Area 52" → "area-52"
        /// </summary>
        public static string ToSlug(string realmName)
        {
            if (string.IsNullOrWhiteSpace(realmName))
                return string.Empty;

            return realmName
                .Replace(" ", "-")
                .Replace("'", "")
                .ToLowerInvariant();
        }
    }
}
