#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    [RequireContext(ContextType.Guild)]
    public class PushGroupComponentHandlers : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly ILogger<PushGroupComponentHandlers> _logger;
        private readonly PushGroupWizardState _wizardState;
        private readonly PushGroupCoordinator _coordinator;

        public PushGroupComponentHandlers(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            ILogger<PushGroupComponentHandlers> logger,
            PushGroupWizardState wizardState,
            PushGroupCoordinator coordinator)
            : base(scopeFactory)
        {
            _client = client;
            _logger = logger;
            _wizardState = wizardState;
            _coordinator = coordinator;
        }

        // ===== Wizard step 1: Dungeon select =====

        [ComponentInteraction("pushgroup_wiz_dungeon~*")]
        public async Task OnPickDungeon(string userIdStr, string[] selected)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            var slug = selected.FirstOrDefault();
            var dungeon = MythicPlusRotation.FindBySlug(slug ?? string.Empty);
            if (dungeon == null) { await FollowupAsync("Unknown dungeon.", ephemeral: true); return; }

            state.DungeonSlug = dungeon.Slug;
            state.DungeonName = dungeon.Name;
            state.CurrentStep = 2;

            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Wizard step 2: Key level =====

        [ComponentInteraction("pushgroup_wiz_key~*~*")]
        public async Task OnPickKey(string userIdStr, string levelStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            if (!int.TryParse(levelStr, out var lvl)) { await FollowupAsync("Bad key value.", ephemeral: true); return; }
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.KeyLevel = lvl;
            state.CurrentStep = 3;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [ComponentInteraction("pushgroup_wiz_keymodal~*")]
        public async Task OnOpenKeyModal(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;

            var modal = new ModalBuilder()
                .WithTitle("Custom key level")
                .WithCustomId($"{ModalConstants.PushGroupWizardKeyModalPrefix}{userIdStr}")
                .AddTextInput("Key level (2–40)", "level", TextInputStyle.Short, placeholder: "e.g., 15", required: true, minLength: 1, maxLength: 2);

            await Context.Interaction.RespondWithModalAsync(modal.Build());
        }

        [ModalInteraction("pushgroup_wiz_keymodal~*")]
        public async Task OnKeyModalSubmit(string userIdStr, KeyLevelModal modal)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            if (!int.TryParse(modal.Level?.Trim(), out var lvl) || lvl < 2 || lvl > 40)
            {
                await FollowupAsync("Key level must be a whole number between 2 and 40.", ephemeral: true);
                return;
            }
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.KeyLevel = lvl;
            state.CurrentStep = 3;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Wizard step 3: Role =====

        [ComponentInteraction("pushgroup_wiz_role~*~*")]
        public async Task OnPickRole(string userIdStr, string role)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.Role = role;
            state.CurrentStep = 4;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Wizard step 4: Time/notes =====

        [ComponentInteraction("pushgroup_wiz_timemodal~*")]
        public async Task OnOpenTimeModal(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;

            var modal = new ModalBuilder()
                .WithTitle("Time & notes (optional)")
                .WithCustomId($"{ModalConstants.PushGroupWizardTimeModalPrefix}{userIdStr}")
                .AddTextInput("When? (e.g. 'in 30', '20:00', '8pm')", "when", TextInputStyle.Short,
                    placeholder: "Leave blank for ASAP", required: false, maxLength: 50)
                .AddTextInput("Notes (one line)", "notes", TextInputStyle.Short,
                    placeholder: "e.g., 'farming portal', 'progression key'", required: false, maxLength: 200);

            await Context.Interaction.RespondWithModalAsync(modal.Build());
        }

        [ModalInteraction("pushgroup_wiz_timemodal~*")]
        public async Task OnTimeModalSubmit(string userIdStr, TimeNotesModal modal)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.ScheduledForUtc = TimeParser.TryParse(modal.When);
            state.Notes = string.IsNullOrWhiteSpace(modal.Notes) ? null : modal.Notes!.Trim();
            state.CurrentStep = 5;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [ComponentInteraction("pushgroup_wiz_skiptime~*")]
        public async Task OnSkipTime(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.CurrentStep = 5;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Wizard step 5: Preview actions =====

        [ComponentInteraction("pushgroup_wiz_post~*")]
        public async Task OnPost(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            if (state.DungeonSlug == null || state.KeyLevel == null || state.Role == null)
            {
                await FollowupAsync("Wizard is missing required fields — go back and try again.", ephemeral: true);
                return;
            }

            var ioWindow = await ResolveIoWindowAsync((long)Context.Guild.Id);
            var displayName = (Context.User as IGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
            var group = await _coordinator.PostGroupAsync(state, Context.Guild.Id, Context.Channel.Id, Context.User.Id, displayName, ioWindow);

            if (group == null)
            {
                await FollowupAsync("Couldn't post the group — check that I have permission to send messages in this channel.", ephemeral: true);
                return;
            }

            _wizardState.Remove(Context.User.Id, Context.Channel.Id);

            await Context.Interaction.ModifyOriginalResponseAsync(p =>
            {
                p.Content = $"✅ Posted **+{group.TargetKeyLevel} {group.DungeonName}** push group above.";
                p.Embed = null;
                p.Components = new ComponentBuilder().Build();
            });
        }

        [ComponentInteraction("pushgroup_wiz_cancel~*")]
        public async Task OnCancel(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            _wizardState.Remove(Context.User.Id, Context.Channel.Id);
            await Context.Interaction.ModifyOriginalResponseAsync(p =>
            {
                p.Content = "Wizard cancelled.";
                p.Embed = null;
                p.Components = new ComponentBuilder().Build();
            });
        }

        [ComponentInteraction("pushgroup_wiz_back~*~*")]
        public async Task OnBack(string userIdStr, string stepStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            if (!int.TryParse(stepStr, out var step)) step = 1;
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.CurrentStep = Math.Max(1, step);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Live-post buttons =====

        [ComponentInteraction("pushgroup_signup~*~*")]
        public async Task OnSignup(string groupIdStr, string role)
        {
            if (!long.TryParse(groupIdStr, out var groupId)) { await RespondAsync("Bad group id.", ephemeral: true); return; }
            await DeferAsync(ephemeral: true);

            var name = (Context.User as IGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
            var msg = await _coordinator.AddSignupAsync(groupId, Context.User.Id, name, role);
            await FollowupAsync(msg, ephemeral: true);
        }

        [ComponentInteraction("pushgroup_withdraw~*")]
        public async Task OnWithdraw(string groupIdStr)
        {
            if (!long.TryParse(groupIdStr, out var groupId)) { await RespondAsync("Bad group id.", ephemeral: true); return; }
            await DeferAsync(ephemeral: true);

            var msg = await _coordinator.WithdrawAsync(groupId, Context.User.Id);
            await FollowupAsync(msg, ephemeral: true);
        }

        [ComponentInteraction("pushgroup_bringkey~*")]
        public async Task OnOpenBringKey(string groupIdStr)
        {
            if (!long.TryParse(groupIdStr, out _)) { await RespondAsync("Bad group id.", ephemeral: true); return; }

            var modal = new ModalBuilder()
                .WithTitle("Bring a key")
                .WithCustomId($"{ModalConstants.PushGroupBringKeyModalPrefix}{groupIdStr}")
                .AddTextInput("Key level", "level", TextInputStyle.Short, placeholder: "e.g., 14", required: true, maxLength: 2)
                .AddTextInput("Dungeon (display name)", "dungeon", TextInputStyle.Short, placeholder: "e.g., Darkflame Cleft", required: true, maxLength: 60);

            await Context.Interaction.RespondWithModalAsync(modal.Build());
        }

        [ModalInteraction("pushgroup_bringkeymodal~*")]
        public async Task OnBringKeyModal(string groupIdStr, BringKeyModal modal)
        {
            if (!long.TryParse(groupIdStr, out var groupId)) { await RespondAsync("Bad group id.", ephemeral: true); return; }
            await DeferAsync(ephemeral: true);

            if (!int.TryParse(modal.Level?.Trim(), out var lvl) || lvl < 2 || lvl > 40)
            {
                await FollowupAsync("Key level must be a whole number between 2 and 40.", ephemeral: true);
                return;
            }

            var name = (Context.User as IGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
            var msg = await _coordinator.SetKeyHolderAsync(groupId, Context.User.Id, name, lvl, modal.Dungeon?.Trim() ?? string.Empty);
            await FollowupAsync(msg, ephemeral: true);
        }

        [ComponentInteraction("pushgroup_close~*")]
        public async Task OnClose(string groupIdStr)
        {
            if (!long.TryParse(groupIdStr, out var groupId)) { await RespondAsync("Bad group id.", ephemeral: true); return; }
            await DeferAsync(ephemeral: true);

            var msg = await _coordinator.CloseAsync(groupId, Context.User.Id);
            await FollowupAsync(msg, ephemeral: true);
        }

        // ===== Helpers =====

        private async Task<bool> GateWizardOwner(string userIdStr)
        {
            if (!ulong.TryParse(userIdStr, out var owner) || owner != Context.User.Id)
            {
                await RespondAsync("This wizard belongs to someone else.", ephemeral: true);
                return false;
            }
            return true;
        }

        private async Task<int> ResolveIoWindowAsync(long guildId)
        {
            var settings = await WithDbAsync(async db =>
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .FirstOrDefaultAsync(db.ServerPushGroupSettings, s => s.DiscordGuildId == guildId));
            return settings?.DefaultIoWindow ?? PushGroupConstants.DefaultIoWindow;
        }
    }

    public class KeyLevelModal : IModal
    {
        public string Title => "Custom key level";

        [InputLabel("Key level (2–40)")]
        [ModalTextInput("level", TextInputStyle.Short, "e.g., 15", 1, 2)]
        public string? Level { get; set; }
    }

    public class TimeNotesModal : IModal
    {
        public string Title => "Time & notes";

        [InputLabel("When?")]
        [ModalTextInput("when", TextInputStyle.Short, "Leave blank for ASAP", maxLength: 50)]
        [RequiredInput(false)]
        public string? When { get; set; }

        [InputLabel("Notes")]
        [ModalTextInput("notes", TextInputStyle.Short, "Optional", maxLength: 200)]
        [RequiredInput(false)]
        public string? Notes { get; set; }
    }

    public class BringKeyModal : IModal
    {
        public string Title => "Bring a key";

        [InputLabel("Key level")]
        [ModalTextInput("level", TextInputStyle.Short, "e.g., 14", 1, 2)]
        public string? Level { get; set; }

        [InputLabel("Dungeon")]
        [ModalTextInput("dungeon", TextInputStyle.Short, "e.g., Darkflame Cleft", 1, 60)]
        public string? Dungeon { get; set; }
    }
}
