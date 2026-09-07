using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public static class RaidRecapView
{
    public static MessageComponent Build(RaidRecapSession s,bool shared=false)
    {
        var c=new ContainerBuilder().WithAccentColor(new Color(88,101,242));
        var controls=new ComponentBuilder();
        var report=s.Report;
        var view=shared?"overview":s.View;
        if(report==null||view=="reports")
        {
            c.AddComponent(new TextDisplayBuilder("# Raid recap\nChoose a recent report"));
            if(!string.IsNullOrEmpty(s.Notice)&&!shared) c.AddComponent(new TextDisplayBuilder(RaidRecapRules.Text(s.Notice,300)));
            var page=RaidRecapService.Page(s.ReportPage,s.Reports.Count);
            c.AddComponent(new TextDisplayBuilder(s.Reports.Count==0
                ?"No reports found for this guild. Upload a guild-associated report or supply its direct URL."
                :$"Newest first · {s.Reports.Count} recent reports · page {page+1}/{Math.Max(1,(s.Reports.Count+24)/25)}\nPrivate to you. Only the selected report is analyzed. For older reports, supply a direct report URL."));
            if(s.Reports.Count>0)
            {
                var menu=new SelectMenuBuilder().WithCustomId(Pick(s,"report")).WithPlaceholder("Select a report");
                foreach(var pair in s.Reports.Select((r,i)=>(r,i)).Skip(page*25).Take(25))
                    menu.AddOption(RaidRecapRules.Text(pair.r.Title,70)+" · "+Date(pair.r.StartTime),pair.i.ToString(CultureInfo.InvariantCulture),description:RaidRecapRules.Text(pair.r.ZoneName,60)+" · span "+Duration((double)pair.r.EndTime-pair.r.StartTime));
                controls.WithSelectMenu(menu,row:0);
                Pages(controls,s,"reports",page,s.Reports.Count,1);
            }
        }
        else
        {
            c.AddComponent(new TextDisplayBuilder("# Raid recap\n"+RaidRecapRules.Text(report.Title,160)));
            c.AddComponent(new SeparatorBuilder().WithIsDivider(true));
            var body=new StringBuilder();
            body.AppendLine($"**{Label(view)}**");
            if(!string.IsNullOrEmpty(s.Notice)&&!shared) body.AppendLine(RaidRecapRules.Text(s.Notice,300)+"\n");
            if(view=="overview") body.Append(Overview(report));
            else if(view=="bosses")
            {
                if(report.Bosses.Count==0) body.AppendLine("No boss encounters are recorded yet. Trash is excluded.");
                else
                {
                    var boss=report.Bosses[Math.Clamp(s.BossIndex,0,report.Bosses.Count-1)];
                    body.AppendLine($"**{RaidRecapRules.Text(boss.Name,100)} · {Difficulty(boss.Difficulty)}**");
                    body.AppendLine($"{boss.Attempts.Count} attempts · {boss.Kills} kills · {boss.Wipes} wipes");
                    body.AppendLine($"Fastest kill: **{Duration(boss.FastestKillMs)}**\nBest wipe remaining: **{Percent(boss.BestRemaining)}**");
                    body.AppendLine("\n**Latest attempts**");
                    foreach(var f in boss.Attempts.Reverse().Take(5)) body.AppendLine($"[Fight {f.Id}]({report.Url}#fight={f.Id}) · {(f.IsKill?"Kill":f.IsWipe?"Wipe":"Live / unknown outcome")} · {Duration(f.DurationMs)} · remaining {Percent(f.Remaining)}");
                    Menu(controls,s,"boss",report.Bosses.Select(b=>RaidRecapRules.Text(b.Name,65)+" · "+Difficulty(b.Difficulty)).ToArray(),s.BossPage,s.BossIndex);
                    Pages(controls,s,"bosses",s.BossPage,report.Bosses.Count,3);
                }
            }
            else
            {
                var kills=report.Fights.Where(f=>f.IsKill).ToArray();
                if(kills.Length==0) body.AppendLine("No completed boss kills yet. Damage and healing rankings exclude wipes and trash.\n\n"+Overview(report));
                else
                {
                    var fight=kills[Math.Clamp(s.KillIndex,0,kills.Length-1)];
                    body.AppendLine($"**{RaidRecapRules.Text(fight.Name,100)} · {Difficulty(fight.Difficulty)} · kill #{fight.Id}**");
                    body.AppendLine($"[Open this fight]({report.Url}#fight={fight.Id}) · elapsed {Duration(fight.DurationMs)}");
                    body.AppendLine(view=="healing"
                        ? "**Effective healing / elapsed seconds (HPS)** · WCL Healing totals; overheal not added."
                        : "**Damage / elapsed seconds (DPS)** · this boss kill only.");
                    body.AppendLine("WCL sources as returned; pets are not manually added. No cross-fight averages.\n");
                    if(s.Performance==null) body.AppendLine("Performance unavailable. Refresh or open this fight on Warcraft Logs.");
                    else if(s.Performance.Count==0) body.AppendLine("No source rows returned for this kill.");
                    else
                    {
                        foreach(var (standing,index) in s.Performance.Select((r,i)=>(r,i)).Skip(s.RankPage*10).Take(10))
                            body.AppendLine($"{index+1}. **{RaidRecapRules.Text(standing.Name,65)}** · {(standing.PerSecond.HasValue?standing.PerSecond.Value.ToString(standing.PerSecond>=1e15?"G6":"N0",CultureInfo.InvariantCulture):"Unknown")}");
                        body.AppendLine($"Sources {s.RankPage*10+1}–{Math.Min(s.Performance.Count,(s.RankPage+1)*10)} of {s.Performance.Count}");
                        controls.WithButton("Previous sources",Nav(s,"ranks_prev"),ButtonStyle.Secondary,disabled:s.RankPage==0,row:4)
                            .WithButton("Next sources",Nav(s,"ranks_next"),ButtonStyle.Secondary,disabled:(s.RankPage+1)*10>=s.Performance.Count,row:4);
                    }
                    Menu(controls,s,"kill",kills.Select(f=>RaidRecapRules.Text(f.Name,60)+$" · {Difficulty(f.Difficulty)} #{f.Id}").ToArray(),s.KillPage,s.KillIndex);
                    Pages(controls,s,"kills",s.KillPage,kills.Length,3);
                }
            }
            body.AppendLine($"\n-# Warcraft Logs · as of <t:{report.AsOf.ToUnixTimeSeconds()}:f> · may still update. Snapshot, not a completion claim.");
            c.AddComponent(new TextDisplayBuilder(body.ToString()));
            if(!shared)
            {
                foreach(var tab in new[]{"overview","bosses","damage","healing"}) controls.WithButton(Label(tab),Nav(s,tab),tab==view?ButtonStyle.Primary:ButtonStyle.Secondary,row:0);
                controls.WithButton("Reports",Nav(s,"reports"),ButtonStyle.Secondary,disabled:s.Reports.Count==0,row:1)
                    .WithButton("Refresh",Nav(s,"refresh"),ButtonStyle.Secondary,row:1)
                    .WithButton(s.ShareAttempted?"Share attempted":"Share overview",Nav(s,"share"),ButtonStyle.Success,disabled:!s.CanShare||s.ShareAttempted,row:1);
            }
            controls.WithButton("Warcraft Logs",style:ButtonStyle.Link,url:report.Url,row:shared?0:1);
        }
        foreach(var row in controls.Build().Components.OfType<ActionRowComponent>()) c.AddComponent(new ActionRowBuilder(row));
        return new ComponentBuilderV2().AddComponent(c).Build();
    }
    public static string Nav(RaidRecapSession s,string action)=>$"rr_nav~{s.Token}~{s.Generation}~{action}";
    public static string Pick(RaidRecapSession s,string action)=>$"rr_pick~{s.Token}~{s.Generation}~{action}";
    private static void Menu(ComponentBuilder c,RaidRecapSession s,string kind,IReadOnlyList<string> names,int page,int selected)
    {
        var m=new SelectMenuBuilder().WithCustomId(Pick(s,kind)).WithPlaceholder(kind=="boss"?"Select encounter / difficulty":"Select a boss kill");
        for(var i=page*25;i<Math.Min(names.Count,(page+1)*25);i++) m.AddOption(names[i],i.ToString(CultureInfo.InvariantCulture),isDefault:i==selected);
        c.WithSelectMenu(m,row:2);
    }
    private static void Pages(ComponentBuilder c,RaidRecapSession s,string kind,int page,int count,int row)
    {
        c.WithButton("Previous",Nav(s,kind+"_prev"),ButtonStyle.Secondary,disabled:page<=0,row:row)
            .WithButton("Next",Nav(s,kind+"_next"),ButtonStyle.Secondary,disabled:(page+1)*25>=count,row:row);
    }
    private static string Overview(RaidRecapReport r)
    {
        var body=new StringBuilder($"{r.Bosses.Count(b=>b.Kills>0)} encounter/difficulty groups cleared across {r.Fights.Count} attempts.\n");
        var progress=r.Bosses.Where(b=>b.Kills==0).OrderByDescending(b=>b.Attempts.Count).FirstOrDefault();
        if(progress!=null) body.AppendLine($"Most-pulled unresolved: **{RaidRecapRules.Text(progress.Name,75)}** · {Difficulty(progress.Difficulty)} · best recorded wipe boss health {Percent(progress.BestRemaining)} (not encounter completion).\n");
        body.AppendLine($"**{r.Kills} kills · {r.Wipes} wipes · {r.Unfinished} live / unknown**\n{r.Bosses.Count} encounter / difficulty groups recorded. Trash excluded.");
        if(r.Bosses.Count==0) body.AppendLine("No boss encounters are recorded yet.");
        foreach(var b in r.Bosses.Take(8)) body.AppendLine($"{(b.Kills>0?"✓":"↗")} **{RaidRecapRules.Text(b.Name,75)}** · {Difficulty(b.Difficulty)} · {b.Kills}K / {b.Wipes}W · best wipe {Percent(b.BestRemaining)}");
        if(r.Bosses.Count>8) body.AppendLine("More encounters in Bosses / the linked report.");
        return body.ToString();
    }
    public static string Difficulty(int? id)=>id switch { 1=>"LFR",3=>"Normal",4=>"Heroic",5=>"Mythic",null=>"Unknown difficulty",_=>$"Difficulty {id}" };
    private static string Label(string view)=>view switch { "overview"=>"Overview","bosses"=>"Bosses","damage"=>"Damage",_=>"Healing" };
    public static string Duration(double? ms)
    {
        // Bound untrusted provider numbers before constructing a TimeSpan; retain day information.
        if(ms is not >0 || !double.IsFinite(ms.Value) || ms>TimeSpan.FromDays(365).TotalMilliseconds) return "Unknown";
        var span=TimeSpan.FromMilliseconds(ms.Value);
        return (span.Days>0?$"{span.Days}d ":"")+span.ToString(@"hh\:mm\:ss",CultureInfo.InvariantCulture);
    }
    private static string Percent(double? p)=>p.HasValue?p.Value.ToString("0.##",CultureInfo.InvariantCulture)+"%":"Unknown";
    private static string Date(long ms)=>ms>=0&&ms<=253402300799999?DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):"Unknown date";
}
