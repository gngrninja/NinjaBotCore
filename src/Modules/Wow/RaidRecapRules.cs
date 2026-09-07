using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Modules.Wow;

public static class RaidRecapRules
{
    public static string ReportCode(string input)
    {
        if (input != null && Regex.IsMatch(input, @"\A[A-Za-z0-9]{16}\z")) return input;
        // Match the raw form before Uri normalization (no credentials, ports, paths or whitespace repair).
        var match = Regex.Match(input ?? "", @"\Ahttps://(?:www\.)?warcraftlogs\.com/reports/([A-Za-z0-9]{16})/?(?:[?#][^\s\\]*)?\z", RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("Use a retail Warcraft Logs HTTPS report URL or its exact 16-character code.");
        return match.Groups[1].Value;
    }

    public static RaidRecapReport ParseReport(JObject raw, string code, DateTimeOffset asOf)
    {
        code = ReportCode(code);
        if (raw?.Value<string>("code") != code || raw["fights"] is not JArray fights || fights.Count > 10000)
            throw new InvalidOperationException("Report unavailable or incomplete. It may be private or deleted.");
        var parsed = fights.Select(f =>
        {
            if (f is not JObject o || Integer(o["id"]) is not int id || id <= 0 || Integer(o["encounterID"]) is not int encounter || encounter < 0)
                throw new InvalidOperationException("Incomplete Warcraft Logs fight data.");
            var health = Number(o["bossPercentage"]);
            if (health < 0 || health > 100) health = null;
            return new RaidRecapFight(id, encounter, Integer(o["difficulty"]), o.Value<string>("name") ?? "Unknown encounter",
                o["kill"]?.Type == JTokenType.Boolean ? (bool?)o["kill"] : null,
                o["inProgress"]?.Type == JTokenType.Boolean ? (bool?)o["inProgress"] : null,
                Number(o["startTime"]), Number(o["endTime"]), health);
        }).ToArray();
        if (parsed.Select(f => f.Id).Distinct().Count() != parsed.Length)
            throw new InvalidOperationException("Duplicate Warcraft Logs fight identity.");
        return new RaidRecapReport(code, raw.Value<string>("title") ?? "Raid report", Integer(raw["revision"]),
            Number(raw["startTime"]), Number(raw["endTime"]), asOf, parsed.Where(f => f.EncounterId > 0).ToArray());
    }

    public static double? Number(JToken value) => value?.Type is JTokenType.Integer or JTokenType.Float
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    private static int? Integer(JToken value) => value?.Type == JTokenType.Integer && int.TryParse(value.ToString(), out var n) ? n : null;

    public static System.Collections.Generic.IReadOnlyList<RaidRecapStanding> Performance(JObject raw,double durationMs)
    {
        if(raw?["data"] is not JObject data || data["entries"] is not JArray entries || entries.Count>1000 || !double.IsFinite(durationMs) || durationMs<=0)
            throw new InvalidOperationException("Warcraft Logs table shape or fight duration is unavailable. Open the report for details.");
        return entries.Select(e=>
        {
            if(e is not JObject row) throw new InvalidOperationException("Warcraft Logs source table is incomplete.");
            var total=Number(row["total"]);
            if(total<0) total=null;
            var rate=total/(durationMs/1000);
            if(rate.HasValue&&!double.IsFinite(rate.Value)) rate=null;
            return new RaidRecapStanding(row.Value<string>("name")??"Unknown source",total,rate);
        }).OrderByDescending(r=>r.PerSecond).ToArray();
    }

    public static string Text(string input, int limit)
    {
        var result = new StringBuilder();
        foreach (var rune in (input ?? "Unknown").EnumerateRunes())
        {
            var value = rune.ToString();
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) value = " ";
            else if (value == "@") value = "＠";
            else if (value == "<") value = "‹";
            else if (value == ">") value = "›";
            else if ("\\`*_{}[]()#+-.!|~".Contains(value, StringComparison.Ordinal)) value = "\\" + value;
            if (result.Length + value.Length > limit) break;
            result.Append(value);
        }
        return result.Length == 0 ? "Unknown" : result.ToString();
    }
}
