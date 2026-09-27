using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;

/// <summary>Offline source-generated query capture / exact-response replay. Never sends network traffic.</summary>
public class RaidRecapPlayerQueryReplayTests
{
    private sealed class Replay : HttpMessageHandler
    {
        public string Input,Output,Pending,Metric="dps";
        public readonly List<string> Executed=new();
        public bool Synthetic=>Input==null;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri.AbsolutePath.Contains("oauth"))return Json(JObject.Parse("{access_token:'offline',expires_in:3600}"));
            Assert.Equal("https://www.warcraftlogs.com/api/v2/client",request.RequestUri.AbsoluteUri);
            var payload=JObject.Parse(await request.Content.ReadAsStringAsync(ct));var query=(string)payload["query"];
            if(query.Contains("rateLimitData"))return Json(JObject.Parse("{data:{rateLimitData:{limitPerHour:1000,pointsSpentThisHour:1,pointsResetIn:100}}}"));
            var name=query.Contains("RaidRecapPlayerRoster")?"PlayerRoster":query.Contains("RaidRecapPlayerParses")?"PlayerParses"+(query.Contains("playerMetric: hps")?"Hps":"Dps"):
                query.Contains("RaidRecapParseServers")?"ParseServers"+(Metric=="hps"?"Hps":"Dps"):query.Contains("table(dataType: Healing")?"HealingTable":query.Contains("table(dataType: DamageDone")?"DamageTable":"Report";
            if(name.StartsWith("PlayerParses",StringComparison.Ordinal))Metric=name.EndsWith("Hps",StringComparison.Ordinal)?"hps":"dps";
            Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,name+".query.graphql"),query+"\n");File.WriteAllText(Path.Combine(Output,name+".variables.json"),payload["variables"].ToString(Formatting.Indented)+"\n");
            if(Input!=null)
            {
                var file=Path.Combine(Input,name+".response.json");
                if(!File.Exists(file)){Pending=name;throw new InvalidOperationException("Exact response is pending; capture only.");}
                var raw=File.ReadAllText(file);Assert.InRange(Encoding.UTF8.GetByteCount(raw),1,4*1024*1024);Executed.Add(name);return Json(JObject.Parse(raw));
            }
            Executed.Add(name);var report=RaidRecapPlayerProviderTests.Metadata();report["title"]="SYNTHETIC replay fixture";report["startTime"]=100000;
            report["rankings"]=RaidRecapPlayerProviderTests.Ranking();report["table"]=JObject.Parse("{data:{entries:[{id:7,name:'Same',total:0},{id:8,name:'Same',total:6000}]}}");
            return Json(new JObject{["data"]=new JObject{["reportData"]=new JObject{["report"]=report},["worldData"]=RaidRecapPlayerProviderTests.Servers()}});
        }
        private static HttpResponseMessage Json(JObject value)=>new(HttpStatusCode.OK){Content=new StringContent(value.ToString())};
    }
    [Fact]
    public async Task CaptureSourceQueriesAndReplaySuppliedExactResponsesThroughHttpServiceAndSdk()
    {
        var input=Environment.GetEnvironmentVariable("RAID_RECAP_PLAYER_REPLAY_DIR");
        var evidence=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");
        // Always exercise synthetic replay in the normal suite. Real input never falls back to it.
        var output=evidence==null?Path.Combine(Path.GetTempPath(),"recap-query-"+Guid.NewGuid().ToString("N")):Path.Combine(evidence,"player-query-replay");
        var config=input==null?new JObject{["code"]="AbCdEfGh12345678",["fightId"]=2}:JObject.Parse(File.ReadAllText(Path.Combine(input,"request.json")));
        var code=RaidRecapRules.ReportCode((string)config["code"]);var fightId=(int)config["fightId"];
        var handler=new Replay{Input=input,Output=output};var factory=new Mock<IHttpClientFactory>();factory.Setup(f=>f.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(handler,false));
        var client=new WarcraftLogsV2Client(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{["WclClientId"]="offline",["WclClientSecret"]="offline"}).Build(),factory.Object,NullLogger<WarcraftLogsV2Client>.Instance);
        var svc=new RaidRecapService(client,new RaidRecapCache());var s=RaidRecapPanelTests.Session();var counts=new Dictionary<string,int>();string status="pending";
        try
        {
            await svc.OpenAsync(s,code);
            s.KillIndex=s.Report.Fights.Where(f=>f.IsKill).ToList().FindIndex(f=>f.Id==fightId);Assert.True(s.KillIndex>=0,"Requested fight must be a completed kill.");
            foreach(var metric in new[]{"damage","healing"})
            {
                await svc.ApplyAsync(s,metric,null);if(handler.Pending!=null)break;
                Assert.NotNull(s.PerformanceParses);Assert.True(s.PerformanceParses.Entries.Count>0,"A complete replay requires at least one verified parse; no synthetic substitution.");
                counts[metric+"Sources"]=s.Performance.Count;counts[metric+"VerifiedParses"]=s.PerformanceParses.Entries.Count;
                s.RankPage=0;await Save(output,metric,s);
                var requests=handler.Executed.Count;var raw=s.Performance;var selected=s.KillIndex;
                var seen=new List<string>();var pages=RaidRecapPlayerPresentation.OutputPages(s);
                for(var page=0;page<pages.Count;page++)
                {
                    Assert.Equal(page,s.RankPage);
                    var closed=RaidRecapOutputTests.Cards(s).SelectMany(c=>c.Components.OfType<Discord.TextDisplayComponent>()).Select(t=>t.Content).ToArray();
                    seen.AddRange(closed);await Save(output,metric+"-page"+(page+1)+"-closed",s);
                    await svc.ApplyAsync(s,"output_help",null);await Save(output,metric+"-page"+(page+1)+"-help",s);
                    Assert.Equal(closed,RaidRecapOutputTests.Cards(s).SelectMany(c=>c.Components.OfType<Discord.TextDisplayComponent>()).Select(t=>t.Content));
                    await svc.ApplyAsync(s,"output_help",null);await svc.ApplyAsync(s,"ranks_next",null);
                }
                var fight=s.Report.Fights.Where(f=>f.IsKill).ElementAt(selected);
                Assert.Equal(raw.Select((r,i)=>RaidRecapPlayerPresentation.Standing(s.Report,fight,r,i+1,metric=="healing"?"HPS":"DPS")),seen);
                Assert.Same(raw,s.Performance);Assert.Equal(selected,s.KillIndex);Assert.Equal(requests,handler.Executed.Count);
                counts[metric+"ReachableSources"]=seen.Count;counts[metric+"Pages"]=pages.Count;
            }
            if(handler.Pending==null)
            {
                var publicBefore=JsonConvert.SerializeObject(RaidRecapView.Build(s,true));await svc.ApplyAsync(s,"players",null);
                counts["catalogPlayers"]=s.PlayerPanel.Roster.Players.Count;Assert.True(counts["catalogPlayers"]>0);
                await svc.ApplyAsync(s,"player",s.PlayerPanel.Roster.Players[0].ActorId.ToString());await svc.ApplyAsync(s,"player_lens","healing");
                RaidRecapPlayerReachabilityTests.Check(s);await Save(output,"player",s);Assert.Equal(publicBefore,JsonConvert.SerializeObject(RaidRecapView.Build(s,true)));status="complete";
            }
        }
        catch(InvalidOperationException) when(handler.Pending!=null) { }
        finally
        {
            Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"summary.json"),JsonConvert.SerializeObject(new{status,synthetic=handler.Synthetic,pending=handler.Pending,executed=handler.Executed,counts},Formatting.Indented));
            if(evidence==null && input==null)Directory.Delete(output,true);
        }
        if(handler.Synthetic)Assert.Equal("complete",status);
        else if((bool?)config["requireComplete"]==true)Assert.Equal("complete",status);
    }
    private static Task Save(string directory,string name,RaidRecapSession s)=>RaidRecapOutputTests.Save(directory,name,s);
}
