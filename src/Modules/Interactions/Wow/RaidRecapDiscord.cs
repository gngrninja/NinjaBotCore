using System;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace NinjaBotCore.Modules.Interactions.Wow;

public sealed record RaidRecapGuild(string Name,string Realm,string Region);
public sealed record RaidRecapAccess(bool View,bool Share);
public interface IRaidRecapDiscord
{
    Task<RaidRecapAccess> AccessAsync(IInteractionContext context);
    Task<RaidRecapGuild> GuildAsync(IInteractionContext context);
    Task<ulong> PublishAsync(IInteractionContext context,MessageComponent component,Func<bool> isCurrent);
}

public sealed class RaidRecapDiscord : IRaidRecapDiscord
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<IInteractionContext,Task<RaidRecapAccess>> _accessOverride;
    public RaidRecapDiscord(IServiceScopeFactory scopes) { _scopes=scopes; }
    internal RaidRecapDiscord(IServiceScopeFactory scopes,Func<IInteractionContext,Task<RaidRecapAccess>> access) : this(scopes)
    { _accessOverride=access??throw new ArgumentNullException(nameof(access)); }
    public static RaidRecapAccess Permissions(bool actorView,bool botView,bool actorSend,bool botSend)
        =>new(actorView&&botView,actorView&&botView&&actorSend&&botSend);

    public async Task<RaidRecapAccess> AccessAsync(IInteractionContext context)
    {
        if(_accessOverride!=null) return await _accessOverride(context);
        if(context.Guild==null||context.Client is not DiscordShardedClient client) return new(false,false);
        // Fresh REST guild includes roles; fresh users/channels avoid gateway-cache revocation gaps.
        var guild=await client.Rest.GetGuildAsync(context.Guild.Id);
        if(guild==null) return new(false,false);
        var channel=await guild.GetTextChannelAsync(context.Channel.Id);
        if(channel==null||channel is IThreadChannel) return new(false,false);
        var actor=await guild.GetUserAsync(context.User.Id);
        var bot=await guild.GetUserAsync(client.CurrentUser.Id);
        if(actor==null||bot==null) return new(false,false);
        var a=actor.GetPermissions(channel); var b=bot.GetPermissions(channel);
        return Permissions(a.ViewChannel,b.ViewChannel,a.SendMessages,b.SendMessages);
    }
    public async Task<RaidRecapGuild> GuildAsync(IInteractionContext context)
    {
        if(context.Guild==null) throw new ArgumentException("Use /setguild in a server, or supply an explicit report.");
        using var scope=_scopes.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var id=checked((long)context.Guild.Id);
        var matches=await db.WowGuildAssociations.AsNoTracking().Where(g=>g.ServerId==id).Take(2).ToListAsync();
        if(matches.Count!=1 || string.IsNullOrWhiteSpace(matches[0].WowGuild) || string.IsNullOrWhiteSpace(matches[0].WowRealm))
            throw new ArgumentException("No unique WoW guild association. Use /setguild or an explicit guild override/report.");
        var guild=matches[0];
        var region=(guild.WowRegion??"us").ToLowerInvariant();
        if(region=="ru") region="eu";
        var realm=string.IsNullOrWhiteSpace(guild.LocalRealmSlug)?guild.WowRealm.ToLowerInvariant().Replace("'","").Replace(" ","-"):guild.LocalRealmSlug;
        return new(guild.WowGuild,realm,region);
    }
    public async Task<ulong> PublishAsync(IInteractionContext context,MessageComponent component,Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        if(!(await AccessAsync(context)).Share) throw new InvalidOperationException("Channel permissions changed before sharing.");
        if(!isCurrent()) throw new InvalidOperationException("This recap expired or was evicted before sharing. Reopen /raid-recap.");
        var sent=await context.Channel.SendMessageAsync(components:component,flags:MessageFlags.ComponentsV2,
            allowedMentions:AllowedMentions.None,options:new RequestOptions { RetryMode=RetryMode.AlwaysFail,Timeout=15000 });
        return sent.Id;
    }
}
