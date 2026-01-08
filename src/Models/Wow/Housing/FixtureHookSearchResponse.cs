using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class FixtureHookSearchResponseResultsItemKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureHookSearchResponseResultsItemData
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class FixtureHookSearchResponseResultsItem
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public FixtureHookSearchResponseResultsItemKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("data")]
        public FixtureHookSearchResponseResultsItemData Data { get; set; }
    }

    public class FixtureHookSearchResponse
    {
        [Newtonsoft.Json.JsonProperty("page")]
        public long Page { get; set; }

        [Newtonsoft.Json.JsonProperty("pageSize")]
        public long PageSize { get; set; }

        [Newtonsoft.Json.JsonProperty("maxPageSize")]
        public long MaxPageSize { get; set; }

        [Newtonsoft.Json.JsonProperty("pageCount")]
        public long PageCount { get; set; }

        [Newtonsoft.Json.JsonProperty("results")]
        public List<FixtureHookSearchResponseResultsItem> Results { get; set; }
    }
}
