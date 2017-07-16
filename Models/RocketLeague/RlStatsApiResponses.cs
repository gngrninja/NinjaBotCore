using System.Collections.Generic;

namespace NinjaBotCore.Models.RocketLeague
{
    public class Platforms
    {
        public List<Value> value { get; set; }
        public int Count { get; set; }
    }
    public class Value
    {
        public int id { get; set; }
        public string name { get; set; }
    }
}