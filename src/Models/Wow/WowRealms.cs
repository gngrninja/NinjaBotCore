using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class WowRealm
    {
        public Realm[] realms { get; set; }        

        public class Realm
        {
            public string type { get; set; }
            public string population { get; set; }
            public bool queue { get; set; }
            public bool status { get; set; }
            public string name { get; set; }
            public string slug { get; set; }
            public string battlegroup { get; set; }
            public string locale { get; set; }
            public string timezone { get; set; }
            public string[] connected_realms { get; set; }
        }
    }
}
