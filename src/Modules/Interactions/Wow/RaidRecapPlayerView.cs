using System;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Modules.Wow;
namespace NinjaBotCore.Modules.Interactions.Wow;

public static class RaidRecapPlayerView
{
    public static MessageComponent Build(RaidRecapSession s)
    {
        var p=s.PlayerPanel;var fight=p.Fight;
        var scope=new ContainerBuilder().WithAccentColor(new Color(0x5865F2));
        scope.AddComponent(new TextDisplayBuilder("# Raid recap · Players\n"+RaidRecapRules.Text(s.Report.Title,120)));
        var controls=new ComponentBuilder();
        foreach(var tab in new[]{"overview","bosses","damage","healing","analysis"})
            controls.WithButton(char.ToUpperInvariant(tab[0])+tab[1..],RaidRecapView.Nav(s,tab),ButtonStyle.Secondary,row:0);
        controls.WithButton("Reports",RaidRecapView.Nav(s,"reports"),ButtonStyle.Secondary,disabled:s.Reports.Count==0,row:1)
            .WithButton("Refresh",RaidRecapView.Nav(s,"refresh"),ButtonStyle.Secondary,row:1)
            .WithButton(s.ShareAttempted?"Share attempted":"Share overview",RaidRecapView.Nav(s,"share"),ButtonStyle.Success,disabled:!s.CanShare||s.ShareAttempted,row:1)
            .WithButton("Warcraft Logs",style:ButtonStyle.Link,url:s.Report.Url,row:1)
            .WithButton("Back",RaidRecapView.Nav(s,"player_back"),ButtonStyle.Secondary,row:1);
        var parse=RaidRecapPlayerPresentation.SelectedParse(p);
        var card=new ContainerBuilder().WithAccentColor(new Color(RaidRecapParsePalette.Badge(parse?.Percentile).Color));
        if(!string.IsNullOrEmpty(s.Notice))card.AddComponent(new TextDisplayBuilder(RaidRecapRules.Text(s.Notice,260)));
        if(fight==null)
        {
            card.AddComponent(new TextDisplayBuilder("## Choose completed pull\nChoose one pull before a player. No observations load automatically."));
            var pulls=s.Report.CompletedPulls;
            if(pulls.Count==0)card.AddComponent(new TextDisplayBuilder("No completed pulls available."));
            else
            {
                var menu=new SelectMenuBuilder().WithCustomId(RaidRecapView.Pick(s,"player_pull")).WithPlaceholder($"Choose completed pull · options page {p.PullPage+1}").WithMinValues(1).WithMaxValues(1);
                foreach(var f in pulls.Skip(p.PullPage*25).Take(25))menu.AddOption($"{RaidRecapRules.Text(f.Name,45)} · {(f.IsKill?"Kill":"Wipe")} #{f.Id}",f.Id.ToString());
                controls.WithSelectMenu(menu,row:2);
                controls.WithButton("Previous pulls",RaidRecapView.Nav(s,"player_pull_prev"),ButtonStyle.Secondary,disabled:p.PullPage==0,row:3)
                    .WithButton("Next pulls",RaidRecapView.Nav(s,"player_pull_next"),ButtonStyle.Secondary,disabled:(p.PullPage+1)*25>=pulls.Count,row:3);
            }
        }
        else
        {
            card.AddComponent(new SectionBuilder(new ButtonBuilder("Change pull",RaidRecapView.Nav(s,"player_change"),ButtonStyle.Secondary),
                new TextDisplayBuilder($"**{RaidRecapRules.Text(fight.Name,60)} · {RaidRecapView.Difficulty(fight.Difficulty)} · {(fight.IsKill?"Kill":"Wipe")} #{fight.Id}**\nFull pull · elapsed {RaidRecapView.Duration(fight.DurationMs)}")));
            if(p.Roster==null)card.AddComponent(new TextDisplayBuilder("Player roster unavailable. Change pull to retry or open WCL."));
            else
            {
                if(!p.Roster.Complete)card.AddComponent(new TextDisplayBuilder("⚠ Partial roster identity. Unverified members are not linked; open the full fight in WCL."));
                if(p.Roster.Players.Count>0)
                {
                    var menu=new SelectMenuBuilder().WithCustomId(RaidRecapView.Pick(s,"player")).WithPlaceholder($"Choose player · options page {p.OptionPage+1}/{(p.Roster.Players.Count+24)/25}").WithMinValues(1).WithMaxValues(1);
                    foreach(var player in p.Roster.Players.Skip(p.OptionPage*25).Take(25))
                        menu.AddOption(RaidRecapRules.Text(player.Name,55)+$" · #{player.ActorId}",player.ActorId.ToString(),description:RaidRecapPlayerPresentation.Identity(player),isDefault:player.ActorId==p.ActorId);
                    controls.WithSelectMenu(menu,row:2);
                }
                if(p.Selected is not { } chosen)card.AddComponent(new TextDisplayBuilder("Choose a player. The catalog includes players with no observations; opening Summary makes no provider request."));
                else
                {
                    card.AddComponent(new TextDisplayBuilder(RaidRecapLinks.Name(s.Report,fight,chosen.ActorId,chosen.Name)+"\n"+RaidRecapPlayerPresentation.Identity(chosen)));
                    var lens=new SelectMenuBuilder().WithCustomId(RaidRecapView.Pick(s,"player_lens")).WithPlaceholder("Choose a lens to load · Summary is local").WithMinValues(1).WithMaxValues(1);
                    foreach(var name in new[]{"summary","damage","healing","deaths","interrupts","dispels",RaidRecapMechanics.Junk,RaidRecapMechanics.Spin})
                        lens.AddOption(RaidRecapPlayerPresentation.Label(name),name,isDefault:name==p.Lens);
                    controls.WithSelectMenu(lens,row:3);
                    var pages=RaidRecapPlayerPresentation.Pages(s);var index=Math.Clamp(p.EvidencePage,0,pages.Count-1);
                    var body=new StringBuilder($"**{RaidRecapPlayerPresentation.Label(p.Lens)} · page {index+1}/{pages.Count}**\n");
                    if(p.Observations.TryGetValue(p.Lens,out var a))
                        body.AppendLine(a.Complete?"✓ Complete observations · events, not unique lives or obligations.":"⚠ Partial / unsupported observations · "+RaidRecapRules.Text(a.Notice,150));
                    if(p.Lens!="summary")
                    {
                        var rule=RaidRecapMechanics.Rule(p.Lens);var view=rule?.View??(p.Lens=="damage"?"damage-done":p.Lens);
                        body.AppendLine($"[Open fight evidence]({s.Report.Url}#fight={fight.Id}&type={view}"+(rule==null?"":$"&ability={rule.SpellId}")+") · player handoff below is general, not an event link.");
                    }
                    if(p.Lens=="healing")body.AppendLine("WCL Healing table total / elapsed seconds; not validated as effective healing or a quality grade.");
                    if(p.Lens=="damage")body.AppendLine("Damage / elapsed seconds; output position is not a parse rank.");
                    foreach(var row in pages[index])body.AppendLine(row);
                    if(parse!=null)body.AppendLine("Accent = overall WCL parse band. Numeric labels remain primary; fractional display floors, never rounds up.");
                    card.AddComponent(new TextDisplayBuilder(body.ToString()));
                    card.AddComponent(new SectionBuilder(new ButtonBuilder("Player in WCL",style:ButtonStyle.Link,url:RaidRecapLinks.Player(s.Report,fight,chosen.ActorId)),
                        new TextDisplayBuilder(RaidRecapPlayerPresentation.Question(p.Lens))));
                    controls.WithButton("Previous rows",RaidRecapView.Nav(s,"player_rows_prev"),ButtonStyle.Secondary,disabled:index==0,row:4)
                        .WithButton("Next rows",RaidRecapView.Nav(s,"player_rows_next"),ButtonStyle.Secondary,disabled:index==pages.Count-1,row:4);
                }
                controls.WithButton("Previous players",RaidRecapView.Nav(s,"player_options_prev"),ButtonStyle.Secondary,disabled:p.OptionPage==0,row:4)
                    .WithButton("Next players",RaidRecapView.Nav(s,"player_options_next"),ButtonStyle.Secondary,disabled:(p.OptionPage+1)*25>=p.Roster.Players.Count,row:4);
            }
        }
        card.AddComponent(new TextDisplayBuilder($"-# Private snapshot · as of <t:{s.Report.AsOf.ToUnixTimeSeconds()}:f> · may still update. Player links open the general player report, not an exact event. Incoming is fight-wide, not a personal total."));
        var rows=controls.Build().Components.OfType<ActionRowComponent>().ToArray();
        foreach(var row in rows.Take(2))scope.AddComponent(new ActionRowBuilder(row));
        foreach(var row in rows.Skip(2))card.AddComponent(new ActionRowBuilder(row));
        return new ComponentBuilderV2().AddComponent(scope).AddComponent(card).Build();
    }
}
