using System;
using System.Reflection;
using System.Linq;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

public class RaidRecapTests
{
    // Reflection lets the first RED execute before the new public contract exists.
    private static object Invoke(string method, params object[] args)
    {
        var type = typeof(WarcraftLogsV2Client).Assembly.GetType("NinjaBotCore.Modules.Wow.RaidRecapRules");
        Assert.NotNull(type);
        Assert.NotNull(type.GetMethod(method));
        try { return type.GetMethod(method).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    [Theory]
    [InlineData("AbCdEfGh12345678", "AbCdEfGh12345678")]
    [InlineData("https://www.warcraftlogs.com/reports/AbCdEfGh12345678#fight=2", "AbCdEfGh12345678")]
    public void ValidReportIdentityIsExtracted(string value, string expected) =>
        Assert.Equal(expected, Invoke("ReportCode", value));

    [Theory]
    [InlineData("https://warcraftlogs.com.evil.test/reports/AbCdEfGh12345678")]
    [InlineData("https://evil.test/AbCdEfGh12345678")]
    [InlineData("http://www.warcraftlogs.com/reports/AbCdEfGh12345678")]
    [InlineData("https://user@www.warcraftlogs.com/reports/AbCdEfGh12345678")]
    [InlineData("https://classic.warcraftlogs.com/reports/AbCdEfGh12345678")]
    [InlineData("../AbCdEfGh12345678")]
    [InlineData(" AbCdEfGh12345678")]
    [InlineData("AbCdEfGh12345678\n")]
    public void UnsafeIdentityIsRejected(string value) =>
        Assert.Throws<ArgumentException>(() => Invoke("ReportCode", value));

    [Fact]
    public void ReportSeparatesDifficultyTrashAndUnknowns()
    {
        // Deliberately synthetic GraphQL-shaped test data, not a captured WCL response.
        var raw = Newtonsoft.Json.Linq.JObject.Parse("""
        {"code":"AbCdEfGh12345678","title":"Progress","revision":3,"startTime":1000000,"endTime":2000000,
         "fights":[
          {"id":1,"encounterID":0,"difficulty":4,"kill":true},
          {"id":2,"encounterID":10,"name":"Boss","difficulty":4,"kill":false,"inProgress":false,"startTime":0,"endTime":60000,"bossPercentage":12.5},
          {"id":3,"encounterID":10,"name":"Boss","difficulty":4,"kill":false,"inProgress":false,"bossPercentage":null},
          {"id":4,"encounterID":10,"name":"Boss","difficulty":5,"kill":true,"inProgress":false,"startTime":100,"endTime":120100},
          {"id":5,"encounterID":20,"name":"Live","difficulty":5,"kill":false,"inProgress":true,"startTime":0,"endTime":20000}
         ]}
        """);
        var parsed = Newtonsoft.Json.Linq.JObject.FromObject(Invoke("ParseReport", raw, "AbCdEfGh12345678", DateTimeOffset.UnixEpoch));
        Assert.Equal(3, parsed["Bosses"].Count());
        Assert.Equal(1, (int)parsed["Kills"]);
        Assert.Equal(2, (int)parsed["Wipes"]);
        Assert.Equal(1, (int)parsed["Unfinished"]);
        Assert.Equal(12.5, (double)parsed["Bosses"][0]["BestRemaining"]);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, parsed["Fights"][1]["DurationMs"].Type);
        Assert.Equal(120000, (double)parsed["Bosses"][1]["FastestKillMs"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"code\":\"ZZZZZZZZ12345678\",\"fights\":[]}")]
    [InlineData("{\"code\":\"AbCdEfGh12345678\",\"fights\":null}")]
    public void ReportFailsClosedForMissingOrMismatchedData(string json) =>
        Assert.Throws<InvalidOperationException>(() => Invoke("ParseReport", Newtonsoft.Json.Linq.JObject.Parse(json), "AbCdEfGh12345678", DateTimeOffset.UnixEpoch));

    [Fact]
    public void WipeOnlyReportKeepsUnknownHealthAndDurationUnknown()
    {
        var raw = Newtonsoft.Json.Linq.JObject.Parse("""
        {"code":"AbCdEfGh12345678","revision":1,"fights":[{"id":2,"encounterID":10,"difficulty":4,"kill":false,"inProgress":false,"bossPercentage":-1,"startTime":50,"endTime":40}]}
        """);
        var parsed = Newtonsoft.Json.Linq.JObject.FromObject(Invoke("ParseReport", raw, "AbCdEfGh12345678", DateTimeOffset.UnixEpoch));
        Assert.Equal(0, (int)parsed["Kills"]);
        Assert.Equal(1, (int)parsed["Wipes"]);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, parsed["Bosses"][0]["BestRemaining"].Type);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, parsed["Bosses"][0]["FastestKillMs"].Type);
    }

    [Fact]
    public void TextIsBoundedAfterEscapingAndMentionsAreNeutralized()
    {
        var result = (string)Invoke("Text", "@everyone **[evil](https://x) <@123>\n" + new string('*', 1000), 100);
        Assert.InRange(result.Length, 1, 100);
        Assert.DoesNotContain("@everyone", result);
        Assert.DoesNotContain("<@123>", result);
        Assert.DoesNotContain("\n", result);
        Assert.Contains("\\*", result);
    }
}
