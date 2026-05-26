#nullable enable

using System;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Common;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Renders each step of the /pushgroup wizard into the caller's ephemeral interaction.
    /// Caller must have already called DeferAsync (ephemeral) before invoking RenderStep.
    /// </summary>
    public static class PushGroupWizardRenderer
    {
        public static Task RenderStep(InteractionModuleBase<ShardedInteractionContext> module, PushGroupWizardState.State state)
            => RenderStep(module.Context.Interaction, state);

        public static async Task RenderStep(IDiscordInteraction interaction, PushGroupWizardState.State state)
        {
            var (embed, components, content) = Build(state);

            await interaction.ModifyOriginalResponseAsync(p =>
            {
                p.Content = content ?? string.Empty;
                p.Embed = embed.Build();
                p.Components = components.Build();
            });
        }

        public static (EmbedBuilder embed, ComponentBuilder components, string? content) Build(PushGroupWizardState.State state)
        {
            return state.CurrentStep switch
            {
                1 => BuildStep1Dungeon(state),
                2 => BuildStep2Key(state),
                3 => BuildStep3Role(state),
                4 => BuildStep4TimeNotes(state),
                5 => BuildStep5Preview(state),
                _ => BuildStep1Dungeon(state),
            };
        }

        private static (EmbedBuilder, ComponentBuilder, string?) BuildStep1Dungeon(PushGroupWizardState.State state)
        {
            var embed = new EmbedBuilder()
                .WithTitle("🗝️ Push Group — Step 1 of 5: Dungeon")
                .WithDescription("Pick the dungeon you want to push.")
                .WithColor(new Color(88, 101, 242));

            var menu = new SelectMenuBuilder()
                .WithCustomId($"{ModalConstants.PushGroupWizardDungeonPrefix}{state.UserId}")
                .WithPlaceholder("Choose a dungeon…")
                .WithMinValues(1).WithMaxValues(1);

            foreach (var d in MythicPlusRotation.Current)
            {
                menu.AddOption(d.Name, d.Slug, description: d.ShortName);
            }

            var components = new ComponentBuilder()
                .WithSelectMenu(menu)
                .WithButton("Cancel", $"{ModalConstants.PushGroupWizardCancelPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✖️"), row: 1);

            return (embed, components, null);
        }

        private static (EmbedBuilder, ComponentBuilder, string?) BuildStep2Key(PushGroupWizardState.State state)
        {
            var embed = new EmbedBuilder()
                .WithTitle("🗝️ Push Group — Step 2 of 5: Key Level")
                .WithDescription($"**Dungeon:** {state.DungeonName}\nPick a target key level (or Custom).")
                .WithColor(new Color(88, 101, 242));

            var row1 = new ActionRowBuilder();
            foreach (var lvl in new[] { 10, 12, 14, 16, 18 })
            {
                row1.WithButton($"+{lvl}", $"{ModalConstants.PushGroupWizardKeyPrefix}{state.UserId}~{lvl}",
                    ButtonStyle.Primary);
            }
            var row2 = new ActionRowBuilder()
                .WithButton("Custom…", $"{ModalConstants.PushGroupWizardKeyModalPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✏️"))
                .WithButton("Back", $"{ModalConstants.PushGroupWizardBackPrefix}{state.UserId}~1",
                    ButtonStyle.Secondary, new Emoji("◀️"))
                .WithButton("Cancel", $"{ModalConstants.PushGroupWizardCancelPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✖️"));

            var components = new ComponentBuilder().AddRow(row1).AddRow(row2);
            return (embed, components, null);
        }

        private static (EmbedBuilder, ComponentBuilder, string?) BuildStep3Role(PushGroupWizardState.State state)
        {
            var charDesc = string.IsNullOrWhiteSpace(state.CharacterName)
                ? "No linked WoW character — sign-up will still work, just without IO context."
                : $"Linked character: **{state.CharacterName} – {state.CharacterRealm}**" +
                  (state.IoRating.HasValue ? $" ({state.IoRating.Value:0} IO)" : "");

            var embed = new EmbedBuilder()
                .WithTitle("🗝️ Push Group — Step 3 of 5: Your Role")
                .WithDescription(
                    $"**Dungeon:** {state.DungeonName}\n" +
                    $"**Key:** +{state.KeyLevel}\n\n" +
                    charDesc)
                .WithColor(new Color(88, 101, 242));

            var row = new ActionRowBuilder()
                .WithButton("Tank", $"{ModalConstants.PushGroupWizardRolePrefix}{state.UserId}~{PushGroupConstants.RoleTank}",
                    ButtonStyle.Primary, new Emoji("🛡️"))
                .WithButton("Healer", $"{ModalConstants.PushGroupWizardRolePrefix}{state.UserId}~{PushGroupConstants.RoleHealer}",
                    ButtonStyle.Success, new Emoji("💚"))
                .WithButton("DPS", $"{ModalConstants.PushGroupWizardRolePrefix}{state.UserId}~{PushGroupConstants.RoleDps}",
                    ButtonStyle.Danger, new Emoji("⚔️"));

            var nav = new ActionRowBuilder()
                .WithButton("Back", $"{ModalConstants.PushGroupWizardBackPrefix}{state.UserId}~2",
                    ButtonStyle.Secondary, new Emoji("◀️"))
                .WithButton("Cancel", $"{ModalConstants.PushGroupWizardCancelPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✖️"));

            return (embed, new ComponentBuilder().AddRow(row).AddRow(nav), null);
        }

        private static (EmbedBuilder, ComponentBuilder, string?) BuildStep4TimeNotes(PushGroupWizardState.State state)
        {
            var embed = new EmbedBuilder()
                .WithTitle("🗝️ Push Group — Step 4 of 5: Time & Notes (optional)")
                .WithDescription(
                    $"**Dungeon:** {state.DungeonName} · +{state.KeyLevel}\n" +
                    $"**Your role:** {state.Role}\n\n" +
                    "Optionally add a time (`in 30`, `20:00`, `8pm`) and a one-line note for the group.")
                .WithColor(new Color(88, 101, 242));

            var row = new ActionRowBuilder()
                .WithButton("Set time / notes", $"{ModalConstants.PushGroupWizardTimeModalPrefix}{state.UserId}",
                    ButtonStyle.Primary, new Emoji("✏️"))
                .WithButton("Skip", $"{ModalConstants.PushGroupWizardSkipTimePrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("⏭️"));

            var nav = new ActionRowBuilder()
                .WithButton("Back", $"{ModalConstants.PushGroupWizardBackPrefix}{state.UserId}~3",
                    ButtonStyle.Secondary, new Emoji("◀️"))
                .WithButton("Cancel", $"{ModalConstants.PushGroupWizardCancelPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✖️"));

            return (embed, new ComponentBuilder().AddRow(row).AddRow(nav), null);
        }

        private static (EmbedBuilder, ComponentBuilder, string?) BuildStep5Preview(PushGroupWizardState.State state)
        {
            var when = state.ScheduledForUtc.HasValue
                ? $"<t:{new DateTimeOffset(state.ScheduledForUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:F> · <t:{new DateTimeOffset(state.ScheduledForUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:R>"
                : "ASAP";

            var embed = new EmbedBuilder()
                .WithTitle("🗝️ Push Group — Preview")
                .WithDescription(
                    $"**Dungeon:** {state.DungeonName}\n" +
                    $"**Key:** +{state.KeyLevel}\n" +
                    $"**Your role:** {state.Role}\n" +
                    $"**Character:** {state.CharacterName ?? "(none linked)"} {(state.IoRating.HasValue ? $"· {state.IoRating.Value:0} IO" : string.Empty)}\n" +
                    $"**Starts:** {when}\n" +
                    (string.IsNullOrWhiteSpace(state.Notes) ? "" : $"**Notes:** {state.Notes}\n"))
                .WithFooter("Posting publishes a live Components-V2 card in this channel and pings eligible roster members.")
                .WithColor(new Color(46, 204, 113));

            var row = new ActionRowBuilder()
                .WithButton("Post", $"{ModalConstants.PushGroupWizardPostPrefix}{state.UserId}",
                    ButtonStyle.Success, new Emoji("📣"))
                .WithButton("Back", $"{ModalConstants.PushGroupWizardBackPrefix}{state.UserId}~4",
                    ButtonStyle.Secondary, new Emoji("◀️"))
                .WithButton("Cancel", $"{ModalConstants.PushGroupWizardCancelPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✖️"));

            return (embed, new ComponentBuilder().AddRow(row), null);
        }
    }
}
