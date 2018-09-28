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
        public string Locale { get; set; }
        public string SetBy { get; set; }
        public Nullable<long> SetById { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }
    }
}