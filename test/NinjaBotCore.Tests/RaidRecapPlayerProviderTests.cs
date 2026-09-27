using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;
namespace NinjaBotCore.Tests;

public class RaidRecapPlayerProviderTests
{
    // Deliberately synthetic: punctuation differs between canonical and display realms.
    public static RaidRecapReport Report=>RaidRecapPanelTests.Report() with { Fights=new[]{Fight} };
    public static RaidRecapFight Fight=>new(2,3497,4,"Boss",true,false,5000,65000,null);
    public static JObject Metadata()=>JObject.Parse("""
    {code:'AbCdEfGh12345678',revision:1,endTime:999999,
     fights:[{id:2,encounterID:3497,difficulty:4,kill:true,inProgress:false,startTime:5000,endTime:65000,friendlyPlayers:[7,8],friendlySpecs:['Holy','Marksmanship']}],
     masterData:{actors:[{id:7,name:'Same',type:'Player',subType:'Priest',server:'TwoWords'}, {id:8,name:'Same',type:'Player',subType:'Hunter',server:'TwoWords'}]},
     details:{data:{playerDetails:{tanks:[],healers:[{id:7,name:'Same',type:'Priest',server:'TwoWords',region:'US',specs:[{spec:'Holy'}]}],dps:[{id:8,name:'Same',type:'Hunter',server:'TwoWords',region:'US',specs:[{spec:'Marksmanship'}]}]}}}}
    """);
    public static JObject Ranking()=>JObject.Parse("""
    {data:[{fightID:2,partition:1,zone:53,encounter:{id:3497},difficulty:4,size:2,kill:1,duration:60000,reportsBlacklistForCharacters:[],
      roles:{tanks:{characters:[]},healers:{characters:[{id:700007,name:'Same',server:{id:42,name:'Two Words',region:'US'},class:'Priest',spec:'Holy',amount:0,rankPercent:0,totalParses:10,bracket:3,bracketData:300,bracketPercent:25}]},dps:{characters:[{id:800008,name:'Same',server:{id:42,name:'Two Words',region:'US'},class:'Hunter',spec:'Marksmanship',amount:100,rankPercent:99.999,totalParses:20,bracket:3,bracketData:300,bracketPercent:100}]}}}]}
    """);
    public static JObject Servers()=>JObject.Parse("{s0:{id:42,name:'Two Words',normalizedName:'TwoWords',slug:'two-words',region:{slug:'US'}}}");
    public static RaidRecapMechanicTransportTests.Handler Handler(JObject metadata=null,JObject rankings=null,JObject servers=null)=>new(){Reply=(i,q)=>
    {
        var query=(string)q["query"];var report=(JObject)(metadata??Metadata()).DeepClone();
        if(query.Contains("RaidRecapPlayerParses")) report["rankings"]=(rankings??Ranking()).DeepClone();
        var envelope=new JObject{["data"]=new JObject{["reportData"]=new JObject{["report"]=report}}};
        if(query.Contains("RaidRecapParseServers"))envelope["data"]["worldData"]=(servers??Servers()).DeepClone();
        return envelope;
    }};
    public static async Task<object> Invoke(WarcraftLogsV2Client client,string method,params object[] args)
    {
        var m=client.GetType().GetMethod(method);Assert.NotNull(m);
        var task=(Task)m.Invoke(client,args);await task;
        return task.GetType().GetProperty("Result").GetValue(task);
    }
    public static Task<object> Roster(WarcraftLogsV2Client client,CancellationToken token=default)=>Invoke(client,"GetRaidRecapRosterAsync",Report,Fight,token);
    public static Task<object> Parses(WarcraftLogsV2Client client,object roster,bool healing=false,CancellationToken token=default)=>Invoke(client,"GetRaidRecapParsesAsync",Report,Fight,healing,roster,token);
    [Fact]
    public async Task RealHttpParserUsesBoundedAuthoritativeBridgeNotRankingIdAsActor()
    {
        var h=Handler();var c=RaidRecapMechanicTransportTests.Client(h);var roster=await Roster(c);var r=JObject.FromObject(await Parses(c,roster));
        Assert.Equal(new[]{7,8},r["Entries"].Select(x=>(int)x["ActorId"]).OrderBy(x=>x));
        Assert.Equal(0,(double)r["Entries"][0]["Percentile"]);Assert.Equal(99.999,(double)r["Entries"][1]["Percentile"]);
        Assert.Equal("Parses",(string)r["Compare"]);Assert.Equal("Today",(string)r["Timeframe"]);Assert.Equal("dps",(string)r["Metric"]);Assert.Equal(1,(int)r["Partition"]);
        Assert.Equal(3,h.Queries.Count);Assert.Contains("includeCombatantInfo: false",(string)h.Queries[0]["query"]);
        Assert.DoesNotContain("events(",(string)h.Queries[0]["query"]);Assert.Contains("compare: Parses, timeframe: Today",(string)h.Queries[1]["query"]);
        Assert.Equal(42,(int)h.Queries[2]["variables"]["server0"]);Assert.DoesNotContain("Two Words",(string)h.Queries[2]["query"]);
    }
    [Theory]
    [InlineData("rankPercent","null")][InlineData("rankPercent","\"0\"")][InlineData("rankPercent","true")][InlineData("rankPercent","{}")][InlineData("rankPercent","-1")][InlineData("rankPercent","101")][InlineData("rankPercent","NaN")][InlineData("rankPercent","Infinity")][InlineData("rankPercent","-Infinity")]
    [InlineData("totalParses","0")][InlineData("totalParses","1.2")][InlineData("totalParses","\"10\"")]
    [InlineData("id","7.2")][InlineData("id","true")][InlineData("server","42")][InlineData("class","'Hunter'")][InlineData("spec","'Shadow'")]
    public async Task InvalidRankOrIdentityCannotAttachPercentile(string field,string value)
    {
        var ranking=Ranking();ranking["data"][0]["roles"]["healers"]["characters"][0][field]=JToken.Parse(value);
        var c=RaidRecapMechanicTransportTests.Client(Handler(rankings:ranking));var r=JObject.FromObject(await Parses(c,await Roster(c)));
        Assert.DoesNotContain(r["Entries"],x=>(int)x["ActorId"]==7);
    }
    [Theory]
    [InlineData("fightID","3")][InlineData("difficulty","5")][InlineData("duration","61000")][InlineData("partition","null")][InlineData("partition","\"1\"")][InlineData("kill","true")][InlineData("reportsBlacklistForCharacters","[7]")][InlineData("roles","7")]
    public async Task WrongOrUnsupportedRankingScopeFailsClosed(string field,string value)
    {
        var ranking=Ranking();ranking["data"][0][field]=JToken.Parse(value);
        var c=RaidRecapMechanicTransportTests.Client(Handler(rankings:ranking));var roster=await Roster(c);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>Parses(c,roster));
    }
    [Theory]
    [InlineData("fights[0].friendlyPlayers[0]","7.0")][InlineData("fights[0].friendlyPlayers[0]","true")][InlineData("fights[0].friendlyPlayers[0]","8")]
    [InlineData("masterData.actors[0].id","8")][InlineData("fights[0].startTime","5001")][InlineData("revision","2")][InlineData("masterData","7")]
    public async Task MalformedOrDriftingRosterIsUnavailable(string path,string value)
    {
        var m=Metadata();m.SelectToken(path).Replace(JToken.Parse(value));var c=RaidRecapMechanicTransportTests.Client(Handler(metadata:m));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>Roster(c));
    }
    [Fact]
    public async Task RealmMismatchNeverFallsBackToNameAndCancellationPropagates()
    {
        var servers=Servers();servers["s0"]["normalizedName"]="two-words";
        var c=RaidRecapMechanicTransportTests.Client(Handler(servers:servers));var r=JObject.FromObject(await Parses(c,await Roster(c)));Assert.Empty(r["Entries"]);
        using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Roster(c,cts.Token));
    }
    [Theory]
    [InlineData(0,0x666666,"0")][InlineData(24.999,0x666666,"24")][InlineData(25,0x1eff00,"25")][InlineData(49.999,0x1eff00,"49")]
    [InlineData(50,0x0070ff,"50")][InlineData(74.999,0x0070ff,"74")][InlineData(75,0xa335ee,"75")][InlineData(94.999,0xa335ee,"94")]
    [InlineData(95,0xff8000,"95")][InlineData(98.999,0xff8000,"98")][InlineData(99,0xe268a8,"99")][InlineData(99.999,0xe268a8,"99")][InlineData(100,0xe5cc80,"100")]
    public void PaletteUsesRawThresholdsAndNonInflatingDisplay(double percentile,int color,string display)
    {
        var t=typeof(RaidRecapReport).Assembly.GetType("NinjaBotCore.Modules.Wow.RaidRecapParsePalette");Assert.NotNull(t);
        var badge=JObject.FromObject(t.GetMethod("Badge").Invoke(null,new object[]{(double?)percentile}));
        Assert.Equal(color,(int)badge["Color"]);Assert.Equal(display,(string)badge["Display"]);Assert.False(string.IsNullOrEmpty((string)badge["Label"]));
    }
}
