using System;
using System.Text.RegularExpressions;
namespace NinjaBotCore.Modules.Wow;

/// <summary>General report-player handoff, not a certified event/view deep link.</summary>
public static class RaidRecapLinks
{
    public static string Player(RaidRecapReport report,RaidRecapFight fight,int actorId)
    {
        if(report==null || !Regex.IsMatch(report.Code??"",@"\A[A-Za-z0-9]{16}\z")
            || fight==null || fight.Id<=0 || actorId<=0 || !System.Linq.Enumerable.Contains(report.Fights,fight))
            throw new ArgumentException("Invalid report-player link scope.");
        return report.Url+"#fight="+fight.Id+"&source="+actorId;
    }
    public static string Name(RaidRecapReport report,RaidRecapFight fight,int actorId,string name,int limit=45)
        => $"[{RaidRecapRules.Text(name,limit)} · #{actorId}]({Player(report,fight,actorId)})";
}
