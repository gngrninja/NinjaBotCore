using Newtonsoft.Json;
using System.Collections.Generic;

namespace NinjaBotCore.Models.Wow
{
    /// <summary>
    /// Root structure for mounts.json scraped from in-game Mount Journal
    /// </summary>
    public class ScrapedMountData
    {
        [JsonProperty("metadata")]
        public ScrapedMountMetadata Metadata { get; set; }

        [JsonProperty("mounts")]
        public Dictionary<string, ScrapedMount> Mounts { get; set; }
    }

    public class ScrapedMountMetadata
    {
        [JsonProperty("scanTimestamp")]
        public string ScanTimestamp { get; set; }

        [JsonProperty("playerFaction")]
        public string PlayerFaction { get; set; }

        [JsonProperty("clientVersion")]
        public string ClientVersion { get; set; }

        [JsonProperty("totalMounts")]
        public int TotalMounts { get; set; }

        [JsonProperty("errorCount")]
        public int ErrorCount { get; set; }
    }

    public class ScrapedMount
    {
        [JsonProperty("mountID")]
        public long MountId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("spellID")]
        public long SpellId { get; set; }

        [JsonProperty("mountTypeID")]
        public int MountTypeId { get; set; }

        [JsonProperty("creatureDisplayID")]
        public long? CreatureDisplayId { get; set; }

        [JsonProperty("isCollected")]
        public bool IsCollected { get; set; }

        [JsonProperty("isFactionSpecific")]
        public bool IsFactionSpecific { get; set; }

        [JsonProperty("faction")]
        public int? Faction { get; set; } // 0 = Horde, 1 = Alliance, null = neutral

        [JsonProperty("shouldHideOnChar")]
        public bool ShouldHideOnChar { get; set; }

        [JsonProperty("source")]
        public ScrapedMountSource Source { get; set; }
    }

    public class ScrapedMountSource
    {
        [JsonProperty("clean")]
        public string Clean { get; set; }

        [JsonProperty("drop")]
        public string Drop { get; set; }

        [JsonProperty("vendor")]
        public string Vendor { get; set; }

        [JsonProperty("zone")]
        public string Zone { get; set; }

        [JsonProperty("cost")]
        public string Cost { get; set; }

        [JsonProperty("achievement")]
        public string Achievement { get; set; }

        [JsonProperty("quest")]
        public string Quest { get; set; }

        [JsonProperty("profession")]
        public string Profession { get; set; }

        [JsonProperty("reputation")]
        public string Reputation { get; set; }

        [JsonProperty("event")]
        public string Event { get; set; }

        [JsonProperty("promotion")]
        public string Promotion { get; set; }

        [JsonProperty("trading_post")]
        public string TradingPost { get; set; }

        [JsonProperty("pvp")]
        public string Pvp { get; set; }

        [JsonProperty("class")]
        public string Class { get; set; }

        [JsonProperty("garrison")]
        public string Garrison { get; set; }

        [JsonProperty("covenant")]
        public string Covenant { get; set; }

        [JsonProperty("store")]
        public string Store { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        /// <summary>
        /// Determines the primary source type based on priority order
        /// </summary>
        public (string sourceType, string sourceDetail) GetPrimarySource()
        {
            if (!string.IsNullOrEmpty(Drop))
                return ("DROP", Drop);
            if (!string.IsNullOrEmpty(Vendor))
                return ("VENDOR", Vendor);
            if (!string.IsNullOrEmpty(Achievement))
                return ("ACHIEVEMENT", Achievement);
            if (!string.IsNullOrEmpty(Quest))
                return ("QUEST", Quest);
            if (!string.IsNullOrEmpty(Profession))
                return ("PROFESSION", Profession);
            if (!string.IsNullOrEmpty(Reputation))
                return ("REPUTATION", Reputation);
            if (!string.IsNullOrEmpty(TradingPost))
                return ("TRADING_POST", TradingPost);
            if (!string.IsNullOrEmpty(Store))
                return ("STORE", Store);
            if (!string.IsNullOrEmpty(Event))
                return ("WORLD_EVENT", Event);
            if (!string.IsNullOrEmpty(Pvp))
                return ("PVP", Pvp);
            if (!string.IsNullOrEmpty(Class))
                return ("CLASS", Class);
            if (!string.IsNullOrEmpty(Covenant))
                return ("COVENANT", Covenant);
            if (!string.IsNullOrEmpty(Garrison))
                return ("GARRISON", Garrison);
            if (!string.IsNullOrEmpty(Promotion))
                return ("PROMOTION", Promotion);

            // Fallback to clean text
            return ("UNKNOWN", Clean);
        }

        /// <summary>
        /// Check if mount is legacy/unobtainable
        /// </summary>
        public bool IsLegacy()
        {
            return Promotion?.Contains("Legacy") == true ||
                   Promotion?.Contains("Unobtainable") == true ||
                   Clean?.Contains("Legacy") == true;
        }
    }
}
