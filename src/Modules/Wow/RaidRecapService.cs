using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Models.Wow;
namespace NinjaBotCore.Modules.Wow;

public interface IRaidRecapSource
{
    Task<IReadOnlyList<WclV2Report>> GetRaidRecapReportsAsync(string guild,string realm,string region);
    Task<RaidRecapReport> GetRaidRecapReportAsync(string code);
    Task<JObject> GetRaidRecapScopedTableAsync(RaidRecapReport report,RaidRecapFight fight,bool healing);
    Task<RaidRecapAnalysis> GetRaidRecapAnalysisAsync(RaidRecapReport report,RaidRecapFight fight,string metric,System.Threading.CancellationToken cancellationToken=default);
}

/// <summary>Only called while the owning session transition is locked, including its final render.</summary>
public sealed partial class RaidRecapService
{
    private readonly IRaidRecapSource _source;
    private readonly RaidRecapCache _cache;
    public RaidRecapService(IRaidRecapSource source,RaidRecapCache cache) { _source=source; _cache=cache; }
    public async Task DiscoverAsync(RaidRecapSession s,string guild,string realm,string region)
    {
        s.OutputHelp=false; s.ResetComparison();
        var key=GuildKey(guild,realm,region);
        var reports=(IReadOnlyList<WclV2Report>)await _cache.GetAsync(key,async()=>await _source.GetRaidRecapReportsAsync(guild,realm,region),TimeSpan.FromSeconds(30));
        s.Reports=reports.GroupBy(r=>RaidRecapRules.ReportCode(r.Code)).Select(g=>g.First()).OrderByDescending(r=>r.StartTime).Take(100).ToArray();
        s.View="reports"; s.ReportPage=0;
        s.GuildName=guild; s.GuildRegion=region;
    }
    public async Task OpenAsync(RaidRecapSession s,string code)
    {
        s.OutputHelp=false; s.ResetComparison(); s.Analysis=null; s.PlayerPanel=null; s.Performance=null; s.PerformanceParses=null; s.PerformanceNotice=null;
        code=RaidRecapRules.ReportCode(code);
        var report=(RaidRecapReport)await _cache.GetAsync(ReportKey(code),async()=>await _source.GetRaidRecapReportAsync(code),TimeSpan.FromSeconds(30));
        s.Analysis=null; s.PullIndex=-1; s.PullPage=s.AnalysisPage=0; s.AnalysisMetric="deaths";
        s.Performance=null; s.Report=report; s.View="overview"; s.BossIndex=s.BossPage=s.AttemptPage=s.KillIndex=s.KillPage=s.RankPage=0;
    }
    public async Task ApplyAsync(RaidRecapSession s,string action,string value)
    {
        // Purely local disclosure: retain warnings and every selection/result. Every report view
        // carries the "How to read" control; the report picker has none, so reject it there.
        if(action=="output_help")
        {
            if(s.Report==null || s.View=="reports")throw InvalidSelection();
            s.OutputHelp=!s.OutputHelp;return;
        }
        if(action is "report" or "reports" or "refresh" or "overview" or "bosses" or "damage" or "healing" or "analysis" or "players" or "kill")s.OutputHelp=false;
        s.Notice=null;
        if(await ApplyPlayersAsync(s,action,value))return;
        if(s.Report!=null && action is "bosses_prev" or "bosses_next" or "kills_prev" or "kills_next")
        {
            if(action.StartsWith("bosses_",StringComparison.Ordinal)) s.BossPage=Page(s.BossPage+(action.EndsWith("next",StringComparison.Ordinal)?1:-1),s.Report.Bosses.Count);
            else s.KillPage=Page(s.KillPage+(action.EndsWith("next",StringComparison.Ordinal)?1:-1),s.Report.Kills);
            return;
        }
        if (action.StartsWith("review_",StringComparison.Ordinal))
        {
            if (s.View!="overview" || s.Report==null || !int.TryParse(action[7..],out var cardIndex))
                throw InvalidSelection();
            var card=RaidRecapReview.Cards(s.Report).ElementAtOrDefault(cardIndex);
            if (card==null) throw InvalidSelection();
            s.ResetComparison(); s.Analysis=null; s.Performance=null; s.View="bosses";
            s.BossIndex=card.BossIndex; s.BossPage=s.BossIndex/25;
            s.AttemptPage=s.Report.Bosses[s.BossIndex].Chronological.ToList().IndexOf(card.A)/10;
            if(card.B!=null) StartComparison(s,card.A,card.B);
            return;
        }
        if (action=="compare" && s.View=="bosses" && s.Report!=null && !s.Comparing)
        {
            var pair=RaidRecapReview.Initial(s.Report.Bosses.ElementAtOrDefault(s.BossIndex));
            StartComparison(s,pair.A,pair.B); return;
        }
        if (s.View=="bosses" && s.Comparing)
        {
            if(action=="attempts") { s.ResetComparison(); return; }
            if(action.StartsWith("compare_",StringComparison.Ordinal))
            { await ApplyComparisonAsync(s,action,value); return; }
        }
        if(s.View is "damage" or "healing" && action is "ranks_prev" or "ranks_next")
        { s.RankPage=Math.Clamp(s.RankPage+(action=="ranks_next"?1:-1),0,RaidRecapPlayerPresentation.OutputPages(s).Count-1);return; }
        // Picker pagination and row pagination are metadata/local-only navigation.
        if (s.View=="analysis" && s.Report!=null)
        {
            if (action is "pulls_prev" or "pulls_next")
            { s.PullPage=Page(s.PullPage+(action=="pulls_next"?1:-1),s.Report.CompletedPulls.Count); return; }
            if (action is "analysis_prev" or "analysis_next")
            { s.AnalysisPage=Math.Clamp(s.AnalysisPage+(action=="analysis_next"?1:-1),0,Math.Max(0,((s.Analysis?.DisplayRows??0)-1)/RaidRecapAnalysisRules.PageSize)); return; }
        }
        s.ResetComparison();
        s.Analysis=null;
        switch(action)
        {
            case "report":
                var index=Index(value,s.ReportPage,s.Reports.Count);
                await OpenAsync(s,s.Reports[index].Code); break;
            case "reports": s.View="reports"; break;
            case "reports_prev": s.ReportPage=Page(s.ReportPage-1,s.Reports.Count); break;
            case "reports_next": s.ReportPage=Page(s.ReportPage+1,s.Reports.Count); break;
            case "refresh" when s.Report!=null: await OpenAsync(s,s.Report.Code); break;
            case "overview" or "bosses" or "damage" or "healing" when s.Report!=null: s.View=action; break;
            case "analysis" when s.Report!=null:
                if(s.PullIndex<0 || s.PullIndex>=s.Report.CompletedPulls.Count || s.View=="bosses")
                {
                    var boss=s.Report.Bosses.ElementAtOrDefault(s.BossIndex);
                    var preferred=s.Report.CompletedPulls.LastOrDefault(f=>f.EncounterId==boss?.EncounterId && f.Difficulty==boss.Difficulty)
                        ?? s.Report.CompletedPulls.FirstOrDefault();
                    s.PullIndex=preferred==null?-1:s.Report.CompletedPulls.ToList().IndexOf(preferred);
                    s.PullPage=Math.Max(0,s.PullIndex)/25;
                }
                s.View="analysis"; s.AnalysisPage=0; break;
            case "mechanics" when s.View=="analysis" && s.Report!=null:
                s.AnalysisMetric=RaidRecapMechanics.IsMetric(s.AnalysisMetric)?s.AnalysisMetric:RaidRecapMechanics.Junk;
                s.AnalysisPage=0; break;
            case "deaths" or "incoming" or "interrupts" or "dispels" or RaidRecapMechanics.Junk or RaidRecapMechanics.Spin when s.View=="analysis" && s.Report!=null:
                s.AnalysisMetric=action; s.AnalysisPage=0; break;
            case "pull" when s.View=="analysis" && s.Report!=null:
                s.PullIndex=Index(value,s.PullPage,s.Report.CompletedPulls.Count); s.AnalysisPage=0; break;
            case "boss" when s.Report!=null: s.BossIndex=Index(value,s.BossPage,s.Report.Bosses.Count); s.AttemptPage=0; break;
            case "attempts_prev" when s.Report?.Bosses.Count>0: s.AttemptPage=Math.Max(0,s.AttemptPage-1); break;
            case "attempts_next" when s.Report?.Bosses.Count>0: s.AttemptPage=Math.Min((s.Report.Bosses[s.BossIndex].Attempts.Count-1)/10,s.AttemptPage+1); break;
            case "bosses_prev" when s.Report!=null: s.BossPage=Page(s.BossPage-1,s.Report.Bosses.Count); s.BossIndex=s.BossPage*25; s.AttemptPage=0; break;
            case "bosses_next" when s.Report!=null: s.BossPage=Page(s.BossPage+1,s.Report.Bosses.Count); s.BossIndex=s.BossPage*25; s.AttemptPage=0; break;
            case "kill" when s.Report!=null: s.KillIndex=Index(value,s.KillPage,s.Report.Kills); s.RankPage=0; break;
            case "kills_prev" when s.Report!=null: s.KillPage=Page(s.KillPage-1,s.Report.Kills); s.KillIndex=s.KillPage*25; s.RankPage=0; break;
            case "kills_next" when s.Report!=null: s.KillPage=Page(s.KillPage+1,s.Report.Kills); s.KillIndex=s.KillPage*25; s.RankPage=0; break;
            case "ranks_prev": s.RankPage=Math.Max(0,s.RankPage-1); break;
            case "ranks_next": s.RankPage=Math.Min(Math.Max(0,((s.Performance?.Count??0)-1)/10),s.RankPage+1); break;
            default: throw new ArgumentException("Invalid or stale recap selection. Reopen /raid-recap.");
        }
        s.Performance=null; s.PerformanceParses=null; s.PerformanceNotice=null;
        if(s.View=="analysis" && s.Report?.CompletedPulls.Count>0)
        {
            var fight=s.Report.CompletedPulls[s.PullIndex];
            if(fight.DurationMs is not >0) { s.Notice="Unknown pull duration; scoped analysis is unavailable."; return; }
            s.Analysis=await LoadAnalysisAsync(s.Report,fight,s.AnalysisMetric);
            return;
        }
        if(s.View is "damage" or "healing" && s.Report?.Kills>0)
        {
            var fight=s.Report.Fights.Where(f=>f.IsKill).ElementAt(s.KillIndex);
            if(fight.DurationMs is not >0) { s.Notice="Unknown kill duration; per-second performance cannot be calculated."; return; }
            var healing=s.View=="healing";
            var output=await LoadOutputAsync(s.Report,fight,healing);
            s.Performance=output.Rows;s.PerformanceParses=output.Parses;s.PerformanceNotice=output.Notice;
            s.RankPage=Math.Clamp(s.RankPage,0,RaidRecapPlayerPresentation.OutputPages(s).Count-1);
        }
    }
    // The live card reads through the same caches as the private recap, so a viewer who opens
    // their recap right after a refresh costs no extra provider calls.
    public Task<RaidRecapAnalysis> GetDeathsAsync(RaidRecapReport report,RaidRecapFight fight)=>LoadAnalysisAsync(report,fight,"deaths");
    public Task<RaidRecapOutput> GetOutputAsync(RaidRecapReport report,RaidRecapFight fight,bool healing)=>LoadOutputAsync(report,fight,healing);
    public Task<RaidRecapRoster> GetRosterAsync(RaidRecapReport report,RaidRecapFight fight)=>LoadRosterAsync(report,fight);
    public static string GuildKey(string guild,string realm,string region)=>"guild:"+Newtonsoft.Json.JsonConvert.SerializeObject(new[]{guild,realm,region});
    public static string ReportKey(string code)=>"report:"+code;
    private static ArgumentException InvalidSelection()=>new("Invalid or stale recap selection. Reopen /raid-recap.");

    private static void StartComparison(RaidRecapSession s,RaidRecapFight a,RaidRecapFight b)
    {
        s.ResetComparison(); s.Comparing=true; s.CompareSnapshotKey=s.Report.SnapshotKey;
        s.Analysis=null; s.Performance=null;
        var candidates=RaidRecapReview.Candidates(s.Report.Bosses.ElementAtOrDefault(s.BossIndex)).ToList();
        s.CompareAIndex=candidates.IndexOf(a); s.CompareBIndex=candidates.IndexOf(b);
        s.CompareAPage=Math.Max(0,s.CompareAIndex)/25; s.CompareBPage=Math.Max(0,s.CompareBIndex)/25;
    }

    private async Task ApplyComparisonAsync(RaidRecapSession s,string action,string value)
    {
        var (a,b)=RaidRecapReview.Selection(s);
        if(a==null) { s.Comparison=null; throw InvalidSelection(); }
        var candidates=RaidRecapReview.Candidates(s.Report.Bosses[s.BossIndex]);
        // Paging changes only the visible options, never either selected fight or the fetched result.
        switch(action)
        {
            case "compare_a_prev": s.CompareAPage=Page(s.CompareAPage-1,candidates.Count); return;
            case "compare_a_next": s.CompareAPage=Page(s.CompareAPage+1,candidates.Count); return;
            case "compare_b_prev": s.CompareBPage=Page(s.CompareBPage-1,candidates.Count); return;
            case "compare_b_next": s.CompareBPage=Page(s.CompareBPage+1,candidates.Count); return;
        }
        if(action is "compare_losses" or "compare_return" or "compare_loss_prev" or "compare_loss_next")
        {
            var current=RaidRecapReview.Current(s);
            if(current==null || !current.DeathsA.Complete || !current.DeathsB.Complete)throw InvalidSelection();
            if(action=="compare_return")s.CompareLosses=false;
            else if(action=="compare_losses") {s.CompareLosses=true;s.CompareLossPage=0;}
            else s.CompareLossPage=Math.Clamp(s.CompareLossPage+(action=="compare_loss_next"?1:-1),0,RaidRecapPlayerPresentation.Pack(RaidRecapPlayerPresentation.FirstLossRows(s)).Count-1);
            return;
        }
        // Fail closed before validating or awaiting, including a failed second load or snapshot drift.
        s.Comparison=null;s.CompareLosses=false;s.CompareLossPage=0;
        if(action is "compare_a" or "compare_b")
        {
            var isA=action=="compare_a";
            var index=Index(value,isA?s.CompareAPage:s.CompareBPage,candidates.Count);
            if(index==(isA?s.CompareBIndex:s.CompareAIndex)) throw InvalidSelection();
            if(isA) s.CompareAIndex=index; else s.CompareBIndex=index;
            return;
        }
        if(action!="compare_deaths") throw InvalidSelection();
        var report=s.Report;
        // Use the same bounded, fingerprinted full-pull analyses as Analysis; never query a clipped range.
        var deathsA=await LoadAnalysisAsync(report,a,"deaths");
        var deathsB=await LoadAnalysisAsync(report,b,"deaths");
        if(deathsA?.Metric!="deaths" || deathsB?.Metric!="deaths")
            throw new InvalidOperationException("Death comparison is unavailable. Open the selected fights on WarcraftLogs.");
        if(s.Report!=report || RaidRecapReview.Selection(s)!=(a,b)) throw InvalidSelection();
        s.Comparison=new(report.SnapshotKey,a,b,deathsA,deathsB);
    }

    private async Task<RaidRecapAnalysis> LoadAnalysisAsync(RaidRecapReport report,RaidRecapFight fight,string metric)
    {
        var version=RaidRecapMechanics.IsMetric(metric)
            ?"mechanics:"+RaidRecapMechanics.Version+":full-pull-player-targets-actorids-abilityids-wipecutoff0-friendlies"
            :"analysis-v1";
        var key=$"{version}:{report.SnapshotKey}:{fight.Id}:{fight.StartMs}:{fight.EndMs}:{metric}:limit100-pages5-rows500";
        return (RaidRecapAnalysis)await _cache.GetAsync(key,async()=>
        {
            var analysis=await _source.GetRaidRecapAnalysisAsync(report,fight,metric);
            if(metric is "interrupts" or "dispels" && analysis.Utility.Any(u=>u.Participants.Count>0))
            {
                RaidRecapRoster roster=null;
                try { if(_source is IRaidRecapPlayerSource)roster=await LoadRosterAsync(report,fight); }
                catch(RaidRecapSnapshotException) {throw;}
                catch(OperationCanceledException) {throw;}
                catch(Exception ex) when(ex is InvalidOperationException or System.Net.Http.HttpRequestException) { }
                var utility=analysis.Utility.Select(u=>u with {Participants=u.Participants.Select(p=>p with {VerifiedPlayer=
                    p.ActorId is int id && roster?.Players.Any(a=>a.ActorId==id && RaidRecapAnalysisRules.Name(new JValue(a.Name))==p.Name)==true}).ToArray()}).ToArray();
                var complete=analysis.Complete && utility.All(u=>u.Participants.All(p=>p.VerifiedPlayer));
                analysis=analysis with {Utility=utility,Complete=complete,Notice=complete?null:RaidRecapAnalysisRules.Partial};
            }
            return analysis;
        },TimeSpan.FromMinutes(2));
    }

    private static int Index(string input,int page,int count)
    {
        if(!int.TryParse(input,out var index)||index<page*25||index>=Math.Min(count,(page+1)*25))
            throw new ArgumentException("Invalid or stale recap selection. Reopen /raid-recap.");
        return index;
    }
    public static int Page(int page,int count)=>Math.Clamp(page,0,Math.Max(0,(count-1)/25));
}
