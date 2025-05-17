using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowCharAssociation
    {
        [Key]
        public long Id { get; set; }
        public Nullable<long> UserId { get; set; }
        public bool IsMain { get; set; }
        public string CharName { get; set; }
        public string ServerName { get; set; }
        public string WowGuild { get; set; }
        public string WowRealm { get; set; }
        public string WowRegion { get; set; }
        public string LocalRealmSlug { get; set; }        
        public string Locale { get; set; }                
        public Nullable<System.DateTime> TimeSet { get; set; }
    }
}