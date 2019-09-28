using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    class ProgressGuildRanks
    {
        public class GuildRank
        {
            public int score { get; set; }
            public int world_rank { get; set; }
            public int area_rank { get; set; }
            public int realm_rank { get; set; }
        }

        public class Ranking
        {
            public int score { get; set; }
            public int world_rank { get; set; }
            public int area_rank { get; set; }
            public int realm_rank { get; set; }
            public string name { get; set; }
            public string url { get; set; }
        }
    }
}
