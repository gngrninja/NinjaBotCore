using System;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Modules.Wow;

public partial class WarcraftLogsV2Client : IRaidRecapSource
{
    public async Task<RaidRecapReport> GetRaidRecapReportAsync(string code)
    {
        code = RaidRecapRules.ReportCode(code);
        const string query = """
            query($code: String!) { reportData { report(code: $code) {
              code title revision startTime endTime
              fights { id encounterID name difficulty kill inProgress startTime endTime bossPercentage }
            } } }
            """;
        var raw = await RecapQueryAsync(query, new { code });
        return RaidRecapRules.ParseReport(raw["report"] as JObject, code, DateTimeOffset.UtcNow);
    }

    public async Task<JObject> GetRaidRecapTableAsync(string code, RaidRecapFight fight, bool healing)
    {
        code = RaidRecapRules.ReportCode(code);
        if (fight == null || !fight.IsKill || fight.EncounterId <= 0 || fight.Id <= 0 || fight.DurationMs is not > 0)
            throw new ArgumentException("Performance requires a completed boss kill with known duration.");
        var metric = healing ? "Healing" : "DamageDone";
        var query = $$"""
            query($code: String!, $fights: [Int]!, $start: Float!, $end: Float!) {
              reportData { report(code: $code) { code revision endTime
                table(dataType: {{metric}}, fightIDs: $fights, startTime: $start, endTime: $end, killType: Kills, viewBy: Source)
              } }
            }
            """;
        var data = await RecapQueryAsync(query, new { code, fights = new[] { fight.Id }, start = fight.StartMs.Value, end = fight.EndMs.Value });
        if (data["report"] is not JObject report || report.Value<string>("code") != code || report["table"] is not JObject)
            throw new InvalidOperationException("Performance table is unavailable.");
        return report;
    }

    public async Task<System.Collections.Generic.IReadOnlyList<NinjaBotCore.Models.Wow.WclV2Report>> GetRaidRecapReportsAsync(string guild, string realm, string region)
    {
        if (string.IsNullOrWhiteSpace(guild) || guild.Length > 100 || string.IsNullOrWhiteSpace(realm) || realm.Length > 100 || region is not ("us" or "eu" or "kr" or "tw" or "cn"))
            throw new ArgumentException("Specify realm, guild, region (us/eu/kr/tw/cn), or use /setguild first.");
        const string query = """
            query($guild: String!, $realm: String!, $region: String!) {
              reportData { reports(guildName: $guild, guildServerSlug: $realm, guildServerRegion: $region, limit: 100) {
                data { code title startTime endTime zone { id name } }
              } }
            }
            """;
        var data = await RecapQueryAsync(query, new { guild, realm, region });
        if (data["reports"]?["data"] is not JArray reports || reports.Count > 100)
            throw new InvalidOperationException("Recent reports are unavailable.");
        var parsed = reports.ToObject<System.Collections.Generic.List<NinjaBotCore.Models.Wow.WclV2Report>>();
        foreach (var report in parsed) RaidRecapRules.ReportCode(report.Code);
        return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderByDescending(parsed, r => r.StartTime));
    }

    public async Task<JObject> GetRaidRecapScopedTableAsync(RaidRecapReport report, RaidRecapFight fight, bool healing)
    {
        if (report?.Revision == null || report.EndTime == null || !System.Linq.Enumerable.Contains(report.Fights, fight))
            throw new InvalidOperationException("Report snapshot has incomplete scope metadata. Refresh or open WarcraftLogs.");
        var raw = await GetRaidRecapTableAsync(report.Code, fight, healing);
        if (RaidRecapRules.Number(raw["revision"]) != report.Revision || RaidRecapRules.Number(raw["endTime"]) != report.EndTime)
            throw new InvalidOperationException("Report changed while loading. Refresh this recap before viewing performance.");
        return (JObject)raw["table"];
    }

    public async Task<RaidRecapAnalysis> GetRaidRecapAnalysisAsync(RaidRecapReport report, RaidRecapFight fight, string metric, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RaidRecapMechanics.IsMetric(metric))
            return await GetRaidRecapMechanicAsync(report, fight, RaidRecapMechanics.Rule(metric), cancellationToken);
        if (report?.Revision == null || report.EndTime == null || fight == null || !report.Fights.Contains(fight)
            || !(fight.IsKill || fight.IsWipe) || fight.EncounterId <= 0 || fight.Id <= 0 || fight.DurationMs is not > 0
            || !double.IsFinite(fight.DurationMs.Value) || metric is not ("deaths" or "incoming" or "interrupts" or "dispels"))
            throw new ArgumentException("Analysis requires one completed boss pull with known duration and snapshot metadata.");
        var code = RaidRecapRules.ReportCode(report.Code);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if (metric != "deaths")
        {
            var type = metric == "incoming" ? "DamageTaken" : metric == "interrupts" ? "Interrupts" : "Dispels";
            var view = metric == "incoming" ? "Ability" : "Source";
            var query = $$"""
                query($code: String!, $fights: [Int]!, $start: Float!, $end: Float!) {
                  reportData { report(code: $code) { code revision endTime
                    table(dataType: {{type}}, fightIDs: $fights, startTime: $start, endTime: $end, killType: All, viewBy: {{view}})
                  } }
                }
                """;
            var data = await RecapQueryAsync(query, new { code, fights = new[] { fight.Id }, start = fight.StartMs.Value, end = fight.EndMs.Value }, deadline.Token);
            var current = AnalysisSnapshot(data, report);
            return RaidRecapAnalysisRules.Table(current["table"] as JObject, metric);
        }

        RaidRecapDeathCollector collector = null;
        var start = fight.StartMs.Value;
        for (var page = 0; page < 5; page++)
        {
            // Player identity/participation is only required for Deaths and is fetched lazily once.
            var roster = page == 0 ? "fights(fightIDs: $fights) { id friendlyPlayers } masterData { actors(type: \"Player\") { id name type } }" : "";
            var query = $$"""
                query($code: String!, $fights: [Int]!, $start: Float!, $end: Float!) {
                  reportData { report(code: $code) { code revision endTime
                    {{roster}}
                    events(dataType: Deaths, fightIDs: $fights, startTime: $start, endTime: $end, killType: All,
                      limit: 100, useActorIDs: true, useAbilityIDs: false) { data nextPageTimestamp }
                  } }
                }
                """;
            JObject data;
            try { data = await RecapQueryAsync(query, new { code, fights = new[] { fight.Id }, start, end = fight.EndMs.Value }, deadline.Token); }
            catch (Exception ex) when (collector != null && !cancellationToken.IsCancellationRequested
                && ex is InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException or Newtonsoft.Json.JsonException)
            { return collector.Result(exhausted: true); }
            // Drift is never a usable partial result: discard all pages and ask for a refresh.
            var current = AnalysisSnapshot(data, report);
            collector ??= new RaidRecapDeathCollector(fight, current);
            if (!collector.AddPage(current["events"] as JObject, start, out var next)) return collector.Result();
            start = next.Value; // exact provider cursor, original fight IDs and end remain unchanged
        }
        return collector.Result(exhausted: true);
    }

    private static JObject AnalysisSnapshot(JObject data, RaidRecapReport snapshot)
    {
        if (data?["report"] is not JObject current || current.Value<string>("code") != snapshot.Code
            || RaidRecapRules.Number(current["revision"]) != snapshot.Revision || RaidRecapRules.Number(current["endTime"]) != snapshot.EndTime)
            throw new InvalidOperationException("Report changed or is unavailable. Refresh this recap before viewing analysis.");
        return current;
    }

    private async Task<JObject> RecapQueryAsync(string query, object variables, CancellationToken cancellationToken = default)
    {
        // Deliberately retain the existing OAuth / rate-limit execution path. Unlike legacy
        // callers, recap treats partial GraphQL errors as unavailable, never as zero activity.
        var result = await ExecuteGraphQLAsync<JObject>(query, variables, cancellationToken: cancellationToken);
        if (result == null || result.Errors?.Count > 0 || result.Data?["reportData"] is not JObject data)
            throw new InvalidOperationException("WarcraftLogs data is unavailable or access was denied. Try again later.");
        return data;
    }
}
