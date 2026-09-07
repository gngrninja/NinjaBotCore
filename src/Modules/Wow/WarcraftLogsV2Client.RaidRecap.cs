using System;
using System.Threading.Tasks;
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
            throw new InvalidOperationException("Report snapshot has incomplete scope metadata. Refresh or open Warcraft Logs.");
        var raw = await GetRaidRecapTableAsync(report.Code, fight, healing);
        if (RaidRecapRules.Number(raw["revision"]) != report.Revision || RaidRecapRules.Number(raw["endTime"]) != report.EndTime)
            throw new InvalidOperationException("Report changed while loading. Refresh this recap before viewing performance.");
        return (JObject)raw["table"];
    }

    private async Task<JObject> RecapQueryAsync(string query, object variables)
    {
        // Deliberately retain the existing OAuth / rate-limit execution path. Unlike legacy
        // callers, recap treats partial GraphQL errors as unavailable, never as zero activity.
        var result = await ExecuteGraphQLAsync<JObject>(query, variables);
        if (result == null || result.Errors?.Count > 0 || result.Data?["reportData"] is not JObject data)
            throw new InvalidOperationException("Warcraft Logs data is unavailable or access was denied. Try again later.");
        return data;
    }
}
