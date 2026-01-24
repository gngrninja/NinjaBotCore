using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow
{
    public class ArmoryGuildRoster
    {
        [JsonProperty("guild")]
        public ArmoryGuildInfo Guild { get; set; }

        [JsonProperty("members")]
        public List<ArmoryGuildMember> Members { get; set; }
    }

    public class ArmoryGuildInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("realm")]
        public ArmoryRealm Realm { get; set; }

        [JsonProperty("faction")]
        public ArmoryType Faction { get; set; }
    }

    public class ArmoryGuildMember
    {
        [JsonProperty("character")]
        public ArmoryGuildCharacter Character { get; set; }

        [JsonProperty("rank")]
        public int Rank { get; set; }
    }

    public class ArmoryGuildCharacter
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("realm")]
        public ArmoryRealm Realm { get; set; }

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("playable_class")]
        public ArmoryClassRef PlayableClass { get; set; }

        [JsonProperty("playable_race")]
        public ArmoryRaceRef PlayableRace { get; set; }
    }

    public class ArmoryClassRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryRaceRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }
}
