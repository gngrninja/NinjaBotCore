using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class FixtureIndexResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureIndexResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public FixtureIndexResponseLinksSelf Self { get; set; }
    }

    public class FixtureIndexResponseFixturesItemKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureIndexResponseFixturesItem
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public FixtureIndexResponseFixturesItemKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class FixtureIndexResponse
    {
        [Newtonsoft.Json.JsonProperty("_links")]
        public FixtureIndexResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("fixtures")]
        public List<FixtureIndexResponseFixturesItem> Fixtures { get; set; }
    }
}
