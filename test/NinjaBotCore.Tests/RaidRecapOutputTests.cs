using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Moq;
using Newtonsoft.Json;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;

// All identities and observations in this fixture are SYNTHETIC, not a live report.
public class RaidRecapOutputTests
{
    internal static RaidRecapSession Sample(string view="healing",int kills=26)
    {
        var s=new RaidRecapSession{Token="SYNTHETICOUTPUT",Report=RaidRecapPanelTests.Report(true,kills) with {Title="SYNTHETIC — output presentation"},View=view};
        var percent=new double?[]{48,54,81,99.9,null,0,25,50,75,95,99,100};
        s.Performance=percent.Select((p,i)=>new RaidRecapStanding("Source"+(i+1),600000-i*1000,10000-i*100)
        {ActorId=i+1,Player=new(i+1,"Source"+(i+1),"Priest","Holy","healers","Realm","US",true),Parse=p.HasValue?new(i+1,1000+i,p.Value,100,50,1,300):null}).ToArray();
        s.PerformanceParses=new(s.Report.SnapshotKey,1,view=="healing"?"hps":"dps","Parses","Today",1,DateTimeOffset.UnixEpoch,s.Performance.Where(r=>r.Parse!=null).Select(r=>r.Parse).ToArray());
        return s;
    }
    internal static ContainerComponent[] Cards(RaidRecapSession s)=>RaidRecapView.Build(s).Components.OfType<ContainerComponent>()
        .Where(c=>c.Components.OfType<TextDisplayComponent>().Any(t=>System.Text.RegularExpressions.Regex.IsMatch(t.Content,@"^\d+\. "))).ToArray();
    internal static string Text(RaidRecapSession s)=>RaidRecapPanelTests.Text(RaidRecapView.Build(s));
    internal static ActionRowComponent[] Rows(RaidRecapSession s)=>RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<ActionRowComponent>().ToArray();
    private static RaidRecapService Local()=>new(Mock.Of<IRaidRecapSource>(MockBehavior.Strict),new RaidRecapCache());
    private static ButtonComponent Button(RaidRecapSession s,string label)=>Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<ButtonComponent>(),b=>b.Label==label);

    internal static async Task Save(string directory,string name,RaidRecapSession s)
    {
        RaidRecapPlayerReachabilityTests.Check(s);var payload=RaidRecapView.Build(s);
        var wire=await RaidRecapAccessTests.CaptureOfflineWire(payload);
        var wireCards=wire["components"].Where(c=>c["components"]?.Any(t=>(int)t["type"]==10 && System.Text.RegularExpressions.Regex.IsMatch((string)t["content"],@"^\d+\. "))==true).ToArray();
        Assert.Equal(Cards(s).Select(c=>c.AccentColor.Value.RawValue),wireCards.Select(c=>(uint)c["accent_color"]));
        if(directory==null)return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,name+".sdk.json"),JsonConvert.SerializeObject(payload,Formatting.Indented));
        File.WriteAllText(Path.Combine(directory,name+".wire.json"),wire.ToString());
        File.WriteAllText(Path.Combine(directory,name+".md"),RaidRecapPanelTests.Text(payload));
    }
    [Fact]
    public async Task ExportSyntheticCardsAndGroupedControlsThroughActualOfflineRestSerialization()
    {
        var root=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");
        var directory=root==null?null:Path.Combine(root,"synthetic-output");
        foreach(var view in new[]{"damage","healing"})
        {
            var s=Sample(view);var svc=Local();
            for(var page=0;page<RaidRecapPlayerPresentation.OutputPages(s).Count;page++)
            {
                await Save(directory,view+"-page"+(page+1)+"-closed",s);
                await svc.ApplyAsync(s,"output_help",null);await Save(directory,view+"-page"+(page+1)+"-help",s);
                await svc.ApplyAsync(s,"output_help",null);await svc.ApplyAsync(s,"ranks_next",null);
            }
            await svc.ApplyAsync(s,"kills_next",null);await Save(directory,view+"-kill-options-page2",s);
            Assert.Contains("kill #1",Text(s));
        }
    }
    [Theory]
    [InlineData("damage")][InlineData("healing")]
    public void FiveWholeSourcesHaveIndependentNativeParseAccentsAndAccessibleLabels(string view)
    {
        var s=Sample(view);var cards=Cards(s);Assert.Equal(5,cards.Length);
        Assert.Equal(new uint[]{0x1eff00,0x0070ff,0xa335ee,0xe268a8,0x7F8C8D},cards.Select(c=>c.AccentColor.Value.RawValue));
        for(var i=0;i<cards.Length;i++)
        {
            var text=Assert.Single(cards[i].Components.OfType<TextDisplayComponent>()).Content;
            Assert.StartsWith((i+1)+". ",text);Assert.Contains("&source="+(i+1),text);
            Assert.Contains(RaidRecapPlayerPresentation.Badge(s.Performance[i].Parse),text);
        }
        Assert.Contains("Sources 1–5 of 12 · page 1/3",Text(s));
        Assert.Contains("Pink P99",Text(s));Assert.DoesNotContain("Gold P100",Text(s));
    }
    [Theory]
    [InlineData(0,0x666666u,"Gray P0")][InlineData(24.999,0x666666u,"Gray P24")]
    [InlineData(25,0x1eff00u,"Green P25")][InlineData(49.999,0x1eff00u,"Green P49")]
    [InlineData(50,0x0070ffu,"Blue P50")][InlineData(74.999,0x0070ffu,"Blue P74")]
    [InlineData(75,0xa335eeu,"Purple P75")][InlineData(94.999,0xa335eeu,"Purple P94")]
    [InlineData(95,0xff8000u,"Orange P95")][InlineData(98.999,0xff8000u,"Orange P98")]
    [InlineData(99,0xe268a8u,"Pink P99")][InlineData(99.999,0xe268a8u,"Pink P99")]
    [InlineData(100,0xe5cc80u,"Gold P100")]
    public void ActualSourceAccentUsesOverallParseNotPositionOrItemLevel(double percentile,uint color,string label)
    {
        var s=Sample();s.Performance=new[]{s.Performance[0] with {Parse=new(1,1000,percentile,100,100,1,300)}};
        Assert.Equal(color,Assert.Single(Cards(s)).AccentColor.Value.RawValue);Assert.Contains(label,Text(s));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void UnavailableParseRemainsNeutralAndUnverifiedIdentityCannotGainALink(bool verified)
    {
        var s=Sample();s.Performance=new[]{s.Performance[0] with {Parse=null,Player=verified?s.Performance[0].Player:null}};
        Assert.Equal(0x7F8C8Du,Assert.Single(Cards(s)).AccentColor.Value.RawValue);
        Assert.Contains("Parse unavailable",Text(s));Assert.DoesNotContain("Gray P0",Text(s));
        if(!verified){Assert.Contains("Unverified source identity",Text(s));Assert.DoesNotContain("&source=",Text(s));}
    }
    [Theory]
    [InlineData("damage")][InlineData("healing")]
    public void CollapsedHeaderIsScanFirstButKeepsEssentialMeaningAndWarnings(string view)
    {
        var s=Sample(view);s.Notice="Visible partial warning";s.PerformanceNotice="Enrichment warning";
        var text=Text(s);Assert.DoesNotContain("Band colors:",text);Assert.DoesNotContain("Fractional display",text);
        Assert.Contains("Visible partial warning",text);Assert.Contains("Enrichment warning",text);
        Assert.Contains("kill #1",text);Assert.Contains("elapsed 00:01:00",text);Assert.Contains("this boss kill only",text);
        Assert.Contains("Parses · Today",text);Assert.Contains("may still update",text);Assert.NotNull(Button(s,"How to read"));
        if(view=="healing")
        {
            Assert.Contains("WCL Healing table total / elapsed seconds",text);Assert.Contains("overheal not added",text);
            Assert.Contains("not validated as effective healing",text);Assert.Contains("not a quality grade",text);
        }
    }
    [Theory]
    [InlineData(1)][InlineData(25)][InlineData(26)][InlineData(51)]
    public async Task SourcePairAndKillOptionPairOccupyDeterministicUnambiguousRows(int kills)
    {
        var s=Sample(kills:kills);var rows=Rows(s);
        Assert.Equal(new[]{"Overview","Bosses","Damage","Healing","Analysis"},rows[0].Components.OfType<ButtonComponent>().Select(b=>b.Label));
        Assert.Contains(rows[1].Components.OfType<ButtonComponent>(),b=>b.Label=="Players");
        Assert.IsType<SelectMenuComponent>(Assert.Single(rows[2].Components));
        var source=Assert.Single(rows,r=>r.Components.OfType<ButtonComponent>().Any(b=>b.Label=="Previous sources"));
        Assert.Equal(new[]{"Previous sources","Next sources","How to read"},source.Components.OfType<ButtonComponent>().Select(b=>b.Label));
        Assert.DoesNotContain(rows.SelectMany(r=>r.Components).OfType<ButtonComponent>(),b=>b.Label is "Previous" or "Next");
        var optionRows=rows.Where(r=>r.Components.OfType<ButtonComponent>().Any(b=>b.Label=="Previous kill options")).ToArray();
        if(kills<=25)Assert.Empty(optionRows);
        else
        {
            Assert.Equal(new[]{"Previous kill options","Next kill options"},Assert.Single(optionRows).Components.OfType<ButtonComponent>().Select(b=>b.Label));
            var svc=Local();await svc.ApplyAsync(s,"kills_next",null);
            Assert.Equal(0,s.KillIndex);Assert.Contains("kill #1",Text(s));Assert.Contains("Kill options · page 2/",Text(s));
            Assert.DoesNotContain(RaidRecapPlayerFlowTests.Menu(s,"kill").Options,o=>o.IsDefault==true);
            await Assert.ThrowsAsync<ArgumentException>(()=>svc.ApplyAsync(s,"kill","0"));
        }
        RaidRecapPlayerReachabilityTests.Check(s);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(5)]
    public void SingleSourcePageOmitsUselessPagingButKeepsHelp(int count)
    {
        var s=Sample(kills:1);s.Performance=s.Performance.Take(count).ToArray();
        Assert.DoesNotContain(Rows(s).SelectMany(r=>r.Components).OfType<ButtonComponent>(),b=>b.Label is "Previous sources" or "Next sources" or "Previous kill options" or "Next kill options");
        Assert.NotNull(Button(s,"How to read"));RaidRecapPlayerReachabilityTests.Check(s);
    }
    [Fact]
    public async Task LocalHelpRetainsRowsPagesSelectionNoticesAndPublicBytesWithoutIo()
    {
        var s=Sample();s.RankPage=1;s.KillPage=1;s.Notice="Partial data retained";
        var rows=s.Performance;var parses=s.PerformanceParses;var page=RaidRecapPlayerPresentation.OutputPages(s).SelectMany(p=>p).ToArray();
        var before=JsonConvert.SerializeObject(RaidRecapView.Build(s,true));var svc=Local();
        await svc.ApplyAsync(s,"output_help",null);
        var text=Text(s);Assert.NotNull(Button(s,"Hide help"));
        foreach(var term in new[]{"Output position","pets","cross-fight","Gray 0+","Green 25+","Blue 50+","Purple 75+","Orange 95+","Pink 99+","Gold exactly 100","Fractional display floors","partition 1","finality unknown","report revision"})Assert.Contains(term,text);
        Assert.Equal("Partial data retained",s.Notice);Assert.Equal(1,s.RankPage);Assert.Equal(1,s.KillPage);Assert.Equal(0,s.KillIndex);
        Assert.Same(rows,s.Performance);Assert.Same(parses,s.PerformanceParses);Assert.Equal(page,RaidRecapPlayerPresentation.OutputPages(s).SelectMany(p=>p));
        await svc.ApplyAsync(s,"output_help",null);Assert.NotNull(Button(s,"How to read"));Assert.DoesNotContain("Band colors:",Text(s));
        Assert.Equal(before,JsonConvert.SerializeObject(RaidRecapView.Build(s,true)));
    }
    [Theory]
    [InlineData("overview")][InlineData("bosses")][InlineData("analysis")][InlineData("reports")][InlineData("dossier")][InlineData("no-report")][InlineData("no-kills")]
    public async Task HelpRejectsOutsideMainPrivateOutput(string state)
    {
        var s=Sample();if(state=="dossier")s.PlayerPanel=new();else if(state=="no-report")s.Report=null;
        else if(state=="no-kills")s.Report=RaidRecapPanelTests.Report(false);else s.View=state;
        await Assert.ThrowsAsync<ArgumentException>(()=>Local().ApplyAsync(s,"output_help",null));
    }
    [Theory]
    [InlineData("overview")][InlineData("reports")][InlineData("healing")][InlineData("refresh")][InlineData("report")][InlineData("kill")][InlineData("players")]
    public async Task HelpResetsOnViewReportRefreshFightOrDossierChange(string action)
    {
        var s=Sample("damage");var source=new Mock<IRaidRecapSource>();var players=source.As<IRaidRecapPlayerSource>();
        source.Setup(x=>x.GetRaidRecapReportAsync(s.Report.Code)).ReturnsAsync(s.Report);
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(s.Report,s.Report.Fights[0],It.IsAny<bool>())).ReturnsAsync(Newtonsoft.Json.Linq.JObject.Parse("{data:{entries:[]}}"));
        players.Setup(x=>x.GetRaidRecapRosterAsync(s.Report,It.IsAny<RaidRecapFight>(),It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(RaidRecapPlayerFlowTests.Roster(s.Report,1));
        players.Setup(x=>x.GetRaidRecapParsesAsync(s.Report,s.Report.Fights[0],It.IsAny<bool>(),It.IsAny<RaidRecapRoster>(),It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((RaidRecapReport r,RaidRecapFight f,bool h,RaidRecapRoster roster,System.Threading.CancellationToken ct)=>new RaidRecapParses(r.SnapshotKey,f.Id,h?"hps":"dps","Parses","Today",1,DateTimeOffset.UnixEpoch,Array.Empty<RaidRecapParse>()));
        s.Reports=new[]{new NinjaBotCore.Models.Wow.WclV2Report{Code=s.Report.Code}};var svc=new RaidRecapService(source.Object,new RaidRecapCache());
        await svc.ApplyAsync(s,"output_help",null);Assert.NotNull(Button(s,"Hide help"));
        await svc.ApplyAsync(s,action,action is "report" or "kill"?"0":null);
        s.PlayerPanel=null;s.View="damage";Assert.NotNull(Button(s,"How to read"));
    }
    [Fact]
    public void BossAttemptPairHasTheSameInsertionRootCauseAndMustStayTogether()
    {
        var s=Sample();s.View="bosses";var rows=Rows(s);
        Assert.IsType<SelectMenuComponent>(Assert.Single(rows[2].Components));
        var attempts=Assert.Single(rows,r=>r.Components.OfType<ButtonComponent>().Any(b=>b.Label=="Previous attempts"));
        Assert.Equal(new[]{"Previous attempts","Next attempts"},attempts.Components.OfType<ButtonComponent>().Select(b=>b.Label));
    }
    [Theory]
    [InlineData("damage")][InlineData("healing")]
    public async Task EveryMaximumSourceSurvivesBothHelpStatesAndSdkBudgetsInOriginalOrder(string view)
    {
        var s=Sample(view,51);s.Report=s.Report with {Title=new string('*',500),Fights=s.Report.Fights.Select(f=>f with {Name=new string('*',500)}).ToArray()};
        s.Notice=new string('*',500);s.PerformanceNotice=new string('*',500);
        s.Performance=Enumerable.Range(1,1000).Select(i=>new RaidRecapStanding("Source"+i+new string('*',100),double.MaxValue,double.MaxValue)
        {ActorId=i,Player=i<=100?new(i,"Source"+i+new string('*',100),"DeathKnight","Unholy","dps","Realm","US",true):null,Parse=i<=100?new(i,i+10000,99.999,100,100,1,300):null}).ToArray();
        var seen=new List<int>();var svc=Local();
        for(var page=0;page<1000;page++)
        {
            s.Notice=new string('*',500); // Exercise maximum warning text on every page, not only page one.
            var closed=Cards(s).SelectMany(c=>c.Components.OfType<TextDisplayComponent>()).Select(t=>t.Content).ToArray();
            Assert.InRange(closed.Length,1,5);RaidRecapPlayerReachabilityTests.Check(s);
            await svc.ApplyAsync(s,"output_help",null);RaidRecapPlayerReachabilityTests.Check(s);
            Assert.Equal(closed,Cards(s).SelectMany(c=>c.Components.OfType<TextDisplayComponent>()).Select(t=>t.Content));
            Assert.All(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>(),m=>Assert.InRange(m.Options.Count,1,25));
            seen.AddRange(closed.Select(t=>int.Parse(t.Split('.')[0])));
            await svc.ApplyAsync(s,"output_help",null);
            if(Button(s,"Next sources").IsDisabled)break;
            await svc.ApplyAsync(s,"ranks_next",null);
        }
        Assert.Equal(Enumerable.Range(1,1000),seen);Assert.Equal(1000,s.Performance.Count);
    }
}
