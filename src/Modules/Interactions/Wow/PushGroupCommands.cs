#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    [RequireContext(ContextType.Guild)]
    [Group("keys", "Mythic+ key groups, vault and leaderboards")]
    public class PushGroupCommands : NinjaBotBaseModule
    {
        private readonly PushGroupWizardState _wizardState;
        private readonly PushGroupCoordinator _coordinator;
        private readonly WowCacheService _wowCache;
        private readonly RaiderIOApi _rio;

        public PushGroupCommands(
            IServiceScopeFactory scopeFactory,
            PushGroupWizardState wizardState,
            PushGroupCoordinator coordinator,
            WowCacheService wowCache,
            RaiderIOApi rio)
            : base(scopeFactory)
        {
            _wizardState = wizardState;
            _coordinator = coordinator;
            _wowCache = wowCache;
            _rio = rio;
        }

        /// <summary>Replaces the deferred response with a Components-V2 card (mention-safe).</summary>
        private Task RespondV2Async(ComponentBuilderV2 components)
            => Context.Interaction.ModifyToV2Async(components.Build());

        [SlashCommand("new", "Start the key-group composer")]
        public async Task PushGroupNew()
        {
            await DeferAsync(ephemeral: true);

            // Always start from a clean slate — a leftover wizard from an earlier run would
            // otherwise leak its dungeon/key/notes into this one.
            _wizardState.Remove(Context.User.Id, Context.Channel.Id);
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);

            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [SlashCommand("create", "Fast-path: create a key group with all options up front")]
        public async Task PushGroupCreate(
            [Summary("dungeon", "Pick the dungeon")]
            [Autocomplete(typeof(DungeonAutocomplete))] string dungeonSlug,
            [Summary("key", "Target keystone level (e.g., 15)")] int keyLevel,
            [Summary("role", "Your role")]
            [Choice("Tank", "Tank"), Choice("Healer", "Healer"), Choice("DPS", "DPS")] string role = "DPS",
            [Summary("time", "When (e.g., 'in 90m', '8pm UTC', '8pm -5', or skip)")] string? time = null,
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
                    await FollowupAsync($"Couldn't read the time `{time}` (or it's in the past). Try `in 90m`, `20:00` (UTC), `8pm -5` (UTC offset), or a Discord timestamp.", ephemeral: true);
                    return;
                }
            }

            _wizardState.Remove(Context.User.Id, Context.Channel.Id);
            var state = _wizardState.GetOrCreate(Context.User.Id, Context.Channel.Id);
            state.DungeonSlug = dungeon.Slug;
            state.DungeonName = dungeon.Name;
            state.KeyLevel = keyLevel;
            state.Role = role;
            state.Notes = notes;
            state.ScheduledForUtc = parsedTime;

            await _coordinator.PrefillCharacterAsync(state, (long)Context.User.Id);
            await PushGroupWizardRenderer.RenderStep(this, state);
        }

        [SlashCommand("pings", "Turn key-group roster pings on or off for yourself")]
        public async Task PushGroupPings(
            [Summary("enabled", "True = ping me when new key groups are posted, False = leave me out")] bool enabled)
        {
            await DeferAsync(ephemeral: true);

            await WithDbAsync(async db =>
            {
                var settings = await db.UserPushGroupSettings.FindAsync((long)Context.User.Id);
                if (settings == null)
                {
                    settings = new UserPushGroupSettings { UserId = (long)Context.User.Id };
                    db.UserPushGroupSettings.Add(settings);
                }
                settings.DmOnRosterPing = enabled;
                settings.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            });

            // Note: the setting is per-user across every server you share with the bot
            // (UserPushGroupSettings has no guild key) — the wording must say so.
            await FollowupAsync(enabled
                ? "🔔 You'll be pinged when new key groups are posted — in every server you share with me."
                : "🔕 You won't be pinged for key groups in any server. Re-enable anytime with `/keys pings`.",
                ephemeral: true);
        }

        // ===== Read surfaces (Components V2) =====

        [SlashCommand("list", "Open key groups and the guild key board")]
        public async Task PushGroupList()
        {
            await DeferAsync();
            var data = await WithDbAsync(db => _coordinator.LoadHubDataAsync(db, (long)Context.Guild.Id));
            await RespondV2Async(PushGroupStatsCards.BuildList(data.Groups, data.Keys));
        }

        [SlashCommand("leaderboard", "This week's best keys across the guild")]
        public async Task PushGroupLeaderboard()
        {
            await DeferAsync();
            var data = await WithDbAsync(db => _coordinator.LoadHubDataAsync(db, (long)Context.Guild.Id));
            await RespondV2Async(PushGroupStatsCards.BuildLeaderboard(data.Top));
        }

        [SlashCommand("mybest", "Your best M+ runs this week")]
        public async Task PushGroupMyBest()
        {
            await DeferAsync(ephemeral: true);
            var userId = (long)Context.User.Id;
            var weekFloor = MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow);
            var rows = await WithDbAsync(async db => await db.WeeklyKeyHistory
                .Where(h => h.UserId == userId && h.WeekStartUtc > weekFloor && h.RunCount > 0)
                .ToListAsync());
            var main = await _wowCache.GetUserMainCharacterAsync(userId);
            await RespondV2Async(PushGroupStatsCards.BuildMyBest(main?.CharName, rows));
        }

        [SlashCommand("vault", "Great Vault M+ progress — yours or the whole guild's")]
        public async Task PushGroupVault(
            [Summary("scope", "Just you (default) or the whole guild")]
            [Choice("me", "me"), Choice("guild", "guild")] string scope = "me")
        {
            if (scope == "guild")
            {
                await DeferAsync();
                var guildId = (long)Context.Guild.Id;
                var weekFloor = MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow);
                var rows = await WithDbAsync(async db =>
                {
                    var memberIds = await db.WowCharAssociation
                        .Where(a => a.ServerId == guildId && a.IsMain && a.UserId != null)
                        .Select(a => a.UserId!.Value)
                        .Distinct()
                        .ToListAsync();
                    return await db.WeeklyKeyHistory
                        .Where(h => memberIds.Contains(h.UserId) && h.WeekStartUtc > weekFloor)
                        .GroupBy(h => h.UserId)
                        .Select(g => new { UserId = g.Key, Runs = g.Sum(h => h.RunCount) })
                        .Where(x => x.Runs > 0)
                        .ToListAsync();
                });
                await RespondV2Async(PushGroupStatsCards.BuildVaultGuild(
                    rows.Select(r => new PushGroupStatsCards.VaultGuildRow(r.UserId, r.Runs)).ToList()));
                return;
            }

            await DeferAsync(ephemeral: true);
            var main = await _wowCache.GetUserMainCharacterAsync((long)Context.User.Id);
            if (main == null)
            {
                await FollowupAsync("Link a character first with `/set-main` so I know who to check.", ephemeral: true);
                return;
            }

            MythicPlusWeekly.VaultProgress progress;
            var live = true;
            try
            {
                var info = await _rio.GetCharMythicPlusInfoAsync(
                    main.CharName, WowRealmSlug.From(main.LocalRealmSlug ?? main.WowRealm), main.WowRegion ?? "us");
                progress = MythicPlusWeekly.VaultFromRunLevels(
                    (info?.MythicPlusWeeklyHighestLevelRuns ?? Array.Empty<RaiderIOModels.MythicPlusRun>())
                    .Select(r => (int)r.MythicLevel));
            }
            catch
            {
                // raider.io unavailable — fall back to the background sync's per-dungeon rollup
                // (best level repeated per run is an approximation; the card says so).
                live = false;
                var userId = (long)Context.User.Id;
                var weekFloor = MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow);
                var rows = await WithDbAsync(async db => await db.WeeklyKeyHistory
                    .Where(h => h.UserId == userId && h.WeekStartUtc > weekFloor && h.RunCount > 0)
                    .ToListAsync());
                progress = MythicPlusWeekly.VaultFromRunLevels(
                    rows.SelectMany(r => Enumerable.Repeat(r.BestKeyLevel, Math.Max(1, r.RunCount))));
            }

            await RespondV2Async(PushGroupStatsCards.BuildVaultSelf(main.CharName, progress, live));
        }

        // ===== Admin =====

        [SlashCommand("hub", "Post the live M+ hub card in this channel (auto-updating)")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task PushGroupHub()
        {
            await DeferAsync(ephemeral: true);
            var guildId = (long)Context.Guild.Id;

            var data = await WithDbAsync(db => _coordinator.LoadHubDataAsync(db, guildId));
            var components = PushGroupStatsCards.BuildHub(guildId, data.Groups, data.Keys, data.Top);

            var posted = await Context.Channel.SendMessageAsync(
                components: components.Build(),
                flags: MessageFlags.ComponentsV2,
                allowedMentions: AllowedMentions.None);

            ulong? oldChannelId = null;
            ulong? oldMessageId = null;
            await WithDbAsync(async db =>
            {
                var settings = await db.ServerPushGroupSettings.FindAsync(guildId);
                if (settings == null)
                {
                    settings = new ServerPushGroupSettings { DiscordGuildId = guildId };
                    db.ServerPushGroupSettings.Add(settings);
                }
                if (settings.HubChannelId.HasValue && settings.HubMessageId.HasValue)
                {
                    oldChannelId = (ulong)settings.HubChannelId.Value;
                    oldMessageId = (ulong)settings.HubMessageId.Value;
                }
                settings.HubChannelId = (long)Context.Channel.Id;
                settings.HubMessageId = (long)posted.Id;
                settings.SetById = (long)Context.User.Id;
                settings.SetByName = Context.User.Username;
                settings.TimeSet = DateTime.UtcNow;
                await db.SaveChangesAsync();
            });

            // Best-effort: remove the superseded hub card so two don't drift apart.
            if (oldMessageId.HasValue)
            {
                try
                {
                    if (Context.Client.GetChannel(oldChannelId!.Value) is IMessageChannel oldChannel)
                    {
                        await oldChannel.DeleteMessageAsync(oldMessageId.Value);
                    }
                }
                catch
                {
                    // old card already gone or no permission — nothing to do
                }
            }

            await FollowupAsync("📌 Hub posted — it live-updates on every signup, withdraw, close and key change. Pin it if you like.", ephemeral: true);
        }

        [SlashCommand("limit", "Cap how many key groups can be open at once (0 = unlimited)")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task PushGroupLimit(
            [Summary("max", "Max open groups at once; 0 removes the cap")][MinValue(0)][MaxValue(25)] int max)
        {
            await DeferAsync(ephemeral: true);
            var guildId = (long)Context.Guild.Id;
            await WithDbAsync(async db =>
            {
                var settings = await db.ServerPushGroupSettings.FindAsync(guildId);
                if (settings == null)
                {
                    settings = new ServerPushGroupSettings { DiscordGuildId = guildId };
                    db.ServerPushGroupSettings.Add(settings);
                }
                settings.MaxOpenGroups = max == 0 ? null : max;
                settings.SetById = (long)Context.User.Id;
                settings.SetByName = Context.User.Username;
                settings.TimeSet = DateTime.UtcNow;
                await db.SaveChangesAsync();
            });
            await FollowupAsync(max == 0
                ? "Open-group cap removed — unlimited groups."
                : $"Open key groups capped at **{max}** per server.",
                ephemeral: true);
        }

        // ===== Key board =====

        [Group("board", "The guild key board")]
        public class PushGroupKeyCommands : NinjaBotBaseModule
        {
            private readonly WowCacheService _wowCache;
            private readonly PushGroupCoordinator _coordinator;

            public PushGroupKeyCommands(
                IServiceScopeFactory scopeFactory,
                WowCacheService wowCache,
                PushGroupCoordinator coordinator)
                : base(scopeFactory)
            {
                _wowCache = wowCache;
                _coordinator = coordinator;
            }

            [SlashCommand("set", "Put your current keystone on the guild key board")]
            public async Task KeySet(
                [Summary("dungeon", "The dungeon on your key")]
                [Autocomplete(typeof(DungeonAutocomplete))] string dungeonSlug,
                [Summary("level", "Keystone level")][MinValue(2)][MaxValue(40)] int level)
            {
                await DeferAsync(ephemeral: true);

                var dungeon = MythicPlusRotation.FindBySlug(dungeonSlug);
                if (dungeon == null)
                {
                    await FollowupAsync($"Unknown dungeon `{dungeonSlug}` — use the autocomplete to pick from the rotation.", ephemeral: true);
                    return;
                }

                var userId = (long)Context.User.Id;
                var main = await _wowCache.GetUserMainCharacterAsync(userId);
                var now = DateTime.UtcNow;

                await WithDbAsync(async db =>
                {
                    var row = await db.UserKeystones.FindAsync(userId);
                    if (row == null)
                    {
                        row = new UserKeystone { UserId = userId };
                        db.UserKeystones.Add(row);
                    }
                    row.CharacterName = main?.CharName;
                    row.DungeonSlug = dungeon.Slug;
                    row.DungeonName = dungeon.Name;
                    row.KeyLevel = level;
                    row.WeekStartUtc = MythicPlusWeekly.WeekStartUtc(main?.WowRegion, now);
                    row.UpdatedAt = now;
                    await db.SaveChangesAsync();
                });

                await _coordinator.UpdateHubAsync((long)Context.Guild.Id);

                // One-click group from the freshly registered key.
                var buttons = new ComponentBuilder()
                    .WithButton("Post as Tank", $"{ModalConstants.PushGroupKeyGoPrefix}{Context.User.Id}~{PushGroupConstants.RoleTank}",
                        ButtonStyle.Primary, new Emoji("🛡️"))
                    .WithButton("Healer", $"{ModalConstants.PushGroupKeyGoPrefix}{Context.User.Id}~{PushGroupConstants.RoleHealer}",
                        ButtonStyle.Success, new Emoji("💚"))
                    .WithButton("DPS", $"{ModalConstants.PushGroupKeyGoPrefix}{Context.User.Id}~{PushGroupConstants.RoleDps}",
                        ButtonStyle.Danger, new Emoji("⚔️"));

                await FollowupAsync(
                    $"🗝️ **+{level} {dungeon.Name}** is on the key board. Post a group with it right now — which role are you?",
                    components: buttons.Build(),
                    ephemeral: true);
            }

            [SlashCommand("clear", "Take your key off the board")]
            public async Task KeyClear()
            {
                await DeferAsync(ephemeral: true);
                var userId = (long)Context.User.Id;
                var removed = await WithDbAsync(async db =>
                {
                    var row = await db.UserKeystones.FindAsync(userId);
                    if (row == null) return false;
                    db.UserKeystones.Remove(row);
                    await db.SaveChangesAsync();
                    return true;
                });

                if (removed) await _coordinator.UpdateHubAsync((long)Context.Guild.Id);
                await FollowupAsync(removed ? "Key removed from the board." : "You had no key registered.", ephemeral: true);
            }
        }
    }
}
