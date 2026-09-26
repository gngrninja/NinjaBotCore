using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

// All identities and report data here are synthetic. Real specimens replay only from scratch.
public class RaidRecapMechanicTransportTests
{
    public const string Junk = "throw-junk-damage-events";
    public const string Spin = "shell-spin-debuff-applications";
    public static RaidRecapFight Fight => new(2,3497,4,"The Lost Explorers",false,false,5000,65000,15);
    public static RaidRecapReport Report => RaidRecapPanelTests.Report(false) with { Fights=new[]{Fight} };
    public sealed class Handler : HttpMessageHandler
    {
        public readonly List<JObject> Queries=new();
        public Func<int,JObject,JObject> Reply = (i,q)=>Envelope(i==1?Metadata():Events());
        public Func<int,CancellationToken,Task> BeforeReply;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(request.RequestUri.AbsolutePath.Contains("oauth")) return Json(JObject.Parse("{\"access_token\":\"synthetic\",\"expires_in\":3600}"));
            Assert.Equal("https://www.warcraftlogs.com/api/v2/client",request.RequestUri.AbsoluteUri);
            var q=JObject.Parse(await request.Content.ReadAsStringAsync(token));
            if(q.Value<string>("query").Contains("rateLimitData")) return Json(JObject.Parse("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":1,\"pointsResetIn\":100}}}"));
            Queries.Add(q);
            if(BeforeReply!=null) await BeforeReply(Queries.Count,token);
            token.ThrowIfCancellationRequested();
            return Json(Reply(Queries.Count,q));
        }
        private static HttpResponseMessage Json(JObject data)=>new(HttpStatusCode.OK){Content=new StringContent(data.ToString())};
    }
    public static WarcraftLogsV2Client Client(Handler h)
    {
        var factory=new Mock<IHttpClientFactory>();factory.Setup(f=>f.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(h,false));
        return new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{["WclClientId"]="test",["WclClientSecret"]="test"}).Build(),factory.Object,NullLogger<WarcraftLogsV2Client>.Instance);
    }
    public static JObject Actor(int id,string type="Player",string name="Synthetic player")=>new(){["id"]=id,["type"]=type,["name"]=name};
    public static JObject Metadata()=>new(){
        ["archiveStatus"]=new JObject{["isArchived"]=false,["isAccessible"]=true},
        ["zone"]=new JObject{["id"]=53,["expansion"]=new JObject{["id"]=7}},
        ["fights"]=new JArray(new JObject{["id"]=2,["encounterID"]=3497,["difficulty"]=4,["kill"]=false,["inProgress"]=false,["completeRaid"]=false,["startTime"]=5000,["endTime"]=65000,["gameZone"]=new JObject{["id"]=3004},["friendlyPlayers"]=new JArray(1,2)}),
        ["masterData"]=new JObject{["gameVersion"]=1,["logVersion"]=17,["actors"]=new JArray(Actor(1,name:"Alpha"),Actor(2,name:"Beta"),Actor(3,"Pet"),Actor(4,"NPC"),Actor(5))}};
    public static JObject Row(double time=6000,int target=1,string metric=Junk,string type=null)=>new(){["timestamp"]=time,["fight"]=2,["abilityGameID"]=metric==Junk?1291935:1291918,["type"]=type??(metric==Junk?"damage":"applydebuff"),["targetID"]=target,["amount"]=0,["absorbed"]=42};
    public static JObject Events(JArray rows=null,JToken cursor=null)=>new(){["events"]=new JObject{["data"]=rows??new JArray(),["nextPageTimestamp"]=cursor??JValue.CreateNull()}};
    public static JObject Envelope(JObject report)=>RaidRecapAnalysisTransportTests.Envelope(report);
    public static Handler With(JObject metadata=null,JObject events=null)=>new(){Reply=(i,q)=>Envelope(i==1?metadata??Metadata():events??Events())};
    public static async Task<JObject> Load(Handler h,string metric=Junk,CancellationToken token=default)=>JObject.FromObject(await Client(h).GetRaidRecapAnalysisAsync(Report,Fight,metric,token));
    private static JToken Mechanic(JObject result) { Assert.NotNull(result["Mechanic"]);return result["Mechanic"]; }

    [Theory]
    [InlineData(Junk,1291935,"DamageTaken")]
    [InlineData(Spin,1291918,"Debuffs")]
    public async Task SelectedRuleOnlyUsesTypedNumericSpellQueryAfterLazyGatedMetadata(string metric,int spell,string type)
    {
        var h=With(events:Events(new JArray(Row(metric:metric))));var result=await Load(h,metric);
        Assert.True((bool)result["Complete"]);var m=Mechanic(result);Assert.Single(m["Events"]);
        Assert.Equal(1,(int)m["DistinctPlayers"]);Assert.Equal(1000,(double)m["Events"][0]["ElapsedMs"]);
        Assert.Equal(0,(double)m["Events"][0]["Amount"]);Assert.Equal(42,(double)m["Events"][0]["Absorbed"]);
        Assert.Equal(2,h.Queries.Count);
        var metadata=(string)h.Queries[0]["query"];Assert.Contains("friendlyPlayers",metadata);Assert.Contains("gameVersion",metadata);Assert.DoesNotContain("abilities",metadata);Assert.DoesNotContain("events(",metadata);
        var q=h.Queries[1];Assert.Equal(type,(string)q["variables"]["eventType"]);Assert.Equal(spell,(int)q["variables"]["ability"]);
        Assert.Equal(5000,(double)q["variables"]["start"]);Assert.Equal(65000,(double)q["variables"]["end"]);Assert.Equal(2,(int)q["variables"]["fights"][0]);
        foreach(var field in new[]{"dataType: $eventType","abilityID: $ability","limit: 100","useActorIDs: true","useAbilityIDs: true","hostilityType: Friendlies","wipeCutoff: 0"}) Assert.Contains(field,(string)q["query"]);
        Assert.DoesNotContain("dataType: All",(string)q["query"]);
    }
    [Fact]
    public async Task TiesRepeatedIdenticalRowsZeroDamageAndMissingOptionalNumbersStillCount()
    {
        var optional=Row(7000);optional["amount"]="malformed";optional.Remove("absorbed");
        var r=await Load(With(events:Events(new JArray(Row(),Row(target:2),Row(),optional))));
        Assert.True((bool)r["Complete"]);var m=Mechanic(r);Assert.Equal(4,m["Events"].Count());Assert.Equal(2,(int)m["DistinctPlayers"]);
        Assert.Equal(3,m["Events"].Count(e=>(double)e["ElapsedMs"]==1000));Assert.Equal(JTokenType.Null,m["Events"][3]["Amount"].Type);
    }
    [Fact]
    public async Task SpinCountsOnlyApplicationsNotRemovalsRefreshStacksDamageCastsOrSummons()
    {
        var rows=new JArray(new[]{"applydebuff","removedebuff","refreshdebuff","applydebuffstack","removedebuffstack","damage","cast","summon"}.Select(t=>Row(metric:Spin,type:t)));
        var r=await Load(With(events:Events(rows)),Spin);Assert.True((bool)r["Complete"]);Assert.Single(Mechanic(r)["Events"]);
    }
    [Fact]
    public async Task ExplicitNullCursorAndValidEmptyDataIsCompleteZero()
    {
        var r=await Load(With());Assert.True((bool)r["Complete"]);Assert.Equal("complete",(string)Mechanic(r)["Coverage"]);Assert.Empty(Mechanic(r)["Events"]);
    }
    [Theory]
    [InlineData("zone.id","54")] [InlineData("zone.expansion.id","8")]
    [InlineData("masterData.gameVersion","2")] [InlineData("masterData.gameVersion","null")]
    [InlineData("fights[0].encounterID","9999")] [InlineData("fights[0].difficulty","5")]
    [InlineData("fights[0].gameZone.id","3005")] [InlineData("fights[0].completeRaid","true")]
    [InlineData("fights[0].kill","null")] [InlineData("fights[0].inProgress","true")]
    [InlineData("zone.id","\"53\"")] [InlineData("zone.id","null")]
    [InlineData("zone.expansion.id","null")] [InlineData("fights[0].completeRaid","null")]
    [InlineData("fights[0].inProgress","null")] [InlineData("fights[0].difficulty","null")]
    [InlineData("fights[0].gameZone.id","null")]
    public async Task UnsupportedOrUnknownScopeNeverQueriesEventsOrClaimsZero(string path,string value)
    {
        var metadata=Metadata();metadata.SelectToken(path).Replace(JToken.Parse(value));var h=With(metadata);
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Equal("unsupported",(string)Mechanic(r)["Coverage"]);Assert.Single(h.Queries);
    }
    [Fact]
    public async Task ParserLogVersionIsNotAnExactClientBuildGate()
    {
        var metadata=Metadata();metadata["masterData"]["logVersion"]=999;var r=await Load(With(metadata));Assert.True((bool)r["Complete"]);
    }
    [Theory]
    [InlineData("archiveStatus")] [InlineData("archiveStatus.isAccessible")]
    [InlineData("fights[0].friendlyPlayers")] [InlineData("masterData.actors")]
    public async Task MissingRequiredMetadataIsUnavailableBeforeEvents(string path)
    {
        var metadata=Metadata();metadata.SelectToken(path).Parent.Remove();var h=With(metadata);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(h));Assert.Single(h.Queries);
    }
    [Theory]
    [InlineData("archiveStatus.isAccessible","false")]
    [InlineData("fights[0].id","99")] [InlineData("fights[0].startTime","0")]
    [InlineData("fights[0].endTime","65001")] [InlineData("fights[0].startTime","null")]
    public async Task DeniedOrChangedFightScopeIsUnavailable(string path,string value)
    {
        var metadata=Metadata();metadata.SelectToken(path).Replace(JToken.Parse(value));var h=With(metadata);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(h));Assert.Single(h.Queries);
    }
    [Theory]
    [InlineData("roster")] [InlineData("actors")] [InlineData("conflict")] [InlineData("fight")]
    [InlineData("empty-roster")] [InlineData("invalid-roster")] [InlineData("huge-roster")] [InlineData("huge-actors")]
    public async Task DuplicateMalformedOrOverBudgetIdentityMetadataIsUnavailable(string kind)
    {
        var metadata=Metadata();var roster=(JArray)metadata["fights"][0]["friendlyPlayers"];var actors=(JArray)metadata["masterData"]["actors"];
        switch(kind)
        {
            case "roster":roster.Add(1);break;
            case "actors":actors.Add(Actor(1));break;
            case "conflict":actors.Add(Actor(1,"Pet"));break;
            case "fight":((JArray)metadata["fights"]).Add(metadata["fights"][0].DeepClone());break;
            case "empty-roster":roster.Clear();break;
            case "invalid-roster":roster.Add("8");break;
            case "huge-roster":for(var i=3;i<=101;i++)roster.Add(i);break;
            case "huge-actors":for(var i=6;i<=2001;i++)actors.Add(Actor(i));break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(With(metadata)));
    }
    [Fact]
    public async Task KnownPetsAndNpcsAreExcludedWithoutOwnerAttribution()
    {
        var r=await Load(With(events:Events(new JArray(Row(target:3),Row(target:4)))));
        Assert.True((bool)r["Complete"]);Assert.Empty(Mechanic(r)["Events"]);
    }
    [Theory]
    [InlineData("unresolved")] [InlineData("out-of-roster")] [InlineData("missing-target")]
    [InlineData("invalid-target")] [InlineData("missing-roster-actor")] [InlineData("unknown-type")]
    public async Task IdentityGapsNeverBecomeCompleteZero(string kind)
    {
        var metadata=Metadata();var row=Row();
        switch(kind)
        {
            case "unresolved":row["targetID"]=99;break;
            case "out-of-roster":row["targetID"]=5;break;
            case "missing-target":row.Remove("targetID");break;
            case "invalid-target":row["targetID"]="1";break;
            case "missing-roster-actor":((JArray)metadata["masterData"]["actors"])[0].Remove();break;
            case "unknown-type":metadata["masterData"]["actors"][0]["type"]="Unknown";break;
        }
        var r=await Load(With(metadata,Events(new JArray(row))));Assert.False((bool)r["Complete"]);Assert.Equal("partial",(string)Mechanic(r)["Coverage"]);Assert.Empty(Mechanic(r)["Events"]);
    }
    [Fact]
    public async Task MissingRosterPlayerMetadataIsPartialEvenForEmptyStream()
    {
        var metadata=Metadata();((JArray)metadata["masterData"]["actors"])[0].Remove();var r=await Load(With(metadata));Assert.False((bool)r["Complete"]);
    }
    [Theory]
    [InlineData("abilityGameID","1291933")] [InlineData("abilityGameID","\"1291935\"")]
    [InlineData("fight","1")] [InlineData("type","\"cast\"")]
    [InlineData("timestamp","4999")] [InlineData("timestamp","65001")] [InlineData("timestamp","null")]
    public async Task WrongSpellFightKindOrTimeIsIncompleteNotSuccessfulZero(string field,string value)
    {
        var row=Row();row[field]=JToken.Parse(value);var r=await Load(With(events:Events(new JArray(row))));Assert.False((bool)r["Complete"]);Assert.Empty(Mechanic(r)["Events"]);
    }
    [Theory]
    [InlineData("missing")] [InlineData("\"7000\"")] [InlineData("5000")]
    [InlineData("4000")] [InlineData("65001")] [InlineData("6000")]
    public async Task UnsupportedCursorIsIncompleteWithoutInventingNextStart(string value)
    {
        var events=Events(new JArray(Row()));if(value=="missing")((JObject)events["events"]).Remove("nextPageTimestamp");else events["events"]["nextPageTimestamp"]=JToken.Parse(value);
        var h=With(events:events);var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Single(Mechanic(r)["Events"]);Assert.Equal(2,h.Queries.Count);
    }
    [Theory]
    [InlineData("missing-events")] [InlineData("null-data")] [InlineData("object-data")] [InlineData("graphql-error")]
    public async Task MalformedInitialEventsAreUnavailableNotPartialEmpty(string kind)
    {
        var events=Events();if(kind=="missing-events")events.Remove("events");if(kind=="null-data")events["events"]["data"]=null;if(kind=="object-data")events["events"]["data"]=new JObject();
        var h=With(events:events);if(kind=="graphql-error")h.Reply=(i,q)=>i==1?Envelope(Metadata()):JObject.Parse("{\"errors\":[{\"message\":\"denied\"}]}");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(h));
    }
    [Fact]
    public async Task ExactCursorAndFixedFiltersAcrossPagesKeepRepeatedRawRows()
    {
        var h=new Handler{Reply=(i,q)=>Envelope(i==1?Metadata():i==2?Events(new JArray(Row(),Row()),new JValue(7000)):Events(new JArray(Row(7000),Row(7000))))};
        var r=await Load(h);Assert.True((bool)r["Complete"]);Assert.Equal(4,Mechanic(r)["Events"].Count());Assert.Equal(3,h.Queries.Count);
        Assert.Equal(7000,(double)h.Queries[2]["variables"]["start"]);
        foreach(var key in new[]{"code","fights","end","ability","eventType"})Assert.True(JToken.DeepEquals(h.Queries[1]["variables"][key],h.Queries[2]["variables"][key]));
    }
    [Theory]
    [InlineData(500,true)] [InlineData(501,false)]
    public async Task RawRowBudgetIsIndependentOfRequestedLimit(int count,bool complete)
    {
        var h=With(events:Events(new JArray(Enumerable.Range(0,count).Select(i=>Row(6000+i)))));var r=await Load(h);
        Assert.Equal(complete,(bool)r["Complete"]);Assert.Equal(500,Mechanic(r)["Events"].Count());Assert.Equal(2,h.Queries.Count);
    }
    [Fact]
    public async Task NonapplicationRowsAlsoConsumeBudget()
    {
        var rows=new JArray(Enumerable.Range(0,500).Select(i=>Row(6000+i,metric:Spin,type:"removedebuff")));rows.Add(Row(7000,metric:Spin));
        var r=await Load(With(events:Events(rows)),Spin);Assert.False((bool)r["Complete"]);Assert.Empty(Mechanic(r)["Events"]);
    }
    [Fact]
    public async Task PageBudgetIsFiveEventRequestsBeyondMetadata()
    {
        var h=new Handler{Reply=(i,q)=>Envelope(i==1?Metadata():Events(new JArray(Row(5000+i*1000)),new JValue(5001+i*1000)))};
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Equal(6,h.Queries.Count);Assert.Equal(5,Mechanic(r)["Events"].Count());
    }
    [Theory]
    [InlineData("graphql")] [InlineData("http")] [InlineData("cancelled-provider")] [InlineData("malformed")]
    public async Task LaterExhaustionRetainsOnlyLowerBoundEvidence(string kind)
    {
        var h=new Handler{Reply=(i,q)=>i==1?Envelope(Metadata()):i==2?Envelope(Events(new JArray(Row()),new JValue(7000))):kind switch {
            "http"=>throw new HttpRequestException(),"cancelled-provider"=>throw new OperationCanceledException(),
            "malformed"=>Envelope(new JObject()),_=>JObject.Parse("{\"errors\":[{\"message\":\"denied\"}]}")}};
        var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Single(Mechanic(r)["Events"]);
    }
    [Theory]
    [InlineData("code",1)] [InlineData("revision",1)] [InlineData("endTime",1)]
    [InlineData("code",2)] [InlineData("revision",2)] [InlineData("endTime",2)]
    [InlineData("code",3)] [InlineData("revision",3)] [InlineData("endTime",3)]
    public async Task SnapshotDriftDiscardsEveryPage(string field,int at)
    {
        var h=new Handler{Reply=(i,q)=>{var r=Envelope(i==1?Metadata():Events(new JArray(Row(i==2?6000:7000)),i==2?new JValue(7000):null));if(i==at)r["data"]["reportData"]["report"][field]=field=="code"?new JValue("ZZZZZZZZ12345678"):new JValue(999);return r;}};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(h));Assert.Equal(at,h.Queries.Count);
    }
    [Fact]
    public async Task EnvironmentNpcSentinelDoesNotMakeRealRosterUnavailableOrBecomeAPlayer()
    {
        var metadata=Metadata();((JArray)metadata["masterData"]["actors"]).Insert(0,Actor(-1,"NPC","Environment"));
        var r=await Load(With(metadata,Events(new JArray(Row(target:-1),Row()))));
        Assert.True((bool)r["Complete"]);Assert.Single(Mechanic(r)["Events"]);Assert.Equal(1,(int)Mechanic(r)["Events"][0]["ActorId"]);
    }
    [Fact]
    public async Task EnvironmentSentinelCannotBeAPlayerOrAmbiguous()
    {
        foreach(var actor in new[]{Actor(-1,"Player"),Actor(-1,"NPC")})
        {
            var metadata=Metadata();var actors=(JArray)metadata["masterData"]["actors"];actors.Add(Actor(-1,"NPC"));actors.Add(actor);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(With(metadata)));
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SharedDecodedBodyCapAlsoProtectsMechanicPages(bool later)
    {
        var h=new Handler{Reply=(i,q)=>i==1?Envelope(Metadata()):later&&i==2?Envelope(Events(new JArray(Row()),new JValue(7000))):Envelope(new JObject{["events"]=new JObject{["data"]=new JArray(),["nextPageTimestamp"]=null,["unexpected"]=new string('x',4*1024*1024)}})};
        if(later){var r=await Load(h);Assert.False((bool)r["Complete"]);Assert.Single(Mechanic(r)["Events"]);}
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>Load(h));
    }
    [Fact]
    public async Task WholeAnalysisDeadlineIncludesMetadataTimeAndCancelsFirstEventRequest()
    {
        var h=With();h.BeforeReply=async(i,t)=>await Task.Delay(TimeSpan.FromSeconds(20),t);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Load(h));
        Assert.Equal(2,h.Queries.Count);Assert.InRange(watch.Elapsed.TotalSeconds,27,38);
    }
    [Theory]
    [InlineData("encounter")] [InlineData("difficulty")] [InlineData("kill")]
    [InlineData("live")] [InlineData("unknown-progress")]
    public async Task UnsupportedLocalScopeStopsBeforeMetadata(string scope)
    {
        var fight=scope switch {"encounter"=>Fight with {EncounterId=3498},"difficulty"=>Fight with {Difficulty=null},
            "kill"=>Fight with {Kill=null},"live"=>Fight with {InProgress=true},_=>Fight with {InProgress=null}};
        var report=Report with {Fights=new[]{fight}};var h=With();
        var r=JObject.FromObject(await Client(h).GetRaidRecapAnalysisAsync(report,fight,Junk));
        Assert.False((bool)r["Complete"]);Assert.Equal("unsupported",(string)Mechanic(r)["Coverage"]);Assert.Empty(h.Queries);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public async Task CallerCancellationNeverReturnsCachedPartial(int at)
    {
        using var c=new CancellationTokenSource();var h=new Handler{Reply=(i,q)=>Envelope(i==1?Metadata():Events(new JArray(Row()),new JValue(7000))),BeforeReply=(i,t)=>{if(i==at)c.Cancel();return Task.CompletedTask;}};
        if(at==0)c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Load(h,token:c.Token));Assert.Equal(at,h.Queries.Count);
    }
}
