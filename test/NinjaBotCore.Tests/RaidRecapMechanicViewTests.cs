using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;
using Xunit;
using static NinjaBotCore.Tests.RaidRecapMechanicTransportTests;

namespace NinjaBotCore.Tests;
public class RaidRecapMechanicViewTests
{
    public static RaidRecapSession Session()
    { var s=RaidRecapPanelTests.Session();s.Report=Report;s.View="analysis";s.PullIndex=0;return s; }
    private static string Text(RaidRecapSession s)=>RaidRecapPanelTests.Text(RaidRecapView.Build(s));
    private static IMessageComponent[] Parts(RaidRecapSession s)=>RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).ToArray();
    private static RaidRecapService Service(Handler h)=>new(Client(h),new RaidRecapCache());
    private static Handler Wire(int rows=1,string name="PRIVATE synthetic player")=>new(){Reply=(i,q)=>
    {
        var query=(string)q["query"];
        if(query.Contains("MechanicsMetadata"))
        {
            var metadata=Metadata();metadata["masterData"]["actors"][0]["name"]=name;return Envelope(metadata);
        }
        if(query.Contains("MechanicsEvents"))return Envelope(Events(new JArray(Enumerable.Range(0,rows).Select(n=>Row(6000+n,metric:(string)q["variables"]["eventType"]=="Debuffs"?Spin:Junk)))));
        if(query.Contains("dataType: Deaths"))return Envelope(RaidRecapAnalysisTransportTests.Deaths());
        return Envelope(new JObject{["table"]=JObject.Parse("{\"data\":{\"entries\":[]}}")});
    }};

    [Fact]
    public async Task FifthAnalysisSubviewLoadsOnlyChosenRuleAndOtherViewsNeverFetchMechanics()
    {
        var h=Wire();var s=Session();var service=Service(h);
        await service.ApplyAsync(s,"overview",null);await service.ApplyAsync(s,"bosses",null);Assert.Empty(h.Queries);
        await service.ApplyAsync(s,"analysis",null);Assert.Single(h.Queries);Assert.DoesNotContain(h.Queries,q=>((string)q["query"]).Contains("Mechanics"));
        var buttons=Parts(s).OfType<ButtonComponent>().ToArray();Assert.Contains(buttons,b=>b.Label=="Mechanics");
        await service.ApplyAsync(s,"mechanics",null);Assert.Equal(Junk,s.Analysis.Metric);Assert.Equal(3,h.Queries.Count);
        await service.ApplyAsync(s,Spin,null);Assert.Equal(Spin,s.Analysis.Metric);Assert.Equal(5,h.Queries.Count);
        await service.ApplyAsync(s,Junk,null);Assert.Equal(5,h.Queries.Count);
        foreach(var action in new[]{"deaths","incoming","interrupts","dispels"})await service.ApplyAsync(s,action,null);
        Assert.Equal(8,h.Queries.Count);Assert.Equal(4,h.Queries.Count(q=>((string)q["query"]).Contains("Mechanics")));
        Assert.DoesNotContain("PRIVATE",Text(s));
    }
    [Theory]
    [InlineData(Junk,"Throw Junk",1291935,"damage-taken")]
    [InlineData(Spin,"Shell Spin",1291918,"auras&spells=debuffs")]
    public async Task SelectedMechanicShowsCountDefinitionScopeAffectedPlayersExactSpellLinkAndQuestion(string metric,string label,int spell,string view)
    {
        var s=Session();var service=Service(Wire(3));await service.ApplyAsync(s,metric,null);var text=Text(s);
        Assert.Contains("🔎 "+label,text);Assert.Contains(metric==Junk?"3 hits":"3 applications",text);Assert.Contains("· 1 player",text);
        Assert.Contains("· 1:00",text);Assert.Contains("`0:01`",text);
        Assert.Contains("PRIVATE synthetic player",text);Assert.DoesNotContain("How often",text);
        s.OutputHelp=true;var help=Text(s);s.OutputHelp=false;Assert.Contains("counts damage hits, including fully absorbed ones",help);Assert.Contains("counts debuff applications",help);
        Assert.Contains($"#fight=2&type={view}&ability={spell}",text);
        Assert.DoesNotContain("stuns",text);Assert.DoesNotContain("failure rate",text);Assert.DoesNotContain("#start=",text);
        Assert.DoesNotContain("No interrupts",text);Export(metric,s);
    }
    [Theory]
    [InlineData("complete")] [InlineData("partial")] [InlineData("unsupported")] [InlineData("unavailable")]
    public async Task MissingPartialUnsupportedAndCompleteZeroRemainDistinct(string status)
    {
        var s=Session();var metadata=Metadata();var events=Events();
        if(status=="unsupported")metadata["zone"]["id"]=999;
        if(status=="partial")((JObject)events["events"]).Remove("nextPageTimestamp");
        if(status=="unavailable")events.Remove("events");
        var service=Service(With(metadata,events));
        if(status=="unavailable")await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(s,"mechanics",null));else await service.ApplyAsync(s,"mechanics",null);
        var text=Text(s);
        if(status=="complete")Assert.Contains("**No hits** on players this pull",text);
        else Assert.DoesNotContain("No hits",text);
        if(status=="partial") {Assert.Contains("Partial data",text);Assert.Contains("at least",text);}
        if(status=="unsupported") {Assert.Contains("Not supported for this fight",text);Assert.DoesNotContain("0 hits",text);}
        if(status=="unavailable") {Assert.Contains("This mechanic is unavailable",text);Assert.DoesNotContain("0 hits",text);}
        Export("mechanics-"+status,s);
    }
    [Fact]
    public async Task EveryRetainedEventPageAndAllPullPickerPagesAreReachableWithoutRefetching()
    {
        var s=Session();var h=Wire(500);var service=Service(h);await service.ApplyAsync(s,"mechanics",null);
        var seen=0;
        for(var page=0;page<63;page++)
        {
            var text=Text(s);Assert.Contains($"page {page+1}/63",text);
            // Every retained event is one row; rows start with their time in the pull.
            seen+=text.Split('\n').Count(line=>line.StartsWith("`0:0",StringComparison.Ordinal));
            Check(s);await service.ApplyAsync(s,"analysis_next",null);
        }
        Assert.Equal(500,seen);Assert.Equal(2,h.Queries.Count);Assert.Equal(62,s.AnalysisPage);
        s.Report=s.Report with {Fights=Enumerable.Range(1,51).Select(i=>Fight with {Id=i}).ToArray()};
        var picks=new HashSet<string>();
        for(var page=0;page<3;page++)
        {
            foreach(var option in Assert.Single(Parts(s).OfType<SelectMenuComponent>()).Options)picks.Add(option.Value);
            await service.ApplyAsync(s,"pulls_next",null);
        }
        Assert.Equal(51,picks.Count);Assert.Equal(2,h.Queries.Count);Assert.DoesNotContain("PRIVATE",Text(s));
    }
    [Theory]
    [InlineData(Junk)] [InlineData(Spin)]
    public async Task HostileNamesStillFitNativeCv2AndSharedOverviewIsExactlyMetadataOnly(string metric)
    {
        var hostile=string.Concat(Enumerable.Repeat("😀**@everyone[]\\\n\u202e",400));
        var s=Session();s.Report=s.Report with {Title=hostile,Fights=new[]{Fight with {Name=hostile}}};
        var publicBefore=JsonConvert.SerializeObject(RaidRecapView.Build(s,true));
        var service=Service(Wire(17,hostile));await service.ApplyAsync(s,metric,null);s.Notice=hostile;
        for(var page=0;page<3;page++) { s.AnalysisPage=page;Check(s);Assert.Contains($"page {page+1}/3",Text(s)); }
        Assert.Equal(publicBefore,JsonConvert.SerializeObject(RaidRecapView.Build(s,true)));
        Assert.Empty(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s,true).Components).OfType<SectionComponent>());
        Assert.DoesNotContain(s.Token,publicBefore);Export("hostile-"+metric,s);
    }
    [Fact]
    public async Task ChangedSelectionReportOrFailedAwaitCannotLeakOldMechanicEvidence()
    {
        var s=Session();var h=Wire();var service=Service(h);await service.ApplyAsync(s,Junk,null);Assert.Contains("PRIVATE",Text(s));
        h.Reply=(i,q)=>throw new InvalidOperationException();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(s,Spin,null));Assert.Null(s.Analysis);Assert.DoesNotContain("PRIVATE",Text(s));
        await service.ApplyAsync(s,Junk,null);Assert.Contains("PRIVATE",Text(s));
        s.Report=s.Report with {Revision=2};Assert.DoesNotContain("PRIVATE",Text(s));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(s,Junk,null));Assert.Null(s.Analysis);
        s.Report=Report;await service.ApplyAsync(s,Junk,null);Assert.Contains("PRIVATE",Text(s));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.OpenAsync(s,"ZbCdEfGh12345678"));Assert.Null(s.Analysis);
    }
    [Fact]
    public async Task CacheSeparatesRulesFightSnapshotAndOptionsWhileReusingIdenticalRule()
    {
        var s=Session();var cache=new RaidRecapCache();var h=Wire();var service=new RaidRecapService(Client(h),cache);
        await service.ApplyAsync(s,Junk,null);await service.ApplyAsync(s,Spin,null);await service.ApplyAsync(s,Junk,null);Assert.Equal(4,h.Queries.Count);Assert.Equal(2,cache.Count);
        h.Reply=(i,q)=>{
            var data=((string)q["query"]).Contains("MechanicsMetadata")?Metadata():Events();
            if(data["fights"] is JArray fights){fights[0]["id"]=s.Report.Fights[0].Id;fights[0]["startTime"]=s.Report.Fights[0].StartMs;fights[0]["endTime"]=s.Report.Fights[0].EndMs;}
            var env=Envelope(data);env["data"]["reportData"]["report"]["revision"]=s.Report.Revision;return env;
        };
        foreach(var report in new[]{Report with {Revision=2},Report with {Fights=new[]{Fight with {Id=3}}},Report with {Fights=new[]{Fight with {StartMs=5001}}}})
        {s.Report=report;await service.ApplyAsync(s,Junk,null);}
        Assert.Equal(10,h.Queries.Count);Assert.Equal(5,cache.Count);
    }
    [Fact]
    public void ExtremeFiniteElapsedValuesAndHostileNamesCannotExceedTextBudget()
    {
        var s=Session();s.Report=s.Report with {Title=new string('*',1000),Fights=new[]{Fight with {EndMs=double.MaxValue}}};
        s.AnalysisMetric=Junk;s.Notice=new string('*',1000);
        s.Analysis=new RaidRecapAnalysis(Junk,true,null){Mechanic=new(s.Report.SnapshotKey,2,5000,double.MaxValue,"complete",
            Enumerable.Range(1,8).Select(i=>new RaidRecapMechanicEvent(i,new string('*',500),1e308,null,null)).ToArray())};
        Check(s);
    }
    [Fact]
    public async Task BothSectionDrilldownsAndFifthSubviewRouteThroughRealSdkModule()
    {
        var s=Session();var service=Service(Wire());await service.ApplyAsync(s,"mechanics",null);
        using var client=new DiscordRestClient();using var router=new InteractionService(client);
        using var deps=new ServiceCollection().AddSingleton(service).AddSingleton(new RaidRecapSessions()).AddSingleton(Mock.Of<IRaidRecapDiscord>())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<RaidRecapCommands>>(NullLogger<RaidRecapCommands>.Instance).BuildServiceProvider();
        await router.AddModuleAsync<RaidRecapCommands>(deps);
        var sections=Parts(s).OfType<SectionComponent>().ToArray();Assert.Equal(2,sections.Length);
        // One switch to the other mechanic, plus the footer's How to read.
        Assert.Equal(new[]{"Shell Spin","How to read"},sections.Select(c=>Assert.IsType<ButtonComponent>(c.Accessory).Label));
        foreach(var component in Parts(s))
        {
            var id=component switch {ButtonComponent b when b.Style!=ButtonStyle.Link=>b.CustomId,SelectMenuComponent m=>m.CustomId,_=>null};if(id==null)continue;
            Assert.True(router.SearchComponentCommand(Mock.Of<IComponentInteraction>(x=>x.Data==Mock.Of<IComponentInteractionData>(d=>d.CustomId==id))).IsSuccess,id);
        }
    }
    private static void Check(RaidRecapSession s)
    {
        var parts=Parts(s);Assert.InRange(parts.Length,1,40);Assert.InRange(Text(s).Length,1,3800);Assert.DoesNotContain("@everyone",Text(s));Assert.DoesNotContain('\u202e',Text(s));
        Assert.Equal(5,parts.OfType<ActionRowComponent>().Count());
        Assert.Equal(new[]{"Overview","Bosses","Damage","Healing","Analysis"},parts.OfType<ActionRowComponent>().First().Components.Cast<ButtonComponent>().Select(b=>b.Label));
        Assert.Equal(new[]{"Deaths","Damage taken","Interrupts","Dispels","Mechanics"},parts.OfType<ActionRowComponent>().ElementAt(3).Components.Cast<ButtonComponent>().Select(b=>b.Label));
        Assert.All(parts.OfType<ActionRowComponent>(),r=>Assert.InRange(r.Components.Count,1,5));
        Assert.All(parts.OfType<SelectMenuComponent>(),m=>{Assert.InRange(m.Options.Count,1,25);Assert.All(m.Options,o=>Assert.InRange(o.Label.Length,1,100));});
        Assert.All(parts.OfType<ButtonComponent>().Where(b=>b.Style!=ButtonStyle.Link),b=>Assert.InRange(b.CustomId.Length,1,100));
    }
    private static void Export(string name,RaidRecapSession s)
    {
        var dir=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");if(string.IsNullOrEmpty(dir))return;
        System.IO.Directory.CreateDirectory(dir);var payload=RaidRecapView.Build(s);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir,"synthetic-"+name+".json"),JsonConvert.SerializeObject(payload,Formatting.Indented));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir,"synthetic-"+name+".md"),"# Synthetic SDK payload — not live Discord rendering\n\n"+RaidRecapPanelTests.Text(payload));
    }
}
