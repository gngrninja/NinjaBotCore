using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Models.Wow
{
    public class ItemSearchResponse
    {
        [JsonProperty("results")]
        public List<ItemSearchResultEntry> Results { get; set; }
    }

    public class ItemSearchResultEntry
    {
        [JsonProperty("data")]
        public ItemSearchResultData Data { get; set; }
    }

    public class ItemSearchResultData
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public ItemSearchLocalizedName Name { get; set; }
    }

    public class ItemSearchLocalizedName
    {
        [JsonProperty("en_US")]
        public string EnUS { get; set; }
    }

    /// <summary>
    /// Converts a JSON value that may be either a plain string or a localized name object
    /// (e.g. {"en_US": "..."}) into an ItemSearchLocalizedName.
    /// Blizzard's API is inconsistent about which format it uses.
    /// </summary>
    public class FlexibleLocalizedNameConverter : JsonConverter<ItemSearchLocalizedName>
    {
        public override ItemSearchLocalizedName ReadJson(JsonReader reader, Type objectType,
            ItemSearchLocalizedName existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            if (token.Type == JTokenType.String)
                return new ItemSearchLocalizedName { EnUS = token.ToString() };
            if (token.Type == JTokenType.Object)
                return token.ToObject<ItemSearchLocalizedName>();
            return null;
        }

        public override void WriteJson(JsonWriter writer, ItemSearchLocalizedName value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }
    }

    public class RecipeResponse
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        [JsonConverter(typeof(FlexibleLocalizedNameConverter))]
        public ItemSearchLocalizedName Name { get; set; }

        [JsonProperty("crafted_item")]
        public RecipeCraftedItem CraftedItem { get; set; }
    }

    public class RecipeCraftedItem
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        [JsonConverter(typeof(FlexibleLocalizedNameConverter))]
        public ItemSearchLocalizedName Name { get; set; }
    }
}
