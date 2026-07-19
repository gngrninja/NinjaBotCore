#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
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

            var state = await GetActiveWizardAsync();
            if (state == null) return;
            var slug = selected.FirstOrDefault();
            var dungeon = MythicPlusRotation.FindBySlug(slug ?? string.Empty);
            if (dungeon == null) { await FollowupAsync("Unknown dungeon.", ephemeral: true); return; }

            state.DungeonSlug = dungeon.Slug;
            state.DungeonName = dungeon.Name;

            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Wizard step 2: Key level =====

        [ComponentInteraction("pushgroup_wiz_key~*~*")]
        public async Task OnPickKey(string userIdStr, string levelStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            if (!int.TryParse(levelStr, out var lvl)) { await FollowupAsync("Bad key value.", ephemeral: true); return; }
            var state = await GetActiveWizardAsync();
            if (state == null) return;
            state.KeyLevel = lvl;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [ComponentInteraction("pushgroup_wiz_keymodal~*")]
        public async Task OnOpenKeyModal(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            if (await GetActiveWizardForModalAsync() == null) return;

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
            var state = await GetActiveWizardAsync();
            if (state == null) return;
            state.KeyLevel = lvl;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Composer: Role =====

        [ComponentInteraction("pushgroup_wiz_role~*~*")]
        public async Task OnPickRole(string userIdStr, string role)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = await GetActiveWizardAsync();
            if (state == null) return;
            state.Role = role;
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Composer: Time/notes modal =====

        [ComponentInteraction("pushgroup_wiz_timemodal~*")]
        public async Task OnOpenTimeModal(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            if (await GetActiveWizardForModalAsync() == null) return;

            var modal = new ModalBuilder()
                .WithTitle("Time & notes (optional)")
                .WithCustomId($"{ModalConstants.PushGroupWizardTimeModalPrefix}{userIdStr}")
                .AddTextInput("When? (UTC unless you add an offset)", "when", TextInputStyle.Short,
                    placeholder: "in 90m · 8pm UTC · 8pm -5 · blank = ASAP", required: false, maxLength: 50)
                .AddTextInput("Notes (one line)", "notes", TextInputStyle.Short,
                    placeholder: "e.g., 'farming portal', 'progression key'", required: false, maxLength: 200);

            await Context.Interaction.RespondWithModalAsync(modal.Build());
        }

        [ModalInteraction("pushgroup_wiz_timemodal~*")]
        public async Task OnTimeModalSubmit(string userIdStr, TimeNotesModal modal)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = await GetActiveWizardAsync();
            if (state == null) return;

            state.Notes = string.IsNullOrWhiteSpace(modal.Notes) ? null : modal.Notes!.Trim();

            // A non-empty time that doesn't parse must not silently become "ASAP" — keep the
            // user on this step and tell them (mirrors the /keys create validation).
            if (!string.IsNullOrWhiteSpace(modal.When))
            {
                var parsed = TimeParser.TryParse(modal.When);
                if (parsed == null)
                {
                    await PushGroupWizardRenderer.RenderStep(this, state);
                    await FollowupAsync(
                        $"Couldn't read the time `{modal.When!.Trim()}` (or it's in the past). Try `in 90m`, `20:00` (UTC), `8pm -5` (UTC offset), or a Discord timestamp. Your notes were kept.",
                        ephemeral: true);
                    return;
                }
                state.ScheduledForUtc = parsed;
            }
            else
            {
                state.ScheduledForUtc = null;
            }

            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        // ===== Composer actions =====

        [ComponentInteraction("pushgroup_wiz_post~*")]
        public async Task OnPost(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var state = await GetActiveWizardAsync();
            if (state == null) return;
            if (state.DungeonSlug == null || state.KeyLevel == null || state.Role == null)
            {
                await FollowupAsync("Wizard is missing required fields — go back and try again.", ephemeral: true);
                return;
            }

            var ioWindow = await ResolveIoWindowAsync((long)Context.Guild.Id);
            var displayName = (Context.User as IGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
            var (group, error) = await _coordinator.PostGroupAsync(state, Context.Guild.Id, Context.Channel.Id, Context.User.Id, displayName, ioWindow);

            if (group == null)
            {
                await FollowupAsync(error ?? "Couldn't post the group — check that I have permission to send messages in this channel.", ephemeral: true);
                return;
            }

            _wizardState.Remove(Context.User.Id, Context.Channel.Id);

            // The composer is a V2 message; the flag can't be removed on edit, so the
            // confirmation is a V2 text display too (the helper also clears any legacy
            // content/embed so pre-rewrite wizard cards upgrade instead of 50035-ing).
            await Context.Interaction.ModifyToV2Async(
                V2Text($"✅ Posted **+{group.TargetKeyLevel} {group.DungeonName}** key group above."));
        }

        [ComponentInteraction("pushgroup_wiz_cancel~*")]
        public async Task OnCancel(string userIdStr)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            _wizardState.Remove(Context.User.Id, Context.Channel.Id);
            await Context.Interaction.ModifyToV2Async(V2Text("Composer closed — nothing was posted."));
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
        public async Task OnOpenBringKey(string payload)
        {
            // The wildcard crosses '~' (no InteractionCustomIdDelimiters configured), so payload is
            // "{groupId}" on posts from older builds or "{groupId}~{targetKeyLevel}" on current ones.
            var parts = payload.Split('~', 2);
            if (!long.TryParse(parts[0], out _)) { await RespondAsync("Bad group id.", ephemeral: true); return; }

            // The group is already pinned to one dungeon, so we don't ask for it again — just the
            // key level. The group's target level rides in the button's custom id (a modal must be
            // the first response, so no DB roundtrip here); the submit handler re-validates the
            // group anyway.
            var prefill = parts.Length > 1 && int.TryParse(parts[1], out var target) ? target.ToString() : string.Empty;

            var modal = new ModalBuilder()
                .WithTitle("Bring a key")
                .WithCustomId($"{ModalConstants.PushGroupBringKeyModalPrefix}{parts[0]}")
                .AddTextInput("Key level", "level", TextInputStyle.Short,
                    placeholder: "e.g., 14", required: true, maxLength: 2,
                    value: prefill);

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
            var msg = await _coordinator.SetKeyHolderAsync(groupId, Context.User.Id, name, lvl);
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

        // ===== Hub / creation shortcuts =====

        [ComponentInteraction("pushgroup_hubnew")]
        public async Task OnHubNewGroup()
        {
            // The hub button must NOT edit the hub card — spawn a fresh ephemeral response
            // (type-5 loading ack) and render the composer into it.
            await ((IComponentInteraction)Context.Interaction).DeferLoadingAsync(ephemeral: true);

            _wizardState.Remove(Context.User.Id, Context.Channel.Id);
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [ComponentInteraction("pushgroup_rerun~*")]
        public async Task OnRerun(string groupIdStr)
        {
            if (!long.TryParse(groupIdStr, out var groupId)) { await RespondAsync("Bad group id.", ephemeral: true); return; }
            await ((IComponentInteraction)Context.Interaction).DeferLoadingAsync(ephemeral: true);

            var old = await WithDbAsync(async db =>
                await db.PushGroups.FirstOrDefaultAsync(g => g.Id == groupId));
            if (old == null)
            {
                await Context.Interaction.ModifyToV2Async(V2Text("That group no longer exists."));
                return;
            }

            // Composer pre-filled from the finished group — same dungeon/key/notes, the
            // clicker becomes the new host. Schedule intentionally not carried (it's past),
            // and the dungeon only carries over if it's still in the current rotation.
            _wizardState.Remove(Context.User.Id, Context.Channel.Id);
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            if (MythicPlusRotation.FindBySlug(old.DungeonSlug) != null)
            {
                state.DungeonSlug = old.DungeonSlug;
                state.DungeonName = old.DungeonName;
            }
            state.KeyLevel = old.TargetKeyLevel;
            state.Notes = old.Notes;
            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [ComponentInteraction("pushgroup_keygo~*~*")]
        public async Task OnKeyGo(string userIdStr, string role)
        {
            if (!await GateWizardOwner(userIdStr)) return;
            await DeferAsync();

            var keystone = await WithDbAsync(async db => await db.UserKeystones.FindAsync((long)Context.User.Id));
            if (keystone == null || keystone.WeekStartUtc <= MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow))
            {
                await Context.Interaction.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = "⌛ No current-week key registered — run `/keys board set` first.";
                    p.Components = new ComponentBuilder().Build();
                });
                return;
            }

            var state = new PushGroupWizardState.State
            {
                UserId = Context.User.Id,
                ChannelId = Context.Channel.Id,
                DungeonSlug = keystone.DungeonSlug,
                DungeonName = keystone.DungeonName,
                KeyLevel = keystone.KeyLevel,
                Role = role,
            };
            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);

            var ioWindow = await ResolveIoWindowAsync((long)Context.Guild.Id);
            var displayName = (Context.User as IGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
            var (group, error) = await _coordinator.PostGroupAsync(state, Context.Guild.Id, Context.Channel.Id, Context.User.Id, displayName, ioWindow);

            await Context.Interaction.ModifyOriginalResponseAsync(p =>
            {
                p.Content = group != null
                    ? $"✅ Posted **+{group.TargetKeyLevel} {group.DungeonName}** with you as {role} and key holder. Good luck!"
                    : (error ?? "Couldn't post the group — check my permissions in this channel.");
                p.Components = new ComponentBuilder().Build();
            });
        }

        // ===== Helpers =====

        private static MessageComponent V2Text(string text) =>
            new ComponentBuilderV2().AddComponent(new TextDisplayBuilder().WithContent(text)).Build();

        private async Task<bool> GateWizardOwner(string userIdStr)
        {
            if (!ulong.TryParse(userIdStr, out var owner) || owner != Context.User.Id)
            {
                await RespondAsync("This wizard belongs to someone else.", ephemeral: true);
                return false;
            }
            return true;
        }

        private const string WizardExpiredMessage = "⌛ This wizard expired — run `/keys new` to start over.";

        /// <summary>
        /// Resolves the caller's live wizard after DeferAsync. If it expired (TTL or restart),
        /// replaces the stale wizard message with an expiry notice instead of silently
        /// advancing an empty wizard.
        /// </summary>
        private async Task<PushGroupWizardState.State?> GetActiveWizardAsync()
        {
            var state = _wizardState.TryGet(Context.User.Id, Context.Channel.Id);
            if (state == null)
            {
                await Context.Interaction.ModifyToV2Async(V2Text(WizardExpiredMessage));
            }
            return state;
        }

        /// <summary>
        /// Same as GetActiveWizardAsync but for handlers that must respond with a modal
        /// (no ack has happened yet, so the expiry notice goes out as the response).
        /// </summary>
        private async Task<PushGroupWizardState.State?> GetActiveWizardForModalAsync()
        {
            var state = _wizardState.TryGet(Context.User.Id, Context.Channel.Id);
            if (state == null)
            {
                await RespondAsync(WizardExpiredMessage, ephemeral: true);
            }
            return state;
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

        [InputLabel("When? (UTC unless you add an offset)")]
        [ModalTextInput("when", TextInputStyle.Short, "in 90m · 8pm UTC · 8pm -5 · blank = ASAP", maxLength: 50)]
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
    }
}
