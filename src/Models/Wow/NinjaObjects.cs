using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class NinjaObjects
    {
        public class GuildObject
        {
            public string guildName { get; set; }
            public string realmName { get; set; }
            public string regionName { get; set; }
            public string locale { get; set; }
            public string realmSlug { get; set; }
        }
    }
}
