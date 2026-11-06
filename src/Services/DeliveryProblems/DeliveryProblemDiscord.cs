#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services.DeliveryProblems
{
    public sealed record DeliveryServerInfo(bool Present, string? Name, ulong? OwnerId, string? OwnerName);

    /// <summary>A channel as the bot sees it now, and which of the needed permissions it lacks there.</summary>
    public sealed record DeliveryChannelInfo(bool Exists, string? Name, IReadOnlyList<string> MissingPermissions);

    public enum DeliveryDmOutcome
    {
        Sent,
        DmsClosed,
        Failed
    }

    /// <summary>What the Actionable Alerts page needs from Discord, so it can be tested offline.</summary>
    public interface IDeliveryProblemDiscord
    {
        DeliveryServerInfo Server(ulong serverId);

        /// <summary>Null when the bot is not in the server.</summary>
        DeliveryChannelInfo? Channel(ulong serverId, ulong channelId, IReadOnlyList<ChannelPermission> needed);

        /// <summary>From the member cache only: null when the cache can't tell.</summary>
        bool? InServer(ulong serverId, ulong userId);

        string? MemberName(ulong serverId, ulong userId);

        /// <summary>Asks Discord when the member cache can't tell. Null when Discord couldn't answer.</summary>
        Task<bool?> IsMemberAsync(ulong serverId, ulong userId);

        Task<DeliveryDmOutcome> SendDmAsync(ulong userId, string text);
    }

    public sealed class DeliveryProblemDiscord : IDeliveryProblemDiscord
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<DeliveryProblemDiscord> _logger;

        public DeliveryProblemDiscord(DiscordShardedClient client, ILogger<DeliveryProblemDiscord> logger)
        {
            _client = client;
            _logger = logger;
        }

        public DeliveryServerInfo Server(ulong serverId)
        {
            var guild = _client.GetGuild(serverId);
            return guild == null
                ? new DeliveryServerInfo(false, null, null, null)
                : new DeliveryServerInfo(true, guild.Name, guild.OwnerId, guild.Owner?.Username);
        }

        public DeliveryChannelInfo? Channel(ulong serverId, ulong channelId, IReadOnlyList<ChannelPermission> needed)
        {
            var guild = _client.GetGuild(serverId);
            if (guild == null)
            {
                return null;
            }

            if (channelId == 0 || guild.GetChannel(channelId) is not SocketGuildChannel channel)
            {
                return new DeliveryChannelInfo(false, null, Array.Empty<string>());
            }

            var permissions = guild.CurrentUser.GetPermissions(channel);
            var missing = needed.Where(p => !permissions.Has(p)).Select(Name).ToArray();
            return new DeliveryChannelInfo(true, channel.Name, missing);
        }

        public bool? InServer(ulong serverId, ulong userId)
        {
            var guild = _client.GetGuild(serverId);
            if (guild == null)
            {
                return null;
            }

            if (guild.GetUser(userId) != null)
            {
                return true;
            }

            return guild.HasAllMembers ? false : null;
        }

        public string? MemberName(ulong serverId, ulong userId)
        {
            var user = _client.GetGuild(serverId)?.GetUser(userId);
            return user?.Username;
        }

        public async Task<bool?> IsMemberAsync(ulong serverId, ulong userId)
        {
            if (InServer(serverId, userId) is { } cached)
            {
                return cached;
            }

            try
            {
                return await _client.Rest.GetGuildUserAsync(serverId, userId) != null;
            }
            catch (Exception ex)
            {
                // A rate limit or outage is not an answer; never report it as "left the server".
                _logger.LogDebug("Delivery problem membership lookup failed ({Type})", ex.GetType().Name);
                return null;
            }
        }

        public async Task<DeliveryDmOutcome> SendDmAsync(ulong userId, string text)
        {
            try
            {
                var user = await _client.Rest.GetUserAsync(userId);
                if (user == null)
                {
                    return DeliveryDmOutcome.Failed;
                }

                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
                return DeliveryDmOutcome.Sent;
            }
            catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
            {
                return DeliveryDmOutcome.DmsClosed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Delivery problem DM failed ({Type})", ex.GetType().Name);
                return DeliveryDmOutcome.Failed;
            }
        }

        /// <summary>The permission as Discord's own settings name it.</summary>
        public static string Name(ChannelPermission permission) => permission switch
        {
            ChannelPermission.ViewChannel => "View Channel",
            ChannelPermission.SendMessages => "Send Messages",
            ChannelPermission.EmbedLinks => "Embed Links",
            ChannelPermission.ManageMessages => "Manage Messages",
            ChannelPermission.ReadMessageHistory => "Read Message History",
            _ => permission.ToString()
        };
    }
}
