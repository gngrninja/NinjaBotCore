using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Models.Wow
{
    [JsonObject]
    public class Character
    {
        private string _thumbNailUrl;
        private string _region;
        private string _insetUrl;
        private string _profilePicUrl;
        private string _armoryUrl;

        public long lastModified { get; set; }
        public string name { get; set; }
        public string realm { get; set; }
        public string battlegroup { get; set; }
        [JsonProperty(PropertyName = "class")]
        public int _class { get; set; }
        public int race { get; set; }
        public int gender { get; set; }
        public string genderName
        {
            get
            {
                string genderName;
                genderName = string.Empty;
                switch (this.gender)
                {
                    case 0:
                        {
                            genderName = "Male";
                            break;
                        }
                    case 1:
                        {
                            genderName = "Female";
                            break;
                        }
                }

                return genderName;
            }
        }
        public int level { get; set; }
        public int achievementPoints { get; set; }
        public string thumbnail { get; set; }
        public string calcClass { get; set; }
        public int faction { get; set; }
        public int totalHonorableKills { get; set; }
        public string raceName
        {
            get
            {
                string theRace = WowApi.Races.races.Where(r => r.id == this.race).Select(r => r.name).FirstOrDefault();
                return theRace;
            }
        }
        public string className
        {
            get
            {
                string theClass = WowApi.Classes.classes.Where(r => r.id == this._class).Select(r => r.name).FirstOrDefault();
                return theClass;
            }
        }
        public string thumbnailURL
        {
            get
            {
                return _thumbNailUrl;
            }
            set
            {
                this._thumbNailUrl = value;
            }
        }
        public string profilePicURL
        {
            get
            {
                return this._profilePicUrl;
            }
            set 
            {
                this._profilePicUrl = value;
            }            
        }
        public string insetURL
        {
            get
            {
                return this._insetUrl;
            }
            set
            {
                this._insetUrl = value;
            }
        }
        [JsonProperty(PropertyName = "achievements")]
        public CharAchievements achievements { get; set; }
        public string armoryURL
        {
            get
            {
                return this._armoryUrl;                            
            }
            set
            {
                this._armoryUrl = value;
            }
        }
        [JsonProperty(PropertyName = "talents")]
        public WoWTalents[] talents { get; set; }
        public string mainSpec
        {
            get
            {
                string mainSpec = string.Empty;
                foreach (WoWTalents talent in this.talents)
                {
                    if (talent.selected)
                    {
                        mainSpec = talent.spec.name;
                    }
                }
                return mainSpec;
            }
        }
        [JsonProperty(PropertyName = "items")]
        public Items items { get; set; }
        public ItemComparison lowestItemLevel
        {
            get
            {
                List<ItemComparison> itemCompareList = new List<ItemComparison>();

                if (this.items.head != null)
                {
                    ItemComparison itemhead = new ItemComparison();
                    itemhead.itemName = this.items.head.name;
                    itemhead.itemLevel = this.items.head.itemLevel;
                    itemCompareList.Add(itemhead);
                }

                if (this.items.back != null)
                {
                    ItemComparison itemback = new ItemComparison();
                    itemback.itemName = this.items.back.name;
                    itemback.itemLevel = this.items.back.itemLevel;
                    itemCompareList.Add(itemback);
                }

                if (this.items.chest != null)
                {
                    ItemComparison itemchest = new ItemComparison();
                    itemchest.itemName = this.items.chest.name;
                    itemchest.itemLevel = this.items.chest.itemLevel;
                    itemCompareList.Add(itemchest);
                }

                if (this.items.wrist != null)
                {
                    ItemComparison itemwrist = new ItemComparison();
                    itemwrist.itemName = this.items.wrist.name;
                    itemwrist.itemLevel = this.items.wrist.itemLevel;
                    itemCompareList.Add(itemwrist);
                }

                if (this.items.feet != null)
                {
                    ItemComparison itemfeet = new ItemComparison();
                    itemfeet.itemName = this.items.feet.name;
                    itemfeet.itemLevel = this.items.feet.itemLevel;
                    itemCompareList.Add(itemfeet);
                }

                if (this.items.waist != null)
                {
                    ItemComparison itemWaist = new ItemComparison();
                    itemWaist.itemName = this.items.waist.name;
                    itemWaist.itemLevel = this.items.waist.itemLevel;
                    itemCompareList.Add(itemWaist);
                }

                if (this.items.finger1 != null)
                {
                    ItemComparison itemring1 = new ItemComparison();
                    itemring1.itemName = this.items.finger1.name;
                    itemring1.itemLevel = this.items.finger1.itemLevel;
                    itemCompareList.Add(itemring1);
                }

                if (this.items.finger2 != null)
                {
                    ItemComparison itemring2 = new ItemComparison();
                    itemring2.itemName = this.items.finger2.name;
                    itemring2.itemLevel = this.items.finger2.itemLevel;
                    itemCompareList.Add(itemring2);
                }

                if (this.items.trinket1 != null)
                {
                    ItemComparison itemtrink1 = new ItemComparison();
                    itemtrink1.itemName = this.items.trinket1.name;
                    itemtrink1.itemLevel = this.items.trinket1.itemLevel;
                    itemCompareList.Add(itemtrink1);
                }

                if (this.items.trinket2 != null)
                {
                    ItemComparison itemtrink2 = new ItemComparison();
                    itemtrink2.itemName = this.items.trinket2.name;
                    itemtrink2.itemLevel = this.items.trinket2.itemLevel;
                    itemCompareList.Add(itemtrink2);
                }

                if (this.items.shoulder != null)
                {
                    ItemComparison itemshoulders = new ItemComparison();
                    itemshoulders.itemName = this.items.shoulder.name;
                    itemshoulders.itemLevel = this.items.shoulder.itemLevel;
                    itemCompareList.Add(itemshoulders);
                }

                if (this.items.mainHand != null)
                {
                    ItemComparison itemweapon = new ItemComparison();
                    itemweapon.itemName = this.items.mainHand.name;
                    itemweapon.itemLevel = this.items.mainHand.itemLevel;
                    itemCompareList.Add(itemweapon);
                }

                if (this.items.offHand != null)
                {
                    ItemComparison itemweapon2 = new ItemComparison();
                    itemweapon2.itemName = this.items.offHand.name;
                    itemweapon2.itemLevel = this.items.offHand.itemLevel;
                    itemCompareList.Add(itemweapon2);
                }
                ItemComparison lowestItem = itemCompareList.OrderBy(i => i.itemLevel).FirstOrDefault();

                return lowestItem;
            }
        }
        public ItemComparison highestItemLevel
        {
            get
            {
                List<ItemComparison> itemCompareList = new List<ItemComparison>();

                if (this.items.head != null)
                {
                    ItemComparison itemhead = new ItemComparison();
                    itemhead.itemName = this.items.head.name;
                    itemhead.itemLevel = this.items.head.itemLevel;
                    itemCompareList.Add(itemhead);
                }

                if (this.items.back != null)
                {
                    ItemComparison itemback = new ItemComparison();
                    itemback.itemName = this.items.back.name;
                    itemback.itemLevel = this.items.back.itemLevel;
                    itemCompareList.Add(itemback);
                }

                if (this.items.chest != null)
                {
                    ItemComparison itemchest = new ItemComparison();
                    itemchest.itemName = this.items.chest.name;
                    itemchest.itemLevel = this.items.chest.itemLevel;
                    itemCompareList.Add(itemchest);
                }

                if (this.items.wrist != null)
                {
                    ItemComparison itemwrist = new ItemComparison();
                    itemwrist.itemName = this.items.wrist.name;
                    itemwrist.itemLevel = this.items.wrist.itemLevel;
                    itemCompareList.Add(itemwrist);
                }

                if (this.items.feet != null)
                {
                    ItemComparison itemfeet = new ItemComparison();
                    itemfeet.itemName = this.items.feet.name;
                    itemfeet.itemLevel = this.items.feet.itemLevel;
                    itemCompareList.Add(itemfeet);
                }

                if (this.items.waist != null)
                {
                    ItemComparison itemWaist = new ItemComparison();
                    itemWaist.itemName = this.items.waist.name;
                    itemWaist.itemLevel = this.items.waist.itemLevel;
                    itemCompareList.Add(itemWaist);
                }

                if (this.items.finger1 != null)
                {
                    ItemComparison itemring1 = new ItemComparison();
                    itemring1.itemName = this.items.finger1.name;
                    itemring1.itemLevel = this.items.finger1.itemLevel;
                    itemCompareList.Add(itemring1);
                }

                if (this.items.finger2 != null)
                {
                    ItemComparison itemring2 = new ItemComparison();
                    itemring2.itemName = this.items.finger2.name;
                    itemring2.itemLevel = this.items.finger2.itemLevel;
                    itemCompareList.Add(itemring2);
                }

                if (this.items.trinket1 != null)
                {
                    ItemComparison itemtrink1 = new ItemComparison();
                    itemtrink1.itemName = this.items.trinket1.name;
                    itemtrink1.itemLevel = this.items.trinket1.itemLevel;
                    itemCompareList.Add(itemtrink1);
                }

                if (this.items.trinket2 != null)
                {
                    ItemComparison itemtrink2 = new ItemComparison();
                    itemtrink2.itemName = this.items.trinket2.name;
                    itemtrink2.itemLevel = this.items.trinket2.itemLevel;
                    itemCompareList.Add(itemtrink2);
                }

                if (this.items.shoulder != null)
                {
                    ItemComparison itemshoulders = new ItemComparison();
                    itemshoulders.itemName = this.items.shoulder.name;
                    itemshoulders.itemLevel = this.items.shoulder.itemLevel;
                    itemCompareList.Add(itemshoulders);
                }

                if (this.items.mainHand != null)
                {
                    ItemComparison itemweapon = new ItemComparison();
                    itemweapon.itemName = this.items.mainHand.name;
                    itemweapon.itemLevel = this.items.mainHand.itemLevel;
                    itemCompareList.Add(itemweapon);
                }

                if (this.items.offHand != null)
                {
                    ItemComparison itemweapon2 = new ItemComparison();
                    itemweapon2.itemName = this.items.offHand.name;
                    itemweapon2.itemLevel = this.items.offHand.itemLevel;
                    itemCompareList.Add(itemweapon2);
                }
                var highestItem = itemCompareList.OrderByDescending(i => i.itemLevel).FirstOrDefault();

                return highestItem;
            }
        }
        public Stats stats { get; set; }
    }

    [JsonObject]
    public class Race
    {
        [JsonProperty]
        public Races[] races { get; set; }
    }

    [JsonObject]
    public class Races
    {
        [JsonProperty]
        public int id { get; set; }
        [JsonProperty]
        public int mask { get; set; }
        [JsonProperty]
        public string side { get; set; }
        [JsonProperty]
        public string name { get; set; }
    }

    public class GuildChar
    {
        public string charName { get; set; }
        public string realmName { get; set; }
        public string regionName { get; set; }
        public string locale { get; set; }
    }

    public class WowClasses
    {
        public WoWClass[] classes { get; set; }
    }

    public class WoWClass
    {
        public int id { get; set; }
        public int mask { get; set; }
        public string powerType { get; set; }
        public string name { get; set; }
    }

    public class Achievements
    {
        public Achievement[] achievements { get; set; }
    }

    public class Achievement
    {
        public int id { get; set; }
        public Achievement1[] achievements { get; set; }
        public string name { get; set; }
        public Category[] categories { get; set; }
    }

    public class Achievement1
    {
        public int id { get; set; }
        public string title { get; set; }
        public int points { get; set; }
        public string description { get; set; }
        public Rewarditem[] rewardItems { get; set; }
        public string icon { get; set; }
        public Criterion[] criteria { get; set; }
        public bool accountWide { get; set; }
        public int factionId { get; set; }
        public string reward { get; set; }
    }

    public class Rewarditem
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public int quality { get; set; }
        public int itemLevel { get; set; }
        public Tooltipparamsac tooltipParams { get; set; }
        public Statac[] stats { get; set; }
        public int armor { get; set; }
        public string context { get; set; }
        public object[] bonusLists { get; set; }
        public int artifactId { get; set; }
        public int displayInfoId { get; set; }
        public int artifactAppearanceId { get; set; }
        public object[] artifactTraits { get; set; }
        public object[] relics { get; set; }
        public Appearanceac appearance { get; set; }
    }

    public class Tooltipparamsac
    {
        public int timewalkerLevel { get; set; }
    }

    public class Appearanceac
    {
    }

    public class Statac
    {
        public int stat { get; set; }
        public int amount { get; set; }
    }

    public class Criterion
    {
        public int id { get; set; }
        public string description { get; set; }
        public int orderIndex { get; set; }
        public int max { get; set; }
    }

    public class Category
    {
        public int id { get; set; }
        public Achievement2[] achievements { get; set; }
        public string name { get; set; }
    }

    public class Achievement2
    {
        public int id { get; set; }
        public string title { get; set; }
        public int points { get; set; }
        public string description { get; set; }
        public object[] rewardItems { get; set; }
        public string icon { get; set; }
        public Criterion1[] criteria { get; set; }
        public bool accountWide { get; set; }
        public int factionId { get; set; }
    }

    public class Criterion1
    {
        public int id { get; set; }
        public string description { get; set; }
        public int orderIndex { get; set; }
        public int max { get; set; }
    }


    public class AchievementChar
    {
        public long lastModified { get; set; }
        public string name { get; set; }
        public string realm { get; set; }
        public string battlegroup { get; set; }
        public int _class { get; set; }
        public int race { get; set; }
        public int gender { get; set; }
        public int level { get; set; }
        public int achievementPoints { get; set; }
        public string thumbnail { get; set; }
        public string calcClass { get; set; }
        public int faction { get; set; }
        public CharAchievements achievements { get; set; }
        public int totalHonorableKills { get; set; }
    }

    public class Stats
    {
        public int health { get; set; }
        public string powerType { get; set; }
        public int power { get; set; }
        public int str { get; set; }
        public int agi { get; set; }
        [JsonProperty(PropertyName = "int")]
        public int _int { get; set; }
        public int sta { get; set; }
        public float speedRating { get; set; }
        public float speedRatingBonus { get; set; }
        public float crit { get; set; }
        public int critRating { get; set; }
        public float haste { get; set; }
        public int hasteRating { get; set; }
        public float hasteRatingPercent { get; set; }
        public float mastery { get; set; }
        public int masteryRating { get; set; }
        public float leech { get; set; }
        public float leechRating { get; set; }
        public float leechRatingBonus { get; set; }
        public int versatility { get; set; }
        public float versatilityDamageDoneBonus { get; set; }
        public float versatilityHealingDoneBonus { get; set; }
        public float versatilityDamageTakenBonus { get; set; }
        public float avoidanceRating { get; set; }
        public float avoidanceRatingBonus { get; set; }
        public int spellPen { get; set; }
        public float spellCrit { get; set; }
        public int spellCritRating { get; set; }
        public float mana5 { get; set; }
        public float mana5Combat { get; set; }
        public int armor { get; set; }
        public float dodge { get; set; }
        public int dodgeRating { get; set; }
        public float parry { get; set; }
        public int parryRating { get; set; }
        public float block { get; set; }
        public int blockRating { get; set; }
        public float mainHandDmgMin { get; set; }
        public float mainHandDmgMax { get; set; }
        public float mainHandSpeed { get; set; }
        public float mainHandDps { get; set; }
        public float offHandDmgMin { get; set; }
        public float offHandDmgMax { get; set; }
        public float offHandSpeed { get; set; }
        public float offHandDps { get; set; }
        public float rangedDmgMin { get; set; }
        public float rangedDmgMax { get; set; }
        public float rangedSpeed { get; set; }
        public float rangedDps { get; set; }
    }

    public class CharAchievements
    {
        public int[] achievementsCompleted { get; set; }
        public long[] achievementsCompletedTimestamp { get; set; }
        public int[] criteria { get; set; }
        public long[] criteriaQuantity { get; set; }
        public long[] criteriaTimestamp { get; set; }
        public long[] criteriaCreated { get; set; }
    }

    public class WoWTalentMain
    {
        public long lastModified { get; set; }
        public string name { get; set; }
        public string realm { get; set; }
        public string battlegroup { get; set; }
        public int _class { get; set; }
        public int race { get; set; }
        public int gender { get; set; }
        public int level { get; set; }
        public int achievementPoints { get; set; }
        public string thumbnail { get; set; }
        public string calcClass { get; set; }
        public int faction { get; set; }
        public WoWTalents[] talents { get; set; }
        public int totalHonorableKills { get; set; }
    }

    public class WoWTalents
    {
        public bool selected { get; set; }
        public Talent1[] talents { get; set; }
        public WoWSpecs spec { get; set; }
        public string calcTalent { get; set; }
        public string calcSpec { get; set; }
    }

    public class WoWSpecs
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Talent1
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell spell { get; set; }
        public Spec1 spec { get; set; }
    }

    public class Spell
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string cooldown { get; set; }
        public string subtext { get; set; }
        public string range { get; set; }
    }

    public class Spec1
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }


    public class TalentList
    {
        [JsonProperty(PropertyName = "1")]
        public _1 _1 { get; set; }
        [JsonProperty(PropertyName = "2")]
        public _2 _2 { get; set; }
        [JsonProperty(PropertyName = "3")]
        public _3 _3 { get; set; }
        [JsonProperty(PropertyName = "4")]
        public _4 _4 { get; set; }
        [JsonProperty(PropertyName = "5")]
        public _5 _5 { get; set; }
        [JsonProperty(PropertyName = "6")]
        public _6 _6 { get; set; }
        [JsonProperty(PropertyName = "7")]
        public _7 _7 { get; set; }
        [JsonProperty(PropertyName = "8")]
        public _8 _8 { get; set; }
        [JsonProperty(PropertyName = "9")]
        public _9 _9 { get; set; }
        [JsonProperty(PropertyName = "10")]
        public _10 _10 { get; set; }
        [JsonProperty(PropertyName = "11")]
        public _11 _11 { get; set; }
        [JsonProperty(PropertyName = "12")]
        public _12 _12 { get; set; }
    }

    public class _1
    {
        public TalentListTalent[][][] talents { get; set; }
        public string _class { get; set; }
        public TalentListSpec1[] specs { get; set; }
    }

    public class TalentListTalent
    {
        public int tier { get; set; }
        public int column { get; set; }
        public TalentListSpell spell { get; set; }
        public TalentListSpec spec { get; set; }
    }

    public class TalentListSpell
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string cooldown { get; set; }
        public string range { get; set; }
        public string powerCost { get; set; }
    }

    public class TalentListSpec
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class TalentListSpec1
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _2
    {
        public TalentList1[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec3[] specs { get; set; }
    }

    public class TalentList1
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell1 spell { get; set; }
        public Spec2 spec { get; set; }
    }

    public class Spell1
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string powerCost { get; set; }
        public string cooldown { get; set; }
    }

    public class Spec2
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec3
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _3
    {
        public Petspec[] petSpecs { get; set; }
        public Talent2[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec5[] specs { get; set; }
    }

    public class Petspec
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Talent2
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell2 spell { get; set; }
        public Spec4 spec { get; set; }
    }

    public class Spell2
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string powerCost { get; set; }
        public string cooldown { get; set; }
    }

    public class Spec4
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec5
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _4
    {
        public Talent3[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec7[] specs { get; set; }
    }

    public class Talent3
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell3 spell { get; set; }
        public Spec6 spec { get; set; }
    }

    public class Spell3
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string powerCost { get; set; }
        public string subtext { get; set; }
        public string cooldown { get; set; }
    }

    public class Spec6
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec7
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _5
    {
        public Talent4[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec9[] specs { get; set; }
    }

    public class Talent4
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell4 spell { get; set; }
        public Spec8 spec { get; set; }
    }

    public class Spell4
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string powerCost { get; set; }
        public string cooldown { get; set; }
        public string subtext { get; set; }
    }

    public class Spec8
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec9
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _6
    {
        public Talent5[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec11[] specs { get; set; }
    }

    public class Talent5
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell5 spell { get; set; }
        public Spec10 spec { get; set; }
    }

    public class Spell5
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string cooldown { get; set; }
        public string powerCost { get; set; }
        public string subtext { get; set; }
    }

    public class Spec10
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec11
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _7
    {
        public Talent6[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec13[] specs { get; set; }
    }

    public class Talent6
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell6 spell { get; set; }
        public Spec12 spec { get; set; }
    }

    public class Spell6
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string range { get; set; }
        public string castTime { get; set; }
        public string cooldown { get; set; }
        public string powerCost { get; set; }
    }

    public class Spec12
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec13
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _8
    {
        public Talent7[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec15[] specs { get; set; }
    }

    public class Talent7
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell7 spell { get; set; }
        public Spec14 spec { get; set; }
    }

    public class Spell7
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string cooldown { get; set; }
        public string powerCost { get; set; }
    }

    public class Spec14
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec15
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _9
    {
        public Talent8[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec17[] specs { get; set; }
    }

    public class Talent8
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell8 spell { get; set; }
        public Spec16 spec { get; set; }
    }

    public class Spell8
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string powerCost { get; set; }
        public string cooldown { get; set; }
        public string subtext { get; set; }
    }

    public class Spec16
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec17
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _10
    {
        public Talent9[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec19[] specs { get; set; }
    }

    public class Talent9
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell9 spell { get; set; }
        public Spec18 spec { get; set; }
    }

    public class Spell9
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string range { get; set; }
        public string castTime { get; set; }
        public string cooldown { get; set; }
        public string subtext { get; set; }
        public string powerCost { get; set; }
    }

    public class Spec18
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec19
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _11
    {
        public Talent10[][][] talents { get; set; }
        [JsonProperty(PropertyName = "class")]
        public string _class { get; set; }
        public Spec21[] specs { get; set; }
    }

    public class Talent10
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell10 spell { get; set; }
        public Spec20 spec { get; set; }
    }

    public class Spell10
    {
        public int id { get; set; }
        public string name { get; set; }
        public string subtext { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string range { get; set; }
        public string castTime { get; set; }
        public string cooldown { get; set; }
        public string powerCost { get; set; }
    }

    public class Spec20
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec21
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class _12
    {
        public Talent11[][][] talents { get; set; }
        public string _class { get; set; }
        public Spec23[] specs { get; set; }
    }

    public class Talent11
    {
        public int tier { get; set; }
        public int column { get; set; }
        public Spell11 spell { get; set; }
        public Spec22 spec { get; set; }
    }

    public class Spell11
    {
        public int id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public string castTime { get; set; }
        public string range { get; set; }
        public string cooldown { get; set; }
        public string powerCost { get; set; }
    }

    public class Spec22
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }

    public class Spec23
    {
        public string name { get; set; }
        public string role { get; set; }
        public string backgroundImage { get; set; }
        public string icon { get; set; }
        public string description { get; set; }
        public int order { get; set; }
    }
}
