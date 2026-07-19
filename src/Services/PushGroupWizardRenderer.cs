#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Common;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Renders the /keys single-screen composer into the caller's ephemeral interaction
    /// response as a Components-V2 card: live summary up top, dungeon select + key buttons +
    /// role buttons all visible at once (pick in any order), Post enabled once complete.
    /// Caller must have already acked ephemerally (DeferAsync/DeferLoadingAsync) before invoking.
    /// </summary>
    public static class PushGroupWizardRenderer
    {
        public static Task RenderStep(InteractionModuleBase<ShardedInteractionContext> module, PushGroupWizardState.State state)
            => RenderStep(module.Context.Interaction, state);

        public static Task RenderStep(IDiscordInteraction interaction, PushGroupWizardState.State state)
            => interaction.ModifyToV2Async(BuildComposer(state).Build());

        public static ComponentBuilderV2 BuildComposer(PushGroupWizardState.State state)
        {
            var container = new ContainerBuilder().WithAccentColor(new Color(88, 101, 242));

            container.AddComponent(new TextDisplayBuilder().WithContent(BuildSummary(state)));
            container.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));

            // Dungeon select — current pick shown as the default option.
            var menu = new SelectMenuBuilder()
                .WithCustomId($"{ModalConstants.PushGroupWizardDungeonPrefix}{state.UserId}")
                .WithPlaceholder("Dungeon…")
                .WithMinValues(1).WithMaxValues(1);
            foreach (var d in MythicPlusRotation.Current)
            {
                menu.AddOption(d.Name, d.Slug, description: d.ShortName, isDefault: d.Slug == state.DungeonSlug);
            }
            container.AddComponent(new ActionRowBuilder().WithSelectMenu(menu));

            // Key level quick-picks — the chosen one lights up green.
            var keyRow = new ActionRowBuilder();
            foreach (var lvl in new[] { 10, 12, 14, 16, 18 })
            {
                keyRow.WithButton($"+{lvl}", $"{ModalConstants.PushGroupWizardKeyPrefix}{state.UserId}~{lvl}",
                    state.KeyLevel == lvl ? ButtonStyle.Success : ButtonStyle.Secondary);
            }
            container.AddComponent(keyRow);

            // Role row — same highlight treatment.
            var roleRow = new ActionRowBuilder()
                .WithButton("Tank", $"{ModalConstants.PushGroupWizardRolePrefix}{state.UserId}~{PushGroupConstants.RoleTank}",
                    state.Role == PushGroupConstants.RoleTank ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji("🛡️"))
                .WithButton("Healer", $"{ModalConstants.PushGroupWizardRolePrefix}{state.UserId}~{PushGroupConstants.RoleHealer}",
                    state.Role == PushGroupConstants.RoleHealer ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji("💚"))
                .WithButton("DPS", $"{ModalConstants.PushGroupWizardRolePrefix}{state.UserId}~{PushGroupConstants.RoleDps}",
                    state.Role == PushGroupConstants.RoleDps ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji("⚔️"));
            container.AddComponent(roleRow);

            var ready = state.DungeonSlug != null && state.KeyLevel != null && state.Role != null;
            var actions = new ActionRowBuilder()
                .WithButton("Custom key", $"{ModalConstants.PushGroupWizardKeyModalPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✏️"))
                .WithButton("Time / notes", $"{ModalConstants.PushGroupWizardTimeModalPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("🕐"))
                .WithButton("Post", $"{ModalConstants.PushGroupWizardPostPrefix}{state.UserId}",
                    ButtonStyle.Success, new Emoji("📣"), disabled: !ready)
                .WithButton("Cancel", $"{ModalConstants.PushGroupWizardCancelPrefix}{state.UserId}",
                    ButtonStyle.Secondary, new Emoji("✖️"));
            container.AddComponent(actions);

            return new ComponentBuilderV2().AddComponent(container);
        }

        private static string BuildSummary(PushGroupWizardState.State state)
        {
            var dungeon = state.DungeonName ?? "*pick a dungeon*";
            var key = state.KeyLevel.HasValue ? $"+{state.KeyLevel}" : "*pick a level*";
            var role = state.Role ?? "*pick your role*";

            var character = string.IsNullOrWhiteSpace(state.CharacterName)
                ? "*(no linked character — group still works, just without IO context)*"
                : $"{state.CharacterName} – {state.CharacterRealm}"
                    + (state.IoRating.HasValue ? $" · {state.IoRating.Value:0} IO" : string.Empty);

            var when = state.ScheduledForUtc.HasValue
                ? $"<t:{new DateTimeOffset(state.ScheduledForUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:F> · <t:{new DateTimeOffset(state.ScheduledForUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:R>"
                : "ASAP";

            var summary =
                "## 🗝️ New Key Group\n" +
                $"**Dungeon:** {dungeon}\n" +
                $"**Key:** {key} · **Your role:** {role}\n" +
                $"**Character:** {character}\n" +
                $"**Starts:** {when}";

            if (!string.IsNullOrWhiteSpace(state.Notes))
            {
                summary += $"\n**Notes:** {state.Notes}";
            }

            summary += "\n*Pick in any order — Post lights up when ready. Posting pings eligible roster members.*";
            return summary;
        }
    }
}
