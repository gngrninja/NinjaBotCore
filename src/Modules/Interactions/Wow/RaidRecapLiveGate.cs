#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>
/// Decides which servers may use the live raid recap. The bot owner sets the mode; during
/// rollout only servers the owner is a member of are allowed.
/// </summary>
public sealed class RaidRecapLiveGate
{
    /// <summary>Used until the owner picks a mode. Servers still have to opt in themselves.</summary>
    public const RaidRecapRolloutMode DefaultMode = RaidRecapRolloutMode.OwnerServers;

    private readonly IServiceScopeFactory _scopes;
    private readonly IRaidRecapLiveDiscord _discord;

    public RaidRecapLiveGate(IServiceScopeFactory scopes, IRaidRecapLiveDiscord discord)
    {
        _scopes = scopes;
        _discord = discord;
    }

    public async Task<RaidRecapRolloutMode> ModeAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var row = await db.RaidRecapRollout.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == RaidRecapRollout.SingletonId);
        return row?.Mode ?? DefaultMode;
    }

    public async Task SetModeAsync(RaidRecapRolloutMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        var row = await db.RaidRecapRollout.FirstOrDefaultAsync(r => r.Id == RaidRecapRollout.SingletonId);
        if (row == null)
        {
            row = new RaidRecapRollout { Id = RaidRecapRollout.SingletonId };
            db.RaidRecapRollout.Add(row);
        }

        row.Mode = mode;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<bool> AllowsAsync(ulong guildId) => await AllowsAsync(guildId, await ModeAsync());

    public async Task<bool> AllowsAsync(ulong guildId, RaidRecapRolloutMode mode)
    {
        switch (mode)
        {
            case RaidRecapRolloutMode.Everyone:
                return true;
            case RaidRecapRolloutMode.OwnerServers:
                foreach (var owner in await _discord.OwnerIdsAsync())
                {
                    if (await _discord.IsMemberAsync(guildId, owner))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
