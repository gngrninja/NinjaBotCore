using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class GuildMembers
    {
        public long lastModified { get; set; }
        public string name { get; set; }
        public string realm { get; set; }
        public string battlegroup { get; set; }
        public int level { get; set; }
        public int side { get; set; }
        public int achievementPoints { get; set; }
        public Member[] members { get; set; }
        public Emblem emblem { get; set; }
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
