#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

public enum RaidRecapLiveSwitchStatus
{
    /// <summary>The card is on in <see cref="RaidRecapLiveSwitchResult.ChannelId"/>.</summary>
    On,

    /// <summary>The card is off, and any live card has stopped.</summary>
    Off,

    /// <summary>The rollout doesn't cover this server yet.</summary>
    NotAvailable,

    /// <summary>The server has no single WoW guild set.</summary>
    NoGuild,

    /// <summary>No channel was given and the server has no /watchlogs channel.</summary>
    NoChannel,

    /// <summary>The bot can't see, post or read history in the channel.</summary>
    CannotPost
}

public sealed record RaidRecapLiveSwitchResult(RaidRecapLiveSwitchStatus Status, ulong? ChannelId = null, string? GuildName = null);

/// <summary>
/// A server's live card as its officers see it. <see cref="TurnedOff"/> means an officer turned
/// it off, so the rollout leaves it off. <see cref="NoticeSentAt"/> is set when the rollout
/// turned the card on. <see cref="Live"/> is the newest live card, if a raid is live.
/// </summary>
public sealed record RaidRecapLiveServerState(
    bool Available,
    bool Enabled,
    bool TurnedOff,
    long? ChannelId,
    bool Selected,
    DateTime? NoticeSentAt,
    string? SetByName,
    DateTime? TimeSet,
    string? WowGuild,
    long? LogChannelId,
    RaidRecapLiveCard? Live)
{
    /// <summary>On, with a channel to post in. Settings saved by Off alone have no channel.</summary>
    public bool On => Enabled && ChannelId != null;
}

/// <summary>
/// The officers' switch for the live raid recap, shared by <c>/raid-recap-live</c> and the web
/// dashboard so both follow the same rules.
/// </summary>
public sealed class RaidRecapLiveSwitch
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RaidRecapLiveGate _gate;
    private readonly RaidRecapLiveCoordinator _coordinator;
    private readonly IRaidRecapLiveDiscord _discord;
    private readonly Func<DateTimeOffset> _clock;

    public RaidRecapLiveSwitch(
        IServiceScopeFactory scopes,
        RaidRecapLiveGate gate,
        RaidRecapLiveCoordinator coordinator,
        IRaidRecapLiveDiscord discord,
        Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _gate = gate;
        _coordinator = coordinator;
        _discord = discord;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Turns the card on. An explicit channel wins, then the /watchlogs channel, then
    /// <paramref name="fallback"/> (where the command was run).
    /// </summary>
    public async Task<RaidRecapLiveSwitchResult> TurnOnAsync(ulong guildId, ulong? channel, ulong? fallback, ulong userId, string userName)
    {
        if (!await _gate.AllowsAsync(guildId))
        {
            return new(RaidRecapLiveSwitchStatus.NotAvailable);
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var id = checked((long)guildId);
        var guild = await RaidRecapDiscord.FindGuildAsync(db, guildId);
        if (guild == null)
        {
            return new(RaidRecapLiveSwitchStatus.NoGuild);
        }

        var target = channel;
        if (target == null)
        {
            var watched = await LogChannelAsync(db, id);
            target = watched != null ? (ulong)watched.Value : fallback;
        }

        if (target == null)
        {
            return new(RaidRecapLiveSwitchStatus.NoChannel, null, guild.Name);
        }

        if (!_discord.CanPost(guildId, target.Value))
        {
            return new(RaidRecapLiveSwitchStatus.CannotPost, target, guild.Name);
        }

        var settings = await db.RaidRecapLiveSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == id);
        if (settings == null)
        {
            settings = new RaidRecapLiveSettings { DiscordGuildId = id };
            db.RaidRecapLiveSettings.Add(settings);
        }

        settings.Enabled = true;
        settings.ChannelId = checked((long)target.Value);
        Stamp(settings, userId, userName);
        await db.SaveChangesAsync();
        await _coordinator.StartServerAsync(guildId);
        return new(RaidRecapLiveSwitchStatus.On, target, guild.Name);
    }

    /// <summary>
    /// Turns the card off. Saved even when it was never on, so the rollout never turns it on later.
    /// </summary>
    public async Task<RaidRecapLiveSwitchResult> TurnOffAsync(ulong guildId, ulong userId, string userName)
    {
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var id = checked((long)guildId);
            var settings = await db.RaidRecapLiveSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == id);
            if (settings == null)
            {
                settings = new RaidRecapLiveSettings { DiscordGuildId = id };
                db.RaidRecapLiveSettings.Add(settings);
            }

            settings.Enabled = false;
            Stamp(settings, userId, userName);
            await db.SaveChangesAsync();
        }

        await _coordinator.StopServerAsync(guildId);
        return new(RaidRecapLiveSwitchStatus.Off);
    }

    public async Task<RaidRecapLiveServerState> StateAsync(ulong guildId)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var id = checked((long)guildId);
        var settings = await db.RaidRecapLiveSettings.AsNoTracking().FirstOrDefaultAsync(s => s.DiscordGuildId == id);
        var guild = await RaidRecapDiscord.FindGuildAsync(db, guildId);
        var live = await db.RaidRecapLiveCards.AsNoTracking()
            .Where(c => c.DiscordGuildId == id && c.State == RaidRecapLiveState.Live)
            .OrderByDescending(c => c.StartedAt)
            .FirstOrDefaultAsync();
        return new RaidRecapLiveServerState(
            await _gate.AllowsAsync(guildId),
            settings?.Enabled == true,
            settings is { Enabled: false },
            settings?.ChannelId,
            settings?.Selected == true,
            settings?.NoticeSentAt,
            settings?.SetByName,
            settings?.TimeSet,
            guild?.Name,
            await LogChannelAsync(db, id),
            live);
    }

    /// <summary>
    /// The server's /watchlogs channel, the one with the latest New log first. PostgreSQL sorts
    /// nulls first when descending, so a row that never had a log goes last explicitly.
    /// </summary>
    public static async Task<long?> LogChannelAsync(NinjaBotEntities db, long id) =>
        await db.LogMonitoring.AsNoTracking()
            .Where(l => l.ServerId == id && l.MonitorLogs && l.ChannelId > 0)
            .OrderByDescending(l => l.LatestLogRetail != null)
            .ThenByDescending(l => l.LatestLogRetail)
            .Select(l => (long?)l.ChannelId)
            .FirstOrDefaultAsync();

    private void Stamp(RaidRecapLiveSettings settings, ulong userId, string userName)
    {
        settings.SetById = checked((long)userId);
        settings.SetByName = userName.Length <= 100 ? userName : userName[..100];
        settings.TimeSet = _clock().UtcDateTime;
    }
}
