using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

// Synthetic snapshots and offline sources only; never contact Discord or WCL.
public class RaidRecapReviewTests
{
    public static RaidRecapFight Pull(int id, bool kill=false, double duration=60000) =>
        new(id,10,4,"Synthetic boss",kill,false,id*100000,id*100000+duration,kill?0:12.5);
    public static RaidRecapSession Session(params RaidRecapFight[] fights)
    {
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report() with { Title="SYNTHETIC snapshot — progression",Fights=fights };return s;
    }
    private sealed class Source : IRaidRecapSource
    {
        public readonly List<(RaidRecapReport Report,RaidRecapFight Fight,string Metric)> Calls=new();
        public RaidRecapReport Report;
        public Func<RaidRecapFight,Task<RaidRecapAnalysis>> Load=_=>Task.FromResult(new RaidRecapAnalysis("deaths",true,null));
        public Task<IReadOnlyList<WclV2Report>> GetRaidRecapReportsAsync(string g,string r,string z)=>throw new Exception("Unexpected discovery");
        public Task<RaidRecapReport> GetRaidRecapReportAsync(string code)=>Task.FromResult(Report);
        public Task<JObject> GetRaidRecapScopedTableAsync(RaidRecapReport r,RaidRecapFight f,bool h)=>throw new Exception("Unexpected table");
        public Task<RaidRecapAnalysis> GetRaidRecapAnalysisAsync(RaidRecapReport r,RaidRecapFight f,string m,CancellationToken t=default)
        { Calls.Add((r,f,m));return Load(f); }
    }
    private static string Text(RaidRecapSession s)=>RaidRecapPanelTests.Text(RaidRecapView.Build(s));
    private static IMessageComponent[] Parts(RaidRecapSession s)=>RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).ToArray();
    private static SelectMenuComponent Picker(RaidRecapSession s,string side)=>Assert.Single(Parts(s).OfType<SelectMenuComponent>(),m=>m.CustomId.EndsWith("~compare_"+side));
    private static async Task Open(RaidRecapService service,RaidRecapSession s)
    { await service.ApplyAsync(s,"bosses",null);await service.ApplyAsync(s,"compare",null); }
    private static RaidRecapAnalysis Deaths(bool complete,params RaidRecapDeath[] deaths)=>new("deaths",complete,complete?null:"Synthetic partial observations") { Deaths=deaths };
    private static RaidRecapDeath Death(int actor,double ms)=>new(actor,"Private @everyone **player**",ms,"Synthetic spell");

    [Fact]
    public async Task ReviewCardsPrioritizeUnresolvedAndLatestWipeKillPairWithLocalDetails()
    {
        var s=Session(Pull(7,true) with {EncounterId=30},Pull(2),Pull(5,true) with {EncounterId=20},Pull(1),
            Pull(4) with {EncounterId=20},Pull(3),Pull(6,true) with {EncounterId=30});
        var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());
        var cards=Parts(s).OfType<SectionComponent>().ToArray();Assert.InRange(cards.Length,2,3);
        var first=string.Join("\n",cards[0].Components.OfType<TextDisplayComponent>().Select(t=>t.Content));
        Assert.Contains("Most-pulled unresolved",first);Assert.Contains("3 attempts",first);Assert.Contains("#fight=3&type=deaths",first);
        Assert.Contains("Review first",Text(s));Assert.Contains("#fight=4",Text(s));Assert.Contains("#fight=5",Text(s));
        Assert.All(cards,c=>Assert.Equal("Details",Assert.IsType<ButtonComponent>(c.Accessory).Label));
        Export("review-first-readable",s);
        await service.ApplyAsync(s,"review_0","999");Assert.Equal("bosses",s.View);Assert.Contains("[Fight 3]",Text(s));
        await service.ApplyAsync(s,"overview",null);await service.ApplyAsync(s,"review_1","999");
        Assert.Equal("bosses",s.View);Assert.Contains("A · Wipe #4",Text(s));Assert.Contains("B · Kill #5",Text(s));
        Assert.Empty(source.Calls);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(s,"review_9",null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PreferredOlderWipeKillMustNotBeLabeledLatestMatchedPair(bool otherBoss)
    {
        var s=Session(Pull(1),Pull(2,true),Pull(3) with {EncounterId=otherBoss?20:10},Pull(4) with {EncounterId=otherBoss?20:10});
        var card=Assert.Single(RaidRecapReview.Cards(s.Report),c=>c.B!=null);
        Assert.Equal(1,card.A.Id);Assert.Equal(2,card.B.Id); // Preserve the deliberate wipe-to-kill priority.
        Assert.DoesNotContain("Latest matched pair",Text(s));
        Assert.Contains("Recommended matched pair",Text(s));
    }

    [Fact]
    public async Task ComparisonDefaultsToLatestTwoCompletedPullsAndKeepsChronologyReachable()
    {
        var s=Session(Pull(3),Pull(1),Pull(4) with {InProgress=true},Pull(2));
        var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());await Open(service,s);
        Assert.Contains("A · Wipe #2",Text(s));Assert.Contains("B · Wipe #3",Text(s));
        Assert.Contains("elapsed 00:01:00",Text(s));Assert.Contains("active boss health 12.5%",Text(s));
        Assert.Contains("not encounter completion",Text(s));Assert.Contains("Compare deaths",Text(s));Assert.Empty(source.Calls);
        Assert.Equal("1",Assert.Single(Picker(s,"a").Options,o=>o.IsDefault==true).Value);
        Assert.Equal("2",Assert.Single(Picker(s,"b").Options,o=>o.IsDefault==true).Value);
        await service.ApplyAsync(s,"attempts",null);Assert.Contains("All attempts · chronological",Text(s));
        Assert.Contains("[Fight 4]",Text(s));Assert.Empty(source.Calls);
    }

    [Theory]
    [InlineData("boss")] [InlineData("difficulty")] [InlineData("unknown")] [InlineData("zero-difficulty")]
    [InlineData("duration")] [InlineData("infinite")] [InlineData("live")] [InlineData("outcome")] [InlineData("trash")]
    [InlineData("same")]
    public async Task UnsupportedCandidatesNeverBecomeAMatchedPair(string kind)
    {
        var second=Pull(2);
        second=kind switch {
            "boss"=>second with {EncounterId=20},"difficulty"=>second with {Difficulty=5},
            "unknown"=>second with {Difficulty=null},"zero-difficulty"=>second with {Difficulty=0},
            "duration"=>second with {EndMs=null},"infinite"=>second with {EndMs=double.PositiveInfinity},
            "live"=>second with {InProgress=true},"outcome"=>second with {Kill=null},
            "trash"=>second with {EncounterId=0},"same"=>Pull(1),_=>second};
        var s=Session(Pull(1),second);var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());
        await Open(service,s);Assert.Contains("No matched pair",Text(s));Assert.Empty(Parts(s).OfType<SelectMenuComponent>());
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(s,"compare_deaths",null));Assert.Empty(source.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task EmptyAndSinglePullOverviewOfferTruthfulFallback(int count)
    {
        var s=Session(Enumerable.Range(1,count).Select(i=>Pull(i)).ToArray());
        Assert.Contains(count==0?"No boss encounters":"No matched pair",Text(s));
        Assert.InRange(Parts(s).OfType<SectionComponent>().Count(),0,3);
        var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());await Open(service,s);
        Assert.Contains("No matched pair",Text(s));Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task BothPickersReachThirtyPagesWithoutChangingSelectionOrLoadingAndRejectOffPageOrSamePull()
    {
        var s=Session(Enumerable.Range(1,776).Reverse().Select(i=>Pull(i)).ToArray());
        var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());await Open(service,s);
        var original=Text(s);Assert.Contains("A · Wipe #775",original);Assert.Contains("B · Wipe #776",original);
        // A's default can be on the penultimate page while B is on the last.
        await service.ApplyAsync(s,"compare_a_next",null);await service.ApplyAsync(s,"compare_b_next",null);
        var seenA=new HashSet<string>();var seenB=new HashSet<string>();
        for(var page=31;page>=0;page--)
        {
            foreach(var o in Picker(s,"a").Options) seenA.Add(o.Value);
            foreach(var o in Picker(s,"b").Options) seenB.Add(o.Value);
            Assert.Contains("A · Wipe #775",Text(s));Assert.Contains("B · Wipe #776",Text(s));
            if(page>0) { await service.ApplyAsync(s,"compare_a_prev",null);await service.ApplyAsync(s,"compare_b_prev",null); }
        }
        Assert.Equal(776,seenA.Count);Assert.Equal(776,seenB.Count);Assert.Empty(source.Calls);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(s,"compare_a","775"));
        await service.ApplyAsync(s,"compare_a","0");Assert.Contains("A · Wipe #1",Text(s));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(s,"compare_b","0"));
        await service.ApplyAsync(s,"compare_b","1");Assert.Contains("B · Wipe #2",Text(s));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(s,"compare_a","-1"));
        await service.ApplyAsync(s,"compare_a_next",null);Assert.Contains("A · Wipe #1",Text(s));
        Assert.DoesNotContain(Picker(s,"a").Options,o=>o.IsDefault==true);Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task ExplicitDeathsUsesFullPullCacheAndInclusiveCommonWindowWithoutNamesOrCausalClaims()
    {
        var s=Session(Pull(1),Pull(2,true,90000));var cache=new RaidRecapCache();
        var source=new Source { Load=f=>Task.FromResult(f.Id==1?Deaths(true,Death(1,0),Death(2,0),Death(1,60000)):
            Deaths(true,Death(1,59000),Death(2,60000),Death(3,60001),Death(1,80000))) };
        var service=new RaidRecapService(source,cache);
        // Load A through the existing single-pull analysis; comparison must reuse that exact key.
        await service.ApplyAsync(s,"analysis",null);await service.ApplyAsync(s,"pull","0");Assert.Equal(2,source.Calls.Count);
        await Open(service,s);await service.ApplyAsync(s,"compare_deaths",null);Assert.Equal(2,source.Calls.Count);
        var text=Text(s);
        Assert.Contains("A full pull: 3 death events · 2 distinct players",text);
        Assert.Contains("B full pull: 4 death events · 3 distinct players",text);
        Assert.Contains("Same elapsed window [0, 00:01:00.000]",text);
        Assert.Contains("A window: 3 death events · 2 distinct players",text);Assert.Contains("B window: 2 death events · 2 distinct players",text);
        Assert.Contains("Window change (B − A): -1 death events · 0 distinct players",text);
        Assert.Contains("First loss: 00:00:00.000 · 2 simultaneous players",text);Assert.Contains("First loss: 00:00:59.000 · 1 simultaneous player",text);
        Assert.Contains("Equal time",text);Assert.Contains("phase",text);Assert.Contains("opportunity",text);Assert.Contains("roster",text);
        Assert.Contains("Repeated deaths are events, not extra players",text);Assert.Contains("First loss is not a cause",text);
        Assert.DoesNotContain("Private",text);Assert.Contains("#fight=1&type=deaths",text);Assert.Contains("#fight=2&type=deaths",text);
        Assert.All(source.Calls,c=>{Assert.Equal("deaths",c.Metric);Assert.Same(s.Report,c.Report);});Assert.Equal(2,cache.Count);
        await service.ApplyAsync(s,"compare_deaths",null);Assert.Equal(2,source.Calls.Count);
        Export("comparison",s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PartialZeroAndPartialEventsSuppressDeltasAndDefiniteFirstLoss(bool rows)
    {
        var s=Session(Pull(1),Pull(2));var source=new Source {Load=f=>Task.FromResult(f.Id==1?Deaths(true,Death(1,2000)):
            Deaths(false,rows?new[]{Death(1,0)}:Array.Empty<RaidRecapDeath>()))};
        var service=new RaidRecapService(source,new RaidRecapCache());await Open(service,s);await service.ApplyAsync(s,"compare_deaths",null);
        var text=Text(s);Assert.Contains("Partial observations",text);Assert.Contains("at least",text);
        Assert.Contains("A full pull: 1 death events · 1 distinct players",text);
        Assert.DoesNotContain("Window change (B − A):",text);Assert.DoesNotContain("First loss:",text);
        Assert.DoesNotContain("No player deaths",text);Export("partial",s);
    }

    [Fact]
    public async Task FailedSecondFetchClearsOldResultsBeforeAwaitAndAllowsDeliberateRetry()
    {
        var now=DateTimeOffset.UnixEpoch;var cache=new RaidRecapCache(()=>now);var source=new Source();
        var service=new RaidRecapService(source,cache);var s=Session(Pull(1),Pull(2));await Open(service,s);
        await service.ApplyAsync(s,"compare_deaths",null);Assert.Contains("A full pull:",Text(s));now+=TimeSpan.FromMinutes(3);
        var pending=new TaskCompletionSource<RaidRecapAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Load=f=>f.Id==1?Task.FromResult(Deaths(true,Death(1,2000))):pending.Task;
        var work=service.ApplyAsync(s,"compare_deaths",null);Assert.False(work.IsCompleted);
        Assert.DoesNotContain("A full pull:",Text(s));pending.SetException(new TimeoutException());
        await Assert.ThrowsAsync<TimeoutException>(()=>work);Assert.DoesNotContain("A full pull:",Text(s));Assert.Contains("unavailable",Text(s));
        source.Load=_=>Task.FromResult(Deaths(true));await service.ApplyAsync(s,"compare_deaths",null);
        Assert.Contains("A full pull: 1 death events",Text(s));Assert.Contains("B full pull: 0 death events",Text(s));
        Assert.Equal(5,source.Calls.Count);
    }

    [Fact]
    public async Task SelectionAndReportChangesResetPayloadAndNeverReuseAnotherSnapshot()
    {
        var s=Session(Pull(1),Pull(2),Pull(3));var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());
        await Open(service,s);await service.ApplyAsync(s,"compare_deaths",null);
        await service.ApplyAsync(s,"compare_a","0");Assert.DoesNotContain("A full pull:",Text(s));Assert.Equal(2,source.Calls.Count);
        await service.ApplyAsync(s,"compare_deaths",null);Assert.Equal(3,source.Calls.Count);
        source.Report=s.Report with {Revision=2,Fights=new[]{Pull(10),Pull(11)}};
        await service.OpenAsync(s,s.Report.Code);Assert.DoesNotContain("A full pull:",Text(s));
        await Open(service,s);Assert.Contains("A · Wipe #10",Text(s));Assert.Contains("B · Wipe #11",Text(s));
        await service.ApplyAsync(s,"compare_deaths",null);Assert.Equal(5,source.Calls.Count);
        s.Report=s.Report with {Revision=3};Assert.DoesNotContain("A full pull:",Text(s));
    }

    [Theory]
    [InlineData("code")] [InlineData("revision")] [InlineData("endTime")]
    public async Task RealOfflineWclDriftOnSecondPullCannotRenderAComparison(string field)
    {
        var h=new RaidRecapAnalysisTransportTests.Handler();var s=Session(Pull(1),Pull(2));
        h.Reply=i=> {
            var q=h.Queries.Last();var id=(int)q["variables"]["fights"][0];var body=RaidRecapAnalysisTransportTests.Deaths();
            body["fights"][0]["id"]=id;var envelope=RaidRecapAnalysisTransportTests.Envelope(body);
            if(i==2) envelope["data"]["reportData"]["report"][field]=field=="code"?new JValue("ZbCdEfGh12345678"):new JValue(888);
            return envelope;
        };
        var service=new RaidRecapService(RaidRecapAnalysisTransportTests.Client(h),new RaidRecapCache());await Open(service,s);Assert.Empty(h.Queries);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(s,"compare_deaths",null));
        Assert.Equal(2,h.Queries.Count);Assert.DoesNotContain("A full pull:",Text(s));Assert.DoesNotContain("No player deaths",Text(s));
        Assert.All(h.Queries,q=> { Assert.Contains("dataType: Deaths",(string)q["query"]);Assert.Equal(60000,(double)q["variables"]["end"]-(double)q["variables"]["start"]); });
    }

    [Fact]
    public async Task NativeCardsAndComparisonRespectActualSdkBudgetsAndSharedOverviewPrivacy()
    {
        var hostile=string.Concat(Enumerable.Repeat("😀**@everyone[]\\\n\u202e",400));
        var s=Session(Enumerable.Range(1,60).Select(i=>Pull(i) with {Name=hostile}).ToArray());s.Report=s.Report with {Title=hostile};s.Notice=hostile;
        var source=new Source();var service=new RaidRecapService(source,new RaidRecapCache());
        var sharedBefore=JsonConvert.SerializeObject(RaidRecapView.Build(s,true));
        Check(s);Export("review-first",s);
        await Open(service,s);Check(s);await service.ApplyAsync(s,"compare_deaths",null);Check(s);
        s.Notice="SECRET PRIVATE NOTICE";
        var shared=RaidRecapView.Build(s,true);var json=JsonConvert.SerializeObject(shared);
        Assert.Equal(sharedBefore,json);Assert.DoesNotContain(s.Token,json);Assert.DoesNotContain("SECRET",json);
        Assert.Empty(RaidRecapPanelTests.Flatten(shared.Components).OfType<SectionComponent>());
        Assert.DoesNotContain("## Review first",RaidRecapPanelTests.Text(shared));Assert.DoesNotContain("Matched pulls",RaidRecapPanelTests.Text(shared));
        Assert.All(RaidRecapPanelTests.Flatten(shared.Components).OfType<ButtonComponent>(),b=>Assert.Equal(ButtonStyle.Link,b.Style));
        Assert.Equal(5,Parts(s).OfType<ActionRowComponent>().Count());
        Assert.Equal(new[]{"Overview","Bosses","Damage","Healing","Analysis"},Parts(s).OfType<ActionRowComponent>().First().Components.Cast<ButtonComponent>().Select(b=>b.Label));
    }
    private static void Check(RaidRecapSession s)
    {
        var parts=Parts(s);Assert.InRange(parts.Length,1,40);Assert.InRange(Text(s).Length,1,4000);Assert.DoesNotContain("@everyone",Text(s));Assert.DoesNotContain('\u202e',Text(s));
        Assert.All(parts.OfType<ActionRowComponent>(),r=>Assert.InRange(r.Components.Count,1,5));
        Assert.All(parts.OfType<SelectMenuComponent>(),m=>{Assert.InRange(m.Options.Count,1,25);Assert.All(m.Options,o=>Assert.InRange(o.Label.Length,1,100));Assert.InRange(m.CustomId.Length,1,100);});
        Assert.All(parts.OfType<ButtonComponent>().Where(b=>b.Style!=ButtonStyle.Link),b=>Assert.InRange(b.CustomId.Length,1,100));
    }
    private static void Export(string name,RaidRecapSession s)
    {
        var path=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");if(string.IsNullOrEmpty(path))return;
        System.IO.Directory.CreateDirectory(path);var panel=RaidRecapView.Build(s);
        System.IO.File.WriteAllText(System.IO.Path.Combine(path,"synthetic-"+name+".json"),JsonConvert.SerializeObject(panel,Formatting.Indented));
        System.IO.File.WriteAllText(System.IO.Path.Combine(path,"synthetic-"+name+".md"),"# SYNTHETIC fixture — not live WCL or Discord data\n\n"+RaidRecapPanelTests.Text(panel));
    }
}
