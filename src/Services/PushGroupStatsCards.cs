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
    /// Components-V2 card builders for the pushgroup read surfaces: hub, list, leaderboard,
    /// personal bests and Great Vault progress. Pure builders — callers fetch the data.
    /// </summary>
    public static class PushGroupStatsCards
    {
        private static readonly Color Blurple = new(88, 101, 242);
        private static readonly Color Gold = new(241, 196, 15);
        private static readonly Color Green = new(46, 204, 113);
        private static readonly Color Teal = new(26, 188, 156);

        public record OpenGroupRow(PushGroup Group, int ActiveSignups, int Capacity);
        public record KeystoneRow(long UserId, string DungeonName, int KeyLevel, DateTime UpdatedAt);
        public record LeaderboardRow(long UserId, string DungeonName, int BestKeyLevel);
        public record VaultGuildRow(long UserId, int RunCount);

        // ---------------------------------------------------------------- hub

        public static ComponentBuilderV2 BuildHub(
            long guildId,
            IReadOnlyList<OpenGroupRow> openGroups,
            IReadOnlyList<KeystoneRow> keystones,
            IReadOnlyList<LeaderboardRow> weeklyTop)
        {
            var container = new ContainerBuilder().WithAccentColor(Blurple);

            container.AddComponent(new TextDisplayBuilder().WithContent(
                $"# 🎯 M+ Key Hub\nLive overview · updated <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>"));
            container.AddComponent(Divider());

            container.AddComponent(new TextDisplayBuilder().WithContent(BuildOpenGroupsBlock(openGroups)));
            container.AddComponent(Divider());

            container.AddComponent(new TextDisplayBuilder().WithContent(BuildKeyBoardBlock(keystones)));

            if (weeklyTop.Count > 0)
            {
                container.AddComponent(Divider());
                var top = string.Join(" · ", weeklyTop.Take(3)
                    .Select((r, i) => $"{Medal(i)} <@{r.UserId}> **+{r.BestKeyLevel}** {r.DungeonName}"));
                container.AddComponent(new TextDisplayBuilder().WithContent($"🏆 **This week's best:** {top}"));
            }

            container.AddComponent(Divider());
            container.AddComponent(new ActionRowBuilder()
                .WithButton("New Group", ModalConstants.PushGroupHubNewId, ButtonStyle.Success, new Emoji("➕")));

            return new ComponentBuilderV2().AddComponent(container);
        }

        private static string BuildOpenGroupsBlock(IReadOnlyList<OpenGroupRow> openGroups)
        {
            var sb = new StringBuilder("**Open groups**\n");
            if (openGroups.Count == 0)
            {
                sb.Append("*None right now — start one with the button below or `/keys new`.*");
                return sb.ToString();
            }

            foreach (var row in openGroups.Take(5))
            {
                var g = row.Group;
                var when = g.ScheduledForUtc.HasValue
                    ? $"<t:{new DateTimeOffset(g.ScheduledForUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:R>"
                    : "ASAP";
                var jump = $"https://discord.com/channels/{g.GuildId}/{g.ChannelId}/{g.MessageId}";
                sb.AppendLine($"🗝️ **+{g.TargetKeyLevel} {g.DungeonName}** · {row.ActiveSignups}/{row.Capacity} · starts {when} · <@{g.CreatorUserId}> · [jump]({jump})");
            }
            if (openGroups.Count > 5) sb.AppendLine($"*…and {openGroups.Count - 5} more — `/keys list`*");
            return sb.ToString().TrimEnd();
        }

        private static string BuildKeyBoardBlock(IReadOnlyList<KeystoneRow> keystones)
        {
            var sb = new StringBuilder("**Key board** — `/keys board set`\n");
            if (keystones.Count == 0)
            {
                sb.Append("*No keys registered this week.*");
                return sb.ToString();
            }

            foreach (var k in keystones.OrderByDescending(k => k.KeyLevel).Take(15))
            {
                sb.AppendLine($"• <@{k.UserId}> — **+{k.KeyLevel} {k.DungeonName}**");
            }
            if (keystones.Count > 15) sb.AppendLine($"*…and {keystones.Count - 15} more*");
            return sb.ToString().TrimEnd();
        }

        // --------------------------------------------------------------- list

        public static ComponentBuilderV2 BuildList(
            IReadOnlyList<OpenGroupRow> openGroups,
            IReadOnlyList<KeystoneRow> keystones)
        {
            var container = new ContainerBuilder().WithAccentColor(Teal);
            container.AddComponent(new TextDisplayBuilder().WithContent("# 📋 Key Groups"));
            container.AddComponent(Divider());
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildOpenGroupsBlock(openGroups)));
            container.AddComponent(Divider());
            container.AddComponent(new TextDisplayBuilder().WithContent(BuildKeyBoardBlock(keystones)));
            return new ComponentBuilderV2().AddComponent(container);
        }

        // -------------------------------------------------------- leaderboard

        public static ComponentBuilderV2 BuildLeaderboard(IReadOnlyList<LeaderboardRow> rows)
        {
            var container = new ContainerBuilder().WithAccentColor(Gold);
            container.AddComponent(new TextDisplayBuilder().WithContent(
                "# 🏆 Weekly Key Leaderboard\nBest completed key per member this reset."));
            container.AddComponent(Divider());

            if (rows.Count == 0)
            {
                container.AddComponent(new TextDisplayBuilder().WithContent(
                    "*No runs recorded yet this week. Runs sync from Raider.IO every few hours — link a character with `/set-main` to appear here.*"));
            }
            else
            {
                var sb = new StringBuilder();
                var i = 0;
                foreach (var r in rows.Take(10))
                {
                    sb.AppendLine($"{Medal(i)} <@{r.UserId}> — **+{r.BestKeyLevel}** {r.DungeonName}");
                    i++;
                }
                container.AddComponent(new TextDisplayBuilder().WithContent(sb.ToString().TrimEnd()));
            }

            return new ComponentBuilderV2().AddComponent(container);
        }

        // ------------------------------------------------------------- mybest

        public static ComponentBuilderV2 BuildMyBest(
            string? characterName,
            IReadOnlyList<WeeklyKeyHistory> rows)
        {
            var container = new ContainerBuilder().WithAccentColor(Green);
            var title = string.IsNullOrWhiteSpace(characterName)
                ? "# 📈 Your Week"
                : $"# 📈 Your Week — {characterName}";
            container.AddComponent(new TextDisplayBuilder().WithContent(title));
            container.AddComponent(Divider());

            if (rows.Count == 0)
            {
                container.AddComponent(new TextDisplayBuilder().WithContent(
                    "*No completed runs this week yet (or your character isn't linked — `/set-main`). Runs sync every few hours.*"));
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var r in rows.OrderByDescending(r => r.BestKeyLevel))
                {
                    var dungeon = MythicPlusRotation.FindBySlug(r.DungeonSlug)?.Name ?? r.DungeonSlug;
                    var runs = r.RunCount > 1 ? $" ({r.RunCount} runs)" : string.Empty;
                    sb.AppendLine($"• **+{r.BestKeyLevel}** {dungeon}{runs}");
                }
                var total = rows.Sum(r => r.RunCount);
                sb.AppendLine();
                sb.Append($"**Total runs this week:** {total}");
                container.AddComponent(new TextDisplayBuilder().WithContent(sb.ToString()));
            }

            return new ComponentBuilderV2().AddComponent(container);
        }

        // -------------------------------------------------------------- vault

        public static ComponentBuilderV2 BuildVaultSelf(
            string? characterName,
            MythicPlusWeekly.VaultProgress progress,
            bool fromLiveData)
        {
            var container = new ContainerBuilder().WithAccentColor(progress.SlotsUnlocked == 3 ? Green : Blurple);
            var title = string.IsNullOrWhiteSpace(characterName)
                ? "# 🏦 Great Vault — M+"
                : $"# 🏦 Great Vault — {characterName}";
            container.AddComponent(new TextDisplayBuilder().WithContent(title));
            container.AddComponent(Divider());

            var sb = new StringBuilder();
            sb.AppendLine(VaultSlotLine(1, MythicPlusWeekly.VaultSlot1Runs, progress.RunCount, progress.Slot1Level));
            sb.AppendLine(VaultSlotLine(2, MythicPlusWeekly.VaultSlot2Runs, progress.RunCount, progress.Slot2Level));
            sb.AppendLine(VaultSlotLine(3, MythicPlusWeekly.VaultSlot3Runs, progress.RunCount, progress.Slot3Level));
            sb.AppendLine();
            sb.Append(progress.SlotsUnlocked == 3
                ? "✅ **All three slots unlocked — enjoy the vault!**"
                : $"**{progress.RunCount}** run{(progress.RunCount == 1 ? "" : "s")} this week · **{progress.RunsToNextSlot}** more to unlock slot {progress.SlotsUnlocked + 1}");
            if (!fromLiveData)
            {
                sb.Append("\n*From the last background sync — may lag the game by a few hours.*");
            }

            container.AddComponent(new TextDisplayBuilder().WithContent(sb.ToString()));
            return new ComponentBuilderV2().AddComponent(container);
        }

        public static ComponentBuilderV2 BuildVaultGuild(IReadOnlyList<VaultGuildRow> rows)
        {
            var container = new ContainerBuilder().WithAccentColor(Blurple);
            container.AddComponent(new TextDisplayBuilder().WithContent(
                "# 🏦 Great Vault — Guild M+ Overview\nSlots unlocked per member this reset (1/4/8 runs)."));
            container.AddComponent(Divider());

            if (rows.Count == 0)
            {
                container.AddComponent(new TextDisplayBuilder().WithContent(
                    "*Nothing synced yet this week. Members appear after the next Raider.IO sync.*"));
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var r in rows.OrderByDescending(r => r.RunCount).Take(20))
                {
                    var slots = r.RunCount >= MythicPlusWeekly.VaultSlot3Runs ? 3
                        : r.RunCount >= MythicPlusWeekly.VaultSlot2Runs ? 2
                        : r.RunCount >= MythicPlusWeekly.VaultSlot1Runs ? 1 : 0;
                    var icon = slots switch { 3 => "🟩🟩🟩", 2 => "🟩🟩⬜", 1 => "🟩⬜⬜", _ => "⬜⬜⬜" };
                    sb.AppendLine($"{icon} <@{r.UserId}> — {r.RunCount} run{(r.RunCount == 1 ? "" : "s")}");
                }
                container.AddComponent(new TextDisplayBuilder().WithContent(sb.ToString().TrimEnd()));
            }

            return new ComponentBuilderV2().AddComponent(container);
        }

        // ------------------------------------------------------------ helpers

        private static string VaultSlotLine(int slot, int threshold, int runCount, int? backingLevel)
        {
            return backingLevel.HasValue
                ? $"🟩 **Slot {slot}** ({threshold} run{(threshold == 1 ? "" : "s")}) — unlocked, from your **+{backingLevel.Value}**"
                : $"⬜ **Slot {slot}** ({threshold} run{(threshold == 1 ? "" : "s")}) — {Math.Max(0, threshold - runCount)} more run{(threshold - runCount == 1 ? "" : "s")}";
        }

        private static SeparatorBuilder Divider() =>
            new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small);

        private static string Medal(int index) => index switch
        {
            0 => "🥇",
            1 => "🥈",
            2 => "🥉",
            _ => $"`#{index + 1}`",
        };
    }
}
