using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;
public class RaidRecapTransportTests
{
    // All responses here are synthetic contract fixtures; no provider credentials or live traffic.
    public sealed class Handler : HttpMessageHandler
    {
        public readonly List<JObject> Queries = new();
        public string Result = "{\"data\":{\"reportData\":{\"report\":{\"code\":\"AbCdEfGh12345678\",\"revision\":1,\"fights\":[]}}}}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if(request.RequestUri.AbsolutePath.Contains("oauth")) return Json("{\"access_token\":\"test-token\",\"expires_in\":3600}");
            Assert.Equal("https://www.warcraftlogs.com/api/v2/client",request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer",request.Headers.Authorization.Scheme);
            var query=JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if(query.Value<string>("query").Contains("rateLimitData")) return Json("{\"data\":{\"rateLimitData\":{\"limitPerHour\":1000,\"pointsSpentThisHour\":1,\"pointsResetIn\":100}}}");
            Queries.Add(query);
            return new HttpResponseMessage(Status) { Content=new StringContent(Result) };
        }
        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content=new StringContent(s) };
    }
    public static WarcraftLogsV2Client Client(Handler handler)
    {
        var factory=new Mock<IHttpClientFactory>();
        factory.Setup(f=>f.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(handler,false));
        return new WarcraftLogsV2Client(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string> { ["WclClientId"]="test",["WclClientSecret"]="test" }).Build(),factory.Object,NullLogger<WarcraftLogsV2Client>.Instance);
    }
    [Fact]
    public async Task ReportQueryUsesVariablesAndDoesNotFetchTables()
    {
        var h=new Handler(); var client=Client(h);
        var result=await client.GetRaidRecapReportAsync("AbCdEfGh12345678");
        var q=Assert.Single(h.Queries);
        Assert.Equal("AbCdEfGh12345678",(string)q["variables"]["code"]);
        Assert.Contains("revision",(string)q["query"]);
        Assert.Contains("inProgress",(string)q["query"]);
        Assert.DoesNotContain("table(",(string)q["query"]);
        Assert.Equal("AbCdEfGh12345678", result.Code);
    }
    [Theory]
    [InlineData("{\"errors\":[{\"message\":\"denied\"}],\"data\":{\"reportData\":{\"report\":{\"code\":\"AbCdEfGh12345678\",\"fights\":[]}}}}")]
    [InlineData("{\"data\":{\"reportData\":{\"report\":null}}}")]
    public async Task PartialErrorsAndDeniedReportsFailClosed(string result)
    {
        var h=new Handler { Result=result };
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Client(h).GetRaidRecapReportAsync("AbCdEfGh12345678"));
    }
    [Fact]
    public async Task UnsafeCodeDoesNotReachHttp()
    {
        var h=new Handler();
        await Assert.ThrowsAsync<ArgumentException>(()=>Client(h).GetRaidRecapReportAsync("bad"));
        Assert.Empty(h.Queries);
    }
    [Fact]
    public async Task TableUsesExactCompletedKillAndRelativeTime()
    {
        var h=new Handler { Result="{\"data\":{\"reportData\":{\"report\":{\"code\":\"AbCdEfGh12345678\",\"table\":{\"data\":{\"entries\":[]}}}}}}" };
        var fight=new RaidRecapFight(2,10,4,"Boss",true,false,5000,65000,null);
        await Client(h).GetRaidRecapTableAsync("AbCdEfGh12345678",fight,false);
        var q=Assert.Single(h.Queries);
        Assert.Equal(5000,(double)q["variables"]["start"]);
        Assert.Equal(65000,(double)q["variables"]["end"]);
        Assert.Equal(2,(int)q["variables"]["fights"][0]);
        Assert.Contains("killType: Kills",(string)q["query"]);
        Assert.Contains("DamageDone",(string)q["query"]);
        Assert.Contains("viewBy: Source",(string)q["query"]);
    }
    [Fact]
    public async Task WipeNeverFetchesPerformanceTable()
    {
        var h=new Handler();
        await Assert.ThrowsAsync<ArgumentException>(()=>Client(h).GetRaidRecapTableAsync("AbCdEfGh12345678",new RaidRecapFight(2,10,4,"Boss",false,false,0,60000,null),true));
        Assert.Empty(h.Queries);
    }
    [Fact]
    public async Task RateLimitHttpIsNotBlindlyRetried()
    {
        var h=new Handler { Status=HttpStatusCode.TooManyRequests,Result="{}" };
        await Assert.ThrowsAsync<HttpRequestException>(()=>Client(h).GetRaidRecapReportAsync("AbCdEfGh12345678"));
        Assert.Single(h.Queries);
    }
}
