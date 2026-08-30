using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaBotCore.Services.Api;

internal enum PollDiscordGuildLookupStatus
{
    Found,
    ClientUnavailable,
    GuildUnavailable
}

internal readonly record struct PollDiscordGuildLookupResult(
    PollDiscordGuildLookupStatus Status,
    SocketGuild Guild);

internal static class PollDiscordGuildResolver
{
    public static PollDiscordGuildLookupResult Resolve(IServiceProvider serviceProvider, long guildId)
    {
        var client = serviceProvider.GetService<DiscordShardedClient>();
        if (client == null || client.ConnectionState != ConnectionState.Connected)
        {
            return new PollDiscordGuildLookupResult(
                PollDiscordGuildLookupStatus.ClientUnavailable,
                null);
        }

        return ResolveReadyGuild(client.GetGuild((ulong)guildId));
    }

    internal static PollDiscordGuildLookupResult ResolveReadyGuild(SocketGuild guild)
    {
        return guild == null
            ? new PollDiscordGuildLookupResult(
                PollDiscordGuildLookupStatus.GuildUnavailable,
                null)
            : new PollDiscordGuildLookupResult(
                PollDiscordGuildLookupStatus.Found,
                guild);
    }

    internal static Task<IGuildUser> ResolveUserAsync(SocketGuild guild, ulong userId) =>
        ResolveUserAsync(
            guild.GetUser(userId),
            () => ((IGuild)guild).GetUserAsync(userId, CacheMode.AllowDownload));

    internal static async Task<IGuildUser> ResolveUserAsync(
        IGuildUser cachedUser,
        Func<Task<IGuildUser>> downloadUser)
    {
        if (cachedUser != null)
        {
            return cachedUser;
        }

        ArgumentNullException.ThrowIfNull(downloadUser);
        return await downloadUser();
    }
}
