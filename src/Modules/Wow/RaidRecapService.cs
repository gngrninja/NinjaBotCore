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
public sealed class RaidRecapService
{
    private readonly IRaidRecapSource _source;
    private readonly RaidRecapCache _cache;
    public RaidRecapService(IRaidRecapSource source,RaidRecapCache cache) { _source=source; _cache=cache; }
    public async Task DiscoverAsync(RaidRecapSession s,string guild,string realm,string region)
    {
        var key="guild:"+Newtonsoft.Json.JsonConvert.SerializeObject(new[]{guild,realm,region});
        var reports=(IReadOnlyList<WclV2Report>)await _cache.GetAsync(key,async()=>await _source.GetRaidRecapReportsAsync(guild,realm,region),TimeSpan.FromSeconds(30));
        s.Reports=reports.GroupBy(r=>RaidRecapRules.ReportCode(r.Code)).Select(g=>g.First()).OrderByDescending(r=>r.StartTime).Take(100).ToArray();
        s.View="reports"; s.ReportPage=0;
    }
    public async Task OpenAsync(RaidRecapSession s,string code)
    {
        code=RaidRecapRules.ReportCode(code);
        var report=(RaidRecapReport)await _cache.GetAsync("report:"+code,async()=>await _source.GetRaidRecapReportAsync(code),TimeSpan.FromSeconds(30));
        s.Analysis=null; s.PullIndex=-1; s.PullPage=s.AnalysisPage=0; s.AnalysisMetric="deaths";
        s.Performance=null; s.Report=report; s.View="overview"; s.BossIndex=s.BossPage=s.AttemptPage=s.KillIndex=s.KillPage=s.RankPage=0;
    }
    public async Task ApplyAsync(RaidRecapSession s,string action,string value)
    {
        s.Notice=null;
        // Picker pagination and row pagination are metadata/local-only navigation.
        if (s.View=="analysis" && s.Report!=null)
        {
            if (action is "pulls_prev" or "pulls_next")
            { s.PullPage=Page(s.PullPage+(action=="pulls_next"?1:-1),s.Report.CompletedPulls.Count); return; }
            if (action is "analysis_prev" or "analysis_next")
            { s.AnalysisPage=Math.Clamp(s.AnalysisPage+(action=="analysis_next"?1:-1),0,Math.Max(0,((s.Analysis?.DisplayRows??0)-1)/RaidRecapAnalysisRules.PageSize)); return; }
        }
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
            case "deaths" or "incoming" or "interrupts" or "dispels" when s.View=="analysis" && s.Report!=null:
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
        s.Performance=null;
        if(s.View=="analysis" && s.Report?.CompletedPulls.Count>0)
        {
            var fight=s.Report.CompletedPulls[s.PullIndex];
            if(fight.DurationMs is not >0) { s.Notice="Unknown pull duration; scoped analysis is unavailable."; return; }
            var report=s.Report; var metric=s.AnalysisMetric;
            var key=$"analysis-v1:{report.SnapshotKey}:{fight.Id}:{fight.StartMs}:{fight.EndMs}:{metric}:limit100-pages5-rows500";
            s.Analysis=(RaidRecapAnalysis)await _cache.GetAsync(key,async()=>await _source.GetRaidRecapAnalysisAsync(report,fight,metric),TimeSpan.FromMinutes(2));
            return;
        }
        if(s.View is "damage" or "healing" && s.Report?.Kills>0)
        {
            var fight=s.Report.Fights.Where(f=>f.IsKill).ElementAt(s.KillIndex);
            if(fight.DurationMs is not >0) { s.Notice="Unknown kill duration; per-second performance cannot be calculated."; return; }
            var healing=s.View=="healing";
            var key=$"table:{s.Report.SnapshotKey}:{fight.Id}:{fight.StartMs}:{fight.EndMs}:{healing}";
            s.Performance=(IReadOnlyList<RaidRecapStanding>)await _cache.GetAsync(key,async()=>
                RaidRecapRules.Performance(await _source.GetRaidRecapScopedTableAsync(s.Report,fight,healing),fight.DurationMs.Value),TimeSpan.FromMinutes(2));
            s.RankPage=Math.Clamp(s.RankPage,0,Math.Max(0,(s.Performance.Count-1)/10));
        }
    }
    private static int Index(string input,int page,int count)
    {
        if(!int.TryParse(input,out var index)||index<page*25||index>=Math.Min(count,(page+1)*25))
            throw new ArgumentException("Invalid or stale recap selection. Reopen /raid-recap.");
        return index;
    }
    public static int Page(int page,int count)=>Math.Clamp(page,0,Math.Max(0,(count-1)/25));
}
