using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;
public class RaidRecapAnalysisTransportTests
{
    // Synthetic contract fixtures. Reflection permits executable behavioral RED before the API exists.
    public static RaidRecapFight Fight => new(2,10,4,"Boss",false,false,5000,65000,15);
    public static RaidRecapReport Report => RaidRecapPanelTests.Report(false) with { Fights=new[]{Fight} };
    public sealed class Handler : HttpMessageHandler
    {
        public readonly List<JObject> Queries=new();
        public Func<int,JObject> Reply= i=>Envelope(Deaths());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri.AbsolutePath.Contains("oauth")) return Json(JObject.Parse("{\"access_token\":\"synthetic\",\"expires_in\":3600}"));
            Assert.Equal("https://www.warcraftlogs.com/api/v2/client",request.RequestUri.AbsoluteUri);
            var q=JObject.Parse(await request.Content.ReadAsStringAsync(token));
            if(q.Value<string>("query").Contains("rateLimitData")) return Json(JObject.Parse("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":1,\"pointsResetIn\":100}}}"));
            Queries.Add(q);return Json(Reply(Queries.Count));
        }
        private static HttpResponseMessage Json(JObject data)=>new(HttpStatusCode.OK){Content=new StringContent(data.ToString())};
    }
    public static WarcraftLogsV2Client Client(Handler h)
    {
        var f=new Mock<IHttpClientFactory>(); f.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(h,false));
        return new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{["WclClientId"]="test",["WclClientSecret"]="test"}).Build(),f.Object,NullLogger<WarcraftLogsV2Client>.Instance);
    }
    public static JObject Death(double time=6000,int actor=1)=>new(){["type"]="death",["timestamp"]=time,["fight"]=2,["targetID"]=actor,["killingAbility"]=new JObject{["name"]="Melee"},["ability"]=new JObject{["name"]="WRONG"}};
    public static JObject Deaths(JArray rows=null,JToken cursor=null)=>new(){
        ["events"]=new JObject{["data"]=rows??new JArray(),["nextPageTimestamp"]=cursor??JValue.CreateNull()},
        ["fights"]=new JArray(new JObject{["id"]=2,["friendlyPlayers"]=new JArray(1,2,3)}),
        ["masterData"]=new JObject{["actors"]=new JArray(
            new JObject{["id"]=1,["type"]="Player",["name"]="Alpha"},
            new JObject{["id"]=2,["type"]="Player",["name"]="Beta"},
            new JObject{["id"]=3,["type"]="Pet",["name"]="Not a player"})}};
    public static JObject Envelope(JObject report)
    {
        var r=(JObject)report.DeepClone();r["code"]=Report.Code;r["revision"]=Report.Revision;r["endTime"]=Report.EndTime;
        return new JObject{["data"]=new JObject{["reportData"]=new JObject{["report"]=r}}};
    }
    public static async Task<JObject> Load(Handler handler,string metric="deaths",RaidRecapReport report=null,RaidRecapFight fight=null,CancellationToken token=default)
    {
        var method=typeof(WarcraftLogsV2Client).GetMethod("GetRaidRecapAnalysisAsync");Assert.NotNull(method);
        try
        {
            var work=(Task)method.Invoke(Client(handler),new object[]{report??Report,fight??Fight,metric,token});
            await work;return JObject.FromObject(work.GetType().GetProperty("Result").GetValue(work));
        }
        catch(TargetInvocationException ex) { throw ex.InnerException; }
    }

    [Fact]
    public async Task DeathPagesRetainExactCursorFightAndEndEvenWhenPageExceedsRequestedLimit()
    {
        var h=new Handler{ Reply=i=>Envelope(Deaths(i==1?new JArray(Enumerable.Range(0,101).Select(n=>Death(6000+n))):new JArray(Death(10000)),i==1?new JValue(10000):null)) };
        var result=await Load(h);
        Assert.True((bool)result["Complete"]);Assert.Equal(102,result["Deaths"].Count());
        Assert.Equal(2,h.Queries.Count);
        Assert.Equal(10000,(double)h.Queries[1]["variables"]["start"]);
        foreach(var q in h.Queries)
        {
            Assert.Equal(65000,(double)q["variables"]["end"]);Assert.Equal(2,(int)q["variables"]["fights"][0]);
            Assert.Contains("limit: 100",(string)q["query"]);Assert.Contains("useActorIDs: true",(string)q["query"]);Assert.Contains("useAbilityIDs: false",(string)q["query"]);
            Assert.DoesNotContain("table(",(string)q["query"]);
        }
    }
    [Fact]
    public async Task RosterIntersectionPreservesTiesRepeatedDeathsAndKillingAbility()
    {
        var h=new Handler{Reply=_=>Envelope(Deaths(new JArray(Death(6000,1),Death(6000,2),Death(7000,1),Death(8000,3),Death(9000,999))))};
        var r=await Load(h);Assert.True((bool)r["Complete"]);Assert.Equal(3,r["Deaths"].Count());
        Assert.Equal(2,(int)r["DistinctPlayers"]);Assert.Equal(1000,(double)r["Deaths"][0]["ElapsedMs"]);
        Assert.Equal(2,r["Deaths"].Count(d=>(double)d["ElapsedMs"]==1000));Assert.Equal("Melee",(string)r["Deaths"][0]["Ability"]);
    }
    [Theory]
    [InlineData(5000)] [InlineData(4000)] [InlineData(65001)]
    public async Task InvalidCursorIsPartialNeverCompleteZero(double cursor)
    {
        var h=new Handler{Reply=_=>Envelope(Deaths(cursor:new JValue(cursor)))};
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Single(h.Queries);Assert.NotNull(r["Notice"]);
    }
    [Fact]
    public async Task MissingCursorIsNotKnownEmpty()
    {
        var page=Deaths();((JObject)page["events"]).Remove("nextPageTimestamp");
        var r=await Load(new Handler{Reply=_=>Envelope(page)});Assert.False((bool)r["Complete"]);
    }
    [Fact]
    public async Task CompleteEmptyDeathPageIsDistinctFromUnavailableRoster()
    {
        Assert.True((bool)(await Load(new Handler()))["Complete"]);
        var page=Deaths();page.Remove("fights");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(new Handler{Reply=_=>Envelope(page)}));
    }
    [Fact]
    public async Task UnresolvedRosterIdentityIsVisiblePartial()
    {
        var page=Deaths(new JArray(Death()));page["masterData"]["actors"]=new JArray();
        var r=await Load(new Handler{Reply=_=>Envelope(page)});Assert.False((bool)r["Complete"]);Assert.NotNull(r["Notice"]);
    }
    [Fact]
    public async Task PageBudgetStopsAfterFiveRequests()
    {
        var h=new Handler{Reply=i=>Envelope(Deaths(new JArray(Death(5000+i*1000)),new JValue(5001+i*1000)))};
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Equal(5,h.Queries.Count);
    }
    [Fact]
    public async Task GlobalEventRowBudgetIsFiveHundred()
    {
        var h=new Handler{Reply=_=>Envelope(Deaths(new JArray(Enumerable.Range(0,501).Select(i=>Death(6000+i)))))};
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Equal(500,r["Deaths"].Count());Assert.Single(h.Queries);
    }
    [Fact]
    public async Task LaterPageErrorsPreserveObservedRowsAsPartial()
    {
        var h=new Handler{Reply=i=>i==1?Envelope(Deaths(new JArray(Death()),new JValue(7000))):JObject.Parse("{\"errors\":[{\"message\":\"denied\"}]}")};
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Single(r["Deaths"]);
    }
    [Theory]
    [InlineData("revision")] [InlineData("endTime")] [InlineData("code")]
    public async Task SnapshotDriftOnLaterResponseDiscardsEntireAnalysis(string field)
    {
        var h=new Handler{Reply=i=>{var e=Envelope(Deaths(new JArray(Death(i==1?6000:8000)),i==1?new JValue(7000):null));if(i==2)e["data"]["reportData"]["report"][field]=field=="code"?new JValue("ZZZZZZZZ12345678"):new JValue(999);return e;}};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(h));Assert.Equal(2,h.Queries.Count);
    }
    [Fact]
    public async Task CancelledAnalysisDoesNotContactProvider()
    {
        var h=new Handler();using var c=new CancellationTokenSource();c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Load(h,token:c.Token));Assert.Empty(h.Queries);
    }
    [Theory]
    [InlineData("incoming","DamageTaken","Ability")] [InlineData("interrupts","Interrupts","Source")] [InlineData("dispels","Dispels","Source")]
    public async Task AnalysisTablesQueryOnlyChosenMetricForExactCompletedWipe(string metric,string type,string view)
    {
        var h=new Handler{Reply=_=>Envelope(new JObject{["table"]=JObject.Parse("{\"data\":{\"entries\":[]}}")})};
        var r=await Load(h,metric);Assert.True((bool)r["Complete"]);
        var q=Assert.Single(h.Queries);Assert.Contains("dataType: "+type,(string)q["query"]);Assert.Contains("viewBy: "+view,(string)q["query"]);
        Assert.Contains("killType: All",(string)q["query"]);Assert.DoesNotContain("events(",(string)q["query"]);
        Assert.Equal(5000,(double)q["variables"]["start"]);Assert.Equal(65000,(double)q["variables"]["end"]);
    }
    [Fact]
    public async Task IncomingUsesTopLevelTotalsWithoutCompositeOrReductionDoubleCounting()
    {
        var h=new Handler{Reply=_=>Envelope(new JObject{["table"]=JObject.Parse("""{"data":{"entries":[{"name":"Composite","total":300,"totalReduced":200,"subentries":[{"total":100},{"total":200}]},{"name":"Large","actorName":"Boss source","total":900}]}}""")})};
        var r=await Load(h,"incoming");Assert.Equal(2,r["Incoming"].Count());Assert.Equal(900,(double)r["Incoming"][0]["Total"]);Assert.Equal("Boss source",(string)r["Incoming"][0]["Source"]);Assert.Equal(300,(double)r["Incoming"][1]["Total"]);
    }
    [Theory]
    [InlineData("interrupts")] [InlineData("dispels")]
    public async Task UtilityUsesNestedSpellCountsAndTopLevelParticipantAttribution(string metric)
    {
        var h=new Handler{Reply=_=>Envelope(new JObject{["table"]=JObject.Parse("""{"data":{"entries":[{"entries":[{"name":"Spell","spellsInterrupted":3,"spellsCompleted":8,"spellChannelsInterrupted":2,"details":[{"id":1,"name":"Owner","total":3,"pets":[{"total":3}]}]}]}]}}""")})};
        var r=await Load(h,metric);var spell=Assert.Single(r["Utility"]);Assert.Equal(3,(double)spell["Actions"]);Assert.Equal(8,(double)spell["CompletedCasts"]);Assert.Equal(2,(double)spell["Channels"]);
        var actor=Assert.Single(spell["Participants"]);Assert.Equal(3,(double)actor["Count"]);Assert.True((bool)spell["AttributionKnown"]);
    }
    [Theory]
    [InlineData("-1")] [InlineData("1.5")] [InlineData("\"3\"")] [InlineData("null")]
    public async Task InvalidUtilityCountsAreUnknownNotZero(string number)
    {
        var table=JObject.Parse("{\"data\":{\"entries\":[{\"entries\":[{\"name\":\"Spell\",\"spellsInterrupted\":"+number+",\"details\":[]}]}]}}");
        var r=await Load(new Handler{Reply=_=>Envelope(new JObject{["table"]=table})},"interrupts");Assert.False((bool)r["Complete"]);Assert.Equal(JTokenType.Null,r["Utility"][0]["Actions"].Type);
    }
    [Fact]
    public async Task MissingAttributionWhenParticipantCountsDoNotMatchActionsIsVisible()
    {
        var table=JObject.Parse("""{"data":{"entries":[{"entries":[{"name":"Spell","spellsInterrupted":3,"spellsCompleted":0,"spellChannelsInterrupted":0,"details":[{"id":1,"name":"Partial","total":1}]}]}]}}""");
        var r=await Load(new Handler{Reply=_=>Envelope(new JObject{["table"]=table})},"interrupts");
        Assert.False((bool)r["Utility"][0]["AttributionKnown"]);Assert.False((bool)r["Complete"]);
    }
    [Fact]
    public async Task MissingUtilityRowShapesConsumeTheBoundedRowBudget()
    {
        var rows=new JArray(Enumerable.Range(0,500).Select(_=>JValue.CreateNull()));
        rows.Add(JObject.Parse("{\"name\":\"Beyond budget\",\"spellsInterrupted\":1}"));
        var table=new JObject{["data"]=new JObject{["entries"]=new JArray(new JObject{["entries"]=rows})}};
        var r=await Load(new Handler{Reply=_=>Envelope(new JObject{["table"]=table})},"dispels");
        Assert.False((bool)r["Complete"]);Assert.Empty(r["Utility"]);
    }

    [Fact]
    public async Task FlatUtilityShapeIsUnavailableNotZeroActions()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(new Handler{Reply=_=>Envelope(new JObject{["table"]=JObject.Parse("{\"data\":{\"entries\":[{\"name\":\"Unexpected flat source\",\"total\":3}]}}")})},"interrupts"));
    }
}
