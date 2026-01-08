using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class MountCollectionResponse
    {
        [JsonProperty("mounts")]
        public List<CollectedMount> Mounts { get; set; }
    }

    public class CollectedMount
    {
        [JsonProperty("mount")]
        public MountReference Mount { get; set; }

        [JsonProperty("is_useable")]
        public bool? IsUseable { get; set; }
    }

    public class MountReference
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class MountIndexResponse
    {
        [JsonProperty("mounts")]
        public List<MountIndexEntry> Mounts { get; set; }
    }

    public class MountIndexEntry
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class MountDetailsResponse
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("source")]
        public MountSource Source { get; set; }

        [JsonProperty("faction")]
        public ApiType Faction { get; set; }

        [JsonProperty("creature_displays")]
        public List<CreatureDisplay> CreatureDisplays { get; set; }
    }

    public class MountSource
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("journal_encounter")]
        public JournalEncounterReference JournalEncounter { get; set; }
    }

    public class JournalEncounterReference
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long? Id { get; set; }
    }

    public class CreatureDisplay
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class ApiLink
    {
        [JsonProperty("href")]
        public string Href { get; set; }
    }

    public class ApiType
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
