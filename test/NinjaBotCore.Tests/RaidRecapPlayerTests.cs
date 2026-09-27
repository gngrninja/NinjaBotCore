using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;

// Synthetic identities unless an explicitly named research replay is requested.
public class RaidRecapPlayerTests
{
    [Fact]
    public void OutputRetainsNumericActorIdentityWithoutGuessingNames()
    {
        var table=JObject.Parse("{data:{entries:[{id:7,name:'Same',total:100},{id:8,name:'Same',total:50},{id:'9',name:'Same',total:1}]}}");
        var rows=JArray.FromObject(RaidRecapRules.Performance(table,1000));
        Assert.Equal(7,(int?)rows[0]["ActorId"]);Assert.Equal(8,(int?)rows[1]["ActorId"]);Assert.Null((int?)rows[2]["ActorId"]);
    }
    [Theory]
    [InlineData("bosses","bosses_next")][InlineData("damage","kills_next")]
    public async Task OptionPagingPreservesSelectedEntityAndDoesNotFetch(string view,string action)
    {
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);var service=new RaidRecapService(source.Object,new RaidRecapCache());
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report(true,51);s.View=view;
        s.Performance=new[]{new RaidRecapStanding("cached",1,1)};var old=s.Performance;
        await service.ApplyAsync(s,action,null);
        Assert.Equal(0,s.BossIndex);Assert.Equal(0,s.KillIndex);Assert.Same(old,s.Performance);source.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task PlayersEntryFromOverviewRequiresExplicitPullAndHasNoProviderIo()
    {
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);var service=new RaidRecapService(source.Object,new RaidRecapCache());
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report();
        await service.ApplyAsync(s,"players",null);
        var c=RaidRecapView.Build(s);Assert.Contains("Choose completed pull",RaidRecapPanelTests.Text(c));
        Assert.Contains(RaidRecapPanelTests.Flatten(c.Components).OfType<SelectMenuComponent>(),m=>m.CustomId.EndsWith("~player_pull"));
        source.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("overview")][InlineData("bosses")][InlineData("damage")][InlineData("healing")][InlineData("analysis")]
    public void PrivateViewsHaveTwoSiblingNativeContainersWithFiveTabs(string view)
    {
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report();s.View=view;
        var c=RaidRecapView.Build(s);Assert.Equal(2,c.Components.OfType<ContainerComponent>().Count());
        var all=RaidRecapPanelTests.Flatten(c.Components).ToArray();Assert.InRange(all.Length,1,40);
        Assert.Equal(new[]{"Overview","Bosses","Damage","Healing","Analysis"},all.OfType<ActionRowComponent>().First().Components.OfType<ButtonComponent>().Select(b=>b.Label));
        Assert.Contains(all.OfType<ButtonComponent>(),b=>b.Label=="Players");
    }
    [Fact]
    public void ReportPickerAlsoUsesSiblingScopeAndEvidenceContainers()
    {
        var s=RaidRecapPanelTests.Session();s.View="reports";
        Assert.Equal(2,RaidRecapView.Build(s).Components.OfType<ContainerComponent>().Count());
    }
    [Fact]
    public void DeathRowsLinkValidatedLocalActorAndNeverInventTargetOrTimeUrls()
    {
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report();s.View="analysis";s.PullIndex=0;
        s.Analysis=new RaidRecapAnalysis("deaths",true,null){Deaths=new[]{new RaidRecapDeath(7,"](@everyone)\u202e😀",1000,"Spell")}};
        var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        Assert.Contains("https://www.warcraftlogs.com/reports/AbCdEfGh12345678#fight=1&source=7",text);
        Assert.DoesNotContain("&target=",text);Assert.DoesNotContain("@everyone",text);Assert.DoesNotContain("\u202e",text,StringComparison.Ordinal);
    }
}
