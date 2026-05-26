#nullable enable

using System;
using System.Threading.Tasks;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    [RequireContext(ContextType.Guild)]
    [Group("pushgroup", "M+ push group coordination")]
    public class PushGroupCommands : NinjaBotBaseModule
    {
        private readonly PushGroupWizardState _wizardState;
        private readonly PushGroupCoordinator _coordinator;

        public PushGroupCommands(
            IServiceScopeFactory scopeFactory,
            PushGroupWizardState wizardState,
            PushGroupCoordinator coordinator)
            : base(scopeFactory)
        {
            _wizardState = wizardState;
            _coordinator = coordinator;
        }

        [SlashCommand("new", "Start the guided push-group creation wizard")]
        public async Task PushGroupNew()
        {
            await DeferAsync(ephemeral: true);

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.CurrentStep = 1;

            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [SlashCommand("create", "Fast-path: create a push group with all options up front")]
        public async Task PushGroupCreate(
            [Summary("dungeon", "Pick the dungeon")]
            [Autocomplete(typeof(DungeonAutocomplete))] string dungeonSlug,
            [Summary("key", "Target keystone level (e.g., 15)")] int keyLevel,
            [Summary("role", "Your role")]
            [Choice("Tank", "Tank"), Choice("Healer", "Healer"), Choice("DPS", "DPS")] string role = "DPS",
            [Summary("time", "When (e.g., 'in 30', '20:00', or skip)")] string? time = null,
            [Summary("notes", "Anything to add (optional)")] string? notes = null)
        {
            await DeferAsync(ephemeral: true);

            var dungeon = MythicPlusRotation.FindBySlug(dungeonSlug);
            if (dungeon == null)
            {
                await FollowupAsync($"Unknown dungeon `{dungeonSlug}`. Use the autocomplete to pick from the current rotation.", ephemeral: true);
                return;
            }

            if (keyLevel < 2 || keyLevel > 40)
            {
                await FollowupAsync("Key level must be between 2 and 40.", ephemeral: true);
                return;
            }

            DateTime? parsedTime = null;
            if (!string.IsNullOrWhiteSpace(time))
            {
                parsedTime = TimeParser.TryParse(time);
                if (parsedTime == null)
                {
                    await FollowupAsync($"Couldn't read the time `{time}`. Try `in 30`, `20:00`, or `8pm`.", ephemeral: true);
                    return;
                }
            }

            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.DungeonSlug = dungeon.Slug;
            state.DungeonName = dungeon.Name;
            state.KeyLevel = keyLevel;
            state.Role = role;
            state.Notes = notes;
            state.ScheduledForUtc = parsedTime;
            state.CurrentStep = 5; // jump to preview

            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }
    }
}
