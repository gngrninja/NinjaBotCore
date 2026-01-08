using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NinjaBotCore.Models.Wow.Housing
{

    public class FixtureHookIndexResponseLinksSelf
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureHookIndexResponseLinks
    {
        [Newtonsoft.Json.JsonProperty("self")]
        public FixtureHookIndexResponseLinksSelf Self { get; set; }
    }

    public class FixtureHookIndexResponseFixtureHooksItemKey
    {
        [Newtonsoft.Json.JsonProperty("href")]
        public string Href { get; set; }
    }

    public class FixtureHookIndexResponseFixtureHooksItem
    {
        [Newtonsoft.Json.JsonProperty("key")]
        public FixtureHookIndexResponseFixtureHooksItemKey Key { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }
    }

    public class FixtureHookIndexResponse
    {
        [Newtonsoft.Json.JsonProperty("_links")]
        public FixtureHookIndexResponseLinks Links { get; set; }

        [Newtonsoft.Json.JsonProperty("fixture_hooks")]
        public List<FixtureHookIndexResponseFixtureHooksItem> FixtureHooks { get; set; }
    }
}
