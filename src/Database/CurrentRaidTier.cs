using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class CurrentRaidTier
    {
        [Key]
        public long Id { get; set; }
        public long WclZoneId { get; set; }
        public string RaidName { get; set; }
        public int? Partition { get; set; }
    }
}
