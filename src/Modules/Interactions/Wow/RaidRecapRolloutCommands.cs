#nullable enable

using System;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Attributes;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>The bot owner's switch for which servers may use the live raid recap.</summary>
[OwnerGuildOnly]
public class RaidRecapRolloutCommands : InteractionModuleBase<IInteractionContext>
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RaidRecapLiveGate _gate;
    private readonly RaidRecapLiveCoordinator _coordinator;
    private readonly NinjaBotCore.Modules.Wow.WarcraftLogsV2Client _warcraftLogs;
    private readonly ILogger<RaidRecapRolloutCommands> _logger;

    public RaidRecapRolloutCommands(
        IServiceScopeFactory scopes,
        RaidRecapLiveGate gate,
        RaidRecapLiveCoordinator coordinator,
        NinjaBotCore.Modules.Wow.WarcraftLogsV2Client warcraftLogs,
        ILogger<RaidRecapRolloutCommands> logger)
    {
        _scopes = scopes;
        _gate = gate;
        _coordinator = coordinator;
        _warcraftLogs = warcraftLogs;
        _logger = logger;
    }

    [Discord.Interactions.RequireOwner]
    [SlashCommand("raid-recap-rollout", "Bot owner: choose which servers may use the live raid recap")]
    public async Task RolloutAsync(
        [Summary("mode", "Where the live raid recap is available")]
        [Choice("Status", "status")]
        [Choice("Off (kill switch)", "off")]
        [Choice("My servers", "mine")]
        [Choice("Everyone", "everyone")]
        string mode = "status")
    {
        await DeferAsync(ephemeral: true);
        try
        {
            RaidRecapRolloutMode? requested = mode switch
            {
                "off" => RaidRecapRolloutMode.Off,
                "mine" => RaidRecapRolloutMode.OwnerServers,
                "everyone" => RaidRecapRolloutMode.Everyone,
                _ => null
            };
            var previous = await _gate.ModeAsync();
            if (requested.HasValue && requested.Value != previous)
            {
                await _gate.SetModeAsync(requested.Value);
            }

            var current = await _gate.ModeAsync();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var status = await RaidRecapLiveStatus.BuildAsync(
                db, current, _coordinator.Health.Snapshot(), _warcraftLogs.LastRateLimit, DateTimeOffset.UtcNow);
            await NoticeAsync(
                RolloutLine(current, previous, requested.HasValue) + "\n" + status.Text(),
                current == RaidRecapRolloutMode.Off ? Color.LightGrey : Color.Green);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap rollout change failed ({Type})", ex.GetType().Name);
            await NoticeAsync("Something went wrong changing the rollout. Try again shortly.", Color.Orange);
        }
    }

    /// <summary>Says "changed" only when the mode really is different from before.</summary>
    public static string RolloutLine(RaidRecapRolloutMode current, RaidRecapRolloutMode previous, bool requested)
    {
        var line = $"Rollout is **{Describe(current)}**";
        if (!requested)
        {
            return line + ".";
        }

        return current == previous
            ? line + ". It was already set to that, so nothing changed."
            : line + $". Changed from {Describe(previous)}.";
    }

    private static string Describe(RaidRecapRolloutMode mode) => mode switch
    {
        RaidRecapRolloutMode.Off => "off everywhere",
        RaidRecapRolloutMode.OwnerServers => "limited to servers you are a member of",
        RaidRecapRolloutMode.Everyone => "open to every server",
        _ => "unknown"
    };

    private Task NoticeAsync(string message, Color accent) =>
        Context.Interaction.ModifyToV2Async(WowCardV2.Notice(RaidRecapLiveCommands.Title, message, accent).Build());
}
