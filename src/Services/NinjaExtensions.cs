using System;
using System.Collections.Generic;

namespace NinjaBotCore.Services
{
    public static class NinjaExtensions
    {
        public static DateTime UnixTimeStampToDateTime(this uint unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddMilliseconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public static DateTime UnixTimeStampToDateTimeSeconds(this uint unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public static string FirstFromSplit(this string source, char delimiter)
        {
            var i = source.IndexOf(delimiter);

            return i == -1 ? source : source.Substring(0, i);
        }

        public static string FirstFromSplit(this string source, string delimiter)
        {
            var i = source.IndexOf(delimiter);

            return i == -1 ? source : source.Substring(0, i);
        }

        public static string OmitFirstFromSplit(this string source, string delimiter)
        {
            var i = source.IndexOf(delimiter) + 1;

            return i == 1 ? source : source.Substring(i);
        }

    }
}