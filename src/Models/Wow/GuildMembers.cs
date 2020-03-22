using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class GuildMembers
    {                                      
        public Member[] members { get; set; }
        public Emblem emblem { get; set; }
        public WowGuild guild { get; set; }        
    }

    public class WowRealmResponseGuild
    {
        public int id { get; set; }
        public string slug { get; set; }
    }

    public class FactionInfo 
    {
        [JsonProperty(PropertyName = "type")]
        public string _type { get; set; }            
    }

    public class WowGuild
    {
        public string name { get; set; }
        public int id { get; set; }
        public WowRealmResponseGuild realm { get; set; } 
        public FactionInfo faction { get; set; }
    }
    
    public class Emblem
    {
        public int icon { get; set; }
        public string iconColor { get; set; }
        public int iconColorId { get; set; }
        public int border { get; set; }
        public string borderColor { get; set; }
        public int borderColorId { get; set; }
        public string backgroundColor { get; set; }
        public int backgroundColorId { get; set; }
    }

    public class Member
    {
        public Character character { get; set; }
        public int rank { get; set; }
    }

    public class Spec
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }
}
