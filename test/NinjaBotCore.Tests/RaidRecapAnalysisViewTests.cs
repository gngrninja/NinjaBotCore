using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;

namespace NinjaBotCore.Tests;
public class RaidRecapAnalysisViewTests
{
    private static RaidRecapSession Session()
    {
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapAnalysisTransportTests.Report;return s;
    }
    private static void Set(RaidRecapSession s,string name,object value)
    { var p=typeof(RaidRecapSession).GetProperty(name);Assert.NotNull(p);p.SetValue(s,value); }
    private static string Text(RaidRecapSession s)=>RaidRecapPanelTests.Text(RaidRecapView.Build(s));
    private static RaidRecapService Service(RaidRecapAnalysisTransportTests.Handler h)=>new(RaidRecapAnalysisTransportTests.Client(h),new RaidRecapCache());

    [Fact]
    public async Task AnalysisLoadsCompletedWipeLazilyAndCachesOnlyTheSelectedMetric()
    {
        var h=new RaidRecapAnalysisTransportTests.Handler();var service=Service(h);var s=Session();
        await service.ApplyAsync(s,"bosses",null);await service.ApplyAsync(s,"overview",null);Assert.Empty(h.Queries);
        await service.ApplyAsync(s,"analysis",null);
        Assert.Contains("No player deaths",Text(s));Assert.Contains("Wipe #2",Text(s));Assert.Single(h.Queries);
        await service.ApplyAsync(s,"analysis",null);Assert.Single(h.Queries);
        h.Reply=_=>RaidRecapAnalysisTransportTests.Envelope(new JObject{["table"]=JObject.Parse("{\"data\":{\"entries\":[]}}")});
        await service.ApplyAsync(s,"incoming",null);Assert.Equal(2,h.Queries.Count);Assert.Contains("WCL damage-taken table totals",Text(s));
        await service.ApplyAsync(s,"deaths",null);Assert.Equal(2,h.Queries.Count);
        await service.ApplyAsync(s,"bosses",null);Assert.Equal(2,h.Queries.Count);
    }
    [Fact]
    public async Task AnalysisDefaultsToCurrentBossCompletedPullAndMakesItsPickerPageReachable()
    {
        var h=new RaidRecapAnalysisTransportTests.Handler{Reply=_=>RaidRecapAnalysisTransportTests.Envelope(new JObject{["table"]=JObject.Parse("{\"data\":{\"entries\":[]}}")})};
        var s=Session();s.Report=s.Report with {Fights=Enumerable.Range(1,26).Select(i=>RaidRecapAnalysisTransportTests.Fight with {Id=i,EncounterId=i,StartMs=i*100000,EndMs=i*100000+60000}).ToArray()};
        s.BossIndex=25;s.View="bosses";Set(s,"AnalysisMetric","incoming");
        var service=Service(h);await service.ApplyAsync(s,"analysis",null);
        Assert.Equal(26,(int)Assert.Single(h.Queries)["variables"]["fights"][0]);
        var picker=Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>());
        Assert.Contains(picker.Options,o=>o.IsDefault==true&&o.Label.Contains("Wipe #26")&&o.Label.Contains("Heroic"));
        await service.ApplyAsync(s,"pulls_prev",null);
        Assert.Equal(25,Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>()).Options.Count);
        await service.ApplyAsync(s,"pull","0");Assert.Contains("Wipe #1",Text(s));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(s,"pull","25"));
    }
    [Fact]
    public async Task SnapshotChangeDoesNotReuseCachedAnalysis()
    {
        var h=new RaidRecapAnalysisTransportTests.Handler();var service=Service(h);var s=Session();
        await service.ApplyAsync(s,"analysis",null);
        s.Report=s.Report with {Revision=9};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(s,"analysis",null));
        Assert.DoesNotContain("No player deaths",Text(s));Assert.Contains("unavailable",Text(s));Assert.Equal(2,h.Queries.Count);
    }
    [Fact]
    public void DeathTimelineKeepsFirstLossTiesAndRepeatedEventsSeparateFromPlayers()
    {
        var s=Session();s.View="analysis";Set(s,"PullIndex",0);
        Set(s,"Analysis",new RaidRecapAnalysis("deaths",true,null){Deaths=new[]{new RaidRecapDeath(1,"Alpha",0,"Melee"),new RaidRecapDeath(2,"Beta",0,"Unknown killing ability"),new RaidRecapDeath(1,"Alpha",2000,"Fire")}});
        var text=Text(s);Assert.Contains("3 observed death events · 2 distinct players",text);Assert.Contains("First loss: **00:00:00.000** · 2 simultaneous",text);
        Assert.Contains("death event 2 for this player",text);Assert.Contains("Unknown killing ability",text);Assert.Contains("not a cause or blame verdict",text);
        Assert.Contains("#fight=2&type=deaths",text);
        Assert.DoesNotContain("Alpha",RaidRecapPanelTests.Text(RaidRecapView.Build(s,true)));
    }
    [Fact]
    public void PartialEmptyDeathsNeverClaimsNoDeathsOrDefiniteFirstLoss()
    {
        var s=Session();s.View="analysis";Set(s,"PullIndex",0);Set(s,"Analysis",new RaidRecapAnalysis("deaths",false,"Partial: cursor unavailable"));
        Assert.Contains("Partial",Text(s));Assert.DoesNotContain("No player deaths",Text(s));Assert.DoesNotContain("First loss:",Text(s));Assert.Contains("observed",Text(s));
    }
    [Theory]
    [InlineData("deaths")] [InlineData("incoming")] [InlineData("interrupts")] [InlineData("dispels")]
    public async Task EveryAnalysisSubviewIsBoundedPrivateAndAllRowsAreReachable(string metric)
    {
        var s=Session();s.View="analysis";Set(s,"PullIndex",0);Set(s,"AnalysisMetric",metric);
        var hostile=string.Concat(Enumerable.Repeat("😀**@everyone[]\\\n",400));
        var analysis=new RaidRecapAnalysis(metric,true,null){
            Deaths=Enumerable.Range(1,17).Select(i=>new RaidRecapDeath(i,hostile,i*1000,hostile)).ToArray(),
            Incoming=Enumerable.Range(1,17).Select(i=>new RaidRecapIncoming(hostile,hostile,i)).ToArray(),
            Utility=new[]{new RaidRecapUtility(hostile,17,4,1,Enumerable.Range(1,17).Select(i=>new RaidRecapParticipant(i,hostile,1)).ToArray(),true)}};
        Set(s,"Analysis",analysis);
        for(var page=0;page<3;page++)
        {
            Set(s,"AnalysisPage",page);var payload=RaidRecapView.Build(s);var all=RaidRecapPanelTests.Flatten(payload.Components).ToArray();
            Assert.InRange(all.Length,1,40);Assert.InRange(RaidRecapPanelTests.Text(payload).Length,1,4000);
            Assert.DoesNotContain("@everyone",RaidRecapPanelTests.Text(payload));
            Assert.Equal(5,all.OfType<ActionRowComponent>().First().Components.Count);
            Assert.Equal(5,all.OfType<ActionRowComponent>().Count());
            Assert.All(all.OfType<ActionRowComponent>(),r=>Assert.InRange(r.Components.Count,1,5));
            Assert.All(all.OfType<SelectMenuComponent>(),m=>{Assert.InRange(m.Options.Count,1,25);Assert.All(m.Options,o=>Assert.InRange(o.Label.Length,1,100));});
            Assert.All(all.OfType<ButtonComponent>().Where(b=>b.Style!=ButtonStyle.Link),b=>Assert.InRange(b.CustomId.Length,1,100));
            Assert.Contains($"page {page+1}/3",RaidRecapPanelTests.Text(payload));
            Assert.Contains(all.OfType<ButtonComponent>(),b=>b.Label=="Next rows"&&b.IsDisabled==(page==2));
            var shared=RaidRecapView.Build(s,true);Assert.DoesNotContain("😀",RaidRecapPanelTests.Text(shared));
        }
        await Task.CompletedTask;
    }
    [Fact]
    public void ExportRepresentativePrivateAnalysisPayloads()
    {
        var directory=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");
        foreach(var metric in new[]{"deaths","incoming","interrupts","dispels"})
        {
            var s=Session();s.View="analysis";Set(s,"PullIndex",0);Set(s,"AnalysisMetric",metric);
            Set(s,"Analysis",new RaidRecapAnalysis(metric,true,null){
                Deaths=new[]{new RaidRecapDeath(1,"Alpha",2480,"Melee"),new RaidRecapDeath(2,"Beta",2480,"Unknown killing ability"),new RaidRecapDeath(1,"Alpha",52000,"Fire")},
                Incoming=new[]{new RaidRecapIncoming("Blast","Boss source",120000),new RaidRecapIncoming("Melee","Composite / unspecified source",90000)},
                Utility=new[]{new RaidRecapUtility("Boss cast",3,2,0,new[]{new RaidRecapParticipant(1,"Alpha",2),new RaidRecapParticipant(2,"Beta",1)},true)}});
            var panel=RaidRecapView.Build(s);Assert.Contains("**Analysis**",RaidRecapPanelTests.Text(panel));
            if(!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"synthetic-analysis-"+metric+".md"),"# Synthetic fixture — not live log data\n\n"+RaidRecapPanelTests.Text(panel));
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"synthetic-analysis-"+metric+".json"),Newtonsoft.Json.JsonConvert.SerializeObject(panel,Newtonsoft.Json.Formatting.Indented));
            }
        }
    }

    [Fact]
    public void UtilityCopyShowsUnassignedCountsWithoutGradingOrCoverage()
    {
        var s=Session();s.View="analysis";Set(s,"PullIndex",0);Set(s,"AnalysisMetric","interrupts");
        Set(s,"Analysis",new RaidRecapAnalysis("interrupts",false,"Partial attribution") {Utility=new[]{new RaidRecapUtility("Dangerous spell",3,9,2,Array.Empty<RaidRecapParticipant>(),false)}});
        var text=Text(s);Assert.Contains("3 observed interrupts",text);Assert.Contains("9 WCL-reported completed casts",text);Assert.Contains("2 channel interrupts",text);
        Assert.Contains("Attribution unavailable / unassigned",text);Assert.Contains("not missed assignments",text);Assert.DoesNotContain("coverage",text);Assert.Contains("#fight=2&type=interrupts",text);
    }
}
