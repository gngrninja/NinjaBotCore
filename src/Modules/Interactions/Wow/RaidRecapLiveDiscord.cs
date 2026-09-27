#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Interactions.Wow;

public enum RaidRecapLiveEdit
{
    Updated,

    /// <summary>The message no longer exists.</summary>
    Missing,

    /// <summary>The channel could not be reached right now.</summary>
    Unavailable
}

/// <summary>Everything the live recap needs from Discord, so the watcher can be tested offline.</summary>
public interface IRaidRecapLiveDiscord
{
    Task<IReadOnlyCollection<ulong>> OwnerIdsAsync();

    Task<bool> IsMemberAsync(ulong guildId, ulong userId);

    string? GuildIconUrl(ulong guildId);

    /// <summary>
    /// True when the bot can see the channel, post in it and read its history. History is
    /// needed to keep editing the card after a restart.
    /// </summary>
    bool CanPost(ulong guildId, ulong channelId);

    /// <summary>Posts the card and returns its message id, or null when the channel is unavailable.</summary>
    Task<ulong?> SendAsync(ulong channelId, MessageComponent card);

    Task<RaidRecapLiveEdit> EditAsync(ulong channelId, ulong messageId, MessageComponent card);
}

public sealed class RaidRecapLiveDiscord : IRaidRecapLiveDiscord
{
    private static readonly TimeSpan OwnerCache = TimeSpan.FromHours(1);

    private readonly DiscordShardedClient _client;
    private readonly IConfigurationRoot _config;
    private readonly ILogger<RaidRecapLiveDiscord> _logger;
    private readonly object _sync = new();
    private IReadOnlyCollection<ulong>? _owners;
    private DateTimeOffset _ownersAt;

    // The bot registers its settings as IConfigurationRoot, like every other service here.
    public RaidRecapLiveDiscord(DiscordShardedClient client, IConfigurationRoot config, ILogger<RaidRecapLiveDiscord> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<ulong>> OwnerIdsAsync()
    {
        lock (_sync)
        {
            if (_owners != null && DateTimeOffset.UtcNow - _ownersAt < OwnerCache)
            {
                return _owners;
            }
        }

        var ids = new HashSet<ulong>();
        var configured = _config.GetValue<ulong>("OwnerId");
        if (configured > 0)
        {
            ids.Add(configured);
        }

        try
        {
            // The same lookup [RequireOwner] uses for the owner-only admin commands.
            var application = await _client.GetApplicationInfoAsync();
            if (application?.Owner != null)
            {
                ids.Add(application.Owner.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap owner lookup failed ({Type})", ex.GetType().Name);
            if (ids.Count == 0)
            {
                // Do not cache an empty answer; try again on the next check.
                return ids;
            }
        }

        lock (_sync)
        {
            _owners = ids;
            _ownersAt = DateTimeOffset.UtcNow;
        }

        return ids;
    }

    public async Task<bool> IsMemberAsync(ulong guildId, ulong userId)
    {
        var guild = _client.GetGuild(guildId);
        if (guild == null)
        {
            return false;
        }

        if (guild.GetUser(userId) != null)
        {
            return true;
        }

        if (guild.HasAllMembers)
        {
            return false;
        }

        // The member list is still downloading after a restart. Ask Discord directly.
        try
        {
            return await _client.Rest.GetGuildUserAsync(guildId, userId) != null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Raid recap membership lookup failed ({Type})", ex.GetType().Name);
            return false;
        }
    }

    public string? GuildIconUrl(ulong guildId) => _client.GetGuild(guildId)?.IconUrl;

    public bool CanPost(ulong guildId, ulong channelId)
    {
        var guild = _client.GetGuild(guildId);
        var channel = guild?.GetTextChannel(channelId);
        if (guild?.CurrentUser == null || channel == null)
        {
            return false;
        }

        var permissions = guild.CurrentUser.GetPermissions(channel);
        return permissions.ViewChannel && permissions.SendMessages && permissions.ReadMessageHistory;
    }

    public async Task<ulong?> SendAsync(ulong channelId, MessageComponent card)
    {
        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            return null;
        }

        var sent = await channel.SendMessageAsync(
            components: card,
            flags: MessageFlags.ComponentsV2,
            allowedMentions: AllowedMentions.None,
            options: new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 });
        return sent.Id;
    }

    public async Task<RaidRecapLiveEdit> EditAsync(ulong channelId, ulong messageId, MessageComponent card)
    {
        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            // Cache miss during startup or an outage. Keep the card and try again later.
            return RaidRecapLiveEdit.Unavailable;
        }

        try
        {
            // Edit by id: one call, and no need for the message to be in the cache.
            await channel.ModifyMessageAsync(messageId, p =>
            {
                p.Components = card;
                p.Flags = MessageFlags.ComponentsV2;
                p.AllowedMentions = AllowedMentions.None;
            }, new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 });
            return RaidRecapLiveEdit.Updated;
        }
        catch (Discord.Net.HttpException ex) when (
            ex.DiscordCode == DiscordErrorCode.UnknownMessage || ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return RaidRecapLiveEdit.Missing;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            return RaidRecapLiveEdit.Unavailable;
        }
    }
}
