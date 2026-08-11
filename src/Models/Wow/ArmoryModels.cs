using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace NinjaBotCore.Models.Wow
{
    public class ArmorySummary
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("equipped_item_level")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int EquippedItemLevel { get; set; }

        [JsonProperty("average_item_level")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int AverageItemLevel { get; set; }

        [JsonProperty("gender")]
        public ArmoryType Gender { get; set; }

        [JsonProperty("faction")]
        public ArmoryType Faction { get; set; }

        [JsonProperty("realm")]
        public ArmoryRealm Realm { get; set; }

        [JsonProperty("character_class")]
        public ArmoryType CharacterClass { get; set; }

        [JsonProperty("active_spec")]
        public ArmoryType ActiveSpec { get; set; }

        [JsonProperty("media")]
        public ArmoryLink Media { get; set; }
    }

    public class ArmoryRealm
    {
        [JsonProperty("slug")]
        public string Slug { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class ArmoryType
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class ArmoryLink
    {
        [JsonProperty("href")]
        public string Href { get; set; }
    }

    public class ArmoryMedia
    {
        [JsonProperty("assets")]
        public List<ArmoryAsset> Assets { get; set; }
    }

    public class ArmoryAsset
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("value")]
        public string Value { get; set; }
    }

    public class ArmoryEquipment
    {
        [JsonProperty("average_item_level")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int AverageItemLevel { get; set; }

        [JsonProperty("equipped_item_level")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int EquippedItemLevel { get; set; }

        [JsonProperty("equipped_items")]
        public List<ArmoryEquippedItem> EquippedItems { get; set; }
    }

    public class ArmoryEquippedItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("item")]
        public ArmoryItemRef Item { get; set; }

        [JsonProperty("slot")]
        public ArmoryType Slot { get; set; }

        [JsonProperty("level")]
        public ArmoryValue Level { get; set; }

        [JsonProperty("quality")]
        public ArmoryType Quality { get; set; }

        [JsonProperty("name_description")]
        public ArmoryNameDescription NameDescription { get; set; }

        [JsonProperty("bonus_list")]
        public List<int> BonusList { get; set; }

        [JsonProperty("context")]
        public int Context { get; set; }

        [JsonProperty("media")]
        public ArmoryMediaRef Media { get; set; }

        [JsonProperty("enchantments")]
        public List<ArmoryEnchantment> Enchantments { get; set; }

        [JsonProperty("sockets")]
        public List<ArmorySocket> Sockets { get; set; }

        [JsonProperty("stats")]
        public List<ArmoryStat> Stats { get; set; }

        [JsonProperty("set")]
        public ArmorySet Set { get; set; }

        [JsonProperty("spells")]
        public List<ArmorySpell> Spells { get; set; }

        [JsonProperty("weapon")]
        public ArmoryWeapon Weapon { get; set; }
    }

    public class ArmoryNameDescription
    {
        [JsonProperty("display_string")]
        public string DisplayString { get; set; }
    }

    public class ArmoryItemRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }
    }

    public class ArmoryValue
    {
        [JsonProperty("value")]
        public int Value { get; set; }
    }

    public class ArmoryDoubleValue
    {
        [JsonProperty("value")]
        [JsonConverter(typeof(FlexibleDoubleConverter))]
        public double Value { get; set; }
    }

    public class ArmoryMediaRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("key")]
        public ArmoryLink Key { get; set; }
    }

    public class ArmoryItemMedia
    {
        [JsonProperty("assets")]
        public List<ArmoryAsset> Assets { get; set; }
    }

    public class ArmoryEnchantment
    {
        [JsonProperty("enchantment_id")]
        public int EnchantmentId { get; set; }

        [JsonProperty("enchantment_slot")]
        public ArmoryEnchantmentSlot EnchantmentSlot { get; set; }

        [JsonProperty("display_string")]
        public string DisplayString { get; set; }
    }

    public class ArmoryEnchantmentSlot
    {
        [JsonProperty("id")]
        public int Id { get; set; }
    }

    public class ArmorySocket
    {
        [JsonProperty("socket_type")]
        public ArmoryType SocketType { get; set; }

        [JsonProperty("item")]
        public ArmoryItemRef Item { get; set; }

        [JsonProperty("display_string")]
        public string DisplayString { get; set; }
    }

    public class ArmoryStat
    {
        [JsonProperty("type")]
        public ArmoryType Type { get; set; }

        [JsonProperty("value")]
        public int Value { get; set; }

        [JsonProperty("is_negated")]
        public bool? IsNegated { get; set; }
    }

    public class ArmorySpell
    {
        [JsonProperty("spell")]
        public ArmoryItemRef Spell { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }
    }

    public class ArmoryWeapon
    {
        [JsonProperty("damage")]
        public ArmoryWeaponDamage Damage { get; set; }

        [JsonProperty("attack_speed")]
        public ArmoryDoubleValue AttackSpeed { get; set; }

        [JsonProperty("dps")]
        public ArmoryDoubleValue DPS { get; set; }
    }

    public class ArmoryWeaponDamage
    {
        [JsonProperty("min_value")]
        public int MinValue { get; set; }

        [JsonProperty("max_value")]
        public int MaxValue { get; set; }

        [JsonProperty("display_string")]
        public string DisplayString { get; set; }
    }

    public class ArmorySet
    {
        [JsonProperty("item_set")]
        public ArmoryItemSet ItemSet { get; set; }

        [JsonProperty("items")]
        public List<ArmorySetItem> Items { get; set; }

        [JsonProperty("effects")]
        public List<ArmorySetEffect> Effects { get; set; }
    }

    public class ArmoryItemSet
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class ArmorySetItem
    {
        [JsonProperty("item")]
        public ArmoryItemRef Item { get; set; }
    }

    public class ArmorySetEffect
    {
        [JsonProperty("display_string")]
        public string DisplayString { get; set; }

        [JsonProperty("required_count")]
        public int RequiredCount { get; set; }

        [JsonProperty("is_active")]
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Handles Blizzard responses that sometimes return an int or an object with { "value": int }.
    /// </summary>
    public class FlexibleIntConverter : JsonConverter<int>
    {
        public override int ReadJson(JsonReader reader, Type objectType, int existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Integer)
            {
                return Convert.ToInt32(reader.Value);
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                var jo = JObject.Load(reader);
                if (jo.TryGetValue("value", out var valueToken) && valueToken.Type == JTokenType.Integer)
                {
                    return valueToken.Value<int>();
                }
            }

            return 0;
        }

        public override void WriteJson(JsonWriter writer, int value, JsonSerializer serializer)
        {
            writer.WriteValue(value);
        }
    }

    /// <summary>
    /// Handles Blizzard responses that sometimes return a double or a stringified double.
    /// </summary>
    public class FlexibleDoubleConverter : JsonConverter<double>
    {
        public override double ReadJson(JsonReader reader, Type objectType, double existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Float || reader.TokenType == JsonToken.Integer)
            {
                return Convert.ToDouble(reader.Value);
            }

            if (reader.TokenType == JsonToken.String && double.TryParse((string)reader.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                var jo = JObject.Load(reader);
                if (jo.TryGetValue("value", out var valueToken) && valueToken.Type != JTokenType.Null)
                {
                    return valueToken.Value<double>();
                }
            }

            return 0d;
        }

        public override void WriteJson(JsonWriter writer, double value, JsonSerializer serializer)
        {
            writer.WriteValue(value);
        }
    }
}
