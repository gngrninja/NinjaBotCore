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
    public class Reports
    {
        public string id { get; set; }
        public string title { get; set; }
        public string owner { get; set; }
        public long start { get; set; }
        public long end { get; set; }
        public int zone { get; set; }
        public string zoneName
        {
            get
            {             
                string theZone = WarcraftLogs.Zones.Where(r => r.id == this.zone).Select(r => r.name).FirstOrDefault();
                return theZone;
            }
        }
        public string reportURL
        {
            get
            {
                string url = string.Empty;

                url = $"https://www.warcraftlogs.com/reports/{id}";

                return url;
            }
        }
    }
    [JsonObject]
    public class Zones
    {
        public int id { get; set; }
        public string name { get; set; }
        public bool frozen { get; set; }
        public Encounter[] encounters { get; set; }
        public Bracket[] brackets { get; set; }
    }
    [JsonObject]
    public class Encounter
    {
        public int id { get; set; }
        public string name { get; set; }
    }
    [JsonObject]
    public class Bracket
    {
        public int id { get; set; }
        public string name { get; set; }
    }
    [JsonObject]
    public class Fights
    {
        public Fight[] fights { get; set; }
        public string lang { get; set; }
        public Friendly[] friendlies { get; set; }
        public Enemy[] enemies { get; set; }
        public Friendlypet[] friendlyPets { get; set; }
        public object[] enemyPets { get; set; }
        public Phase[] phases { get; set; }
        public string title { get; set; }
        public string owner { get; set; }
        public long start { get; set; }
        public long end { get; set; }
        public int zone { get; set; }
    }
    [JsonObject]
    public class Fight
    {
        public int id { get; set; }
        public int start_time { get; set; }
        public int end_time { get; set; }
        public int boss { get; set; }
        public int size { get; set; }
        public int difficulty { get; set; }
        public bool kill { get; set; }
        public int partial { get; set; }
        public int bossPercentage { get; set; }
        public string name { get; set; }
    }
    [JsonObject]
    public class Friendly
    {
        public string name { get; set; }
        public int id { get; set; }
        public int guid { get; set; }
        public string type { get; set; }
        public Fight1[] fights { get; set; }
    }
    [JsonObject]
    public class Fight1
    {
        public int id { get; set; }
        public int instances { get; set; }
    }
    [JsonObject]
    public class Enemy
    {
        public string name { get; set; }
        public int id { get; set; }
        public int guid { get; set; }
        public string type { get; set; }
        public Fight2[] fights { get; set; }
    }
    [JsonObject]
    public class Fight2
    {
        public int id { get; set; }
        public int instances { get; set; }
    }
    [JsonObject]
    public class Friendlypet
    {
        public string name { get; set; }
        public int id { get; set; }
        public int guid { get; set; }
        public string type { get; set; }
        public int petOwner { get; set; }
        public Fight3[] fights { get; set; }
    }
    [JsonObject]
    public class Fight3
    {
        public int id { get; set; }
        public int instances { get; set; }
    }
    [JsonObject]
    public class Phase
    {
        public int boss { get; set; }
        public string[] phases { get; set; }
    }
    [JsonObject]
    public class CharParses
    {
        public int difficulty { get; set; }
        public int size { get; set; }
        public int kill { get; set; }
        public string name { get; set; }
        public LogSpec[] specs { get; set; }
        public bool variable { get; set; }
        public int partition { get; set; }
    }
    [JsonObject]
    public class LogSpec
    {
        public string _class { get; set; }
        public string spec { get; set; }
        public bool combined { get; set; }
        public Datum[] data { get; set; }
        public int best_persecondamount { get; set; }
        public int best_duration { get; set; }
        public int best_historical_percent { get; set; }
        public float best_allstar_points { get; set; }
        public int best_combined_allstar_points { get; set; }
        public int possible_allstar_points { get; set; }
        public Best_Talents[] best_talents { get; set; }
        public Best_Gear[] best_gear { get; set; }
        public int historical_total { get; set; }
        public float historical_median { get; set; }
        public float historical_avg { get; set; }
    }
    [JsonObject]
    public class Datum
    {
        public int character_id { get; set; }
        public string character_name { get; set; }
        public int persecondamount { get; set; }
        public int ilvl { get; set; }
        public int duration { get; set; }
        public long start_time { get; set; }
        public string report_code { get; set; }
        public int report_fight { get; set; }
        public int ranking_id { get; set; }
        public string guild { get; set; }
        public int total { get; set; }
        public string rank { get; set; }
        public float percent { get; set; }
        public int exploit { get; set; }
        public bool banned { get; set; }
        public int historical_count { get; set; }
        public int historical_percent { get; set; }
        public LogTalent[] talents { get; set; }
        public Gear[] gear { get; set; }
    }
    [JsonObject]
    public class LogTalent
    {
        public string name { get; set; }
        public int id { get; set; }
    }
    [JsonObject]
    public class LogGear
    {
        public string name { get; set; }
        public string quality { get; set; }
        public int id { get; set; }
    }
    [JsonObject]
    public class Best_Talents
    {
        public string name { get; set; }
        public int id { get; set; }
    }
    [JsonObject]
    public class Best_Gear
    {
        public string name { get; set; }
        public string quality { get; set; }
        public int id { get; set; }
    }
    [JsonObject]
    public class LogCharRankings
    {
        public int encounter { get; set; }
        public string encounterName
        {
            get
            {
                string encounterName = string.Empty;
                foreach (Zones zone in WarcraftLogs.Zones)
                {
                    foreach (Encounter encounter in zone.encounters)
                    {
                        if (encounter.id == this.encounter)
                        {
                            encounterName = encounter.name;
                        }
                    }
                }

                return encounterName;
            }
        }
        [JsonProperty(PropertyName = "class")]
        public int classID { get; set; }
        public string className
        {
            get
            {
                string name = string.Empty;                
                List<CharClasses> charClasses = WarcraftLogs.CharClasses;

                name = charClasses.Where(c => c.id == this.classID).Select(c => c.name).FirstOrDefault();

                return name;
            }
        }
        public int spec { get; set; }
        public string specName
        {
            get
            {
                string name = string.Empty;                
                List<CharClasses> charClasses = WarcraftLogs.CharClasses;

                foreach (CharClasses classItem in charClasses)
                {
                    if (classItem.id == this.classID)
                    {
                        name = classItem.specs.Where(c => c.id == this.spec).Select(c => c.name).FirstOrDefault();
                    }
                }

                return name;
            }
        }
        public string guild { get; set; }
        public int rank { get; set; }
        public int outOf { get; set; }
        public int rankPercentage
        {
            get
            {
                int rankPercentage = (int)Math.Round((double)(100 * this.rank) / this.outOf);
                return rankPercentage;
            }
        }
        public int duration { get; set; }
        public long startTime { get; set; }
        public string reportID { get; set; }
        public string reportURL
        {
            get
            {
                string url = string.Empty;
                url = $"https://www.warcraftlogs.com/reports/{this.reportID}";
                return url;
            }
        }
        public int fightID { get; set; }
        public int difficulty { get; set; }
        public string difficultyName
        {
            get
            {
                string name = string.Empty;

                switch (this.difficulty)
                {
                    case 1:
                        {
                            name = "LFR";
                            break;
                        }
                    case 2:
                        {
                            name = "Flex";
                            break;
                        }
                    case 3:
                        {
                            name = "Normal";
                            break;
                        }
                    case 4:
                        {
                            name = "Heroic";
                            break;
                        }
                    case 5:
                        {
                            name = "Mythic";
                            break;
                        }
                }

                return name;
            }
        }
        public int size { get; set; }
        public int itemLevel { get; set; }
        public int total { get; set; }
        public bool estimated { get; set; }
    }

    public class CharClasses
    {
        public int id { get; set; }
        public string name { get; set; }
        public ClassSpec[] specs { get; set; }
    }

    public class ClassSpec
    {
        public int id { get; set; }
        public string name { get; set; }
    }
}