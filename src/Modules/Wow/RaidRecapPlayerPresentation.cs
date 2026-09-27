using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace NinjaBotCore.Modules.Wow;

public static class RaidRecapPlayerPresentation
{
    public static string Identity(RaidRecapPlayer p)=>p?.Role switch
    {
        "tanks"=>"🛡️ Tank · "+p.Class+" / "+p.Spec,
        "healers"=>"💚 Healer · "+p.Class+" / "+p.Spec,
        "dps"=>"⚔️ Damage · "+p.Class+" / "+p.Spec,
        _=>"👤 Player · Class/spec/role unavailable"
    };
    public static string Badge(RaidRecapParse p)
    {var b=RaidRecapParsePalette.Badge(p?.Percentile);return p==null?"Parse unavailable":b.Label+" P"+b.Display;}
    public static RaidRecapParse SelectedParse(RaidRecapPlayerPanel panel)
    {
        if(panel?.Selected is not { } player)return null;
        var output=panel.Lens=="damage"?panel.Damage:panel.Lens=="healing"?panel.Healing:null;
        return output?.Parses?.Entries.SingleOrDefault(p=>p.ActorId==player.ActorId);
    }
    public static string Standing(RaidRecapReport report,RaidRecapFight fight,RaidRecapStanding row,int position,string metric)
        =>$"{position}. "+(row.Player==null?RaidRecapRules.Text(row.Name,45):RaidRecapLinks.Name(report,fight,row.Player.ActorId,row.Player.Name))
            +$" · {Amount(row.PerSecond)} {metric} · {Badge(row.Parse)}\n{(row.Player==null?"Unverified source identity":Identity(row.Player))}";
    public static string Amount(double? value)=>value.HasValue?value.Value.ToString(value>=1e15?"G6":"N0",CultureInfo.InvariantCulture):"Unknown";
    public static string Elapsed(double value)=>double.IsFinite(value) && value>=0 && value<=TimeSpan.FromDays(365).TotalMilliseconds
        ?TimeSpan.FromMilliseconds(value).ToString(@"hh\:mm\:ss\.fff",CultureInfo.InvariantCulture):"Unknown";
    public static IReadOnlyList<IReadOnlyList<string>> Pack(IReadOnlyList<string> rows,int rowLimit=8,int textLimit=1800)
    {
        var pages=new List<IReadOnlyList<string>>();var page=new List<string>();var length=0;
        foreach(var row in rows)
        {
            if(row.Length+1>textLimit)throw new InvalidOperationException("An evidence row exceeds the presentation budget.");
            if(page.Count==rowLimit || length+row.Length+1>textLimit) {pages.Add(page.ToArray());page.Clear();length=0;}
            page.Add(row);length+=row.Length+1;
        }
        if(page.Count>0 || pages.Count==0)pages.Add(page.ToArray());return pages;
    }
    public static string Question(string lens)=>lens switch
    {
        "deaths"=>"What happened immediately before this loss, and what recovery followed?",
        "interrupts"=>"Do these observed actions match the assigned rotation? Check casts and assignments on WCL.",
        "dispels"=>"Was the timing consistent with the strategy? Counts alone do not show obligations.",
        RaidRecapMechanics.Junk or RaidRecapMechanics.Spin=>"What was happening around these applications? Counts alone do not establish avoidability.",
        "damage" or "healing"=>"Does this comparison match your spec, assignment and kill context?",
        _=>"Which observation would help you review this pull? Choose one lens to load; Summary never fetches."
    };
    public static IReadOnlyList<IReadOnlyList<string>> OutputPages(RaidRecapSession s)
    {
        var fight=s.Report?.Fights.Where(f=>f.IsKill).ElementAtOrDefault(s.KillIndex);
        return Pack((s.Performance??Array.Empty<RaidRecapStanding>()).Select((r,i)=>Standing(s.Report,fight,r,i+1,s.View=="healing"?"HPS":"DPS")).ToArray(),10);
    }
    private static string UtilitySummary(RaidRecapAnalysis analysis,int actorId)
    {
        var participants=analysis.Utility.SelectMany(u=>u.Participants).Where(p=>p.VerifiedPlayer && p.ActorId==actorId).ToArray();
        var known=participants.Where(p=>p.Count.HasValue).ToArray();
        if(known.Length==0)return "Unknown attributed actions (not obligations)";
        var total=Amount(known.Sum(p=>p.Count.Value));
        return total+" attributed actions"+(analysis.Complete && known.Length==participants.Length
            ?" (not obligations)":" retained (lower bound; total unknown; not obligations)");
    }
    public static IReadOnlyList<IReadOnlyList<string>> Pages(RaidRecapSession s)=>Pack(Rows(s));
    public static IReadOnlyList<string> Rows(RaidRecapSession s)
    {
        var p=s.PlayerPanel;var rows=new List<string>();
        if(p?.Selected==null)return rows;
        if(p.Lens=="summary")
        {
            rows.Add("Damage "+(p.Damage==null?"— Not loaded":"loaded")+" · Healing "+(p.Healing==null?"— Not loaded":"loaded"));
            foreach(var lens in new[]{"deaths","interrupts","dispels",RaidRecapMechanics.Junk,RaidRecapMechanics.Spin})
                {
                if(!p.Observations.TryGetValue(lens,out var a)) {rows.Add(Label(lens)+" · — Not loaded");continue;}
                var count=lens=="deaths"?$"{a.Deaths.Count(d=>d.ActorId==p.ActorId)} observed death events":RaidRecapMechanics.IsMetric(lens)
                    ?$"{a.Mechanic?.Events.Count(d=>d.ActorId==p.ActorId)??0} retained event rows"
                    :UtilitySummary(a,p.Selected.ActorId);
                rows.Add(Label(lens)+" · "+(a.Complete?"✓ Complete · ":"⚠ Partial / unsupported · ")+count);
            }
            return rows;
        }
        if(p.Lens is "damage" or "healing")
        {
            var output=p.Lens=="damage"?p.Damage:p.Healing;
            if(output==null) {rows.Add(p.Fight.IsKill?"Output unavailable / not loaded. Choose this lens to retry.":"Completed kill required. No wipe parse is inferred.");return rows;}
            var standing=output.Rows.SingleOrDefault(r=>r.Player?.ActorId==p.ActorId);
            var parse=SelectedParse(p);
            if(standing==null)
            {
                rows.Add("No verified source row for this player. No zero is inferred.");
                rows.Add(Badge(parse));
            }
            else rows.Add(Standing(s.Report,p.Fight,standing with {Parse=parse},output.Rows.ToList().IndexOf(standing)+1,p.Lens=="damage"?"DPS":"HPS"));
            if(parse!=null)
                rows.Add($"Overall population: {parse.TotalParses} parses · ilvl parse: {(parse.ItemLevelPercentile.HasValue?RaidRecapParsePalette.Badge(parse.ItemLevelPercentile).Display:"unavailable")} · bracket ID {parse.Bracket} / ilvl {parse.ItemLevel}. No bracket sample count or world-rank claim.");
            rows.Add(output.Parses is { } context?$"WCL {context.Metric.ToUpperInvariant()} · Parses · Today · partition {context.Partition} · as of <t:{context.AsOf.ToUnixTimeSeconds()}:f>. Changes independently of report revision; finality unknown.":"Parse unavailable · raw output is separate.");
            return rows;
        }
        if(!p.Observations.TryGetValue(p.Lens,out var analysis)) {rows.Add("Observations unavailable / not loaded. Choose this lens to retry.");return rows;}
        if(p.Lens=="deaths")
            rows.AddRange(analysis.Deaths.Where(d=>d.ActorId==p.ActorId).Select(d=>$"{Elapsed(d.ElapsedMs)} · {RaidRecapLinks.Name(s.Report,p.Fight,d.ActorId,d.Name)} · {RaidRecapRules.Text(d.Ability,55)}"));
        else if(RaidRecapMechanics.IsMetric(p.Lens))
            rows.AddRange((analysis.Mechanic?.Events??Array.Empty<RaidRecapMechanicEvent>()).Where(d=>d.ActorId==p.ActorId).Select(d=>$"{Elapsed(d.ElapsedMs)} · {RaidRecapLinks.Name(s.Report,p.Fight,d.ActorId,d.Name)} · {Label(p.Lens)}"));
        else
            foreach(var spell in analysis.Utility)
                rows.AddRange(spell.Participants.Where(d=>d.VerifiedPlayer && d.ActorId==p.ActorId).Select(d=>$"{RaidRecapRules.Text(spell.Name,55)} · {RaidRecapLinks.Name(s.Report,p.Fight,d.ActorId.Value,p.Selected.Name)} · {Amount(d.Count)} attributed actions"));
        if(rows.Count==0)rows.Add(analysis.Complete && p.Lens=="deaths"?"No player death events in this complete scoped result.":"No retained observations for this player; no unobserved obligations or zero actions are inferred.");
        return rows;
    }
    public static IReadOnlyList<RaidRecapDeath> FirstLosses(IReadOnlyList<RaidRecapDeath> deaths)=>deaths.Count==0?Array.Empty<RaidRecapDeath>():deaths.Where(d=>d.ElapsedMs==deaths.Min(r=>r.ElapsedMs)).GroupBy(d=>d.ActorId).Select(g=>g.First()).ToArray();
    public static IReadOnlyList<string> FirstLossRows(RaidRecapSession s)
    {
        var result=RaidRecapReview.Current(s);
        if(result==null || !result.DeathsA.Complete || !result.DeathsB.Complete)return Array.Empty<string>();
        return new[]{("A",result.A,result.DeathsA),("B",result.B,result.DeathsB)}.SelectMany(pair=>FirstLosses(pair.Item3.Deaths)
            .Select(d=>$"{pair.Item1} · {Elapsed(d.ElapsedMs)} · {RaidRecapLinks.Name(s.Report,pair.Item2,d.ActorId,d.Name)}")).ToArray();
    }
    public static string Label(string lens)=>lens switch {"summary"=>"Summary","damage"=>"⚔️ Damage","healing"=>"💚 Healing","deaths"=>"☠ Deaths","interrupts"=>"✋ Interrupts","dispels"=>"✨ Dispels",RaidRecapMechanics.Junk=>"🔎 Throw Junk",RaidRecapMechanics.Spin=>"🔎 Shell Spin",_=>"Players"};
}
