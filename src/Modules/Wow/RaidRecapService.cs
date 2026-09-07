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
        s.Performance=null; s.Report=report; s.View="overview"; s.BossIndex=s.BossPage=s.KillIndex=s.KillPage=s.RankPage=0;
    }
    public async Task ApplyAsync(RaidRecapSession s,string action,string value)
    {
        s.Notice=null;
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
            case "boss" when s.Report!=null: s.BossIndex=Index(value,s.BossPage,s.Report.Bosses.Count); break;
            case "bosses_prev" when s.Report!=null: s.BossPage=Page(s.BossPage-1,s.Report.Bosses.Count); s.BossIndex=s.BossPage*25; break;
            case "bosses_next" when s.Report!=null: s.BossPage=Page(s.BossPage+1,s.Report.Bosses.Count); s.BossIndex=s.BossPage*25; break;
            case "kill" when s.Report!=null: s.KillIndex=Index(value,s.KillPage,s.Report.Kills); s.RankPage=0; break;
            case "kills_prev" when s.Report!=null: s.KillPage=Page(s.KillPage-1,s.Report.Kills); s.KillIndex=s.KillPage*25; s.RankPage=0; break;
            case "kills_next" when s.Report!=null: s.KillPage=Page(s.KillPage+1,s.Report.Kills); s.KillIndex=s.KillPage*25; s.RankPage=0; break;
            case "ranks_prev": s.RankPage=Math.Max(0,s.RankPage-1); break;
            case "ranks_next": s.RankPage=Math.Min(Math.Max(0,((s.Performance?.Count??0)-1)/10),s.RankPage+1); break;
            default: throw new ArgumentException("Invalid or stale recap selection. Reopen /raid-recap.");
        }
        s.Performance=null;
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
