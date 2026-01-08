using Newtonsoft.Json;
using System.Collections.Generic;

namespace NinjaBotCore.Models.Wow
{
    public class JournalEncounterResponse
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("instance")]
        public JournalInstanceReference Instance { get; set; }

        [JsonProperty("creatures")]
        public List<CreatureReference> Creatures { get; set; }
    }

    public class JournalInstanceReference
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class JournalInstanceResponse
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("category")]
        public ApiType Category { get; set; }

        [JsonProperty("expansion")]
        public ApiType Expansion { get; set; }

        [JsonProperty("location")]
        public ApiType Location { get; set; }
    }

    public class CreatureReference
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class JournalEncounterIndexResponse
    {
        [JsonProperty("encounters")]
        public List<JournalEncounterIndexEntry> Encounters { get; set; }
    }

    public class JournalEncounterIndexEntry
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class JournalInstanceIndexResponse
    {
        [JsonProperty("instances")]
        public List<JournalInstanceIndexEntry> Instances { get; set; }
    }

    public class JournalInstanceIndexEntry
    {
        [JsonProperty("key")]
        public ApiLink Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("id")]
        public long Id { get; set; }
    }
}
