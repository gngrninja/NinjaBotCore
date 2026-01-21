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

        // Backing field for zone name (used by v2 API to avoid lookup)
        private string _zoneNameOverride;

        public string zoneName
        {
            get
            {
                // If zone name was explicitly set (v2 API), use that
                if (!string.IsNullOrEmpty(_zoneNameOverride))
                    return _zoneNameOverride;

                // Otherwise, look up by zone ID (v1 API compatibility)
                string theZone = WarcraftLogs.Zones.Where(r => r.id == this.zone).Select(r => r.name).FirstOrDefault();
                return theZone;
            }
            set
            {
                _zoneNameOverride = value;
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
        public Bracket brackets { get; set; }
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
        public int min { get; set; }
        public float max { get; set; }
        public float bucket { get; set; }
        public string type { get; set; }
        public int sub_bucket { get; set; }
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
        public string guildName { get; set; }
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
        // API returns "encounterID" (capital ID)
        [JsonProperty(PropertyName = "encounterID")]
        public int encounterId { get; set; }

        // WCL API returns encounterName directly - use JsonIgnore on the computed property
        // to avoid conflicts, and map JSON to this backing field
        [JsonProperty(PropertyName = "encounterName")]
        public string encounterNameFromApi { get; set; }

        [JsonIgnore]
        public string encounterName
        {
            get
            {
                // Use API-provided name if available
                if (!string.IsNullOrEmpty(encounterNameFromApi))
                    return encounterNameFromApi;

                // Fall back to lookup from zones
                string name = string.Empty;
                if (WarcraftLogs.Zones != null)
                {
                    foreach (Zones zone in WarcraftLogs.Zones)
                    {
                        if (zone.encounters == null) continue;
                        foreach (Encounter encounter in zone.encounters)
                        {
                            if (encounter.id == this.encounterId)
                            {
                                name = encounter.name;
                            }
                        }
                    }
                }

                // If still empty, return a placeholder with the ID
                if (string.IsNullOrEmpty(name))
                    return $"Encounter #{encounterId}";

                return name;
            }
        }

        // WCL API returns class as either int (old) or string (new)
        // Store raw value and provide classID/className accessors
        [JsonProperty(PropertyName = "class")]
        public object classRaw { get; set; }

        public int classID
        {
            get
            {
                if (classRaw is int intVal) return intVal;
                if (classRaw is long longVal) return (int)longVal;
                if (classRaw is string strVal)
                {
                    // Try to look up class ID by name
                    var charClass = WarcraftLogs.CharClasses?.FirstOrDefault(c =>
                        string.Equals(c.name, strVal, StringComparison.OrdinalIgnoreCase));
                    return charClass?.id ?? 0;
                }
                return 0;
            }
        }

        public string className
        {
            get
            {
                // If raw value is already a string, use it directly
                if (classRaw is string strVal) return strVal;

                // Otherwise look up by ID
                string name = string.Empty;
                List<CharClasses> charClasses = WarcraftLogs.CharClasses;
                name = charClasses?.Where(c => c.id == this.classID).Select(c => c.name).FirstOrDefault();
                return name ?? string.Empty;
            }
        }

        // WCL API returns spec as either int (old) or string (new)
        [JsonProperty(PropertyName = "spec")]
        public object specRaw { get; set; }

        public int specID
        {
            get
            {
                if (specRaw is int intVal) return intVal;
                if (specRaw is long longVal) return (int)longVal;
                if (specRaw is string strVal)
                {
                    // Try to look up spec ID by name within the class
                    var charClass = WarcraftLogs.CharClasses?.FirstOrDefault(c => c.id == this.classID);
                    var spec = charClass?.specs?.FirstOrDefault(s =>
                        string.Equals(s.name, strVal, StringComparison.OrdinalIgnoreCase));
                    return spec?.id ?? 0;
                }
                return 0;
            }
        }

        public string specName
        {
            get
            {
                // If raw value is already a string, use it directly
                if (specRaw is string strVal) return strVal;

                // Otherwise look up by ID
                string name = string.Empty;
                List<CharClasses> charClasses = WarcraftLogs.CharClasses;

                foreach (CharClasses classItem in charClasses ?? new List<CharClasses>())
                {
                    if (classItem.id == this.classID)
                    {
                        name = classItem.specs?.Where(c => c.id == this.specID).Select(c => c.name).FirstOrDefault();
                    }
                }

                return name ?? string.Empty;
            }
        }
        public string guildName { get; set; }
        public int rank { get; set; }
        public int outOf { get; set; }

        // API provides percentile directly - more accurate than calculating
        public double percentile { get; set; }

        // Computed inverse for ranking display (keeping for backwards compat)
        [JsonIgnore]
        public int rankPercentage
        {
            get
            {
                // If API provided percentile, use it
                if (percentile > 0)
                    return (int)Math.Round(100 - percentile);
                // Fall back to calculation
                if (outOf > 0)
                    return (int)Math.Round((double)(100 * this.rank) / this.outOf);
                return 0;
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

        // API returns ilvlKeyOrPatch for item level
        [JsonProperty(PropertyName = "ilvlKeyOrPatch")]
        public int itemLevel { get; set; }

        public double total { get; set; }
        public bool estimated { get; set; }

        // Additional fields from API
        public int characterID { get; set; }
        public string characterName { get; set; }
        public string server { get; set; }
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