using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class FixtureDetailResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureDetailResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public FixtureDetailResponseLinksSelf Self { get; set; }
    }

    public class FixtureDetailResponse
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("_links")]
        public FixtureDetailResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }
    }
}
