#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>
/// The two switches for the live raid recap: officers turn it on for their server, and the
/// bot owner decides which servers are eligible at all.
/// </summary>
public class RaidRecapLiveCommands : InteractionModuleBase<IInteractionContext>
{
    private const string Title = "Live raid recap";

    private readonly IServiceScopeFactory _scopes;
    private readonly RaidRecapLiveGate _gate;
    private readonly RaidRecapLiveCoordinator _coordinator;
    private readonly IRaidRecapLiveDiscord _discord;
    private readonly ILogger<RaidRecapLiveCommands> _logger;

    public RaidRecapLiveCommands(
        IServiceScopeFactory scopes,
        RaidRecapLiveGate gate,
        RaidRecapLiveCoordinator coordinator,
        IRaidRecapLiveDiscord discord,
        ILogger<RaidRecapLiveCommands> logger)
    {
        _scopes = scopes;
        _gate = gate;
        _coordinator = coordinator;
        _discord = discord;
        _logger = logger;
    }

    [RequireUserPermission(GuildPermission.KickMembers)]
    [DefaultMemberPermissions(GuildPermission.KickMembers)]
    [SlashCommand("raid-recap-live", "Post a live raid recap card that updates while your raid is logging")]
    public async Task ToggleAsync(
        [Summary("state", "Turn the live card on or off, or check its status")]
        [Choice("Status", "status")]
        [Choice("On", "on")]
        [Choice("Off", "off")]
        string state = "status",
        [Summary("channel", "Where to post the card. Defaults to your /watchlogs channel")]
        ITextChannel? channel = null)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            if (Context.Guild == null)
            {
                await NoticeAsync("Use this command in a server.", Color.Orange);
                return;
            }

            var guildId = Context.Guild.Id;
            var id = checked((long)guildId);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var settings = await db.RaidRecapLiveSettings.FirstOrDefaultAsync(s => s.DiscordGuildId == id);
            var allowed = await _gate.AllowsAsync(guildId);

            switch (state)
            {
                case "on":
                    await TurnOnAsync(db, settings, channel, allowed);
                    return;
                case "off":
                    if (settings != null)
                    {
                        settings.Enabled = false;
                        Stamp(settings);
                        await db.SaveChangesAsync();
                    }

                    await _coordinator.StopServerAsync(guildId);
                    await NoticeAsync("The live raid recap is **off** for this server. Any card that was live has stopped updating.", Color.LightGrey);
                    return;
                default:
                    await StatusAsync(db, settings, allowed);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap live toggle failed ({Type})", ex.GetType().Name);
            await NoticeAsync("Something went wrong changing the live raid recap. Try again shortly.", Color.Orange);
        }
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
            if (requested.HasValue)
            {
                await _gate.SetModeAsync(requested.Value);
            }

            var current = await _gate.ModeAsync();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var servers = await db.RaidRecapLiveSettings.CountAsync(s => s.Enabled);
            var live = await db.RaidRecapLiveCards.CountAsync(c => c.State == RaidRecapLiveState.Live);
            await NoticeAsync(
                $"Rollout is **{Describe(current)}**{(requested.HasValue ? " (just changed)" : "")}.\n"
                + $"Servers opted in: **{servers}**\n"
                + $"Cards live right now: **{live}** of {RaidRecapLiveCoordinator.MaxLiveCards} allowed",
                current == RaidRecapRolloutMode.Off ? Color.LightGrey : Color.Green);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap rollout change failed ({Type})", ex.GetType().Name);
            await NoticeAsync("Something went wrong changing the rollout. Try again shortly.", Color.Orange);
        }
    }

    private async Task TurnOnAsync(NinjaBotEntities db, RaidRecapLiveSettings? settings, ITextChannel? channel, bool allowed)
    {
        var guildId = Context.Guild.Id;
        var id = checked((long)guildId);
        if (!allowed)
        {
            await NoticeAsync("The live raid recap is in a limited rollout and isn't available in this server yet.", Color.Orange);
            return;
        }

        var guild = await RaidRecapDiscord.FindGuildAsync(db, guildId);
        if (guild == null)
        {
            await NoticeAsync("This server has no WoW guild set. Run `/setguild` first, then turn the live raid recap on.", Color.Orange);
            return;
        }

        // An explicit channel wins, then the /watchlogs channel, then wherever this was run.
        var target = channel?.Id;
        if (target == null)
        {
            var watched = await db.LogMonitoring.AsNoTracking()
                .Where(l => l.ServerId == id && l.MonitorLogs)
                .Select(l => (long?)l.ChannelId)
                .FirstOrDefaultAsync();
            target = watched > 0 ? (ulong)watched.Value : Context.Channel.Id;
        }

        if (!_discord.CanPost(guildId, target.Value))
        {
            await NoticeAsync($"I can't keep a card updated in <#{target}>. Give me View Channel, Send Messages and Read Message History there, or pick another channel.", Color.Orange);
            return;
        }

        if (settings == null)
        {
            settings = new RaidRecapLiveSettings { DiscordGuildId = id };
            db.RaidRecapLiveSettings.Add(settings);
        }

        settings.Enabled = true;
        settings.ChannelId = checked((long)target.Value);
        Stamp(settings);
        await db.SaveChangesAsync();
        await NoticeAsync(
            $"The live raid recap is **on**.\n"
            + $"When the next raid log for **{NinjaBotCore.Modules.Wow.RaidRecapRules.Text(guild.Name, 80)}** goes live, "
            + $"I'll post one card in <#{target}> and keep it updated until the raid ends.",
            Color.Green);
    }

    private async Task StatusAsync(NinjaBotEntities db, RaidRecapLiveSettings? settings, bool allowed)
    {
        var id = checked((long)Context.Guild.Id);
        if (!allowed)
        {
            await NoticeAsync("The live raid recap is in a limited rollout and isn't available in this server yet.", Color.LightGrey);
            return;
        }

        if (settings?.Enabled != true || settings.ChannelId == null)
        {
            await NoticeAsync("The live raid recap is **off** for this server. Run `/raid-recap-live state:On` to turn it on.", Color.LightGrey);
            return;
        }

        var live = await db.RaidRecapLiveCards.AsNoTracking()
            .Where(c => c.DiscordGuildId == id && c.State == RaidRecapLiveState.Live)
            .OrderByDescending(c => c.StartedAt)
            .FirstOrDefaultAsync();
        var text = $"The live raid recap is **on**, posting in <#{settings.ChannelId}>.\n";
        text += live == null
            ? "No raid is live right now. I check for a new log every few minutes."
            : $"A raid is live: https://discord.com/channels/{Context.Guild.Id}/{live.ChannelId}/{live.MessageId}";
        await NoticeAsync(text, Color.Green);
    }

    private void Stamp(RaidRecapLiveSettings settings)
    {
        settings.SetById = checked((long)Context.User.Id);
        var name = Context.User.Username ?? "";
        settings.SetByName = name.Length <= 100 ? name : name[..100];
        settings.TimeSet = DateTime.UtcNow;
    }

    private static string Describe(RaidRecapRolloutMode mode) => mode switch
    {
        RaidRecapRolloutMode.Off => "off everywhere",
        RaidRecapRolloutMode.OwnerServers => "limited to servers you are a member of",
        RaidRecapRolloutMode.Everyone => "open to every server",
        _ => "unknown"
    };

    private Task NoticeAsync(string message, Color accent) =>
        Context.Interaction.ModifyToV2Async(WowCardV2.Notice(Title, message, accent).Build());
}
