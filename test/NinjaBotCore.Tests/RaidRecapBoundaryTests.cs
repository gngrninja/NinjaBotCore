using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;
public class RaidRecapBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"inProgress\":null")]
    [InlineData(",\"inProgress\":\"false\"")]
    public void UnknownCompletionIsNotCountedAsKillOrWipe(string extra)
    {
        var raw=JObject.Parse("{\"code\":\"AbCdEfGh12345678\",\"fights\":[{\"id\":1,\"encounterID\":10,\"kill\":true,\"startTime\":0,\"endTime\":10000"+extra+"}]}");
        var report=RaidRecapRules.ParseReport(raw,"AbCdEfGh12345678",DateTimeOffset.UnixEpoch);
        Assert.Equal(0,report.Kills); Assert.Equal(0,report.Wipes); Assert.Equal(1,report.Unfinished);
        Assert.Null(report.Fights[0].DurationMs);
    }

    [Theory]
    [InlineData(2,65000)]
    [InlineData(1,66000)]
    public async Task TableRevisionOrEndTimeDriftIsRejected(int revision,int end)
    {
        var h=new RaidRecapTransportTests.Handler { Result=$"{{\"data\":{{\"reportData\":{{\"report\":{{\"code\":\"AbCdEfGh12345678\",\"revision\":{revision},\"endTime\":{end},\"table\":{{\"data\":{{\"entries\":[]}}}}}}}}}}}}" };
        var report=new RaidRecapReport("AbCdEfGh12345678","Test",1,100000,65000,DateTimeOffset.UnixEpoch,new[]{new RaidRecapFight(2,10,4,"Boss",true,false,5000,65000,null)});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>RaidRecapTransportTests.Client(h).GetRaidRecapScopedTableAsync(report,report.Fights[0],false));
    }

    [Fact]
    public async Task DiscoveryIsMetadataOnlyNewestFirstAndStrictAboutPartialErrors()
    {
        var h=new RaidRecapTransportTests.Handler { Result="""
        {"data":{"reportData":{"reports":{"data":[{"code":"AbCdEfGh12345678","title":"Old","startTime":1},{"code":"ZbCdEfGh12345678","title":"New","startTime":2}]}}}}
        """ };
        var result=await RaidRecapTransportTests.Client(h).GetRaidRecapReportsAsync("Guild","realm","us");
        Assert.Equal("New",result[0].Title);
        Assert.DoesNotContain("fights",(string)Assert.Single(h.Queries)["query"]);
        h.Result="{\"errors\":[{\"message\":\"denied\"}],\"data\":{\"reportData\":{\"reports\":{\"data\":[]}}}}";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>RaidRecapTransportTests.Client(h).GetRaidRecapReportsAsync("Guild","realm","us"));
    }
}
