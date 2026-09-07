using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;

namespace NinjaBotCore.Tests;
public class RaidRecapPanelTests
{
    public static RaidRecapReport Report(bool kills=true,int bosses=1) => new("AbCdEfGh12345678","Tuesday **raid** @everyone",1,100000,999999,DateTimeOffset.UnixEpoch,
        Enumerable.Range(1,bosses).Select(i=>new RaidRecapFight(i,i,4,"Boss "+i,kills,false,1000,61000,kills?null:12.5)).ToArray());
    public static RaidRecapSession Session() => new RaidRecapSessions().Create(1,2,3);
    public static IEnumerable<IMessageComponent> Flatten(IEnumerable<IMessageComponent> components)
    {
        foreach(var component in components)
        {
            yield return component;
            var children=component switch { ContainerComponent c=>c.Components,ActionRowComponent r=>r.Components,_=>null };
            if(children!=null) foreach(var child in Flatten(children)) yield return child;
        }
    }
    public static string Text(MessageComponent c)=>string.Join("\n",Flatten(c.Components).OfType<TextDisplayComponent>().Select(t=>t.Content));

    [Fact]
    public async Task ConcurrentDiscoverySingleFlightsByGuildIdentity()
    {
        var source=new Mock<IRaidRecapSource>();var completion=new TaskCompletionSource<System.Collections.Generic.IReadOnlyList<WclV2Report>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(x=>x.GetRaidRecapReportsAsync("Guild","realm","us")).Returns(completion.Task);
        var service=new RaidRecapService(source.Object,new RaidRecapCache());
        var a=service.DiscoverAsync(Session(),"Guild","realm","us");var b=service.DiscoverAsync(Session(),"Guild","realm","us");
        completion.SetResult(Array.Empty<WclV2Report>());await Task.WhenAll(a,b);
        source.Verify(x=>x.GetRaidRecapReportsAsync("Guild","realm","us"),Times.Once);
    }

    [Fact]
    public async Task DiscoveryDoesNotLoadDetailsAndPickerPagesReachAllReports()
    {
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);
        source.Setup(x=>x.GetRaidRecapReportsAsync("Guild","realm","us")).ReturnsAsync(Enumerable.Range(1,26).Select(i=>new WclV2Report { Code=$"A{i:000000000000000}",Title="Raid "+i,StartTime=i }).ToArray());
        var service=new RaidRecapService(source.Object,new RaidRecapCache()); var s=Session();
        await service.DiscoverAsync(s,"Guild","realm","us");
        var options=Assert.Single(Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>()).Options;
        Assert.Equal(25,options.Count); Assert.Contains("26",options.First().Label);
        await service.ApplyAsync(s,"reports_next",null);
        options=Assert.Single(Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>()).Options;
        Assert.Single(options); Assert.Equal("25",options.First().Value);
        source.Verify(x=>x.GetRaidRecapReportsAsync("Guild","realm","us"),Times.Once);
        source.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task SelectedReportLoadsOnceAndWipeOnlyPerformanceDoesNotFetchTables()
    {
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);
        source.Setup(x=>x.GetRaidRecapReportAsync("AbCdEfGh12345678")).ReturnsAsync(Report(false));
        var service=new RaidRecapService(source.Object,new RaidRecapCache()); var s=Session();
        await service.OpenAsync(s,"AbCdEfGh12345678");
        await service.ApplyAsync(s,"damage",null);
        Assert.Contains("No completed boss kills",Text(RaidRecapView.Build(s)));
        Assert.Contains("12.5%",Text(RaidRecapView.Build(s)));
        source.Verify(x=>x.GetRaidRecapReportAsync(It.IsAny<string>()),Times.Once);
        source.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("overview")]
    [InlineData("bosses")]
    [InlineData("damage")]
    [InlineData("healing")]
    public void EveryViewBoundsUntrustedTextComponentsAndPaging(string view)
    {
        var s=Session(); s.Report=Report(true,51) with { Title=new string('*',10000)+"@everyone" }; s.View=view; s.CanShare=true;
        var component=RaidRecapView.Build(s); var all=Flatten(component.Components).ToArray();
        Assert.InRange(all.Length,1,40);
        Assert.InRange(Text(component).Length,1,4000);
        Assert.DoesNotContain("@everyone",Text(component));
        Assert.Contains("may still update",Text(component));
        foreach(var menu in all.OfType<SelectMenuComponent>())
        { Assert.InRange(menu.Options.Count,1,25); Assert.InRange(menu.CustomId.Length,1,100); Assert.All(menu.Options,o=>Assert.InRange(o.Label.Length,1,100)); }
        foreach(var button in all.OfType<ButtonComponent>().Where(b=>b.Style!=ButtonStyle.Link)) Assert.InRange(button.CustomId.Length,1,100);
        if(view!="overview") Assert.Contains(all.OfType<ButtonComponent>(),b=>b.Label=="Next"&&!b.IsDisabled);
    }
    [Fact]
    public void PickerNoticeIsBoundedEscapedAndPrivate()
    {
        var s=Session();s.View="reports";s.Notice="**Failure** @everyone "+new string('*',1000);
        var notice=Assert.Single(Flatten(RaidRecapView.Build(s).Components).OfType<TextDisplayComponent>(),t=>t.Content.Contains("Failure"));
        Assert.Equal(RaidRecapRules.Text(s.Notice,300),notice.Content);
        Assert.InRange(notice.Content.Length,1,300);
        Assert.DoesNotContain("@everyone",notice.Content);
        Assert.DoesNotContain("Failure",Text(RaidRecapView.Build(s,true)));
    }

    [Fact]
    public void SharedOverviewIsReadOnlyAndCannotLeakPrivateSessionToken()
    {
        var s=Session();s.Report=Report();s.CanShare=true;
        var c=RaidRecapView.Build(s,true);
        Assert.DoesNotContain(Flatten(c.Components).OfType<ButtonComponent>(),b=>b.Style!=ButtonStyle.Link);
        Assert.Empty(Flatten(c.Components).OfType<SelectMenuComponent>());
        Assert.DoesNotContain(s.Token,Newtonsoft.Json.JsonConvert.SerializeObject(c));
        Assert.Contains("1 kills",Text(c));
    }
    [Fact]
    public void ExportRepresentativeSyntheticComponentTree()
    {
        var s=Session();s.CanShare=true;
        s.Report=Report(false,2) with { Title="SYNTHETIC fixture — raid progression" };
        var privateCard=RaidRecapView.Build(s);
        var sharedCard=RaidRecapView.Build(s,true);
        Assert.Contains("Most-pulled unresolved",Text(privateCard));
        var destination=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");
        if(!string.IsNullOrEmpty(destination))
        {
            System.IO.Directory.CreateDirectory(destination);
            System.IO.File.WriteAllText(System.IO.Path.Combine(destination,"synthetic-cv2-tree.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new { fixture="Synthetic test data, not a live Warcraft Logs report",privateCard,sharedCard },Newtonsoft.Json.Formatting.Indented));
            System.IO.File.WriteAllText(System.IO.Path.Combine(destination,"synthetic-cv2-text.md"),"# Synthetic CV2 fixture (not live log data)\n\n"+Text(privateCard));
        }
    }

    [Fact]
    public async Task InvalidSelectionCannotFetchAnUnlistedReport()
    {
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict); var service=new RaidRecapService(source.Object,new RaidRecapCache());
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(Session(),"report","999"));
        source.VerifyNoOtherCalls();
    }
}
