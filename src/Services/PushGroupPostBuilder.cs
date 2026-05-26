#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Common;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Builds the live Components-V2 Container post for a PushGroup.
    /// Single source of truth — used both for the initial post and for every edit.
    /// </summary>
    public static class PushGroupPostBuilder
    {
        public class BuiltPost
        {
            public ComponentBuilderV2 Components { get; init; } = new();
            public MessageFlags Flags { get; init; } = MessageFlags.ComponentsV2;
        }

        public static BuiltPost Build(PushGroup group, IReadOnlyList<PushGroupSignup> signups)
        {
            var container = new ContainerBuilder()
                .WithAccentColor(AccentForStatus(group.Status));

            // Header
            container.AddComponent(new TextDisplayBuilder().WithContent(
                $"# 🗝️ +{group.TargetKeyLevel} {group.DungeonName} — Push Group\n" +
                $"Hosted by <@{group.CreatorUserId}> · IO window: {FormatIoWindow(group)}"));

            container.AddComponent(new SeparatorBuilder()
                .WithIsDivider(true)
                .WithSpacing(SeparatorSpacingSize.Small));

            // "Looking for" line
            var openSlots = ComputeOpenSlots(signups);
            container.AddComponent(new TextDisplayBuilder().WithContent(
                $"**Looking for:** {(openSlots.Count == 0 ? "✅ Group is full" : string.Join(" · ", openSlots))}"));

            container.AddComponent(new SeparatorBuilder()
                .WithIsDivider(true)
                .WithSpacing(SeparatorSpacingSize.Small));

            // Role rosters — TextDisplay siblings inside the container (V2 Section requires an
            // accessory we don't have, so use TextDisplays directly).
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildRoleText("🛡️ Tank", PushGroupConstants.RoleTank, 1, signups)));
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildRoleText("💚 Healer", PushGroupConstants.RoleHealer, 1, signups)));
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildRoleText("⚔️ DPS", PushGroupConstants.RoleDps, PushGroupConstants.DefaultDpsSlots, signups)));

            container.AddComponent(new SeparatorBuilder()
                .WithIsDivider(true)
                .WithSpacing(SeparatorSpacingSize.Small));

            // Key holder + schedule
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildKeyAndScheduleBlock(group)));

            if (!string.IsNullOrWhiteSpace(group.Notes))
            {
                container.AddComponent(new SeparatorBuilder()
                    .WithIsDivider(false)
                    .WithSpacing(SeparatorSpacingSize.Small));
                container.AddComponent(new TextDisplayBuilder().WithContent($"📝 {group.Notes}"));
            }

            container.AddComponent(new SeparatorBuilder()
                .WithIsDivider(true)
                .WithSpacing(SeparatorSpacingSize.Small));

            // Status footer
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildStatusFooter(group)));

            // Action rows
            var isActive = group.Status == PushGroupConstants.StatusOpen || group.Status == PushGroupConstants.StatusFull;
            if (isActive)
            {
                var signupRow = new ActionRowBuilder()
                    .WithButton("Sign Up: Tank", $"{ModalConstants.PushGroupSignupPrefix}{group.Id}~{PushGroupConstants.RoleTank}",
                        ButtonStyle.Primary, new Emoji("🛡️"),
                        disabled: IsRoleFull(signups, PushGroupConstants.RoleTank, 1))
                    .WithButton("Healer", $"{ModalConstants.PushGroupSignupPrefix}{group.Id}~{PushGroupConstants.RoleHealer}",
                        ButtonStyle.Success, new Emoji("💚"),
                        disabled: IsRoleFull(signups, PushGroupConstants.RoleHealer, 1))
                    .WithButton("DPS", $"{ModalConstants.PushGroupSignupPrefix}{group.Id}~{PushGroupConstants.RoleDps}",
                        ButtonStyle.Danger, new Emoji("⚔️"),
                        disabled: IsRoleFull(signups, PushGroupConstants.RoleDps, PushGroupConstants.DefaultDpsSlots))
                    .WithButton("Withdraw", $"{ModalConstants.PushGroupWithdrawPrefix}{group.Id}",
                        ButtonStyle.Secondary, new Emoji("↩️"));
                container.AddComponent(signupRow);
            }

            // Manage row — always present so the creator can close even after group advances.
            var manageRow = new ActionRowBuilder();
            if (isActive)
            {
                manageRow.WithButton("Bring Key", $"{ModalConstants.PushGroupBringKeyPrefix}{group.Id}",
                    ButtonStyle.Secondary, new Emoji("🗝️"));
            }
            if (group.Status != PushGroupConstants.StatusCancelled && group.Status != PushGroupConstants.StatusCompleted)
            {
                manageRow.WithButton("Close Group", $"{ModalConstants.PushGroupClosePrefix}{group.Id}",
                    ButtonStyle.Secondary, new Emoji("🔒"));
            }
            if (manageRow.Components.Count > 0)
            {
                container.AddComponent(manageRow);
            }

            var components = new ComponentBuilderV2().AddComponent(container);

            return new BuiltPost
            {
                Components = components,
                Flags = MessageFlags.ComponentsV2,
            };
        }

        private static string BuildRoleText(string label, string role, int slots, IReadOnlyList<PushGroupSignup> signups)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**{label}**");

            var roleSignups = signups
                .Where(s => s.RoleSlot == role && s.WithdrewAt == null)
                .OrderBy(s => s.SlotIndex)
                .ToList();

            for (int i = 0; i < slots; i++)
            {
                var match = roleSignups.FirstOrDefault(s => s.SlotIndex == i);
                sb.AppendLine(match != null ? FormatSignupLine(match) : "⏳ *open*");
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatSignupLine(PushGroupSignup s)
        {
            var bits = new List<string> { $"✅ <@{s.UserId}>" };
            var specBits = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.WowClass)) specBits.Add(s.WowClass!);
            if (!string.IsNullOrWhiteSpace(s.WowSpec)) specBits.Add(s.WowSpec!);
            if (specBits.Count > 0) bits.Add($"({string.Join(" ", specBits)})");
            if (s.IoRating.HasValue) bits.Add($"{s.IoRating.Value:0} IO");
            if (s.IoBestThisWeek.HasValue) bits.Add($"best +{s.IoBestThisWeek.Value} this wk");
            return string.Join(" · ", bits);
        }

        private static string BuildKeyAndScheduleBlock(PushGroup group)
        {
            var sb = new StringBuilder();
            if (group.KeyHolderUserId.HasValue)
            {
                var keyDesc = group.KeyHolderKeyLevel.HasValue
                    ? $"+{group.KeyHolderKeyLevel.Value} {group.KeyHolderDungeonName ?? group.DungeonName}"
                    : "(level TBD)";
                sb.AppendLine($"🗝️ **Key holder:** <@{group.KeyHolderUserId.Value}> — {keyDesc}");
            }
            else
            {
                sb.AppendLine("🗝️ **Key holder:** *needed*");
            }

            if (group.ScheduledForUtc.HasValue)
            {
                var unix = new DateTimeOffset(group.ScheduledForUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds();
                sb.Append($"⏰ **Starts:** <t:{unix}:F> · <t:{unix}:R>");
            }
            else
            {
                sb.Append("⏰ **Starts:** ASAP");
            }
            return sb.ToString();
        }

        private static string BuildStatusFooter(PushGroup group)
        {
            return group.Status switch
            {
                PushGroupConstants.StatusOpen => "🟢 Status: Open — looking for players",
                PushGroupConstants.StatusFull => "🟡 Status: Full — ready to roll",
                PushGroupConstants.StatusInProgress => "🔵 Status: In progress",
                PushGroupConstants.StatusCompleted => "✅ Status: Completed",
                PushGroupConstants.StatusCancelled => "⚫ Status: Cancelled",
                _ => $"Status: {group.Status}",
            };
        }

        private static Color AccentForStatus(string status) => status switch
        {
            PushGroupConstants.StatusOpen => new Color(88, 101, 242),     // blurple
            PushGroupConstants.StatusFull => new Color(241, 196, 15),     // gold
            PushGroupConstants.StatusInProgress => new Color(52, 152, 219), // blue
            PushGroupConstants.StatusCompleted => new Color(46, 204, 113),  // green
            PushGroupConstants.StatusCancelled => new Color(99, 110, 114),  // grey
            _ => Color.Default,
        };

        private static List<string> ComputeOpenSlots(IReadOnlyList<PushGroupSignup> signups)
        {
            var open = new List<string>();
            if (!IsRoleFull(signups, PushGroupConstants.RoleTank, 1)) open.Add("Tank");
            if (!IsRoleFull(signups, PushGroupConstants.RoleHealer, 1)) open.Add("Healer");
            var filledDps = signups.Count(s => s.RoleSlot == PushGroupConstants.RoleDps && s.WithdrewAt == null);
            var dpsNeeded = PushGroupConstants.DefaultDpsSlots - filledDps;
            if (dpsNeeded > 0) open.Add(dpsNeeded == 1 ? "1 DPS" : $"{dpsNeeded} DPS");
            return open;
        }

        private static bool IsRoleFull(IReadOnlyList<PushGroupSignup> signups, string role, int capacity) =>
            signups.Count(s => s.RoleSlot == role && s.WithdrewAt == null) >= capacity;

        private static string FormatIoWindow(PushGroup group)
        {
            if (group.IoRatingTarget.HasValue && group.IoRatingMin.HasValue && group.IoRatingMax.HasValue)
            {
                return $"{group.IoRatingMin.Value:0}–{group.IoRatingMax.Value:0} (around {group.IoRatingTarget.Value:0})";
            }
            return "any";
        }
    }
}
