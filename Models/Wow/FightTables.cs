using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class ReportTable
    {
        public Entry[] entries { get; set; }
        public int totalTime { get; set; }
    }

    public class Entry
    {
        public string name { get; set; }
        public int id { get; set; }
        public int guid { get; set; }
        public string type { get; set; }
        public int itemLevel { get; set; }
        public string icon { get; set; }
        public int total { get; set; }
        public int activeTime { get; set; }
        public int activeTimeReduced { get; set; }
        public Ability[] abilities { get; set; }
        public object[] damageAbilities { get; set; }
        public Target[] targets { get; set; }
        public Talent[] talents { get; set; }
        public Gear[] gear { get; set; }
        public int blocked { get; set; }
        public int totalReduced { get; set; }
        public Pet[] pets { get; set; }
    }

    public class Ability
    {
        public string name { get; set; }
        public int total { get; set; }
        public int type { get; set; }
        public int totalReduced { get; set; }
        public string petName { get; set; }
    }

    public class Target
    {
        public string name { get; set; }
        public int total { get; set; }
        public string type { get; set; }
        public int totalReduced { get; set; }
    }

    public class Talent
    {
        public string name { get; set; }
        public int guid { get; set; }
        public int type { get; set; }
    }

    public class Gear
    {
        public int id { get; set; }
        public int slot { get; set; }
        public int itemLevel { get; set; }
        public int[] bonusIDs { get; set; }
        public string name { get; set; }
        public int permanentEnchant { get; set; }
        public string permanentEnchantName { get; set; }
        public Gem[] gems { get; set; }
        public int onUseEnchant { get; set; }
    }

    public class Gem
    {
        public int id { get; set; }
        public int itemLevel { get; set; }
    }

    public class Pet
    {
        public string name { get; set; }
        public int id { get; set; }
        public int guid { get; set; }
        public string type { get; set; }
        public int icon { get; set; }
        public int total { get; set; }
        public int totalReduced { get; set; }
        public int activeTime { get; set; }
    }
}
