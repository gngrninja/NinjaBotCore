using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class WarcraftlogRankings
    {
        public class RankingObject
        {
            public int total { get; set; }
            public Ranking[] rankings { get; set; }
        }

        public class Ranking
        {
            public string name { get; set; }
            [JsonProperty(PropertyName = "class")]
            public int _class { get; set; }
            public int spec { get; set; }
            public decimal total { get; set; }
            public int duration { get; set; }
            public long startTime { get; set; }
            public int fightID { get; set; }
            public string reportID { get; set; }
            public string guild { get; set; }
            public string server { get; set; }
            public string region { get; set; }
            public int itemLevel { get; set; }
            public int exploit { get; set; }
            public Talent[] talents { get; set; }
            public Gear[] gear { get; set; }
            public int rankid { get; set; }
            public int size { get; set; }
        }

        public class Talent
        {
            public string name { get; set; }
            public int? id { get; set; }
        }

        public class Gear
        {
            public string name { get; set; }
            public string quality { get; set; }
            public int? id { get; set; }
        }
    }
}
