using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class FixtureHookDetailResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureHookDetailResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public FixtureHookDetailResponseLinksSelf Self { get; set; }
    }

    public class FixtureHookDetailResponseParentFixtureKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureHookDetailResponseParentFixture
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public FixtureHookDetailResponseParentFixtureKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }
    }

    public class FixtureHookDetailResponse
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Newtonsoft.Json.JsonProperty("_links")]
        public FixtureHookDetailResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("type_name")]
        public string TypeName { get; set; }

        [Newtonsoft.Json.JsonProperty("parent_fixture")]
        public FixtureHookDetailResponseParentFixture ParentFixture { get; set; }
    }
}
