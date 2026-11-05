#nullable enable

using System;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>
/// Officers turn the live raid recap on for their server. Which servers are eligible at all
/// is the owner's switch in <see cref="RaidRecapRolloutCommands"/>.
/// </summary>
public class RaidRecapLiveCommands : InteractionModuleBase<IInteractionContext>
{
    internal const string Title = "Live raid recap";

    private readonly RaidRecapLiveSwitch _switch;
    private readonly ILogger<RaidRecapLiveCommands> _logger;

    public RaidRecapLiveCommands(RaidRecapLiveSwitch liveSwitch, ILogger<RaidRecapLiveCommands> logger)
    {
        _switch = liveSwitch;
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
            switch (state)
            {
                case "on":
                    var result = await _switch.TurnOnAsync(guildId, channel?.Id, Context.Channel.Id, Context.User.Id, Context.User.Username ?? "");
                    await (result.Status switch
                    {
                        RaidRecapLiveSwitchStatus.NotAvailable => NoticeAsync(NotAvailableText, Color.Orange),
                        RaidRecapLiveSwitchStatus.NoGuild => NoticeAsync("This server has no WoW guild set. Run `/setguild` first, then turn the live raid recap on.", Color.Orange),
                        RaidRecapLiveSwitchStatus.CannotPost => NoticeAsync($"I can't keep a card updated in <#{result.ChannelId}>. Give me View Channel, Send Messages and Read Message History there, or pick another channel.", Color.Orange),
                        RaidRecapLiveSwitchStatus.On => NoticeAsync(
                            $"The live raid recap is **on**.\n"
                            + $"When the next raid log for **{NinjaBotCore.Modules.Wow.RaidRecapRules.Text(result.GuildName ?? "", 80)}** goes live, "
                            + $"I'll post one card in <#{result.ChannelId}> and keep it updated until the raid ends.",
                            Color.Green),
                        _ => NoticeAsync("Pick a `channel` for the card.", Color.Orange)
                    });
                    return;
                case "off":
                    await _switch.TurnOffAsync(guildId, Context.User.Id, Context.User.Username ?? "");
                    await NoticeAsync("The live raid recap is **off** for this server. Any card that was live has stopped updating.", Color.LightGrey);
                    return;
                default:
                    await StatusAsync(await _switch.StateAsync(guildId));
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Raid recap live toggle failed ({Type})", ex.GetType().Name);
            await NoticeAsync("Something went wrong changing the live raid recap. Try again shortly.", Color.Orange);
        }
    }

    private const string NotAvailableText = "The live raid recap is in a limited rollout and isn't available in this server yet.";

    private async Task StatusAsync(RaidRecapLiveServerState state)
    {
        if (!state.Available)
        {
            await NoticeAsync(NotAvailableText, Color.LightGrey);
            return;
        }

        if (!state.On)
        {
            await NoticeAsync("The live raid recap is **off** for this server. Run `/raid-recap-live state:On` to turn it on.", Color.LightGrey);
            return;
        }

        var text = $"The live raid recap is **on**, posting in <#{state.ChannelId}>.\n";
        text += state.Live == null
            ? "No raid is live right now. I check for a new log every few minutes."
            : $"A raid is live: https://discord.com/channels/{Context.Guild.Id}/{state.Live.ChannelId}/{state.Live.MessageId}";
        await NoticeAsync(text, Color.Green);
    }

    private Task NoticeAsync(string message, Color accent) =>
        Context.Interaction.ModifyToV2Async(WowCardV2.Notice(Title, message, accent).Build());
}
