using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class DecorSearchResponseResultsItemKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class DecorSearchResponseResultsItemDataItemName
    {
        [Newtonsoft.Json.JsonProperty("it_IT")]
        public string ItIT { get; set; }

        [Newtonsoft.Json.JsonProperty("ru_RU")]
        public string RuRU { get; set; }

        [Newtonsoft.Json.JsonProperty("en_GB")]
        public string EnGB { get; set; }

        [Newtonsoft.Json.JsonProperty("zh_TW")]
        public string ZhTW { get; set; }

        [Newtonsoft.Json.JsonProperty("ko_KR")]
        public string KoKR { get; set; }

        [Newtonsoft.Json.JsonProperty("en_US")]
        public string EnUS { get; set; }

        [Newtonsoft.Json.JsonProperty("es_MX")]
        public string EsMX { get; set; }

        [Newtonsoft.Json.JsonProperty("pt_BR")]
        public string PtBR { get; set; }

        [Newtonsoft.Json.JsonProperty("es_ES")]
        public string EsES { get; set; }

        [Newtonsoft.Json.JsonProperty("zh_CN")]
        public string ZhCN { get; set; }

        [Newtonsoft.Json.JsonProperty("fr_FR")]
        public string FrFR { get; set; }

        [Newtonsoft.Json.JsonProperty("de_DE")]
        public string DeDE { get; set; }
    }

    public class DecorSearchResponseResultsItemDataItem
    {
        [Newtonsoft.Json.JsonProperty("name")]
        public DecorSearchResponseResultsItemDataItemName Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class DecorSearchResponseResultsItemDataName
    {
        [Newtonsoft.Json.JsonProperty("it_IT")]
        public string ItIT { get; set; }

        [Newtonsoft.Json.JsonProperty("ru_RU")]
        public string RuRU { get; set; }

        [Newtonsoft.Json.JsonProperty("en_GB")]
        public string EnGB { get; set; }

        [Newtonsoft.Json.JsonProperty("zh_TW")]
        public string ZhTW { get; set; }

        [Newtonsoft.Json.JsonProperty("ko_KR")]
        public string KoKR { get; set; }

        [Newtonsoft.Json.JsonProperty("en_US")]
        public string EnUS { get; set; }

        [Newtonsoft.Json.JsonProperty("es_MX")]
        public string EsMX { get; set; }

        [Newtonsoft.Json.JsonProperty("pt_BR")]
        public string PtBR { get; set; }

        [Newtonsoft.Json.JsonProperty("es_ES")]
        public string EsES { get; set; }

        [Newtonsoft.Json.JsonProperty("zh_CN")]
        public string ZhCN { get; set; }

        [Newtonsoft.Json.JsonProperty("fr_FR")]
        public string FrFR { get; set; }

        [Newtonsoft.Json.JsonProperty("de_DE")]
        public string DeDE { get; set; }
    }

    public class DecorSearchResponseResultsItemData
    {
        [Newtonsoft.Json.JsonProperty("item")]
        public DecorSearchResponseResultsItemDataItem Item { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public DecorSearchResponseResultsItemDataName Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class DecorSearchResponseResultsItem
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public DecorSearchResponseResultsItemKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("data")]
        public DecorSearchResponseResultsItemData Data { get; set; }
    }

    public class DecorSearchResponse
    {
        [Newtonsoft.Json.JsonProperty("page")]
        public long Page { get; set; }

        [Newtonsoft.Json.JsonProperty("pageSize")]
        public long PageSize { get; set; }

        [Newtonsoft.Json.JsonProperty("maxPageSize")]
        public long MaxPageSize { get; set; }

        [Newtonsoft.Json.JsonProperty("pageCount")]
        public long PageCount { get; set; }

        [Newtonsoft.Json.JsonProperty("resultCountCapped")]
        public bool ResultCountCapped { get; set; }

        [Newtonsoft.Json.JsonProperty("results")]
        public List<DecorSearchResponseResultsItem> Results { get; set; }
    }
}
