using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Modules.Interactions.Wow;

public class RaidRecapCommands : InteractionModuleBase<IInteractionContext>
{
    private readonly RaidRecapService _service;
    private readonly RaidRecapSessions _sessions;
    private readonly IRaidRecapDiscord _discord;
    private readonly ILogger<RaidRecapCommands> _logger;
    private const string Unavailable="Warcraft Logs is unavailable, access was denied, or the report changed. Refresh (30-second metadata cache), reopen /raid-recap, or open the report on Warcraft Logs.";
    public RaidRecapCommands(RaidRecapService service,RaidRecapSessions sessions,IRaidRecapDiscord discord,ILogger<RaidRecapCommands> logger)
    { _service=service;_sessions=sessions;_discord=discord;_logger=logger; }

    [SlashCommand("raid-recap","Privately explore a Warcraft Logs raid report")]
    public async Task StartAsync(
        [Summary("guild","Optional realm, guild, region override (retail)")] string guild=null,
        [Summary("report","Optional retail Warcraft Logs HTTPS report URL or exact code")] string report=null)
    {
        await DeferAsync(ephemeral:true);
        try
        {
            if(Context.Guild==null) { await NoticeAsync("Use /raid-recap in a server text channel."); return; }
            if(guild!=null&&report!=null) { await NoticeAsync("Choose either a guild override or a direct report, not both."); return; }
            if(report!=null) report=RaidRecapRules.ReportCode(report);
            var access=await _discord.AccessAsync(Context);
            if(!access.View) { await NoticeAsync("You and NinjaBot must be members with View Channel access. Use a normal server text channel (threads are not supported).");return; }
            var s=_sessions.Create(Context.User.Id,Context.Guild.Id,Context.Channel.Id);
            await _sessions.RunAsync(s.Token,s.Actor,s.Guild,s.Channel,0,async state=>
            {
                state.CanShare=access.Share;
                if(report!=null) await _service.OpenAsync(state,report);
                else
                {
                    RaidRecapGuild target;
                    if(guild==null) target=await _discord.GuildAsync(Context);
                    else
                    {
                        var parts=guild.Split(',').Select(v=>v.Trim()).ToArray();
                        if(parts.Length is <2 or >3||parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("Use realm, guild[, region] for the guild override.");
                        target=new RaidRecapGuild(parts[1],parts[0].ToLowerInvariant().Replace("'","").Replace(" ","-"),parts.Length==3?parts[2].ToLowerInvariant():"us");
                    }
                    await _service.DiscoverAsync(state,target.Name,target.Realm,target.Region);
                }
                if(!(await _discord.AccessAsync(Context)).View) { await NoticeAsync("Channel access changed. Reopen /raid-recap from an accessible text channel.");return; }
                if(!_sessions.IsCurrent(state)) { await ReopenAsync();return; }
                await Context.Interaction.ModifyToV2Async(RaidRecapView.Build(state));
            });
        }
        catch(ArgumentException ex) { await NoticeAsync(RaidRecapRules.Text(ex.Message,400)); }
        catch(Exception ex) { _logger.LogWarning("Raid recap open failed ({Type})",ex.GetType().Name);await NoticeAsync(Unavailable); }
    }

    [ComponentInteraction("rr_nav~*~*~*")]
    public Task NavigateAsync(string token,string generation,string action)=>TransitionAsync(token,generation,action,null);

    [ComponentInteraction("rr_pick~*~*~*")]
    public Task SelectAsync(string token,string generation,string action,string[] values)=>TransitionAsync(token,generation,action,values?.Length==1?values[0]:null);

    private async Task TransitionAsync(string token,string generation,string action,string value)
    {
        // Component acknowledgement updates the existing private message, never creates a public panel.
        await DeferAsync();
        if(Context.Guild==null||!int.TryParse(generation,NumberStyles.None,CultureInfo.InvariantCulture,out var version)) { await ReopenAsync();return; }
        var accepted=await _sessions.RunAsync(token,Context.User.Id,Context.Guild.Id,Context.Channel.Id,version,async s=>
        {
            try
            {
                var access=await SafeAccessAsync();
                if(!access.View||!_sessions.IsCurrent(s)) { await ReopenAsync();return; }
                s.CanShare=access.Share;
                if(action=="share")
                {
                    if(s.Report==null||!access.Share) s.Notice="Sharing requires current Send Messages and View Channel permissions for you and NinjaBot in this channel.";
                    else if(!s.TryBeginShare()) s.Notice="A share was already attempted; check the channel. Reopening is a new explicit share, not a retry.";
                    else
                    {
                        // Freeze the read-only overview before dispatch. An exception never resets this sentinel.
                        var snapshot=RaidRecapView.Build(s,shared:true);
                        try
                        {
                            var id=await _discord.PublishAsync(Context,snapshot,()=>_sessions.IsCurrent(s));
                            s.Notice=id>0?"Overview shared in this channel.":"Share outcome is unknown; check the channel. No automatic retry.";
                        }
                        catch(Exception ex)
                        {
                            _logger.LogWarning("Raid recap share outcome uncertain ({Type}); not retrying",ex.GetType().Name);
                            s.Notice="Share outcome is unknown; check the channel. No automatic retry.";
                        }
                    }
                }
                else await _service.ApplyAsync(s,action,value);
            }
            catch(Exception ex)
            {
                _logger.LogWarning("Raid recap transition failed ({Type})",ex.GetType().Name);
                s.Notice=Unavailable;
                s.Performance=null;
            }
            if(!(await SafeAccessAsync()).View||!_sessions.IsCurrent(s)) { await ReopenAsync();return; }
            // Keep the lock through the Discord edit, including error rendering.
            await Context.Interaction.ModifyToV2Async(RaidRecapView.Build(s));
        });
        if(!accepted) await ReopenAsync();
    }
    private async Task<RaidRecapAccess> SafeAccessAsync()
    {
        try { return await _discord.AccessAsync(Context); }
        catch(Exception ex) { _logger.LogWarning("Raid recap access check failed ({Type})",ex.GetType().Name);return new(false,false); }
    }
    private Task NoticeAsync(string message)=>Context.Interaction.ModifyToV2Async(WowCardV2.Notice("Raid recap",message,Color.Orange).Build());
    private Task ReopenAsync()=>FollowupAsync("This recap is expired, stale, or belongs to another user/channel. Reopen /raid-recap.",ephemeral:true,allowedMentions:AllowedMentions.None);
}
