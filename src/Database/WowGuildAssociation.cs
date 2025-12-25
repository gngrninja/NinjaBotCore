using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowGuildAssociations
    {
        [Key]
        public long Id { get; set; }
        public Nullable<long> ServerId { get; set; }
        public string ServerName { get; set; }
        public string WowGuild { get; set; }
        public string WowRealm { get; set; }
        public string WowRegion { get; set; }
        public string LocalRealmSlug { get; set; }
        public string Locale { get; set; }
        public string SetBy { get; set; }
        public Nullable<long> SetById { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }

        // WCL Activity Tracking (nullable for backward compatibility)
        public Nullable<System.DateTime> LastWclReportDate { get; set; }
        public Nullable<System.DateTime> LastWclCheckDate { get; set; }
        public Nullable<int> ActivityTier { get; set; } // 1=Active, 2=Semi-Active, 3=Inactive
        public Nullable<int> ConsecutiveNoReports { get; set; }
        public Nullable<bool> WclGuildExists { get; set; } // null=unknown, true=exists, false=GraphQL error
    }
}