using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;
public class RaidRecapPlayerFlowTests
{
    public static RaidRecapRoster Roster(RaidRecapReport r,int count=100)=>new(r.SnapshotKey,r.Fights[0].Id,Enumerable.Range(1,count).Select(i=>new RaidRecapPlayer(i,"Same ](@everyone) 😀", "Hunter","Marksmanship","dps","Realm","US",true)).ToArray(),true);
    public static (RaidRecapService Service,Mock<IRaidRecapSource> Source,Mock<IRaidRecapPlayerSource> Players,RaidRecapSession Session) Setup(int count=100)
    {
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);var players=source.As<IRaidRecapPlayerSource>();
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report();
        players.Setup(x=>x.GetRaidRecapRosterAsync(s.Report,It.IsAny<RaidRecapFight>(),It.IsAny<CancellationToken>())).ReturnsAsync(Roster(s.Report,count));
        return(new RaidRecapService(source.Object,new RaidRecapCache()),source,players,s);
    }
    public static SelectMenuComponent Menu(RaidRecapSession s,string action)=>Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>(),m=>m.CustomId.EndsWith("~"+action));
    [Fact]
    public async Task FullRosterCatalogHasStableNumericSelectionsWithNoInitialDefaultAndIndependentPages()
    {
        var (svc,source,players,s)=Setup();await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");
        Assert.DoesNotContain(Menu(s,"player").Options,o=>o.IsDefault==true);
        await svc.ApplyAsync(s,"player","1");var all=new System.Collections.Generic.List<int>();
        for(var p=0;p<4;p++)
        {
            var options=Menu(s,"player").Options;all.AddRange(options.Select(o=>int.Parse(o.Value)));
            Assert.All(options,o=>Assert.Contains("#"+o.Value,o.Label));
            if(p>0)Assert.DoesNotContain(options,o=>o.IsDefault==true);
            Assert.Contains("#1]",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
            if(p<3)await svc.ApplyAsync(s,"player_options_next",null);
        }
        Assert.Equal(Enumerable.Range(1,100),all);
        await Assert.ThrowsAsync<ArgumentException>(()=>svc.ApplyAsync(s,"player","1"));
        await svc.ApplyAsync(s,"player_lens","summary");
        players.Verify(x=>x.GetRaidRecapRosterAsync(s.Report,It.IsAny<RaidRecapFight>(),It.IsAny<CancellationToken>()),Times.Once);source.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(25)][InlineData(26)]
    public async Task CatalogBoundaryPagesIncludeEveryValidatedRosterActorWithoutSelectingOne(int count)
    {
        var (svc,source,players,s)=Setup(count);await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");
        var seen=new System.Collections.Generic.HashSet<string>();
        for(var page=0;page<Math.Max(1,(count+24)/25);page++)
        {
            var menu=RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>().SingleOrDefault(m=>m.CustomId.EndsWith("~player"));
            if(menu!=null){Assert.DoesNotContain(menu.Options,o=>o.IsDefault==true);foreach(var o in menu.Options)seen.Add(o.Value);}
            Assert.Null(s.PlayerPanel.ActorId);RaidRecapPlayerReachabilityTests.Check(s);
            if(count>0)await svc.ApplyAsync(s,"player_options_next",null);
        }
        Assert.Equal(count,seen.Count);players.Verify(x=>x.GetRaidRecapRosterAsync(s.Report,It.IsAny<RaidRecapFight>(),It.IsAny<CancellationToken>()),Times.Once);source.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task OneLensIsSingleFlightAcrossPlayersAndSessionsAndBackIsLocal()
    {
        var (svc,source,players,a)=Setup();var b=RaidRecapPanelTests.Session();b.Report=a.Report;
        var pending=new TaskCompletionSource<RaidRecapAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(x=>x.GetRaidRecapAnalysisAsync(a.Report,a.Report.Fights[0],"deaths",It.IsAny<CancellationToken>())).Returns(pending.Task);
        foreach(var s in new[]{a,b}){await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");await svc.ApplyAsync(s,"player","1");}
        var first=svc.ApplyAsync(a,"player_lens","deaths");var second=svc.ApplyAsync(b,"player_lens","deaths");
        pending.SetResult(new RaidRecapAnalysis("deaths",true,null){Deaths=new[]{new RaidRecapDeath(1,"Same",1000,"Spell"),new RaidRecapDeath(2,"Same",2000,"Spell")}});
        await Task.WhenAll(first,second);await svc.ApplyAsync(a,"player","2");await svc.ApplyAsync(a,"player_lens","summary");await svc.ApplyAsync(a,"player_back",null);
        Assert.Equal("overview",a.View);Assert.Contains("**Overview**",RaidRecapPanelTests.Text(RaidRecapView.Build(a)));
        source.Verify(x=>x.GetRaidRecapAnalysisAsync(a.Report,a.Report.Fights[0],"deaths",It.IsAny<CancellationToken>()),Times.Once);
        players.Verify(x=>x.GetRaidRecapRosterAsync(a.Report,a.Report.Fights[0],It.IsAny<CancellationToken>()),Times.Once);
    }
    [Fact]
    public async Task FailedLensEntryEvictedForExplicitRetryAndPullChangeClearsBeforeAwait()
    {
        var (svc,source,players,s)=Setup();await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");await svc.ApplyAsync(s,"player","1");
        source.SetupSequence(x=>x.GetRaidRecapAnalysisAsync(s.Report,s.Report.Fights[0],"deaths",It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException()).ReturnsAsync(new RaidRecapAnalysis("deaths",true,null){Deaths=new[]{new RaidRecapDeath(1,"PRIVATE OBSERVATION",1000,"Spell")}});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>svc.ApplyAsync(s,"player_lens","deaths"));
        Assert.DoesNotContain("PRIVATE OBSERVATION",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
        await svc.ApplyAsync(s,"player_lens","deaths");Assert.Contains("00:00:01",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
        await svc.ApplyAsync(s,"player_change",null);Assert.Contains("Choose completed pull",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
        Assert.DoesNotContain("00:00:01",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
    }
    [Fact]
    public async Task ParseFailurePreservesRawOutputAndFailedEnrichmentRetriesWithoutRefetchingTable()
    {
        var (svc,source,players,s)=Setup(2);
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(s.Report,s.Report.Fights[0],false)).ReturnsAsync(JObject.Parse("{data:{entries:[{id:1,name:'Same ](@everyone) 😀',total:6000}]}}"));
        players.SetupSequence(x=>x.GetRaidRecapParsesAsync(s.Report,s.Report.Fights[0],false,It.IsAny<RaidRecapRoster>(),It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private provider details"))
            .ReturnsAsync(new RaidRecapParses(s.Report.SnapshotKey,1,"dps","Parses","Today",1,DateTimeOffset.UnixEpoch,new[]{new RaidRecapParse(1,999,99,50,100,10,320)}));
        await svc.ApplyAsync(s,"damage",null);var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        Assert.Contains("100",text);Assert.Contains("Parse unavailable",text);Assert.DoesNotContain("private provider details",text);
        await svc.ApplyAsync(s,"damage",null);text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));Assert.Contains("Pink P99",text);Assert.Contains("Today",text);
        Assert.Contains("#fight=1&source=1",text);source.Verify(x=>x.GetRaidRecapScopedTableAsync(s.Report,s.Report.Fights[0],false),Times.Once);
        players.Verify(x=>x.GetRaidRecapParsesAsync(s.Report,s.Report.Fights[0],false,It.IsAny<RaidRecapRoster>(),It.IsAny<CancellationToken>()),Times.Exactly(2));
    }
    [Fact]
    public async Task OptionalEnrichmentNeverHidesSnapshotDrift()
    {
        var (svc,source,players,s)=Setup(2);
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(s.Report,s.Report.Fights[0],false)).ReturnsAsync(JObject.Parse("{data:{entries:[{id:1,name:'Same',total:6000}]}}"));
        players.Setup(x=>x.GetRaidRecapParsesAsync(s.Report,s.Report.Fights[0],false,It.IsAny<RaidRecapRoster>(),It.IsAny<CancellationToken>())).ThrowsAsync(new RaidRecapSnapshotException());
        await Assert.ThrowsAsync<RaidRecapSnapshotException>(()=>svc.ApplyAsync(s,"damage",null));Assert.Null(s.Performance);
    }
    [Fact]
    public async Task PublicPayloadIsByteIdenticalAfterPrivatePlayerEnrichment()
    {
        var (svc,source,players,s)=Setup();var before=JsonConvert.SerializeObject(RaidRecapView.Build(s,true));
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player_pull","1");await svc.ApplyAsync(s,"player","1");
        s.Notice="PRIVATE ONLY";var after=JsonConvert.SerializeObject(RaidRecapView.Build(s,true));Assert.Equal(before,after);
        var tree=RaidRecapView.Build(s);var all=RaidRecapPanelTests.Flatten(tree.Components).ToArray();Assert.InRange(all.Length,1,40);Assert.InRange(RaidRecapPanelTests.Text(tree).Length,1,3800);
        Assert.InRange(all.OfType<ActionRowComponent>().Count(),1,5);Assert.Contains(all.OfType<SectionComponent>(),c=>c.Accessory is ButtonComponent b && b.Label=="Change pull");
    }
}
