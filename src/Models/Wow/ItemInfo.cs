using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class ItemInfo
    {
        public int id { get; set; }
        public int disenchantingSkillRank { get; set; }
        public string description { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public int stackable { get; set; }
        public int itemBind { get; set; }
        public Bonusstat[] bonusStats { get; set; }
        public object[] itemSpells { get; set; }
        public int buyPrice { get; set; }
        public int itemClass { get; set; }
        public int itemSubClass { get; set; }
        public int containerSlots { get; set; }
        public Weapon weaponInfo { get; set; }
        public int inventoryType { get; set; }
        public bool equippable { get; set; }
        public int itemLevel { get; set; }
        public int maxCount { get; set; }
        public int maxDurability { get; set; }
        public int minFactionId { get; set; }
        public int minReputation { get; set; }
        public int quality { get; set; }
        public int sellPrice { get; set; }
        public int requiredSkill { get; set; }
        public int requiredLevel { get; set; }
        public int requiredSkillRank { get; set; }
        public Itemsource itemSource { get; set; }
        public int baseArmor { get; set; }
        public bool hasSockets { get; set; }
        public bool isAuctionable { get; set; }
        public int armor { get; set; }
        public int displayInfoId { get; set; }
        public string nameDescription { get; set; }
        public string nameDescriptionColor { get; set; }
        public bool upgradable { get; set; }
        public bool heroicTooltip { get; set; }
        public string context { get; set; }
        public object[] bonusLists { get; set; }
        public string[] availableContexts { get; set; }
        public Bonussummary bonusSummary { get; set; }
        public int artifactId { get; set; }
    }

    public class Weapon
    {
        public Dmg damage { get; set; }
        public float weaponSpeed { get; set; }
        public float dps { get; set; }
    }

    public class Dmg
    {
        public int min { get; set; }
        public int max { get; set; }
        public float exactMin { get; set; }
        public float exactMax { get; set; }
    }

    public class Itemsource
    {
        public int sourceId { get; set; }
        public string sourceType { get; set; }
    }

    public class Bonussummary
    {
        public object[] defaultBonusLists { get; set; }
        public object[] chanceBonusLists { get; set; }
        public object[] bonusChances { get; set; }
    }

    public class Bonusstat
    {
        public int stat { get; set; }
        public int amount { get; set; }
    }
}
