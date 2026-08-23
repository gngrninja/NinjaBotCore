using System;

namespace NinjaBotCore.Common
{
    public static class RaiderIoLinks
    {
        private const string Origin = "https://raider.io";

        public static string FromRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !path.StartsWith("/", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal)
                || path.Contains('\\'))
            {
                return Origin;
            }

            return IsAllowed(new Uri(Origin + path, UriKind.Absolute), out var url)
                ? url
                : Origin;
        }

        public static string FromAbsolute(Uri uri) =>
            IsAllowed(uri, out var url) ? url : Origin;

        private static bool IsAllowed(Uri uri, out string url)
        {
            url = null;
            if (uri == null
                || !uri.IsAbsoluteUri
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "raider.io", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            url = uri.AbsoluteUri;
            return true;
        }
    }
}
