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
public class RaidRecapPlayerReachabilityTests
{
    internal static void Check(RaidRecapSession s)
    {
        var c=RaidRecapView.Build(s);var parts=RaidRecapPanelTests.Flatten(c.Components).ToArray();
        Assert.InRange(parts.Length,1,40);Assert.InRange(RaidRecapPanelTests.Text(c).Length,1,3800);
        Assert.InRange(parts.OfType<ActionRowComponent>().Count(),1,5);
        Assert.All(parts.OfType<ActionRowComponent>(),r=>Assert.InRange(r.Components.Count,1,5));
        var ids=parts.Select(p=>p switch{ButtonComponent b when b.Style!=ButtonStyle.Link=>b.CustomId,SelectMenuComponent m=>m.CustomId,_=>null}).Where(i=>i!=null).ToArray();
        Assert.Equal(ids.Length,ids.Distinct().Count());Assert.All(ids,id=>Assert.InRange(id.Length,1,100));
        Assert.All(parts.OfType<SectionComponent>(),sec=>{Assert.InRange(sec.Components.Count,1,3);Assert.NotNull(sec.Accessory);});
        Assert.All(parts.OfType<ContainerComponent>(),c=>Assert.DoesNotContain(c.Components,p=>p is ContainerComponent));
        var serialized=JsonConvert.SerializeObject(c);Assert.DoesNotContain("@everyone",serialized);
    }
    [Fact]
    public async Task MaximumOutputPagesKeepEveryWholeEscapedLinkedRowWithinBudgetWithoutIo()
    {
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report() with {Title=new string('*',500)};s.View="healing";s.Notice=new string('*',500);
        var raw=Enumerable.Range(1,1000).Select(i=>new RaidRecapStanding("Source"+i+new string('*',100),1,1){ActorId=i,Player=i<=100?new RaidRecapPlayer(i,"Source"+i+new string('*',100),"DeathKnight","Unholy","dps","realm","US",true):null,Parse=i<=100?new RaidRecapParse(i,i+10000,99.999,100,100,1,300):null}).ToArray();
        s.Performance=raw;s.PerformanceParses=new(s.Report.SnapshotKey,1,"hps","Parses","Today",1,DateTimeOffset.UnixEpoch,raw.Where(r=>r.Parse!=null).Select(r=>r.Parse).ToArray());
        var svc=new RaidRecapService(Mock.Of<IRaidRecapSource>(MockBehavior.Strict),new RaidRecapCache());var seen=new System.Collections.Generic.HashSet<int>();
        for(var page=0;page<1000;page++)
        {
            Check(s);var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
            foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^(\d+)\. "))seen.Add(int.Parse(m.Groups[1].Value));
            var next=Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<ButtonComponent>(),b=>b.Label=="Next players");
            if(next.IsDisabled)break;await svc.ApplyAsync(s,"ranks_next",null);
        }
        Assert.Equal(Enumerable.Range(1,1000),seen.OrderBy(i=>i));
    }
    [Theory]
    [InlineData(RaidRecapMechanics.Junk)][InlineData(RaidRecapMechanics.Spin)]
    public void MechanicsLinkEveryAffectedPlayerWithinBudget(string metric)
    {
        var s=RaidRecapMechanicViewTests.Session();s.AnalysisMetric=metric;s.Notice=new string('*',500);
        s.Report=s.Report with {Title=new string('*',500)};
        s.Analysis=new(metric,true,null){Mechanic=new(s.Report.SnapshotKey,2,5000,65000,"complete",Enumerable.Range(1,8).Select(i=>new RaidRecapMechanicEvent(i,new string('*',200),i*1000,0,42)).ToArray())};
        var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        foreach(var i in Enumerable.Range(1,8))Assert.Contains($"#fight=2&source={i})",text);
        Check(s);
    }
    [Theory]
    [InlineData("interrupts")][InlineData("dispels")]
    public async Task UtilityLinksOnlyNumericallyValidatedPlayerRosterParticipants(string metric)
    {
        var (svc,source,players,s)=RaidRecapPlayerFlowTests.Setup(2);s.View="analysis";s.PullIndex=0;
        var name=RaidRecapPlayerFlowTests.Roster(s.Report,2).Players[0].Name;
        source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,s.Report.Fights[0],metric,It.IsAny<CancellationToken>())).ReturnsAsync(new RaidRecapAnalysis(metric,true,null){Utility=new[]{new RaidRecapUtility("Spell",3,0,0,new[]{new RaidRecapParticipant(1,name,1),new RaidRecapParticipant(999,name,2)},true)}});
        await svc.ApplyAsync(s,metric,null);var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        Assert.Contains("#fight=1&source=1)",text);Assert.DoesNotContain("&source=999",text);Assert.Contains("Partial",text);
        await svc.ApplyAsync(s,"players",null);await svc.ApplyAsync(s,"player","1");await svc.ApplyAsync(s,"player_lens",metric);
        Assert.Contains(RaidRecapPanelTests.Text(RaidRecapView.Build(s)).Split('\n'),line=>line.TrimEnd('\r').EndsWith("** · 1",StringComparison.Ordinal));Check(s);
    }
    [Theory]
    [InlineData("deaths",500)][InlineData("interrupts",1500)][InlineData("dispels",1500)]
    [InlineData(RaidRecapMechanics.Junk,500)][InlineData(RaidRecapMechanics.Spin,500)]
    public async Task EveryRetainedObservationIsReachableAtCollectorCaps(string metric,int expected)
    {
        var s=RaidRecapMechanicViewTests.Session();s.AnalysisMetric=metric;s.Notice=new string('*',500);
        s.Analysis=new(metric,true,null){
            Deaths=Enumerable.Range(1,500).Select(i=>new RaidRecapDeath(i,"Row"+i.ToString("0000"),i,"Spell")).ToArray(),
            Utility=Enumerable.Range(1,500).Select(i=>new RaidRecapUtility("Row"+i.ToString("0000"),2,0,0,new[]{new RaidRecapParticipant(1,"Row"+(500+i*2-1).ToString("0000"),1){VerifiedPlayer=true},new RaidRecapParticipant(2,"Row"+(500+i*2).ToString("0000"),1){VerifiedPlayer=true}},true)).ToArray(),
            Mechanic=new(s.Report.SnapshotKey,2,5000,65000,"complete",Enumerable.Range(1,500).Select(i=>new RaidRecapMechanicEvent(i,"Row"+i.ToString("0000"),i,0,0)).ToArray())};
        var service=new RaidRecapService(Mock.Of<IRaidRecapSource>(MockBehavior.Strict),new RaidRecapCache());var seen=new System.Collections.Generic.HashSet<int>();
        for(var page=0;page<200;page++)
        {
            Check(s);var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
            foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,@"Row(\d{4})"))seen.Add(int.Parse(m.Groups[1].Value));
            var next=Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<ButtonComponent>(),b=>b.Label=="Next rows");
            if(next.IsDisabled)break;await service.ApplyAsync(s,"analysis_next",null);
        }
        Assert.Equal(expected,seen.Count);
    }
    [Fact]
    public async Task AllFirstLossTiesRemainLinkedAndReachableWithoutProviderRefetch()
    {
        var s=RaidRecapPanelTests.Session();s.Report=s.ReportOrFallback();
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,It.IsAny<RaidRecapFight>(),"deaths",It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RaidRecapAnalysis("deaths",true,null){Deaths=Enumerable.Range(1,100).Select(i=>new RaidRecapDeath(i,"Tie"+i,1000,"Spell")).ToArray()});
        var svc=new RaidRecapService(source.Object,new RaidRecapCache());await svc.ApplyAsync(s,"bosses",null);await svc.ApplyAsync(s,"compare",null);await svc.ApplyAsync(s,"compare_deaths",null);
        var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));Assert.Contains("&source=1)",text);Assert.Contains("(100 together)",text);Check(s);
        await svc.ApplyAsync(s,"compare_losses",null);var seen=new System.Collections.Generic.HashSet<string>();
        for(var page=0;page<100;page++)
        {
            Check(s);text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
            foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,@"#fight=\d+&source=\d+"))seen.Add(m.Value);
            var next=Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<ButtonComponent>(),b=>b.Label=="Next ties");
            if(next.IsDisabled)break;await svc.ApplyAsync(s,"compare_loss_next",null);
        }
        Assert.Equal(200,seen.Count);source.Verify(x=>x.GetRaidRecapAnalysisAsync(s.Report,It.IsAny<RaidRecapFight>(),"deaths",It.IsAny<CancellationToken>()),Times.Exactly(2));
    }
}
internal static class RaidRecapPlayerTestReport
{
    public static RaidRecapReport ReportOrFallback(this RaidRecapSession s)=>RaidRecapPanelTests.Report() with {Fights=new[]{RaidRecapReviewTests.Pull(1),RaidRecapReviewTests.Pull(2)}};
}
