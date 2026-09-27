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
        if(!shared && s.PlayerPanel!=null) return RaidRecapPlayerView.Build(s);
        var scope=new ContainerBuilder().WithAccentColor(new Color(88,101,242));
        var c=new ContainerBuilder().WithAccentColor(new Color(shared?0x5865F2u:s.View=="damage"?0xE06C75u:s.View=="healing"?0x2AA198u:0x8B80D9u));
        var controls=new ComponentBuilder();
        var report=s.Report;
        var view=shared?"overview":s.View;
        if(report==null||view=="reports")
        {
            (shared?c:scope).AddComponent(new TextDisplayBuilder("# Raid recap\nChoose a recent report"));
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
            (shared?c:scope).AddComponent(new TextDisplayBuilder("# Raid recap\n"+RaidRecapRules.Text(report.Title,160)));
            (shared?c:scope).AddComponent(new SeparatorBuilder().WithIsDivider(true));
            if(!shared)
            {
                foreach(var tab in new[]{"overview","bosses","damage","healing","analysis"}) controls.WithButton(Label(tab),Nav(s,tab),tab==view?ButtonStyle.Primary:ButtonStyle.Secondary,row:0);
                controls.WithButton("Reports",Nav(s,"reports"),ButtonStyle.Secondary,disabled:s.Reports.Count==0,row:1)
                    .WithButton("Refresh",Nav(s,"refresh"),ButtonStyle.Secondary,row:1)
                    .WithButton(s.ShareAttempted?"Share attempted":"Share overview",Nav(s,"share"),ButtonStyle.Success,disabled:!s.CanShare||s.ShareAttempted,row:1);
            }
            controls.WithButton("Warcraft Logs",style:ButtonStyle.Link,url:report.Url,row:shared?0:1);
            if(!shared && !(view=="bosses" && s.Comparing)) controls.WithButton("Players",Nav(s,"players"),ButtonStyle.Secondary,row:1);
            var body=new StringBuilder();
            body.AppendLine($"**{Label(view)}**");
            if(!string.IsNullOrEmpty(s.Notice)&&!shared) body.AppendLine(RaidRecapRules.Text(s.Notice,300)+"\n");
            if(view=="overview") body.Append(shared?Overview(report):PrivateSynopsis(report));
            else if(view=="bosses")
            {
                if(s.Comparing) ComparisonBody(c,body,controls,s);
                else if(report.Bosses.Count==0) body.AppendLine("No boss encounters are recorded yet. Trash is excluded.");
                else
                {
                    var boss=report.Bosses[Math.Clamp(s.BossIndex,0,report.Bosses.Count-1)];
                    body.AppendLine($"**{RaidRecapRules.Text(boss.Name,100)} · {Difficulty(boss.Difficulty)}**");
                    body.AppendLine($"{boss.Attempts.Count} attempts · {boss.Kills} kills · {boss.Wipes} wipes");
                    body.AppendLine($"Completed combat time: **{Duration(boss.CombatMs)}** ({boss.KnownDurations}/{boss.Completed} known durations)");
                    body.AppendLine($"Median completed-wipe duration: **{Duration(boss.MedianWipeMs)}** (n={boss.WipeDurationSamples}/{boss.Wipes})");
                    body.AppendLine($"Fastest kill: **{Duration(boss.FastestKillMs)}**\nBest wipe active boss health: **{Percent(boss.BestRemaining)}**\nLatest wipe active boss health: **{Percent(boss.LatestRemaining)}**");
                    body.AppendLine($"\n**All attempts · chronological · page {s.AttemptPage+1}/{(boss.Attempts.Count+9)/10}**");
                    foreach(var f in boss.Chronological.Skip(s.AttemptPage*10).Take(10)) body.AppendLine($"[Fight {f.Id}]({report.Url}#fight={f.Id}) · {(f.IsKill?"Kill":f.IsWipe?"Wipe":"Live / unknown outcome")} · {Duration(f.DurationMs)} · active boss health {Percent(f.Remaining)}");
                    controls.WithButton("Previous attempts",Nav(s,"attempts_prev"),ButtonStyle.Secondary,disabled:s.AttemptPage==0,row:4)
                        .WithButton("Next attempts",Nav(s,"attempts_next"),ButtonStyle.Secondary,disabled:(s.AttemptPage+1)*10>=boss.Attempts.Count,row:4);
                    Menu(controls,s,"boss",report.Bosses.Select(b=>RaidRecapRules.Text(b.Name,65)+" · "+Difficulty(b.Difficulty)).ToArray(),s.BossPage,s.BossIndex);
                    Pages(controls,s,"bosses",s.BossPage,report.Bosses.Count,3);
                }
            }
            else if(view=="analysis") AnalysisBody(c,body,controls,s);
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
                        ? "**WCL Healing table total / elapsed seconds** · overheal not added; not validated as effective healing; not a quality grade."
                        : "**Damage / elapsed seconds (DPS)** · this boss kill only.");
                    body.AppendLine("Output position in this kill · WCL sources as returned; pets are not manually added. No cross-fight averages.");
                    body.AppendLine(s.PerformanceParses is { } parses?$"WCL {(view=="healing"?"HPS":"DPS")} · Parses · Today · partition {parses.Partition} · as of <t:{parses.AsOf.ToUnixTimeSeconds()}:f>. Finality unknown.":"Parse unavailable · raw output is separate.");
                    body.AppendLine("Band colors: Gray 0+ · Green 25+ · Blue 50+ · Purple 75+ · Orange 95+ · Pink 99+ · Gold exactly 100. Fractional display floors, never rounds up.\n");
                    if(s.Performance==null) body.AppendLine("Performance unavailable. Refresh or open this fight on Warcraft Logs.");
                    else if(s.Performance.Count==0) body.AppendLine("No source rows returned for this kill.");
                    else
                    {
                        var outputPages=RaidRecapPlayerPresentation.OutputPages(s);var outputPage=Math.Clamp(s.RankPage,0,outputPages.Count-1);
                        foreach(var line in outputPages[outputPage])body.AppendLine(line);
                        var first=outputPages.Take(outputPage).Sum(page=>page.Count)+1;
                        body.AppendLine($"Sources {first}–{first+outputPages[outputPage].Count-1} of {s.Performance.Count} · page {outputPage+1}/{outputPages.Count}");
                        controls.WithButton("Previous sources",Nav(s,"ranks_prev"),ButtonStyle.Secondary,disabled:s.RankPage==0,row:4)
                            .WithButton("Next sources",Nav(s,"ranks_next"),ButtonStyle.Secondary,disabled:outputPage==outputPages.Count-1,row:4);
                    }
                    Menu(controls,s,"kill",kills.Select(f=>RaidRecapRules.Text(f.Name,60)+$" · {Difficulty(f.Difficulty)} #{f.Id}").ToArray(),s.KillPage,s.KillIndex);
                    Pages(controls,s,"kills",s.KillPage,kills.Length,3);
                }
            }
            if(view=="overview" && !shared)
            {
                c.AddComponent(new TextDisplayBuilder(body.ToString())); body.Clear();
                ReviewCards(c,s);
            }
            body.AppendLine($"\n-# Warcraft Logs · as of <t:{report.AsOf.ToUnixTimeSeconds()}:f> · may still update. Snapshot, not a completion claim.");
            c.AddComponent(new TextDisplayBuilder(body.ToString()));
            if(view=="bosses" && !s.Comparing)
                c.AddComponent(new SectionBuilder(new ButtonBuilder("Compare",Nav(s,"compare"),ButtonStyle.Secondary),
                    new TextDisplayBuilder("**Matched pulls**\nCompare two completed pulls of this encounter / difficulty. Choose the pair before loading deaths.")));

        }
        var rows=controls.Build().Components.OfType<ActionRowComponent>().ToArray();
        if(shared || report==null || view=="reports")
        {
            foreach(var row in rows) c.AddComponent(new ActionRowBuilder(row));
            return shared?new ComponentBuilderV2().AddComponent(c).Build():new ComponentBuilderV2().AddComponent(scope).AddComponent(c).Build();
        }
        foreach(var row in rows.Take(2)) scope.AddComponent(new ActionRowBuilder(row));
        foreach(var row in rows.Skip(2)) c.AddComponent(new ActionRowBuilder(row));
        return new ComponentBuilderV2().AddComponent(scope).AddComponent(c).Build();
    }
    private static string PrivateSynopsis(RaidRecapReport r) =>
        $"{r.Bosses.Count(b=>b.Kills>0)} encounter/difficulty groups cleared across {r.Fights.Count} attempts.\n"
        +$"**{r.Kills} kills · {r.Wipes} wipes · {r.Unfinished} live / unknown** · trash excluded.\n"
        +(r.Bosses.Count==0?"No boss encounters are recorded yet.\n":"");

    private static void ReviewCards(ContainerBuilder c,RaidRecapSession s)
    {
        c.AddComponent(new TextDisplayBuilder("## Review first\nObservations and questions from this snapshot, not grades or cause verdicts."));
        var cards=RaidRecapReview.Cards(s.Report);
        for(var i=0;i<cards.Count;i++)
        {
            var card=cards[i]; var boss=s.Report.Bosses[card.BossIndex];
            var name=RaidRecapRules.Text(boss.Name,75)+" · "+Difficulty(boss.Difficulty);
            string text;
            if(card.B==null)
            {
                text=$"**Most-pulled unresolved · {name}**\n{boss.Attempts.Count} attempts · best recorded wipe boss health {Percent(boss.BestRemaining)} (not encounter completion).\n";
                text+=card.A.IsWipe
                    ?$"Next: [Review first deaths]({s.Report.Url}#fight={card.A.Id}&type=deaths) and their lead-up in Analysis. First death is not a cause verdict. Details opens these attempts."
                    :$"[Latest attempt #{card.A.Id}]({s.Report.Url}#fight={card.A.Id}) has no completed wipe to review. Details opens the recorded attempts; no outcome is inferred.";
            }
            else
                text=$"**Recommended matched pair · {name}**\nA: [{(card.A.IsKill?"Kill":"Wipe")} #{card.A.Id}]({s.Report.Url}#fight={card.A.Id}) → B: [{(card.B.IsKill?"Kill":"Wipe")} #{card.B.Id}]({s.Report.Url}#fight={card.B.Id}).\nNext: what changed within the same elapsed window? Details opens both selectors; Compare deaths loads observations only when requested.";
            c.AddComponent(new SeparatorBuilder().WithIsDivider(true));
            c.AddComponent(new SectionBuilder(new ButtonBuilder("Details",Nav(s,"review_"+i),ButtonStyle.Secondary),new TextDisplayBuilder(text)));
        }
        if(!cards.Any(card=>card.B!=null)) c.AddComponent(new TextDisplayBuilder(RaidRecapReview.NoPair+" Review recorded attempts in Bosses / Analysis."));
    }

    private static void ComparisonBody(ContainerBuilder c,StringBuilder body,ComponentBuilder controls,RaidRecapSession s)
    {
        controls.WithButton("All attempts",Nav(s,"attempts"),ButtonStyle.Secondary,row:1);
        body.AppendLine("**Matched pulls** · A and B are explicit selections from one report snapshot.");
        var (a,b)=RaidRecapReview.Selection(s);
        if(s.CompareLosses && RaidRecapReview.Current(s) is { } tiesResult && tiesResult.DeathsA.Complete && tiesResult.DeathsB.Complete)
        {
            var pages=RaidRecapPlayerPresentation.Pack(RaidRecapPlayerPresentation.FirstLossRows(s));var page=Math.Clamp(s.CompareLossPage,0,pages.Count-1);
            body.AppendLine($"**All first-loss evidence · page {page+1}/{pages.Count}**\nA #{a.Id} / B #{b.Id} · full pulls · simultaneous players, not a cause verdict.");
            foreach(var line in pages[page])body.AppendLine(line);
            controls.WithButton("Comparison",Nav(s,"compare_return"),ButtonStyle.Secondary,row:2)
                .WithButton("Previous ties",Nav(s,"compare_loss_prev"),ButtonStyle.Secondary,disabled:page==0,row:2)
                .WithButton("Next ties",Nav(s,"compare_loss_next"),ButtonStyle.Secondary,disabled:page==pages.Count-1,row:2);
            return;
        }
        if(a==null) { body.AppendLine(RaidRecapReview.NoPair); return; }
        body.AppendLine($"**{RaidRecapRules.Text(a.Name,80)} · {Difficulty(a.Difficulty)}**");
        c.AddComponent(new TextDisplayBuilder(body.ToString())); body.Clear();
        foreach(var (label,fight) in new[]{("A",a),("B",b)})
        {
            c.AddComponent(new TextDisplayBuilder($"**{label} · {(fight.IsKill?"Kill":"Wipe")} #{fight.Id}** · elapsed {Duration(fight.DurationMs)}\n"
                +$"active boss health {Percent(fight.Remaining)} (not encounter completion). [Open {label} deaths]({s.Report.Url}#fight={fight.Id}&type=deaths)"));
        }
        var candidates=RaidRecapReview.Candidates(s.Report.Bosses[s.BossIndex]);
        CompareMenu(controls,s,candidates,"a",s.CompareAPage,s.CompareAIndex,2);
        CompareMenu(controls,s,candidates,"b",s.CompareBPage,s.CompareBIndex,3);
        controls.WithButton("A previous",Nav(s,"compare_a_prev"),ButtonStyle.Secondary,disabled:s.CompareAPage==0,row:4)
            .WithButton("A next",Nav(s,"compare_a_next"),ButtonStyle.Secondary,disabled:(s.CompareAPage+1)*25>=candidates.Count,row:4)
            .WithButton("B previous",Nav(s,"compare_b_prev"),ButtonStyle.Secondary,disabled:s.CompareBPage==0,row:4)
            .WithButton("B next",Nav(s,"compare_b_next"),ButtonStyle.Secondary,disabled:(s.CompareBPage+1)*25>=candidates.Count,row:4)
            .WithButton("Compare deaths",Nav(s,"compare_deaths"),ButtonStyle.Primary,row:4);
        c.AddComponent(new SeparatorBuilder().WithIsDivider(true));
        body.AppendLine("Equal time does not mean equal phase, opportunity or roster. Repeated deaths are events, not extra players. First loss is not a cause or blame verdict; inspect the lead-up, healing, defensives and assignments.");
        var result=RaidRecapReview.Current(s);
        if(result==null)
        {
            body.AppendLine("\nDeath comparison unavailable / not loaded. Select **Compare deaths** to load just A and B, or use their exact WCL links. No analysis is fetched while browsing.");return;
        }
        var complete=result.DeathsA.Complete && result.DeathsB.Complete;
        if(!complete) body.AppendLine("\n**Partial observations** · at least the retained observations, not complete totals. Numeric changes and definite first-loss claims are withheld.");
        FullDeaths(body,"A",result.DeathsA,complete); FullDeaths(body,"B",result.DeathsB,complete);
        if(complete)
        {
            foreach(var (label,fight,deaths) in new[]{("A",a,result.DeathsA.Deaths),("B",b,result.DeathsB.Deaths)})
            {
                var ties=RaidRecapPlayerPresentation.FirstLosses(deaths);
                if(ties.Count>0)body.AppendLine(label+" first-loss players: "+string.Join(", ",ties.Take(3).Select(d=>RaidRecapLinks.Name(s.Report,fight,d.ActorId,d.Name,30)))
                    +(ties.Count>3?$" · +{ties.Count-3} — All first-loss evidence":""));
            }
            c.AddComponent(new SectionBuilder(new ButtonBuilder("All first-loss evidence",Nav(s,"compare_losses"),ButtonStyle.Secondary),
                new TextDisplayBuilder("Who was lost at the same moment? Every first-loss tie is reachable; review the lead-up before drawing conclusions.")));
        }
        var window=Math.Min(a.DurationMs.Value,b.DurationMs.Value);
        var windowA=result.DeathsA.Deaths.Where(d=>d.ElapsedMs>=0 && d.ElapsedMs<=window).ToArray();
        var windowB=result.DeathsB.Deaths.Where(d=>d.ElapsedMs>=0 && d.ElapsedMs<=window).ToArray();
        body.AppendLine($"\n**Same elapsed window [0, {Elapsed(window)}]** · inclusive endpoints; not phase-aligned.");
        WindowDeaths(body,"A",windowA,result.DeathsA.Complete,complete);
        WindowDeaths(body,"B",windowB,result.DeathsB.Complete,complete);
        if(complete) body.AppendLine($"Window change (B − A): {windowB.Length-windowA.Length} death events · {Players(windowB)-Players(windowA)} distinct players. Descriptive, not a success judgment.");
    }

    private static int Players(IReadOnlyList<RaidRecapDeath> deaths)=>deaths.Select(d=>d.ActorId).Distinct().Count();
    private static void FullDeaths(StringBuilder body,string label,RaidRecapAnalysis analysis,bool firstKnown)
    {
        body.AppendLine($"\n{label} full pull: {analysis.Deaths.Count} death events · {analysis.DistinctPlayers} distinct players · {(analysis.Complete?"complete observations":"partial; at least these observations")}");
        if(firstKnown) FirstLoss(body,analysis.Deaths);
    }
    private static void WindowDeaths(StringBuilder body,string label,IReadOnlyList<RaidRecapDeath> deaths,bool complete,bool firstKnown)
    {
        body.AppendLine($"{label} window: {deaths.Count} death events · {Players(deaths)} distinct players"+(complete?"":" · partial; at least these observations"));
        if(firstKnown) FirstLoss(body,deaths);
    }
    private static void FirstLoss(StringBuilder body,IReadOnlyList<RaidRecapDeath> deaths)
    {
        if(deaths.Count==0) { body.AppendLine("No player deaths in this complete scope.");return; }
        var first=deaths.Min(d=>d.ElapsedMs);var ties=deaths.Where(d=>d.ElapsedMs==first).Select(d=>d.ActorId).Distinct().Count();
        body.AppendLine($"First loss: {Elapsed(first)} · {ties} simultaneous player{(ties==1?"":"s")}");
    }
    private static void CompareMenu(ComponentBuilder controls,RaidRecapSession s,IReadOnlyList<RaidRecapFight> candidates,string side,int page,int selected,int row)
    {
        var menu=new SelectMenuBuilder().WithCustomId(Pick(s,"compare_"+side))
            .WithPlaceholder($"{side.ToUpperInvariant()} · options page {page+1}/{(candidates.Count+24)/25} · choose a distinct pull");
        for(var i=page*25;i<Math.Min(candidates.Count,(page+1)*25);i++)
            menu.AddOption($"{side.ToUpperInvariant()} · {(candidates[i].IsKill?"Kill":"Wipe")} #{candidates[i].Id} · {Duration(candidates[i].DurationMs)}",i.ToString(CultureInfo.InvariantCulture),isDefault:i==selected);
        controls.WithSelectMenu(menu,row:row);
    }

    private static void AnalysisBody(ContainerBuilder c, StringBuilder body, ComponentBuilder controls, RaidRecapSession s)
    {
        var pulls=s.Report.CompletedPulls;
        body.AppendLine("Private · one completed pull only. Observations, not player grades.");
        if(pulls.Count==0) { body.AppendLine("No completed boss pulls yet. Live / unknown outcomes and trash are excluded."); return; }
        var fight=pulls[Math.Clamp(s.PullIndex,0,pulls.Count-1)];
        body.AppendLine($"**{RaidRecapRules.Text(fight.Name,80)} · {Difficulty(fight.Difficulty)} · {(fight.IsKill?"Kill":"Wipe")} #{fight.Id}** · elapsed {Duration(fight.DurationMs)}");
        var metric=s.AnalysisMetric;
        var mechanic=RaidRecapMechanics.Rule(metric);
        var type=metric=="incoming"?"damage-taken":metric;
        if(mechanic==null) body.AppendLine($"[Open this fight's {Label(metric)} view]({s.Report.Url}#fight={fight.Id}&type={type})");
        Menu(controls,s,"pull",pulls.Select(f=>RaidRecapRules.Text(f.Name,40)+$" · {Difficulty(f.Difficulty)} · {(f.IsKill?"Kill":"Wipe")} #{f.Id}").ToArray(),s.PullPage,s.PullIndex);
        foreach(var subview in new[]{"deaths","incoming","interrupts","dispels","mechanics"})
            controls.WithButton(Label(subview),Nav(s,subview),(metric==subview || (subview=="mechanics" && mechanic!=null))?ButtonStyle.Primary:ButtonStyle.Secondary,row:3);
        controls.WithButton("Previous pulls",Nav(s,"pulls_prev"),ButtonStyle.Secondary,disabled:s.PullPage==0,row:4)
            .WithButton("Next pulls",Nav(s,"pulls_next"),ButtonStyle.Secondary,disabled:(s.PullPage+1)*25>=pulls.Count,row:4);

        if(mechanic!=null) { MechanicBody(c,body,controls,s,fight,mechanic); return; }
        if(metric=="deaths") body.AppendLine("Inspect lead-up damage, healing, defensives and assignments. First death / killing blow is not a cause or blame verdict; availability and preventability are not inferred.");
        else if(metric=="incoming") body.AppendLine("**WCL damage-taken table totals** · mitigation / absorb semantics not validated as net or effective damage. Composite parents counted once. Check assignments, soaks and mitigation against major sources; not an avoidable-damage verdict.");
        else if(metric=="interrupts") body.AppendLine("Observed actions only. Completed casts are not missed assignments; not every cast is interruptible. Review cast timing and assigned rotation on WCL.");
        else body.AppendLine("Observed dispels only. Remaining applications are not failed obligations. Review dispel timing, strategy and assignments on WCL.");
        var analysis=s.Analysis;
        if(analysis==null || analysis.Metric!=metric) { body.AppendLine("\nAnalysis unavailable. Select a subview to retry, refresh, or open this fight on Warcraft Logs."); return; }
        if(!analysis.Complete) body.AppendLine("\n**Partial observations** · "+RaidRecapRules.Text(analysis.Notice??"Data incomplete",180));
        var lines=new List<string>();
        if(metric=="deaths")
        {
            body.AppendLine($"\n{analysis.Deaths.Count} observed death events · {analysis.DistinctPlayers} distinct players"+(analysis.Complete?"":" · at least these observations; incomplete"));
            if(analysis.Deaths.Count==0 && analysis.Complete) body.AppendLine("No player deaths in the complete scoped result.");
            if(analysis.Deaths.Count>0 && analysis.Complete)
            {
                var first=analysis.Deaths.Min(d=>d.ElapsedMs);
                var ties=analysis.Deaths.Where(d=>d.ElapsedMs==first).Select(d=>d.ActorId).Distinct().Count();
                body.AppendLine($"First loss: **{Elapsed(first)}** · {ties} simultaneous player{(ties==1?"":"s")}; ties preserved below.");
            }
            var seen=new Dictionary<int,int>();
            foreach(var d in analysis.Deaths)
            {
                seen.TryGetValue(d.ActorId,out var n);seen[d.ActorId]=++n;
                lines.Add($"{Elapsed(d.ElapsedMs)} · {RaidRecapLinks.Name(s.Report,fight,d.ActorId,d.Name)} · {RaidRecapRules.Text(d.Ability,55)}"+(n>1?$" · death event {n} for this player":""));
            }
        }
        else if(metric=="incoming")
        {
            if(analysis.Incoming.Count==0 && analysis.Complete) body.AppendLine("No incoming source rows returned for this pull.");
            foreach(var row in analysis.Incoming) lines.Add($"**{RaidRecapRules.Text(row.Name,55)}** · {Amount(row.Total)} · {RaidRecapRules.Text(row.Source,50)}");
        }
        else
        {
            if(analysis.Utility.Count==0 && analysis.Complete) body.AppendLine("No observed spell rows returned for this pull.");
            foreach(var spell in analysis.Utility)
            {
                var name=RaidRecapRules.Text(spell.Name,50);
                lines.Add($"**{name}** · {Amount(spell.Actions)} observed {metric}"+(metric=="interrupts"?$" · {Amount(spell.CompletedCasts)} WCL-reported completed casts · {Amount(spell.Channels)} channel interrupts (separate)":"")
                    +(spell.AttributionKnown?"":" · Attribution unavailable / unassigned"));
                foreach(var participant in spell.Participants)
                    lines.Add($"↳ {name} · "+(participant.VerifiedPlayer && participant.ActorId is int actorId?RaidRecapLinks.Name(s.Report,fight,actorId,participant.Name,40):RaidRecapRules.Text(participant.Name,40))
                        +$" · {Amount(participant.Count)} attributed actions"+(!participant.VerifiedPlayer?" · unassigned identity":""));
            }
        }
        var page=Math.Clamp(s.AnalysisPage,0,Math.Max(0,(lines.Count-1)/RaidRecapAnalysisRules.PageSize));
        body.AppendLine($"\n**{Label(metric)} · page {page+1}/{Math.Max(1,(lines.Count+RaidRecapAnalysisRules.PageSize-1)/RaidRecapAnalysisRules.PageSize)}**");
        foreach(var line in lines.Skip(page*RaidRecapAnalysisRules.PageSize).Take(RaidRecapAnalysisRules.PageSize)) body.AppendLine(line);
        controls.WithButton("Previous rows",Nav(s,"analysis_prev"),ButtonStyle.Secondary,disabled:page==0,row:4)
            .WithButton("Next rows",Nav(s,"analysis_next"),ButtonStyle.Secondary,disabled:(page+1)*RaidRecapAnalysisRules.PageSize>=lines.Count,row:4);
    }
    private static void MechanicBody(ContainerBuilder c,StringBuilder body,ComponentBuilder controls,RaidRecapSession s,
        RaidRecapFight fight,RaidRecapMechanicRule rule)
    {
        c.AddComponent(new TextDisplayBuilder(body.ToString())); body.Clear();
        foreach(var metric in new[]{RaidRecapMechanics.Junk,RaidRecapMechanics.Spin})
        {
            var candidate=RaidRecapMechanics.Rule(metric);
            c.AddComponent(new SectionBuilder(new ButtonBuilder(metric==RaidRecapMechanics.Junk?"Throw Junk":"Shell Spin",Nav(s,metric),
                metric==rule.Metric?ButtonStyle.Primary:ButtonStyle.Secondary),
                new TextDisplayBuilder($"**{candidate.Label}** · spell {candidate.SpellId}")));
        }
        body.AppendLine($"\n**{rule.Label}** · Full pull · elapsed {Elapsed(fight.DurationMs??double.NaN)}");
        body.AppendLine($"[WCL fight / spell {rule.SpellId}]({s.Report.Url}#fight={fight.Id}&type={rule.View}&ability={rule.SpellId}) · elapsed timestamps below, no timestamp-link claim.");
        body.AppendLine(rule.CountDefinition);
        body.AppendLine("Selected roster + Player identity only; pets/NPCs excluded. No opportunity denominator, position judgment or cause verdict.");
        body.AppendLine($"Review: {rule.Question}");
        var analysis=s.Analysis;var result=analysis?.Mechanic;
        if(analysis?.Metric!=rule.Metric || result==null || result.SnapshotKey!=s.Report.SnapshotKey
            || result.FightId!=fight.Id || result.StartMs!=fight.StartMs || result.EndMs!=fight.EndMs)
        { body.AppendLine("\nMechanic analysis unavailable. Select a rule to retry, refresh, or open WCL. No zero is inferred.");return; }
        if(result.Coverage=="unsupported") { body.AppendLine("\n**Unsupported mechanic scope** · "+RaidRecapRules.Text(analysis.Notice,250));return; }
        if(!analysis.Complete) body.AppendLine("\n**Partial observations** · at least these event rows and distinct players; not a complete total.");
        body.AppendLine($"\n{result.Events.Count} observed {(rule.Metric==RaidRecapMechanics.Junk?"damage event rows":"debuff applications")} · {result.DistinctPlayers} distinct players · {(analysis.Complete?"complete observations":"lower bound only")}");
        if(result.Events.Count==0 && analysis.Complete) body.AppendLine("No qualifying player event rows in this complete scope.");
        var page=Math.Clamp(s.AnalysisPage,0,Math.Max(0,(result.Events.Count-1)/RaidRecapAnalysisRules.PageSize));
        body.AppendLine($"\n**Affected players · page {page+1}/{Math.Max(1,(result.Events.Count+RaidRecapAnalysisRules.PageSize-1)/RaidRecapAnalysisRules.PageSize)}**");
        foreach(var row in result.Events.Skip(page*RaidRecapAnalysisRules.PageSize).Take(RaidRecapAnalysisRules.PageSize))
            body.AppendLine($"{Elapsed(row.ElapsedMs)} (+{row.ElapsedMs.ToString(row.ElapsedMs>=1e15?"G6":"0.###",CultureInfo.InvariantCulture)} ms) · {RaidRecapLinks.Name(s.Report,fight,row.ActorId,row.Name,45)}");
        controls.WithButton("Previous rows",Nav(s,"analysis_prev"),ButtonStyle.Secondary,disabled:page==0,row:4)
            .WithButton("Next rows",Nav(s,"analysis_next"),ButtonStyle.Secondary,disabled:(page+1)*RaidRecapAnalysisRules.PageSize>=result.Events.Count,row:4);
    }

    private static string Amount(double? value)=>value.HasValue?value.Value.ToString(value>=1e15?"G6":"N0",CultureInfo.InvariantCulture):"Unknown";
    private static string Elapsed(double ms)
    {
        if(!double.IsFinite(ms)||ms<0||ms>TimeSpan.FromDays(365).TotalMilliseconds) return "Unknown";
        var span=TimeSpan.FromMilliseconds(ms);
        return (span.Days>0?$"{span.Days}d ":"")+span.ToString(@"hh\:mm\:ss\.fff",CultureInfo.InvariantCulture);
    }
    public static string Nav(RaidRecapSession s,string action)=>$"rr_nav~{s.Token}~{s.Generation}~{action}";
    public static string Pick(RaidRecapSession s,string action)=>$"rr_pick~{s.Token}~{s.Generation}~{action}";
    private static void Menu(ComponentBuilder c,RaidRecapSession s,string kind,IReadOnlyList<string> names,int page,int selected)
    {
        var m=new SelectMenuBuilder().WithCustomId(Pick(s,kind)).WithPlaceholder(kind=="boss"?"Select encounter / difficulty":kind=="pull"?"Select one completed pull":"Select a boss kill");
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
        var review=progress?.Chronological.LastOrDefault(f=>f.IsWipe);
        if(review!=null) body.AppendLine($"Next: [Review first deaths]({r.Url}#fight={review.Id}&type=deaths), then compare your own attempts in Bosses / Analysis. First death is not a cause verdict.\n");
        else body.AppendLine("Next: compare your own completed attempts in Bosses / Analysis; review assignments before throughput.\n");
        body.AppendLine($"**{r.Kills} kills · {r.Wipes} wipes · {r.Unfinished} live / unknown**\n{r.Bosses.Count} encounter / difficulty groups recorded. Trash excluded.");
        if(r.Bosses.Count==0) body.AppendLine("No boss encounters are recorded yet.");
        foreach(var b in r.Bosses.Take(8)) body.AppendLine($"{(b.Kills>0?"✓":"↗")} **{RaidRecapRules.Text(b.Name,75)}** · {Difficulty(b.Difficulty)} · {b.Kills}K / {b.Wipes}W · best wipe {Percent(b.BestRemaining)}");
        if(r.Bosses.Count>8) body.AppendLine("More encounters in Bosses / the linked report.");
        return body.ToString();
    }
    public static string Difficulty(int? id)=>id switch { 1=>"LFR",3=>"Normal",4=>"Heroic",5=>"Mythic",null=>"Unknown difficulty",_=>$"Difficulty {id}" };
    private static string Label(string view)=>view switch { "overview"=>"Overview","bosses"=>"Bosses","damage"=>"Damage","analysis"=>"Analysis","deaths"=>"Deaths","incoming"=>"Incoming","interrupts"=>"Interrupts","dispels"=>"Dispels","mechanics"=>"Mechanics",_=>"Healing" };
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
