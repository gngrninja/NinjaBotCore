using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;
public class RaidRecapPlayerBoundaryTests
{
    [Fact]
    public async Task PartialGraphQlCannotConcealLateSnapshotDriftFromUtilityIdentityEnrichment()
    {
        var h=RaidRecapPlayerProviderTests.Handler();var reply=h.Reply;
        h.Reply=(i,q)=>
        {
            var env=reply(i,q);
            if(((string)q["query"]).Contains("RaidRecapPlayerRoster")) {env["errors"]=new JArray(new JObject{["message"]="PRIVATE"});env["data"]["reportData"]["report"]["revision"]=2;}
            else env["data"]["reportData"]["report"]["table"]=JObject.Parse("{data:{entries:[{entries:[{name:'Spell',spellsInterrupted:1,spellsCompleted:0,spellChannelsInterrupted:0,details:[{id:7,name:'Same',total:1}]}]}]}}");
            return env;
        };
        var svc=new RaidRecapService(RaidRecapMechanicTransportTests.Client(h),new RaidRecapCache());var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPlayerProviderTests.Report;s.View="analysis";s.PullIndex=0;
        await Assert.ThrowsAsync<RaidRecapSnapshotException>(()=>svc.ApplyAsync(s,"interrupts",null));Assert.Null(s.Analysis);
    }
    [Theory]
    [InlineData("deaths","deaths")][InlineData("interrupts","interrupts")][InlineData("dispels","dispels")]
    public async Task DossierKeepsFightEvidenceHandoffAndSummaryReportsOnlyLoadedActorObservations(string lens,string view)
    {
        var (svc,source,players,s)=RaidRecapPlayerFlowTests.Setup(2);
        var name=RaidRecapPlayerFlowTests.Roster(s.Report,2).Players[0].Name;
        source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,s.Report.Fights[0],lens,It.IsAny<CancellationToken>())).ReturnsAsync(new RaidRecapAnalysis(lens,true,null)
        {Deaths=new[]{new RaidRecapDeath(1,name,1000,"Spell"),new RaidRecapDeath(1,name,2000,"Spell"),new RaidRecapDeath(2,name,3000,"Spell")},
          Utility=new[]{new RaidRecapUtility("Spell",3,0,0,new[]{new RaidRecapParticipant(1,name,2),new RaidRecapParticipant(2,name,1)},true)}});
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");await svc.ApplyAsync(s,"player","1");await svc.ApplyAsync(s,"player_lens",lens);
        Assert.Contains($"#fight=1&type={view}",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
        await svc.ApplyAsync(s,"player_lens","summary");var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        Assert.Contains(lens=="deaths"?"2 observed death events":"2 attributed actions",text);Assert.Contains("Damage — Not loaded",text);
        RaidRecapPlayerReachabilityTests.Check(s);
    }
    [Fact]
    public async Task RefreshFailureClearsPlayerScopeBeforeAwaitAndBackRestoresExactOriginWithoutRefetch()
    {
        var (svc,source,players,s)=RaidRecapPlayerFlowTests.Setup(2);s.View="bosses";s.AttemptPage=3;s.BossPage=2;s.BossIndex=0;
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");await svc.ApplyAsync(s,"player_back",null);
        Assert.Equal("bosses",s.View);Assert.Equal(3,s.AttemptPage);Assert.Equal(2,s.BossPage);
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");await svc.ApplyAsync(s,"player","1");
        var pending=new TaskCompletionSource<RaidRecapReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(x=>x.GetRaidRecapReportAsync(s.Report.Code)).Returns(pending.Task);
        var work=svc.ApplyAsync(s,"refresh",null);Assert.Null(s.PlayerPanel);pending.SetException(new InvalidOperationException());await Assert.ThrowsAsync<InvalidOperationException>(()=>work);Assert.Null(s.PlayerPanel);
    }
    [Fact]
    public async Task TwoSessionsShareTodayScopeButFreshTtlSnapshotAndMetricDoNotReuseParses()
    {
        var now=DateTimeOffset.UnixEpoch;var cache=new RaidRecapCache(()=>now);var h=RaidRecapPlayerProviderTests.Handler();var reply=h.Reply;
        h.Reply=(i,q)=>{var e=reply(i,q);e["data"]["reportData"]["report"]["table"]=JObject.Parse("{data:{entries:[{id:7,name:'Same',total:0}]}}");return e;};
        var svc=new RaidRecapService(RaidRecapMechanicTransportTests.Client(h),cache);var a=RaidRecapPanelTests.Session();a.Report=RaidRecapPlayerProviderTests.Report;var b=RaidRecapPanelTests.Session();b.Report=a.Report;
        await svc.ApplyAsync(a,"damage",null);await svc.ApplyAsync(b,"damage",null);Assert.Equal(4,h.Queries.Count);
        await svc.ApplyAsync(a,"healing",null);Assert.Equal(7,h.Queries.Count);
        now+=TimeSpan.FromMinutes(3);await svc.ApplyAsync(b,"damage",null);Assert.Equal(11,h.Queries.Count);
        b.Report=b.Report with {AsOf=DateTimeOffset.UnixEpoch.AddSeconds(1)};await svc.ApplyAsync(b,"damage",null);Assert.Equal(15,h.Queries.Count);
        Assert.Equal("hps",a.PerformanceParses.Metric);Assert.Equal("dps",b.PerformanceParses.Metric);
    }
    [Fact]
    public async Task ProviderRosterRejectsOversizedIdWithoutPayloadBearingConversionFailure()
    {
        var m=RaidRecapPlayerProviderTests.Metadata();m["masterData"]["actors"][0]["id"]=JToken.Parse("99999999999999999999999999999999999999999999");
        var c=RaidRecapMechanicTransportTests.Client(RaidRecapPlayerProviderTests.Handler(metadata:m));
        var ex=await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>c.GetRaidRecapRosterAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight));Assert.DoesNotContain("999999",ex.ToString());
    }
    [Theory]
    [InlineData("name","{}")][InlineData("type","{}")][InlineData("server","[]")]
    public async Task MalformedDetailFieldsCannotBecomeStringifiedIdentity(string field,string value)
    {
        var m=RaidRecapPlayerProviderTests.Metadata();m["details"]["data"]["playerDetails"]["healers"][0][field]=JToken.Parse(value);
        var c=RaidRecapMechanicTransportTests.Client(RaidRecapPlayerProviderTests.Handler(metadata:m));var r=await c.GetRaidRecapRosterAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight);
        var parses=await c.GetRaidRecapParsesAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight,false,r);
        Assert.DoesNotContain(parses.Entries,p=>p.ActorId==7);Assert.Equal(2,r.Players.Count);
    }
    [Fact]
    public async Task UnavailableOptionalReportIsNotInventedSnapshotDriftAndRetainsRawOutput()
    {
        var h=RaidRecapPlayerProviderTests.Handler();var reply=h.Reply;
        h.Reply=(i,q)=>
        {
            if(((string)q["query"]).Contains("RaidRecapPlayerParses"))return JObject.Parse("{data:{reportData:{report:null}}}");
            var e=reply(i,q);e["data"]["reportData"]["report"]["table"]=JObject.Parse("{data:{entries:[{id:7,name:'Same',total:0}]}}");return e;
        };
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPlayerProviderTests.Report;
        await new RaidRecapService(RaidRecapMechanicTransportTests.Client(h),new RaidRecapCache()).ApplyAsync(s,"damage",null);
        Assert.Single(s.Performance);Assert.Null(s.PerformanceParses);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OptionalParseTimeoutKeepsRawOutput(bool canceled)
    {
        var (svc,source,players,s)=RaidRecapPlayerFlowTests.Setup(2);
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(s.Report,s.Report.Fights[0],false)).ReturnsAsync(JObject.Parse("{data:{entries:[{id:1,name:'Same',total:6000}]}}"));
        players.Setup(x=>x.GetRaidRecapParsesAsync(s.Report,s.Report.Fights[0],false,It.IsAny<RaidRecapRoster>(),It.IsAny<CancellationToken>()))
            .ThrowsAsync(canceled?new OperationCanceledException():new TimeoutException());
        await svc.ApplyAsync(s,"damage",null);Assert.Single(s.Performance);Assert.Contains("Parse unavailable",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
    }
    [Fact]
    public async Task UnknownRankingInvalidationMarkerFailsClosed()
    {
        var ranks=RaidRecapPlayerProviderTests.Ranking();ranks["data"][0]["roles"]["healers"]["characters"][0]["invalidated"]=true;
        var c=RaidRecapMechanicTransportTests.Client(RaidRecapPlayerProviderTests.Handler(rankings:ranks));var roster=await c.GetRaidRecapRosterAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight);
        Assert.DoesNotContain((await c.GetRaidRecapParsesAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight,false,roster)).Entries,p=>p.ActorId==7);
    }
    // Synthetic HTTP specimens exercise parser -> service -> private presentation, never live WCL.
    private static RaidRecapMechanicTransportTests.Handler PresentationHandler(JObject table,JObject rankings=null,bool unavailable=false)
    {
        var h=RaidRecapPlayerProviderTests.Handler(rankings:rankings);var reply=h.Reply;
        h.Reply=(i,q)=>
        {
            var e=reply(i,q);var report=e["data"]["reportData"]["report"];report["table"]=table.DeepClone();
            if(unavailable && ((string)q["query"]).Contains("RaidRecapPlayerParses"))report["rankings"]=JValue.CreateNull();
            return e;
        };
        return h;
    }
    private static string PlayerText(RaidRecapSession s)=>RaidRecapPanelTests.Text(RaidRecapView.Build(s));
    private static string PublicPayload(RaidRecapSession s)=>Newtonsoft.Json.JsonConvert.SerializeObject(RaidRecapView.Build(s,true));
    [Theory]
    [InlineData("damage",20)][InlineData("healing",20)][InlineData("damage",0)][InlineData("healing",0)]
    public async Task IndependentParseSurvivesMissingSourceRowAndFollowsSelectedLocalActor(string lens,double percentile)
    {
        var ranking=RaidRecapPlayerProviderTests.Ranking();ranking["data"][0]["roles"]["healers"]["characters"][0]["rankPercent"]=percentile;
        var h=PresentationHandler(JObject.Parse("{data:{entries:[{id:8,name:'Same',total:6000}]}}"),ranking);
        var svc=new RaidRecapService(RaidRecapMechanicTransportTests.Client(h),new RaidRecapCache());
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPlayerProviderTests.Report;var publicBefore=PublicPayload(s);
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","2");await svc.ApplyAsync(s,"player","7");await svc.ApplyAsync(s,"player_lens",lens);
        var output=lens=="damage"?s.PlayerPanel.Damage:s.PlayerPanel.Healing;
        Assert.Equal(8,Assert.Single(output.Rows).Player.ActorId);
        var parse=Assert.Single(output.Parses.Entries,p=>p.ActorId==7);Assert.Equal(700007,parse.CharacterId);Assert.Equal(percentile,parse.Percentile);
        Assert.Equal(s.Report.SnapshotKey,output.Parses.SnapshotKey);Assert.Equal(2,output.Parses.FightId);
        Assert.Equal(lens=="damage"?"dps":"hps",output.Parses.Metric);
        var calls=h.Queries.Count;
        foreach(var actor in new[]{7,8,7})
        {
            await svc.ApplyAsync(s,"player",actor.ToString());
            var selected=Assert.Single(output.Parses.Entries,p=>p.ActorId==actor);var badge=RaidRecapParsePalette.Badge(selected.Percentile);
            var text=PlayerText(s);var label=badge.Label+" P"+badge.Display;
            Assert.Contains(label,text);Assert.Equal(1,text.Split(label,StringSplitOptions.None).Length-1);
            Assert.Contains($"Overall population: {selected.TotalParses} parses",text);
            Assert.Contains($"ilvl parse: {RaidRecapParsePalette.Badge(selected.ItemLevelPercentile).Display} · bracket ID 3 / ilvl 300",text);
            Assert.Contains("Parses · Today · partition 1 · as of",text);Assert.Contains("finality unknown",text);
            Assert.Contains($"#fight=2&source={actor}",text);Assert.DoesNotContain("source=700007",text);
            Assert.Contains("Accent = overall WCL parse band",text);
            Assert.Equal(new Color(badge.Color),RaidRecapView.Build(s).Components.OfType<ContainerComponent>().Last().AccentColor);
            if(actor==7)
            {
                Assert.Contains("No verified source row for this player. No zero is inferred.",text);
                Assert.DoesNotContain(" · 0 "+(lens=="damage"?"DPS":"HPS"),text);Assert.DoesNotContain("Pink P99",text);
                Assert.DoesNotContain(output.Rows,r=>r.ActorId==7);Assert.Single(output.Rows);
            }
            else
            {
                Assert.Contains("100 "+(lens=="damage"?"DPS":"HPS"),text);
                Assert.DoesNotContain("No verified source row",text);
            }
            Assert.Equal(publicBefore,PublicPayload(s));RaidRecapPlayerReachabilityTests.Check(s);
        }
        Assert.Equal(calls,h.Queries.Count);
    }
    [Theory]
    [InlineData("damage","missing")][InlineData("healing","missing")]
    [InlineData("damage","invalid")][InlineData("healing","invalid")]
    [InlineData("damage","unavailable")][InlineData("healing","unavailable")]
    [InlineData("damage","scope")][InlineData("healing","scope")]
    public async Task MissingSourceWithoutValidSelectedParseStaysUnavailable(string lens,string state)
    {
        var ranking=RaidRecapPlayerProviderTests.Ranking();var characters=(JArray)ranking["data"][0]["roles"]["healers"]["characters"];
        if(state=="missing")characters.Clear();
        if(state=="invalid")characters[0]["rankPercent"]=JValue.CreateNull();
        if(state=="scope")ranking["data"][0]["fightID"]=3;
        var h=PresentationHandler(JObject.Parse("{data:{entries:[{id:8,name:'Same',total:6000}]}}"),ranking,state=="unavailable");
        var svc=new RaidRecapService(RaidRecapMechanicTransportTests.Client(h),new RaidRecapCache());
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPlayerProviderTests.Report;var publicBefore=PublicPayload(s);
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","2");await svc.ApplyAsync(s,"player","7");await svc.ApplyAsync(s,"player_lens",lens);
        var output=lens=="damage"?s.PlayerPanel.Damage:s.PlayerPanel.Healing;
        Assert.Equal(8,Assert.Single(output.Rows).Player.ActorId);Assert.DoesNotContain(output.Parses?.Entries??Array.Empty<RaidRecapParse>(),p=>p.ActorId==7);
        var text=PlayerText(s);Assert.Contains("No verified source row",text);Assert.Contains("No zero is inferred",text);
        Assert.DoesNotContain("Overall population:",text);Assert.DoesNotContain("ilvl parse:",text);Assert.DoesNotContain("Pink P99",text);
        Assert.DoesNotContain("Accent = overall WCL parse band",text);Assert.DoesNotContain(" · 0 "+(lens=="damage"?"DPS":"HPS"),text);
        Assert.Equal(new Color(0x7F8C8D),RaidRecapView.Build(s).Components.OfType<ContainerComponent>().Last().AccentColor);
        Assert.Equal(publicBefore,PublicPayload(s));RaidRecapPlayerReachabilityTests.Check(s);
    }
    [Theory]
    [InlineData("interrupts","unknown")][InlineData("dispels","unknown")]
    [InlineData("interrupts","mixed")][InlineData("dispels","mixed")]
    [InlineData("interrupts","mixed-zero")][InlineData("dispels","mixed-zero")]
    [InlineData("interrupts","empty-attribution")][InlineData("dispels","empty-attribution")]
    [InlineData("interrupts","empty-result")][InlineData("dispels","empty-result")]
    [InlineData("interrupts","other-actor")][InlineData("dispels","other-actor")]
    [InlineData("interrupts","zero")][InlineData("dispels","zero")]
    [InlineData("interrupts","complete")][InlineData("dispels","complete")]
    [InlineData("interrupts","partial-known")][InlineData("dispels","partial-known")]
    public async Task UtilitySummaryPreservesUnknownAttributionAndLabelsRetainedLowerBoundsWithoutIo(string lens,string state)
    {
        JObject Spell(string name,int actions,string details)=>new(){["name"]=name,["spellsInterrupted"]=actions,["spellsCompleted"]=0,["spellChannelsInterrupted"]=0,["details"]=JArray.Parse(details)};
        var unknown=Spell("Unknown count",1,"[{id:7,name:'Same',total:null}]");
        var known=Spell("Known count",2,"[{id:7,name:'Same',total:2}]");
        var zero=Spell("Observed zero",0,"[{id:7,name:'Same',total:0}]");
        var spells=state switch
        {
            "unknown"=>new JArray(unknown),"mixed"=>new JArray(known,unknown),"mixed-zero"=>new JArray(zero,unknown),
            "empty-attribution"=>new JArray(Spell("No attribution",1,"[]")),"empty-result"=>new JArray(),
            "other-actor"=>new JArray(Spell("Other player",11,"[{id:8,name:'Same',total:11}]")),"zero"=>new JArray(zero),
            "complete"=>new JArray(known,Spell("More known",14,"[{id:7,name:'Same',total:3},{id:8,name:'Same',total:11}]")),
            "partial-known"=>new JArray(Spell("Partial attribution",3,"[{id:7,name:'Same',total:2}]")),
            _=>throw new InvalidOperationException()
        };
        var table=new JObject{["data"]=new JObject{["entries"]=new JArray(new JObject{["entries"]=spells})}};
        var h=PresentationHandler(table);var svc=new RaidRecapService(RaidRecapMechanicTransportTests.Client(h),new RaidRecapCache());
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPlayerProviderTests.Report;var publicBefore=PublicPayload(s);
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","2");await svc.ApplyAsync(s,"player","7");await svc.ApplyAsync(s,"player_lens",lens);
        var analysis=s.PlayerPanel.Observations[lens];var participants=analysis.Utility.SelectMany(u=>u.Participants).Where(p=>p.ActorId==7).ToArray();
        Assert.All(participants,p=>Assert.True(p.VerifiedPlayer));
        if(state is "unknown" or "mixed" or "mixed-zero")
        {
            Assert.Contains(participants,p=>p.Count==null);Assert.Contains("Unknown attributed actions",PlayerText(s));Assert.False(analysis.Complete);
        }
        if(state=="zero") {Assert.Equal(0,Assert.Single(participants).Count);Assert.True(analysis.Complete);Assert.Contains("0 attributed actions",PlayerText(s));}
        if(state=="complete")Assert.True(analysis.Complete);
        if(state=="partial-known")Assert.False(analysis.Complete);
        var calls=h.Queries.Count;
        await svc.ApplyAsync(s,"player_lens","summary");Assert.Equal(calls,h.Queries.Count);Assert.Same(analysis,s.PlayerPanel.Observations[lens]);
        var row=Assert.Single(RaidRecapPlayerPresentation.Rows(s),r=>r.StartsWith(RaidRecapPlayerPresentation.Label(lens)+" · "));
        if(state is "unknown" or "empty-attribution" or "empty-result" or "other-actor")
        {
            Assert.Contains("Unknown attributed actions",row);Assert.DoesNotContain("0 attributed actions",row);
        }
        else if(state is "mixed" or "mixed-zero" or "partial-known")
        {
            Assert.Contains((state=="mixed-zero"?"0":"2")+" attributed actions retained",row);
            Assert.Contains("lower bound",row);Assert.Contains("total unknown",row);
        }
        else
        {
            Assert.Contains((state=="zero"?"0":"5")+" attributed actions (not obligations)",row);
            Assert.DoesNotContain("lower bound",row);Assert.DoesNotContain("Unknown",row);
        }
        Assert.Contains("not obligations",row);Assert.DoesNotContain("11 attributed actions",row);
        Assert.Contains(row,PlayerText(s));Assert.Equal(publicBefore,PublicPayload(s));RaidRecapPlayerReachabilityTests.Check(s);
    }
    [Fact]
    public async Task DuplicateCanonicalIdentityAndMultipleSpecsRemainUnranked()
    {
        var m=RaidRecapPlayerProviderTests.Metadata();m["details"]["data"]["playerDetails"]["healers"][0]["specs"]=new JArray(new JObject{["spec"]="Holy"},new JObject{["spec"]="Discipline"});
        var c=RaidRecapMechanicTransportTests.Client(RaidRecapPlayerProviderTests.Handler(metadata:m));var roster=await c.GetRaidRecapRosterAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight);
        Assert.DoesNotContain((await c.GetRaidRecapParsesAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight,false,roster)).Entries,p=>p.ActorId==7);
        roster=roster with {Players=roster.Players.Concat(new[]{roster.Players.Single(p=>p.ActorId==8) with {ActorId=99}}).ToArray()};
        Assert.Empty((await c.GetRaidRecapParsesAsync(RaidRecapPlayerProviderTests.Report,RaidRecapPlayerProviderTests.Fight,false,roster)).Entries);
    }
}
