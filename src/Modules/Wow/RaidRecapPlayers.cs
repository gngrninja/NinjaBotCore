using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace NinjaBotCore.Modules.Wow;

public interface IRaidRecapPlayerSource
{
    Task<RaidRecapRoster> GetRaidRecapRosterAsync(RaidRecapReport report,RaidRecapFight fight,CancellationToken cancellationToken=default);
    Task<RaidRecapParses> GetRaidRecapParsesAsync(RaidRecapReport report,RaidRecapFight fight,bool healing,RaidRecapRoster roster,CancellationToken cancellationToken=default);
}
public sealed class RaidRecapSnapshotException : InvalidOperationException
{
    public RaidRecapSnapshotException():base("Report changed while loading. Refresh this recap before reviewing players.") { }
}
public sealed record RaidRecapPlayer(int ActorId,string Name,string Class,string Spec,string Role,string Realm,string Region,bool BridgeEligible);
public sealed record RaidRecapRoster(string SnapshotKey,int FightId,IReadOnlyList<RaidRecapPlayer> Players,bool Complete);
public sealed record RaidRecapParse(int ActorId,int CharacterId,double Percentile,int TotalParses,double? ItemLevelPercentile,int? Bracket,int? ItemLevel);
public sealed record RaidRecapParses(string SnapshotKey,int FightId,string Metric,string Compare,string Timeframe,int Partition,DateTimeOffset AsOf,IReadOnlyList<RaidRecapParse> Entries);
public sealed record RaidRecapParseBadge(uint Color,string Label,string Display)
{
    public bool Known => Label != "Unavailable";

    /// <summary>The coloured dot used beside parses on every WarcraftLogs card.</summary>
    public string Emoji => Label switch
    {
        "Gold" => "🟡",
        "Pink" => "🩷",
        "Orange" => "🟠",
        "Purple" => "🟣",
        "Blue" => "🔵",
        "Green" => "🟢",
        _ => "⚪"
    };
}

/// <summary>
/// The single WarcraftLogs parse palette. /char and /raid-recap both read it, so a
/// percentile always has the same colour, dot and tier everywhere.
/// </summary>
public static class RaidRecapParsePalette
{
    public static RaidRecapParseBadge Badge(double? value)
    {
        if(value is not >=0 or >100 || !double.IsFinite(value.Value))return new(0x7F8C8D,"Unavailable","—");
        var (color,label)=value==100?(0xe5cc80u,"Gold"):value>=99?(0xe268a8u,"Pink"):value>=95?(0xff8000u,"Orange"):
            value>=75?(0xa335eeu,"Purple"):value>=50?(0x0070ffu,"Blue"):value>=25?(0x1eff00u,"Green"):(0x666666u,"Gray");
        return new(color,label,Math.Floor(value.Value).ToString("0",CultureInfo.InvariantCulture));
    }
}

public static class RaidRecapPlayerRules
{
    internal static readonly string[] Roles={"tanks","healers","dps"};
    internal static JToken Field(JToken o,string name)=>(o as JObject)?[name];
    internal static string String(JToken v)=>v?.Type==JTokenType.String && !string.IsNullOrWhiteSpace((string)v) && ((string)v).Length<=200?(string)v:null;
    internal static int? Id(JToken v)=>RaidRecapAnalysisRules.Id(v);
    internal static double? Percent(JToken v)=>RaidRecapRules.Number(v) is double n && n>=0 && n<=100?n:null;
    internal static InvalidOperationException Unavailable()=>new("Player identity or parse scope is unavailable. Open WarcraftLogs; no zero is inferred.");
    internal static void Scope(RaidRecapReport r,RaidRecapFight f)
    {
        if(r?.Revision==null || r.EndTime==null || f==null || !r.Fights.Contains(f) || !(f.IsKill || f.IsWipe) || f.Id<=0 || f.EncounterId<=0
            || f.DurationMs is not >0 || !double.IsFinite(f.DurationMs.Value))throw Unavailable();
        RaidRecapRules.ReportCode(r.Code);
    }
    internal static JObject Snapshot(JObject data,RaidRecapReport r)
    {
        var current=Field(Field(data,"reportData"),"report") as JObject;
        if(current==null || String(current["code"])!=r.Code || Id(current["revision"])!=r.Revision || RaidRecapRules.Number(current["endTime"])!=r.EndTime)
            throw new RaidRecapSnapshotException();
        return current;
    }
    // Exact provider identifiers only. Unknown/new specs remain readable, without a role or parse claim.
    public static string Role(string c,string spec) => (c,spec) switch
    {
        ("DeathKnight","Blood") or ("DemonHunter","Vengeance") or ("Druid","Guardian") or ("Monk","Brewmaster") or ("Paladin","Protection") or ("Warrior","Protection")=>"tanks",
        ("Druid","Restoration") or ("Evoker","Preservation") or ("Monk","Mistweaver") or ("Paladin","Holy") or ("Priest","Holy" or "Discipline") or ("Shaman","Restoration")=>"healers",
        ("DeathKnight","Frost" or "Unholy") or ("DemonHunter","Havoc" or "Devourer") or ("Druid","Balance" or "Feral") or ("Evoker","Devastation" or "Augmentation")
        or ("Hunter","BeastMastery" or "Marksmanship" or "Survival") or ("Mage","Arcane" or "Fire" or "Frost") or ("Monk","Windwalker") or ("Paladin","Retribution")
        or ("Priest","Shadow") or ("Rogue","Assassination" or "Outlaw" or "Subtlety") or ("Shaman","Elemental" or "Enhancement")
        or ("Warlock","Affliction" or "Demonology" or "Destruction") or ("Warrior","Arms" or "Fury")=>"dps",
        _=>null
    };
    public static RaidRecapRoster Roster(JObject current,RaidRecapReport report,RaidRecapFight fight)
    {
        if(current?["fights"] is not JArray fights || fights.Count!=1 || fights[0] is not JObject f || Id(f["id"])!=fight.Id)throw Unavailable();
        if(Id(f["encounterID"])!=fight.EncounterId || Id(f["difficulty"])!=fight.Difficulty || f["kill"]?.Type!=JTokenType.Boolean || (bool)f["kill"]!=fight.Kill
            || f["inProgress"]?.Type!=JTokenType.Boolean || (bool)f["inProgress"]!=false || RaidRecapRules.Number(f["startTime"])!=fight.StartMs || RaidRecapRules.Number(f["endTime"])!=fight.EndMs)
            throw new RaidRecapSnapshotException();
        if(f["friendlyPlayers"] is not JArray roster || roster.Count>100 || Field(current["masterData"],"actors") is not JArray actors || actors.Count>2000)throw Unavailable();
        var ids=roster.Select(Id).ToArray();if(ids.Any(i=>i==null) || ids.Distinct().Count()!=ids.Length)throw Unavailable();
        var metadata=new Dictionary<int,JObject>();
        foreach(var token in actors)
        {
            if(token is not JObject a)throw Unavailable();
            if(a["id"]?.Type==JTokenType.Integer && a["id"].ToString()=="-1" && String(a["type"])=="NPC")continue;
            if(Id(a["id"]) is not int id || !metadata.TryAdd(id,a))throw Unavailable();
        }
        var specs=f["friendlySpecs"] as JArray;
        var details=Field(Field(current["details"],"data"),"playerDetails") as JObject;
        var detailRows=new List<(string Role,JObject Row)>();
        foreach(var role in Roles)
            if(details?[role] is JArray rows && rows.Count<=100)
                detailRows.AddRange(rows.OfType<JObject>().Select(row=>(role,row)));
        var players=new List<RaidRecapPlayer>();var complete=true;
        for(var i=0;i<ids.Length;i++)
        {
            var id=ids[i].Value;
            if(!metadata.TryGetValue(id,out var actor) || String(actor["type"])!="Player") {complete=false;continue;}
            var name=String(actor["name"]);var c=String(actor["subType"]);var spec=specs?.Count==ids.Length?String(specs[i]):null;
            var realm=String(actor["server"]);var role=Role(c,spec);
            var matches=detailRows.Where(d=>Id(d.Row["id"])==id).ToArray();string region=null;var eligible=false;
            if(matches.Length==1)
            {
                var d=matches[0];region=String(d.Row["region"]);
                eligible=name!=null && realm!=null && role!=null && d.Role==role && region is "US" or "EU" or "KR" or "TW" or "CN"
                    && String(d.Row["name"])==name && String(d.Row["type"])==c && String(d.Row["server"])==realm
                    && d.Row["specs"] is JArray ds && ds.Count==1 && String(Field(ds[0],"spec"))==spec;
            }
            if(name==null)complete=false;
            players.Add(new(id,name??"Unknown player",role==null?null:c,role==null?null:spec,eligible?role:null,realm,region,eligible));
        }
        return new(report.SnapshotKey,fight.Id,players.OrderBy(p=>p.Name,StringComparer.Ordinal).ThenBy(p=>p.ActorId).ToArray(),complete);
    }
    internal static (int Partition,List<(string Role,JObject Row)> Rows) Ranking(JObject ranking,RaidRecapFight fight)
    {
        if(ranking?["data"] is not JArray data || data.Count!=1 || data[0] is not JObject f || Id(f["fightID"])!=fight.Id
            || Id(Field(f["encounter"],"id"))!=fight.EncounterId || Id(f["difficulty"])!=fight.Difficulty || Id(f["kill"])!=1
            || RaidRecapRules.Number(f["duration"])!=fight.DurationMs || Id(f["partition"]) is not int partition || Id(f["zone"])==null
            || f["reportsBlacklistForCharacters"] is not JArray blacklist || blacklist.Count!=0 || f["roles"] is not JObject roles)throw Unavailable();
        var rows=new List<(string Role,JObject Row)>();
        foreach(var role in Roles)
        {
            if(Field(roles[role],"characters") is not JArray chars || chars.Count>100 || chars.Any(c=>c is not JObject))throw Unavailable();
            rows.AddRange(chars.Cast<JObject>().Select(c=>(role,c)));
        }
        if(rows.Count>100)throw Unavailable();return (partition,rows);
    }
    internal static IReadOnlyList<RaidRecapParse> Join(List<(string Role,JObject Row)> ranks,JObject servers,RaidRecapRoster roster)
    {
        var result=new List<RaidRecapParse>();
        var serverRows=servers?.Properties().Select(p=>p.Value).OfType<JObject>().ToArray()??Array.Empty<JObject>();
        foreach(var (role,row) in ranks)
        {
            if(row.Properties().Any(p=>!new[]{"id","name","server","class","spec","amount","bracketData","bracket","rank","best","totalParses","bracketPercent","rankPercent"}.Contains(p.Name,StringComparer.Ordinal)))continue;
            if(Id(row["id"]) is not int character || ranks.Count(r=>Id(r.Row["id"])==character)!=1 || Id(Field(row["server"],"id")) is not int serverId
                || Percent(row["rankPercent"]) is not double percent || Id(row["totalParses"]) is not int population)continue;
            var canonical=serverRows.Where(s=>Id(s["id"])==serverId).ToArray();if(canonical.Length!=1)continue;
            var server=canonical[0];var realm=String(server["normalizedName"]);var region=String(Field(server["region"],"slug"));
            if(realm==null || region==null || String(server["name"])!=String(Field(row["server"],"name")) || region!=String(Field(row["server"],"region")))continue;
            var matches=roster.Players.Where(p=>p.BridgeEligible && p.Name==String(row["name"]) && p.Realm==realm && p.Region==region
                && p.Class==String(row["class"]) && p.Spec==String(row["spec"]) && p.Role==role).ToArray();
            if(matches.Length!=1)continue;
            var bracket=Id(row["bracket"]);var ilvl=Id(row["bracketData"]);
            result.Add(new(matches[0].ActorId,character,percent,population,bracket!=null && ilvl!=null?Percent(row["bracketPercent"]):null,bracket,ilvl));
        }
        // Two global identities may not attach to the same local player, even if both match by context.
        return result.Where(p=>result.Count(r=>r.ActorId==p.ActorId)==1).OrderBy(p=>p.ActorId).ToArray();
    }
}
