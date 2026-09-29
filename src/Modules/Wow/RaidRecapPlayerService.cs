using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace NinjaBotCore.Modules.Wow;

public sealed class RaidRecapPlayerPanel
{
    public string SnapshotKey { get; init; }
    public RaidRecapFight Fight { get; set; }
    public RaidRecapRoster Roster { get; set; }
    public int? ActorId { get; set; }
    public int PullPage { get; set; }
    public int OptionPage { get; set; }
    public int EvidencePage { get; set; }
    public string Lens { get; set; }="summary";
    public RaidRecapOutput Damage { get; set; }
    public RaidRecapOutput Healing { get; set; }
    public Dictionary<string,RaidRecapAnalysis> Observations { get; }=new();
    public RaidRecapPlayer Selected=>Roster?.Players.SingleOrDefault(p=>p.ActorId==ActorId);
    public void Clear()
    { Fight=null;Roster=null;ActorId=null;OptionPage=EvidencePage=0;Lens="summary";Damage=Healing=null;Observations.Clear(); }
    public void InvalidateLens()
    {if(Lens=="damage")Damage=null;else if(Lens=="healing")Healing=null;else Observations.Remove(Lens);}
}
public sealed record RaidRecapOutput(IReadOnlyList<RaidRecapStanding> Rows,RaidRecapParses Parses,string Notice);

public sealed partial class RaidRecapService
{
    private async Task<RaidRecapRoster> LoadRosterAsync(RaidRecapReport report,RaidRecapFight fight)
    {
        if(_source is not IRaidRecapPlayerSource source)throw RaidRecapPlayerRules.Unavailable();
        var roster=(RaidRecapRoster)await _cache.GetAsync($"players-v1:{report.SnapshotKey}:{fight.Id}:{fight.StartMs}:{fight.EndMs}",
            async()=>await source.GetRaidRecapRosterAsync(report,fight),TimeSpan.FromMinutes(2));
        if(roster?.SnapshotKey!=report.SnapshotKey || roster.FightId!=fight.Id)throw new RaidRecapSnapshotException();
        return roster;
    }
    private async Task<IReadOnlyList<RaidRecapStanding>> LoadTableAsync(RaidRecapReport report,RaidRecapFight fight,bool healing)
    {
        var key=$"table:{report.SnapshotKey}:{fight.Id}:{fight.StartMs}:{fight.EndMs}:{healing}";
        return (IReadOnlyList<RaidRecapStanding>)await _cache.GetAsync(key,async()=>
            RaidRecapRules.Performance(await _source.GetRaidRecapScopedTableAsync(report,fight,healing),fight.DurationMs.Value),TimeSpan.FromMinutes(2));
    }
    private async Task<RaidRecapOutput> LoadOutputAsync(RaidRecapReport report,RaidRecapFight fight,bool healing)
    {
        var raw=await LoadTableAsync(report,fight,healing);
        var rows=raw;RaidRecapParses parses=null;
        try
        {
            if(_source is IRaidRecapPlayerSource source)
            {
                var roster=await LoadRosterAsync(report,fight);
                rows=raw.Select(row=>row with {Player=row.ActorId is int id && raw.Count(r=>r.ActorId==id)==1
                    ?roster.Players.SingleOrDefault(p=>p.ActorId==id && RaidRecapAnalysisRules.Name(new Newtonsoft.Json.Linq.JValue(p.Name))==row.Name):null}).ToArray();
                // The request uses the provider-selected partition; the immutable value retains its
                // actual partition. Today has its own TTL, independent of the report revision.
                var parseKey=$"parses-v1:{report.SnapshotKey}:{fight.Id}:{fight.StartMs}:{fight.EndMs}:{(healing?"hps":"dps")}:Parses:Today:provider-partition";
                parses=(RaidRecapParses)await _cache.GetAsync(parseKey,async()=>await source.GetRaidRecapParsesAsync(report,fight,healing,roster),TimeSpan.FromMinutes(2));
                if(parses?.SnapshotKey!=report.SnapshotKey || parses.FightId!=fight.Id || parses.Metric!=(healing?"hps":"dps") || parses.Compare!="Parses" || parses.Timeframe!="Today" || parses.Partition<=0)
                    throw new RaidRecapSnapshotException();
                rows=rows.Select(row=>row with {Parse=row.Player==null?null:parses.Entries.SingleOrDefault(p=>p.ActorId==row.Player.ActorId)}).ToArray();
            }
        }
        catch(RaidRecapSnapshotException) {throw;}
        // This optional service call has no caller token; a provider deadline may degrade
        // enrichment, not valid raw output. The direct source APIs still propagate caller cancellation.
        catch(Exception ex) when(ex is InvalidOperationException or System.Net.Http.HttpRequestException or Newtonsoft.Json.JsonException or OperationCanceledException or TimeoutException) { }
        return new(rows,parses,parses==null?"Parses are unavailable right now. Select this tab again to retry.":null);
    }
    private async Task OpenPlayerFightAsync(RaidRecapSession s,RaidRecapFight fight)
    {
        var p=s.PlayerPanel;p.Clear();p.Fight=fight;
        p.Roster=await LoadRosterAsync(s.Report,fight);
    }
    private async Task<bool> ApplyPlayersAsync(RaidRecapSession s,string action,string value)
    {
        if(action=="players" && s.Report!=null && !s.Comparing && s.View!="reports")
        {
            s.PlayerPanel=new(){SnapshotKey=s.Report.SnapshotKey};
            var fight=s.View is "damage" or "healing"?s.Report.Fights.Where(f=>f.IsKill).ElementAtOrDefault(s.KillIndex)
                :s.View=="analysis"?s.Report.CompletedPulls.ElementAtOrDefault(s.PullIndex):null;
            if(fight!=null)
            {
                await OpenPlayerFightAsync(s,fight);
                if(s.View=="damage" && s.Performance!=null)s.PlayerPanel.Damage=new(s.Performance,s.PerformanceParses,s.PerformanceNotice);
                if(s.View=="healing" && s.Performance!=null)s.PlayerPanel.Healing=new(s.Performance,s.PerformanceParses,s.PerformanceNotice);
                if(s.View=="analysis" && s.Analysis!=null)s.PlayerPanel.Observations[s.AnalysisMetric]=s.Analysis;
            }
            return true;
        }
        var p=s.PlayerPanel;
        if(p==null)return false;
        if(p.SnapshotKey!=s.Report?.SnapshotKey) {s.PlayerPanel=null;throw InvalidSelection();}
        if(action=="player_back") {s.PlayerPanel=null;return true;}
        if(action=="player_change") {p.Clear();return true;}
        if(action is "overview" or "bosses" or "damage" or "healing" or "analysis" or "reports" or "refresh" or "report")
        {s.PlayerPanel=null;return false;}
        if(action is "player_pull_prev" or "player_pull_next")
        {p.PullPage=Page(p.PullPage+(action.EndsWith("next",StringComparison.Ordinal)?1:-1),s.Report.CompletedPulls.Count);return true;}
        if(action=="player_pull" && p.Fight==null)
        {
            var id=SelectionId(value);
            var fight=s.Report.CompletedPulls.Skip(p.PullPage*25).Take(25).SingleOrDefault(f=>f.Id==id)??throw InvalidSelection();
            await OpenPlayerFightAsync(s,fight);return true;
        }
        if(action is "player_options_prev" or "player_options_next")
        {p.OptionPage=Page(p.OptionPage+(action.EndsWith("next",StringComparison.Ordinal)?1:-1),p.Roster?.Players.Count??0);return true;}
        if(action=="player")
        {
            var id=SelectionId(value);
            if(p.Roster?.Players.Skip(p.OptionPage*25).Take(25).Any(player=>player.ActorId==id)!=true)throw InvalidSelection();
            p.ActorId=id;p.EvidencePage=0;return true;
        }
        if(action is "player_rows_prev" or "player_rows_next")
        {p.EvidencePage=Math.Clamp(p.EvidencePage+(action.EndsWith("next",StringComparison.Ordinal)?1:-1),0,Math.Max(0,RaidRecapPlayerPresentation.Pages(s).Count-1));return true;}
        if(action=="player_lens" && p.Fight!=null && p.Selected!=null)
        {
            if(value is not ("summary" or "damage" or "healing" or "deaths" or "interrupts" or "dispels" or RaidRecapMechanics.Junk or RaidRecapMechanics.Spin))throw InvalidSelection();
            p.Lens=value;p.EvidencePage=0;
            if(value=="summary")return true;
            p.InvalidateLens();
            if(value is "damage" or "healing")
            {
                if(!p.Fight.IsKill) {s.Notice="Damage / Healing parses require a completed kill; no wipe parse is inferred.";return true;}
                var output=await LoadOutputAsync(s.Report,p.Fight,value=="healing");
                if(value=="healing")p.Healing=output;else p.Damage=output;
            }
            else p.Observations[value]=await LoadAnalysisAsync(s.Report,p.Fight,value);
            return true;
        }
        throw InvalidSelection();
    }
    private static int SelectionId(string value)=>int.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var id) && id>0?id:throw InvalidSelection();
}
