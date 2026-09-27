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
        // ShardedInteractionContext exposes its socket shard through IInteractionContext.Client.
        if(context.Guild==null||context.Client is not BaseSocketClient client) return new(false,false);
        // Fresh REST guild includes roles; fresh users/channels avoid gateway-cache revocation gaps.
        var guild=await client.Rest.GetGuildAsync(context.Guild.Id);
        if(guild==null) return new(false,false);
        var channel=await guild.GetTextChannelAsync(context.Channel.Id);
        // Voice channels also derive from RestTextChannel in Discord.Net; require a text/news type.
        if(channel==null||channel.ChannelType is not (ChannelType.Text or ChannelType.News)) return new(false,false);
        var actor=await guild.GetUserAsync(context.User.Id);
        var bot=await guild.GetUserAsync(client.CurrentUser.Id);
        if(actor==null||bot==null) return new(false,false);
        var a=actor.GetPermissions(channel); var b=bot.GetPermissions(channel);
        // Discord.Net resolves roles/overwrites but does not apply communication_disabled_until.
        // Preserve private viewing while refusing bot-mediated sends for a fresh active timeout.
        var now=DateTimeOffset.UtcNow;
        return Permissions(a.ViewChannel,b.ViewChannel,
            a.SendMessages&&!(actor.TimedOutUntil>now),b.SendMessages&&!(bot.TimedOutUntil>now));
    }
    public async Task<RaidRecapGuild> GuildAsync(IInteractionContext context)
    {
        if(context.Guild==null) throw new ArgumentException("Use /setguild in a server, or supply an explicit report.");
        using var scope=_scopes.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        return await FindGuildAsync(db,context.Guild.Id)
            ??throw new ArgumentException("No unique WoW guild association. Use /setguild or an explicit guild override/report.");
    }
    /// <summary>The server's single associated WoW guild, or null when there is none or more than one.</summary>
    public static async Task<RaidRecapGuild> FindGuildAsync(NinjaBotEntities db,ulong serverId)
    {
        var id=checked((long)serverId);
        var matches=await db.WowGuildAssociations.AsNoTracking().Where(g=>g.ServerId==id).Take(2).ToListAsync();
        if(matches.Count!=1 || string.IsNullOrWhiteSpace(matches[0].WowGuild) || string.IsNullOrWhiteSpace(matches[0].WowRealm))
            return null;
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
