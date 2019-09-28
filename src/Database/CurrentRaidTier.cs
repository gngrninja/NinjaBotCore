using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class CurrentRaidTier
    {
        [Key]
        public int Id { get; set; }
        public int WclZoneId { get; set; }
        public string RaidName { get; set; }
        public int? Partition { get; set; }
    }
}
