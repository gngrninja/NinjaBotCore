using System;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;
public class RaidRecapPerformanceTests
{
    // Synthetic Source-view table contract. Never recursively add pets or add overheal.
    private static JObject Table()=>JObject.Parse("""
    {"data":{"entries":[
     {"id":1,"name":"Alpha","total":600000,"activeTime":1000,"overheal":900000,"pets":[{"total":800000}]},
     {"id":2,"name":"Beta","total":1200000},
     {"id":3,"name":"Unknown","total":null},
     {"id":4,"name":"Bad","total":-1}
    ]}}
    """);
    [Fact]
    public void RatesUseExactKillElapsedTimeWithoutPetOrOverhealDoubleCounting()
    {
        var rows=RaidRecapRules.Performance(Table(),60000);
        Assert.Equal("Beta",rows[0].Name); Assert.Equal(20000,rows[0].PerSecond);
        Assert.Equal(10000,rows[1].PerSecond); Assert.Equal(600000,rows[1].Total);
        Assert.Null(rows[2].PerSecond); Assert.Null(rows[3].PerSecond);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"entries\":null}}")]
    public void UnknownTableShapesFailClosed(string raw)=>Assert.Throws<InvalidOperationException>(()=>RaidRecapRules.Performance(JObject.Parse(raw),60000));
    [Fact]
    public async Task PerformanceIsLazyScopedCachedAndRenderedWithDenominator()
    {
        var r=RaidRecapPanelTests.Report(); var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);
        source.Setup(x=>x.GetRaidRecapReportAsync(r.Code)).ReturnsAsync(r);
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(r,r.Fights[0],false)).ReturnsAsync(Table());
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(r,r.Fights[0],true)).ReturnsAsync(Table());
        var service=new RaidRecapService(source.Object,new RaidRecapCache()); var s=RaidRecapPanelTests.Session();
        await service.OpenAsync(s,r.Code);
        source.Verify(x=>x.GetRaidRecapScopedTableAsync(It.IsAny<RaidRecapReport>(),It.IsAny<RaidRecapFight>(),It.IsAny<bool>()),Times.Never);
        await service.ApplyAsync(s,"damage",null); await service.ApplyAsync(s,"overview",null); await service.ApplyAsync(s,"damage",null);
        source.Verify(x=>x.GetRaidRecapScopedTableAsync(r,r.Fights[0],false),Times.Once);
        var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        Assert.Contains("20,000",text); Assert.Contains("elapsed",text); Assert.Contains("Beta",text);
        await service.ApplyAsync(s,"healing",null);
        source.Verify(x=>x.GetRaidRecapScopedTableAsync(r,r.Fights[0],true),Times.Once);
        Assert.Contains("overheal",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
    }
    [Fact]
    public async Task RankingPagesReachAllSources()
    {
        var r=RaidRecapPanelTests.Report(); var source=new Mock<IRaidRecapSource>();
        source.Setup(x=>x.GetRaidRecapScopedTableAsync(r,r.Fights[0],false)).ReturnsAsync(new JObject { ["data"]=new JObject { ["entries"]=new JArray(Enumerable.Range(1,21).Select(i=>new JObject { ["id"]=i,["name"]="Source"+i,["total"]=i*60000 })) } });
        var s=RaidRecapPanelTests.Session();s.Report=r; var service=new RaidRecapService(source.Object,new RaidRecapCache());
        await service.ApplyAsync(s,"damage",null); await service.ApplyAsync(s,"ranks_next",null); await service.ApplyAsync(s,"ranks_next",null);
        Assert.Contains("Source1",RaidRecapPanelTests.Text(RaidRecapView.Build(s)));
        Assert.Equal(2,s.RankPage);
    }
}
