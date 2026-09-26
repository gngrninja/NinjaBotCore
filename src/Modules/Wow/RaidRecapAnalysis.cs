using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Modules.Wow;

// Normalized, bounded observations only. Never retain raw events/tables in the cache.
public sealed record RaidRecapDeath(int ActorId, string Name, double ElapsedMs, string Ability);
public sealed record RaidRecapIncoming(string Name, string Source, double? Total);
public sealed record RaidRecapParticipant(int? ActorId, string Name, double? Count);
public sealed record RaidRecapUtility(string Name, double? Actions, double? CompletedCasts, double? Channels,
    IReadOnlyList<RaidRecapParticipant> Participants, bool AttributionKnown);
public sealed record RaidRecapAnalysis(string Metric, bool Complete, string Notice)
{
    public IReadOnlyList<RaidRecapDeath> Deaths { get; init; } = Array.Empty<RaidRecapDeath>();
    public IReadOnlyList<RaidRecapIncoming> Incoming { get; init; } = Array.Empty<RaidRecapIncoming>();
    public IReadOnlyList<RaidRecapUtility> Utility { get; init; } = Array.Empty<RaidRecapUtility>();
    public RaidRecapMechanic Mechanic { get; init; }
    public int DistinctPlayers => Deaths.Select(d => d.ActorId).Distinct().Count();
    // Utility attribution gets its own rows, so no participant is hidden behind a text cutoff.
    public int DisplayRows => RaidRecapMechanics.IsMetric(Metric) ? Mechanic?.Events.Count ?? 0
        : Metric == "deaths" ? Deaths.Count : Metric == "incoming" ? Incoming.Count
        : Utility.Sum(u => 1 + u.Participants.Count);
}

public static class RaidRecapAnalysisRules
{
    public const int MaxRows = 500;
    public const int MaxParticipants = 1000;
    public const int PageSize = 8;
    internal const string Partial = "Partial observations: some data, identity or attribution is unavailable, malformed, or exceeds the analysis limits. Open this fight on Warcraft Logs.";
    internal static string Name(JToken token, string fallback = "Unknown") => token?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token)
        ? string.Concat(((string)token).EnumerateRunes().Take(80).Select(r => r.ToString())) : fallback;
    internal static int? Id(JToken token) => token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out var id) && id > 0 ? id : null;
    private static double? Amount(JToken token) => RaidRecapRules.Number(token) is double n && n >= 0 ? n : null;
    private static double? Count(JToken token) => Amount(token) is double n && n <= 9007199254740991 && Math.Truncate(n) == n ? n : null;

    public static RaidRecapAnalysis Table(JObject table, string metric)
    {
        if (metric is not ("incoming" or "interrupts" or "dispels") || table?["data"]?["entries"] is not JArray entries)
            throw new InvalidOperationException("Analysis table shape is unavailable. Open this fight on Warcraft Logs.");
        var complete = true;
        if (metric == "incoming")
        {
            var rows = new List<RaidRecapIncoming>();
            if (entries.Count > MaxRows) complete = false;
            foreach (var entry in entries.Take(MaxRows))
            {
                if (entry is not JObject row) { complete = false; continue; }
                var total = Amount(row["total"]);
                if (total == null) complete = false;
                // Composite parent already represents subentries. Neither add them nor use totalReduced.
                rows.Add(new(Name(row["name"], "Unknown ability"), Name(row["actorName"], "Composite / unspecified source"), total));
            }
            return new(metric, complete, complete ? null : Partial) { Incoming = rows.OrderByDescending(r => r.Total).ToArray() };
        }

        var spells = new List<RaidRecapUtility>();
        var participantBudget = MaxParticipants;
        var spellRows = 0;
        if (entries.Count > 100) complete = false;
        foreach (var group in entries.Take(100))
        {
            if (group?["entries"] is not JArray children)
                throw new InvalidOperationException("Observed utility spell table is unavailable; unexpected table shape.");
            foreach (var child in children)
            {
                if (spellRows++ >= MaxRows) { complete = false; break; }
                if (child is not JObject row) { complete = false; continue; }
                var actions = Count(row["spellsInterrupted"]);
                var completed = Count(row["spellsCompleted"]);
                var channels = Count(row["spellChannelsInterrupted"]);
                if (actions == null || (metric == "interrupts" && (completed == null || channels == null))) complete = false;
                var people = new List<RaidRecapParticipant>();
                var attributed = row["details"] is JArray;
                var ids = new HashSet<int>();
                if (row["details"] is JArray details)
                {
                    if (details.Count > 100 || details.Count > participantBudget) attributed = false;
                    foreach (var detail in details.Take(Math.Min(100, participantBudget)))
                    {
                        participantBudget--;
                        if (detail is not JObject person) { attributed = false; continue; }
                        var id = Id(person["id"]);
                        if (id.HasValue && !ids.Add(id.Value)) { attributed = false; continue; }
                        var count = Count(person["total"]);
                        if (id == null || count == null || person["name"]?.Type != JTokenType.String) attributed = false;
                        // Owner attribution as returned. Never recurse through nested pet details.
                        people.Add(new(id, Name(person["name"], "Unassigned participant"), count));
                    }
                    if (actions == null || people.Sum(p => p.Count ?? 0) != actions) attributed = false;
                }
                if (!attributed) complete = false;
                spells.Add(new(Name(row["name"], "Unknown spell"), actions, completed, channels, people.ToArray(), attributed));
            }
        }
        return new(metric, complete, complete ? null : Partial) { Utility = spells.ToArray() };
    }
}

internal sealed class RaidRecapDeathCollector
{
    private readonly RaidRecapFight _fight;
    private readonly HashSet<int> _roster;
    private readonly Dictionary<int, string> _players = new();
    private readonly HashSet<int> _nonPlayers = new();
    private readonly List<RaidRecapDeath> _deaths = new();
    private bool _complete = true;
    private int _observedRows;
    private double _lastTimestamp;
    public RaidRecapDeathCollector(RaidRecapFight fight, JObject report)
    {
        _fight = fight; _lastTimestamp = fight.StartMs.Value;
        if (report?["fights"] is not JArray fights || fights.Count != 1 || RaidRecapAnalysisRules.Id(fights[0]?["id"]) != fight.Id
            || fights[0]?["friendlyPlayers"] is not JArray roster || roster.Count is 0 or > 100
            || report["masterData"]?["actors"] is not JArray actors || actors.Count > 2000)
            throw new InvalidOperationException("Player roster / identity is unavailable for this pull. Open Warcraft Logs.");
        _roster = new HashSet<int>();
        foreach (var item in roster)
        {
            if (RaidRecapAnalysisRules.Id(item) is not int id || !_roster.Add(id))
                throw new InvalidOperationException("Player roster is incomplete for this pull.");
        }
        var seen = new HashSet<int>();
        foreach (var actor in actors)
        {
            if (actor is not JObject a || RaidRecapAnalysisRules.Id(a["id"]) is not int id) continue;
            if (!seen.Add(id)) throw new InvalidOperationException("Ambiguous Warcraft Logs actor identity.");
            if ((string)a["type"] == "Player")
            {
                _players[id] = RaidRecapAnalysisRules.Name(a["name"], "Unknown player #" + id);
                if (a["name"]?.Type != JTokenType.String) _complete = false;
            }
            else if ((string)a["type"] is "Pet" or "NPC") _nonPlayers.Add(id);
        }
        if (_roster.Any(id => !_players.ContainsKey(id) && !_nonPlayers.Contains(id))) _complete = false;
    }
    public bool AddPage(JObject page, double start, out double? next)
    {
        next = null;
        if (page?["data"] is not JArray rows) { _complete = false; return false; }
        foreach (var token in rows)
        {
            if (_observedRows >= RaidRecapAnalysisRules.MaxRows) { _complete = false; return false; }
            _observedRows++;
            if (token is not JObject e || (string)e["type"] != "death" || RaidRecapAnalysisRules.Id(e["fight"]) != _fight.Id
                || RaidRecapAnalysisRules.Id(e["targetID"]) is not int target || RaidRecapRules.Number(e["timestamp"]) is not double time
                || time < start || time < _lastTimestamp || time > _fight.EndMs)
            { _complete = false; return false; }
            _lastTimestamp = time;
            if (_roster.Contains(target) && _players.TryGetValue(target, out var name))
                _deaths.Add(new(target, name, time - _fight.StartMs.Value, RaidRecapAnalysisRules.Name(e["killingAbility"]?["name"], "Unknown killing ability")));
        }
        var cursor = page.Property("nextPageTimestamp");
        if (cursor == null) { _complete = false; return false; }
        if (cursor.Value.Type == JTokenType.Null) return false;
        next = RaidRecapRules.Number(cursor.Value);
        // Do not add an epsilon or guess a continuation after unsupported/backward boundaries.
        if (next == null || next <= start || next <= _lastTimestamp || next > _fight.EndMs || _observedRows >= RaidRecapAnalysisRules.MaxRows)
        { _complete = false; return false; }
        return true;
    }
    public RaidRecapAnalysis Result(bool exhausted = false) => new("deaths", _complete && !exhausted,
        _complete && !exhausted ? null : RaidRecapAnalysisRules.Partial) { Deaths = _deaths.ToArray() };
}
