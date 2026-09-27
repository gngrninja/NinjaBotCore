using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace NinjaBotCore.Modules.Wow;

public partial class WarcraftLogsV2Client : IRaidRecapPlayerSource
{
    public async Task<RaidRecapRoster> GetRaidRecapRosterAsync(RaidRecapReport report,RaidRecapFight fight,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();RaidRecapPlayerRules.Scope(report,fight);
        const string query="""
            query RaidRecapPlayerRoster($code: String!, $fights: [Int]!, $start: Float!, $end: Float!) {
              reportData { report(code: $code) { code revision endTime
                fights(fightIDs: $fights) { id encounterID difficulty kill inProgress startTime endTime friendlyPlayers friendlySpecs }
                masterData { actors(type: "Player") { id name type subType server } }
                details: playerDetails(fightIDs: $fights, startTime: $start, endTime: $end, includeCombatantInfo: false)
              } }
            }
            """;
        var data=await PlayerQueryAsync(query,new {code=report.Code,fights=new[]{fight.Id},start=fight.StartMs.Value,end=fight.EndMs.Value},report,cancellationToken);
        return RaidRecapPlayerRules.Roster(RaidRecapPlayerRules.Snapshot(data,report),report,fight);
    }
    public async Task<RaidRecapParses> GetRaidRecapParsesAsync(RaidRecapReport report,RaidRecapFight fight,bool healing,RaidRecapRoster roster,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();RaidRecapPlayerRules.Scope(report,fight);
        if(!fight.IsKill || roster?.SnapshotKey!=report.SnapshotKey || roster.FightId!=fight.Id)throw RaidRecapPlayerRules.Unavailable();
        var metric=healing?"hps":"dps";
        var query=$$"""
            query RaidRecapPlayerParses($code: String!, $fights: [Int]!) {
              reportData { report(code: $code) { code revision endTime
                rankings(fightIDs: $fights, playerMetric: {{metric}}, compare: Parses, timeframe: Today)
              } }
            }
            """;
        var data=await PlayerQueryAsync(query,new {code=report.Code,fights=new[]{fight.Id}},report,cancellationToken);
        var current=RaidRecapPlayerRules.Snapshot(data,report);
        var (partition,rows)=RaidRecapPlayerRules.Ranking(current["rankings"] as JObject,fight);
        var ids=rows.Select(r=>RaidRecapPlayerRules.Id(RaidRecapPlayerRules.Field(r.Row["server"],"id"))).Where(i=>i.HasValue).Select(i=>i.Value).Distinct().OrderBy(i=>i).ToArray();
        // No server discovery/enumeration: only referenced IDs, one bounded metadata operation.
        if(ids.Length>25)throw RaidRecapPlayerRules.Unavailable();
        JObject servers=new();
        if(ids.Length>0)
        {
            var (serverQuery,variables)=ParseServerQuery(report.Code,ids);
            var world=await PlayerQueryAsync(serverQuery,variables,report,cancellationToken);
            RaidRecapPlayerRules.Snapshot(world,report); // a late realm lookup must not mask report drift
            servers=world["worldData"] as JObject??throw RaidRecapPlayerRules.Unavailable();
        }
        return new(report.SnapshotKey,fight.Id,metric,"Parses","Today",partition,DateTimeOffset.UtcNow,RaidRecapPlayerRules.Join(rows,servers,roster));
    }
    internal static (string Query,Dictionary<string,object> Variables) ParseServerQuery(string code,IReadOnlyList<int> ids)
    {
        if(ids.Count is <1 or >25 || ids.Any(id=>id<=0) || ids.Distinct().Count()!=ids.Count)throw RaidRecapPlayerRules.Unavailable();
        var variables=new Dictionary<string,object>{{"code",RaidRecapRules.ReportCode(code)}};
        for(var i=0;i<ids.Count;i++)variables.Add("server"+i,ids[i]);
        var declarations=string.Join(", ",ids.Select((_,i)=>"$server"+i+": Int!"));
        var aliases=string.Join("\n",ids.Select((_,i)=>$"s{i}: server(id: $server{i}) {{ id name normalizedName slug region {{ slug }} }}"));
        return ($"query RaidRecapParseServers($code: String!, {declarations}) {{ reportData {{ report(code: $code) {{ code revision endTime }} }} worldData {{ {aliases} }} }}",variables);
    }
    private async Task<JObject> PlayerQueryAsync(string query,object variables,RaidRecapReport snapshot,CancellationToken token)
    {
        var response=await ExecuteGraphQLAsync<JObject>(query,variables,cancellationToken:token);
        // Inspect any returned fingerprint before partial-error rejection: optional enrichment may
        // degrade, but a visible snapshot change may never degrade to apparently valid old output.
        if(response?.Data is not JObject data)throw RaidRecapPlayerRules.Unavailable();
        if(RaidRecapPlayerRules.Field(data["reportData"],"report") is not JObject)throw RaidRecapPlayerRules.Unavailable();
        RaidRecapPlayerRules.Snapshot(data,snapshot);
        if(response.Errors?.Count>0)throw RaidRecapPlayerRules.Unavailable();
        return data;
    }
}
