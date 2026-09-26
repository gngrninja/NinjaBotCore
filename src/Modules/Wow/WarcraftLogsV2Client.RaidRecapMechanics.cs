using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Modules.Wow;

public partial class WarcraftLogsV2Client
{
    // Deliberately smaller than the exploratory metadata document: no report-wide ability catalog.
    // Exact final documents require their own provider acceptance, separate from specimen replay.
    internal const string RecapMechanicMetadataQuery = """
        query MechanicsMetadata($code: String!, $fights: [Int]!) {
          reportData { report(code: $code) {
            code revision endTime archiveStatus { isArchived isAccessible }
            zone { id expansion { id } }
            fights(fightIDs: $fights, killType: All, translate: true) {
              id encounterID difficulty kill inProgress completeRaid
              startTime endTime friendlyPlayers gameZone { id }
            }
            masterData(translate: true) { gameVersion actors { id name type } }
          } }
        }
        """;
    internal const string RecapMechanicEventsQuery = """
        query MechanicsEvents($code: String!, $fights: [Int]!, $start: Float!, $end: Float!, $ability: Float!, $eventType: EventDataType!) {
          reportData { report(code: $code) {
            code revision endTime
            events(dataType: $eventType, abilityID: $ability, fightIDs: $fights,
              startTime: $start, endTime: $end, killType: All, hostilityType: Friendlies,
              wipeCutoff: 0, limit: 100, useActorIDs: true, useAbilityIDs: true,
              includeResources: false, translate: true) { data nextPageTimestamp }
          } }
        }
        """;

    private async Task<RaidRecapAnalysis> GetRaidRecapMechanicAsync(RaidRecapReport report, RaidRecapFight fight,
        RaidRecapMechanicRule rule, CancellationToken cancellationToken)
    {
        if (report?.Revision == null || report.EndTime is not double endTime || !double.IsFinite(endTime)
            || fight == null || !report.Fights.Contains(fight) || fight.Id <= 0
            || fight.StartMs is not > 0 || !double.IsFinite(fight.StartMs.Value)
            || fight.EndMs is not double end || !double.IsFinite(end) || end <= fight.StartMs)
            throw RaidRecapMechanics.Unavailable();
        var code = RaidRecapRules.ReportCode(report.Code);
        if (!RaidRecapMechanics.LocalScope(fight))
            return RaidRecapMechanicCollector.Result(report, fight, rule, "unsupported", Array.Empty<RaidRecapMechanicEvent>());
        // One budget covers OAuth, quota, metadata, every page and decoded response reads.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var metadata = MechanicSnapshot(await RecapQueryAsync(RecapMechanicMetadataQuery,
            new { code, fights = new[] { fight.Id } }, deadline.Token), report);
        if (metadata["archiveStatus"] is not JObject archive
            || RaidRecapMechanics.Boolean(archive["isAccessible"]) != true
            || RaidRecapMechanics.Boolean(archive["isArchived"]) == null)
            throw RaidRecapMechanics.Unavailable();
        var selected = RaidRecapMechanicCollector.SelectedFight(metadata, fight);
        if (!RaidRecapMechanicCollector.Supported(metadata, selected))
            return RaidRecapMechanicCollector.Result(report, fight, rule, "unsupported", Array.Empty<RaidRecapMechanicEvent>());
        if (RaidRecapMechanics.Boolean(selected["kill"]) != fight.Kill)
            throw RaidRecapMechanics.Unavailable();
        var collector = new RaidRecapMechanicCollector(report, fight, rule, metadata, selected);
        var start = fight.StartMs.Value;
        for (var page = 0; page < 5; page++)
        {
            JObject data;
            try
            {
                data = await RecapQueryAsync(RecapMechanicEventsQuery, new
                {
                    code, fights = new[] { fight.Id }, start, end,
                    ability = rule.SpellId, eventType = rule.DataType
                }, deadline.Token);
            }
            catch (Exception ex) when (page > 0 && !cancellationToken.IsCancellationRequested
                && ex is InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException or Newtonsoft.Json.JsonException)
            { return collector.Result(exhausted: true); }
            // Snapshot drift, including a missing report, is never a mixed-snapshot partial.
            var current = MechanicSnapshot(data, report);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!collector.AddPage(current["events"] as JObject, start, out var next)) return collector.Result();
                start = next.Value; // exact provider cursor; never use last event + epsilon
            }
            catch (InvalidOperationException) when (page > 0) { return collector.Result(exhausted: true); }
        }
        return collector.Result(exhausted: true);
    }

    private static JObject MechanicSnapshot(JObject data, RaidRecapReport snapshot)
    {
        if (data?["report"] is not JObject current || RaidRecapMechanics.String(current["code"]) != snapshot.Code
            || RaidRecapRules.Number(current["revision"]) != snapshot.Revision
            || RaidRecapRules.Number(current["endTime"]) != snapshot.EndTime)
            throw new InvalidOperationException("Report changed or is unavailable. Refresh before viewing mechanics.");
        return current;
    }
}
