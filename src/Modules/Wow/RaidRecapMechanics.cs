using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Modules.Wow;

// Numeric identities, never translated ability names. This observational pack has no opportunity model.
public sealed record RaidRecapMechanicRule(string Metric, string Label, int SpellId, string DataType,
    string EventType, string CountDefinition, string Question)
{
    public string View => DataType == "DamageTaken" ? "damage-taken" : "auras&spells=debuffs";
}

public static class RaidRecapMechanics
{
    public const string Version = "lost-explorers-3497-heroic-observed-events-v1";
    public const string Junk = "throw-junk-damage-events";
    public const string Spin = "shell-spin-debuff-applications";
    public static RaidRecapMechanicRule Rule(string metric) => metric switch
    {
        Junk => new(Junk, "Throw Junk damage events", 1291935, "DamageTaken", "damage",
            "One qualifying damage event row per selected Player target, including zero-amount absorbed rows. Repeated rows are retained; not unique incidents.",
            "What was happening around these damage events and absorbs?"),
        Spin => new(Spin, "Shell Spin debuff applications", 1291918, "Debuffs", "applydebuff",
            "One qualifying applydebuff row per selected Player target. Removals, refreshes and stack updates are not applications. Repeated rows are retained.",
            "What was happening around these applications?"),
        _ => null
    };
    public static bool IsMetric(string metric) => metric is Junk or Spin;
    internal static bool LocalScope(RaidRecapFight fight) => fight.EncounterId == 3497 && fight.Difficulty == 4
        && fight.Kill.HasValue && fight.InProgress == false;
    internal static string String(JToken token) => token?.Type == JTokenType.String ? (string)token : null;
    internal static bool? Boolean(JToken token) => token?.Type == JTokenType.Boolean ? (bool)token : null;
    // WCL's observed Environment actor has ID -1 and type NPC, not a positive player identity.
    internal static int? ActorId(JToken token) => token?.Type == JTokenType.Integer && token.ToString() == "-1"
        ? -1 : RaidRecapAnalysisRules.Id(token);
    internal static InvalidOperationException Unavailable() => new("Mechanic scope, roster or event data is unavailable. Refresh or open this fight on Warcraft Logs.");
}

// Cached data is normalized and bounded; no raw JSON or report-wide catalogs are retained.
public sealed record RaidRecapMechanicEvent(int ActorId, string Name, double ElapsedMs, double? Amount, double? Absorbed);
public sealed record RaidRecapMechanic(string SnapshotKey, int FightId, double StartMs, double EndMs, string Coverage,
    IReadOnlyList<RaidRecapMechanicEvent> Events)
{
    public int DistinctPlayers => Events.Select(e => e.ActorId).Distinct().Count();
}

internal sealed class RaidRecapMechanicCollector
{
    private readonly RaidRecapReport _snapshot;
    private readonly RaidRecapFight _fight;
    private readonly RaidRecapMechanicRule _rule;
    private readonly HashSet<int> _roster = new();
    private readonly Dictionary<int, string> _players = new();
    private readonly HashSet<int> _nonPlayers = new();
    private readonly List<RaidRecapMechanicEvent> _events = new();
    private bool _complete = true;
    private int _rawRows;
    private double _lastTime;

    // Metadata query requests one fight. Accept bounded additional fights in provider specimens,
    // but require unique numeric IDs and exactly one selected fight; never use an arbitrary first row.
    internal static JObject SelectedFight(JObject report, RaidRecapFight fight)
    {
        if (report["fights"] is not JArray fights || fights.Count is 0 or > 100)
            throw RaidRecapMechanics.Unavailable();
        var ids = new HashSet<int>();
        JObject selected = null;
        foreach (var token in fights)
        {
            if (token is not JObject row || RaidRecapAnalysisRules.Id(row["id"]) is not int id || !ids.Add(id))
                throw RaidRecapMechanics.Unavailable();
            if (id == fight.Id) selected = row;
        }
        if (selected == null || RaidRecapRules.Number(selected["startTime"]) != fight.StartMs
            || RaidRecapRules.Number(selected["endTime"]) != fight.EndMs)
            throw RaidRecapMechanics.Unavailable();
        return selected;
    }

    internal static bool Supported(JObject report, JObject fight) =>
        RaidRecapAnalysisRules.Id(report["zone"] is JObject zone ? zone["id"] : null) == 53
        && RaidRecapAnalysisRules.Id((report["zone"] as JObject)?["expansion"] is JObject expansion ? expansion["id"] : null) == 7
        && RaidRecapAnalysisRules.Id((report["masterData"] as JObject)?["gameVersion"]) == 1
        && RaidRecapAnalysisRules.Id(fight["encounterID"]) == 3497
        && RaidRecapAnalysisRules.Id(fight["difficulty"]) == 4
        && RaidRecapAnalysisRules.Id((fight["gameZone"] as JObject)?["id"]) == 3004
        && RaidRecapMechanics.Boolean(fight["kill"]).HasValue
        && RaidRecapMechanics.Boolean(fight["inProgress"]) == false
        && RaidRecapMechanics.Boolean(fight["completeRaid"]) == false;

    public RaidRecapMechanicCollector(RaidRecapReport snapshot, RaidRecapFight fight, RaidRecapMechanicRule rule,
        JObject report, JObject selected)
    {
        _snapshot = snapshot; _fight = fight; _rule = rule; _lastTime = fight.StartMs.Value;
        if (selected["friendlyPlayers"] is not JArray roster || roster.Count is 0 or > 100
            || (report["masterData"] as JObject)?["actors"] is not JArray actors || actors.Count > 2000)
            throw RaidRecapMechanics.Unavailable();
        foreach (var item in roster)
            if (RaidRecapAnalysisRules.Id(item) is not int id || !_roster.Add(id))
                throw RaidRecapMechanics.Unavailable();
        var seen = new HashSet<int>();
        foreach (var token in actors)
        {
            if (token is not JObject actor || RaidRecapMechanics.ActorId(actor["id"]) is not int id || !seen.Add(id))
                throw RaidRecapMechanics.Unavailable();
            var type = RaidRecapMechanics.String(actor["type"]);
            if (id == -1 && type != "NPC") throw RaidRecapMechanics.Unavailable();
            if (type == "Player")
            {
                _players[id] = RaidRecapAnalysisRules.Name(actor["name"], "Unknown player #" + id);
                if (string.IsNullOrWhiteSpace(RaidRecapMechanics.String(actor["name"]))) _complete = false;
            }
            else if (type is "Pet" or "NPC") _nonPlayers.Add(id);
        }
        if (_roster.Any(id => !_players.ContainsKey(id))) _complete = false;
    }

    public bool AddPage(JObject page, double start, out double? next)
    {
        next = null;
        if (page?["data"] is not JArray rows) throw RaidRecapMechanics.Unavailable();
        foreach (var token in rows)
        {
            if (_rawRows >= RaidRecapAnalysisRules.MaxRows) { _complete = false; return false; }
            _rawRows++;
            if (token is not JObject row || RaidRecapAnalysisRules.Id(row["fight"]) != _fight.Id
                || RaidRecapAnalysisRules.Id(row["abilityGameID"]) != _rule.SpellId
                || RaidRecapRules.Number(row["timestamp"]) is not double time
                || time < start || time < _lastTime || time > _fight.EndMs)
            { _complete = false; return false; }
            _lastTime = time;
            var type = RaidRecapMechanics.String(row["type"]);
            if (type != _rule.EventType)
            {
                if (_rule.Metric == RaidRecapMechanics.Spin && type is "removedebuff" or "refreshdebuff" or "applydebuffstack" or "removedebuffstack" or "damage" or "cast" or "summon") continue;
                _complete = false; return false;
            }
            if (RaidRecapMechanics.ActorId(row["targetID"]) is not int target) { _complete = false; continue; }
            if (_nonPlayers.Contains(target)) continue;
            if (!_roster.Contains(target) || !_players.TryGetValue(target, out var name)) { _complete = false; continue; }
            // Amount is optional context, never a condition for counting a qualifying event row.
            _events.Add(new(target, name, time - _fight.StartMs.Value, Amount(row["amount"]), Amount(row["absorbed"])));
        }
        var cursor = page.Property("nextPageTimestamp");
        if (cursor == null) { _complete = false; return false; }
        if (cursor.Value.Type == JTokenType.Null) return false;
        next = RaidRecapRules.Number(cursor.Value);
        // Equality with the final row has no verified tie-boundary continuation. Do not guess an epsilon.
        if (next == null || next <= start || next <= _lastTime || next > _fight.EndMs || _rawRows >= RaidRecapAnalysisRules.MaxRows)
        { _complete = false; return false; }
        return true;
    }
    private static double? Amount(JToken token) => RaidRecapRules.Number(token) is double n && n >= 0 ? n : null;
    public RaidRecapAnalysis Result(bool exhausted = false) => Result(_snapshot, _fight, _rule,
        _complete && !exhausted ? "complete" : "partial", _events.ToArray());
    internal static RaidRecapAnalysis Result(RaidRecapReport snapshot, RaidRecapFight fight, RaidRecapMechanicRule rule,
        string coverage, IReadOnlyList<RaidRecapMechanicEvent> events) => new(rule.Metric, coverage == "complete",
            coverage == "complete" ? null : coverage == "unsupported"
                ? "Unsupported mechanic scope. This pack requires the verified Lost Explorers Heroic game / zone combination and a completed individual pull. Unknown scope is not zero."
                : "Partial observations: at least these event rows; scope, identity, pagination or fetch limits prevent a complete total.")
        { Mechanic = new(snapshot.SnapshotKey, fight.Id, fight.StartMs.Value, fight.EndMs.Value, coverage, events) };
}
