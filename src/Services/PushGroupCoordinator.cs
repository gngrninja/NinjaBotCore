#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Heavy-lifting service for the /keys feature. Owns:
    /// - Character prefill from WoW associations + Raider.IO
    /// - Posting new groups, signups/withdrawals, key-holder updates, close
    /// - Auto-ping follow-up for roster members within ±IO window
    /// </summary>
    public class PushGroupCoordinator
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DiscordShardedClient _client;
        private readonly WowCacheService _wowCache;
        private readonly RaiderIOApi _rio;
        private readonly ILogger<PushGroupCoordinator> _logger;

        // Single-instance bot — in-process per-group lock serializes signup/withdraw to prevent
        // duplicate slot-index assignment. A persistent unique index is the longer-term fix.
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> _groupLocks = new();

        public PushGroupCoordinator(
            IServiceScopeFactory scopeFactory,
            DiscordShardedClient client,
            WowCacheService wowCache,
            RaiderIOApi rio,
            ILogger<PushGroupCoordinator> logger)
        {
            _scopeFactory = scopeFactory;
            _client = client;
            _wowCache = wowCache;
            _rio = rio;
            _logger = logger;
        }

        /// <summary>Fills wizard state with the user's main-character details + current IO if available.</summary>
        public async Task PrefillCharacterAsync(PushGroupWizardState.State state, long userId)
        {
            try
            {
                var main = await _wowCache.GetUserMainCharacterAsync(userId);
                if (main == null) return;

                state.CharacterName = main.CharName;
                state.CharacterRealm = main.WowRealm;
                state.CharacterRegion = main.WowRegion ?? "us";

                var slug = SlugForRio(main.LocalRealmSlug ?? main.WowRealm);
                var info = await TryFetchRioCharAsync(main.CharName, slug, state.CharacterRegion);
                var (cls, spec, io) = SnapshotFrom(info);
                state.CharacterClass = cls;
                state.CharacterSpec = spec;
                state.IoRating = io;
                // Capture the weekly runs so PostGroupAsync doesn't re-fetch the same profile.
                state.WeeklyRuns = info?.MythicPlusWeeklyHighestLevelRuns;
                state.WeeklyRunsFetchedAt = info != null ? DateTime.UtcNow : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefill character lookup failed for user {UserId}", userId);
            }
        }

        private static string SlugForRio(string? realm) => WowRealmSlug.From(realm);

        /// <summary>
        /// Persists a new push group, posts the live message, runs the auto-ping follow-up.
        /// Returns the group, or (null, user-facing reason) on failure.
        /// </summary>
        public async Task<(PushGroup? Group, string? Error)> PostGroupAsync(PushGroupWizardState.State state, ulong guildId, ulong channelId, ulong creatorUserId, string creatorUserName, int ioWindow)
        {
            if (string.IsNullOrWhiteSpace(state.DungeonSlug) || string.IsNullOrWhiteSpace(state.DungeonName) || state.KeyLevel == null)
            {
                return (null, "The composer is missing a dungeon or key level — pick them and try again.");
            }

            var channel = _client.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                _logger.LogWarning("PostGroup: channel {ChannelId} not resolvable", channelId);
                return (null, "I can't see this channel — check my permissions.");
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Per-guild open-group cap (ServerPushGroupSettings.MaxOpenGroups, null = unlimited).
            var settings = await db.ServerPushGroupSettings.FindAsync((long)guildId);
            if (settings?.MaxOpenGroups is int cap)
            {
                var openCount = await db.PushGroups.CountAsync(g => g.GuildId == (long)guildId
                    && (g.Status == PushGroupConstants.StatusOpen || g.Status == PushGroupConstants.StatusFull));
                if (openCount >= cap)
                {
                    return (null, $"This server caps open key groups at **{cap}** — close one first (`/keys list`).");
                }
            }

            var now = DateTime.UtcNow;

            // Creator stats come first — nothing is persisted yet, so a slow or failing
            // raider.io call can't leave half-created rows behind. Prefill already fetched the
            // profile; reuse its weekly runs when fresh instead of a second identical request.
            int? creatorBestThisWeek = null;
            if (state.WeeklyRuns != null && state.WeeklyRunsFetchedAt > DateTime.UtcNow.AddMinutes(-5))
            {
                creatorBestThisWeek = BestKeyFromRuns(state.WeeklyRuns, state.DungeonSlug!);
            }
            else if (!string.IsNullOrWhiteSpace(state.CharacterName) && !string.IsNullOrWhiteSpace(state.CharacterRealm))
            {
                var info = await TryFetchRioCharAsync(
                    state.CharacterName!, SlugForRio(state.CharacterRealm!), state.CharacterRegion ?? "us");
                creatorBestThisWeek = BestKeyThisWeekFrom(info, state.DungeonSlug!);
            }

            var group = new PushGroup
            {
                GuildId = (long)guildId,
                ChannelId = (long)channelId,
                CreatorUserId = (long)creatorUserId,
                CreatorUserName = creatorUserName,
                DungeonSlug = state.DungeonSlug!,
                DungeonName = state.DungeonName!,
                TargetKeyLevel = state.KeyLevel!.Value,
                IoRatingTarget = state.IoRating,
                IoRatingMin = state.IoRating.HasValue ? Math.Max(0m, state.IoRating.Value - ioWindow) : null,
                IoRatingMax = state.IoRating.HasValue ? state.IoRating.Value + ioWindow : null,
                KeyHolderUserId = (long)creatorUserId,
                KeyHolderDungeonName = state.DungeonName,
                KeyHolderKeyLevel = state.KeyLevel,
                ScheduledForUtc = state.ScheduledForUtc,
                Status = PushGroupConstants.StatusOpen,
                Notes = state.Notes,
                Region = state.CharacterRegion,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // Add the creator as the first signup in their chosen role. Group + signup go in
            // one SaveChangesAsync (linked via the navigation property) so creation is atomic —
            // no window where an Open group exists without its creator.
            var creatorSignup = new PushGroupSignup
            {
                PushGroup = group,
                UserId = (long)creatorUserId,
                UserName = creatorUserName,
                RoleSlot = state.Role ?? PushGroupConstants.RoleDps,
                SlotIndex = 0,
                WowCharacterName = state.CharacterName,
                WowCharacterRealm = state.CharacterRealm,
                WowClass = state.CharacterClass,
                WowSpec = state.CharacterSpec,
                IoRating = state.IoRating,
                IoBestThisWeek = creatorBestThisWeek,
                SignedUpAt = now,
            };
            db.PushGroups.Add(group);
            db.PushGroupSignups.Add(creatorSignup);
            await db.SaveChangesAsync();

            // AllowedMentions.None: the card renders user-typed Notes and <@id> roster mentions —
            // none of it may notify. The intentional pings live in the AutoPing follow-up message.
            var built = PushGroupPostBuilder.Build(group, new[] { creatorSignup });
            IUserMessage posted;
            try
            {
                posted = await channel.SendMessageAsync(
                    components: built.Components.Build(),
                    flags: built.Flags,
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex)
            {
                // Cancel rather than delete: a timeout can throw AFTER Discord actually created
                // the message, and buttons on such a zombie card should resolve to "isn't
                // accepting signups" — not "no longer exists".
                _logger.LogWarning(ex, "Failed to post push group {GroupId} in channel {ChannelId}; marking cancelled", group.Id, channelId);
                group.Status = PushGroupConstants.StatusCancelled;
                group.ArchivedAt = DateTime.UtcNow;
                group.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return (null, "Couldn't post the group — check that I have permission to send messages in this channel.");
            }

            group.MessageId = (long)posted.Id;
            await db.SaveChangesAsync();

            // Auto-ping roster matches (best-effort)
            try
            {
                var followupId = await AutoPingAsync(group, db, channel);
                if (followupId.HasValue)
                {
                    group.FollowupMessageId = (long)followupId.Value;
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-ping failed for push group {GroupId}", group.Id);
            }

            await UpdateHubAsync((long)guildId);
            return (group, null);
        }

        /// <summary>Adds a signup row (if room) and rebuilds the live post. Returns user-facing message.</summary>
        public async Task<string> AddSignupAsync(long groupId, ulong userId, string userName, string role)
        {
            var sem = _groupLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var group = await db.PushGroups.FirstOrDefaultAsync(g => g.Id == groupId);
                EvictLockIfTerminal(groupId, group);
                if (group == null) return "That group no longer exists.";
                if (group.Status != PushGroupConstants.StatusOpen && group.Status != PushGroupConstants.StatusFull)
                    return "That group isn't accepting signups anymore.";

                var current = await db.PushGroupSignups
                    .Where(s => s.PushGroupId == groupId && s.WithdrewAt == null)
                    .ToListAsync();

                if (current.Any(s => s.UserId == (long)userId))
                    return "You're already signed up for that group.";

                var capacity = role == PushGroupConstants.RoleDps ? PushGroupConstants.DefaultDpsSlots : 1;
                var taken = current.Where(s => s.RoleSlot == role).Select(s => s.SlotIndex).ToHashSet();
                int? nextSlot = Enumerable.Range(0, capacity).Cast<int?>().FirstOrDefault(i => !taken.Contains(i!.Value));
                if (nextSlot == null) return $"All {role} slots are filled.";

                var main = await _wowCache.GetUserMainCharacterAsync((long)userId);
                string? cls = null;
                string? spec = null;
                decimal? io = null;
                int? bestThisWeek = null;
                string? charName = main?.CharName;
                string? charRealm = main?.WowRealm;
                if (main != null)
                {
                    // One raider.io call serves class/spec/IO and this week's best run.
                    var slug = SlugForRio(main.LocalRealmSlug ?? main.WowRealm);
                    var region = main.WowRegion ?? "us";
                    var info = await TryFetchRioCharAsync(main.CharName, slug, region);
                    (cls, spec, io) = SnapshotFrom(info);
                    bestThisWeek = BestKeyThisWeekFrom(info, group.DungeonSlug);
                }

                var signup = new PushGroupSignup
                {
                    PushGroupId = groupId,
                    UserId = (long)userId,
                    UserName = userName,
                    RoleSlot = role,
                    SlotIndex = nextSlot.Value,
                    WowCharacterName = charName,
                    WowCharacterRealm = charRealm,
                    WowClass = cls,
                    WowSpec = spec,
                    IoRating = io,
                    IoBestThisWeek = bestThisWeek,
                    SignedUpAt = DateTime.UtcNow,
                };
                db.PushGroupSignups.Add(signup);

                // Recompute status if now full
                var afterAdd = current.Append(signup).ToList();
                if (IsRosterFull(afterAdd)) group.Status = PushGroupConstants.StatusFull;
                group.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await RebuildLivePostAsync(group, afterAdd);
                await UpdateHubAsync(group.GuildId);
                return $"Signed up as **{role}**.";
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task<string> WithdrawAsync(long groupId, ulong userId)
        {
            var sem = _groupLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var group = await db.PushGroups.FirstOrDefaultAsync(g => g.Id == groupId);
                EvictLockIfTerminal(groupId, group);
                if (group == null) return "That group no longer exists.";

                var signup = await db.PushGroupSignups
                    .Where(s => s.PushGroupId == groupId && s.UserId == (long)userId && s.WithdrewAt == null)
                    .FirstOrDefaultAsync();
                if (signup == null) return "You aren't signed up for that group.";

                signup.WithdrewAt = DateTime.UtcNow;
                if (group.Status == PushGroupConstants.StatusFull) group.Status = PushGroupConstants.StatusOpen;
                group.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                var current = await db.PushGroupSignups
                    .Where(s => s.PushGroupId == groupId && s.WithdrewAt == null)
                    .ToListAsync();
                await RebuildLivePostAsync(group, current);
                await UpdateHubAsync(group.GuildId);
                return "Withdrew you from the group.";
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task<string> SetKeyHolderAsync(long groupId, ulong userId, string userName, int keyLevel)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var group = await db.PushGroups.FirstOrDefaultAsync(g => g.Id == groupId);
            EvictLockIfTerminal(groupId, group);
            if (group == null) return "That group no longer exists.";
            if (group.Status != PushGroupConstants.StatusOpen && group.Status != PushGroupConstants.StatusFull)
                return "That group isn't active anymore.";

            // Only the creator or an active signup can take the key — prevents random users from
            // hijacking the key holder field.
            var isCreator = group.CreatorUserId == (long)userId;
            var isSignup = await db.PushGroupSignups.AnyAsync(s =>
                s.PushGroupId == groupId && s.UserId == (long)userId && s.WithdrewAt == null);
            if (!isCreator && !isSignup)
            {
                return "Only the creator or someone signed up to the group can set the key holder.";
            }

            group.KeyHolderUserId = (long)userId;
            group.KeyHolderKeyLevel = keyLevel;
            group.KeyHolderDungeonName = group.DungeonName;   // group is pinned to one dungeon
            group.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var current = await db.PushGroupSignups
                .Where(s => s.PushGroupId == groupId && s.WithdrewAt == null)
                .ToListAsync();
            await RebuildLivePostAsync(group, current);
            await UpdateHubAsync(group.GuildId);
            return $"Set you as key holder with +{keyLevel} {group.DungeonName}.";
        }

        public async Task<string> CloseAsync(long groupId, ulong actorUserId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var group = await db.PushGroups.FirstOrDefaultAsync(g => g.Id == groupId);
            EvictLockIfTerminal(groupId, group);
            if (group == null) return "That group no longer exists.";
            if (group.CreatorUserId != (long)actorUserId) return "Only the creator can close this group.";

            group.Status = PushGroupConstants.StatusCancelled;
            group.UpdatedAt = DateTime.UtcNow;
            group.ArchivedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var current = await db.PushGroupSignups
                .Where(s => s.PushGroupId == groupId && s.WithdrewAt == null)
                .ToListAsync();
            await RebuildLivePostAsync(group, current);

            // The group is done — drop its lock. Together with EvictLockIfTerminal this bounds
            // the dictionary to groups still being interacted with; groups abandoned without any
            // further clicks keep their entry until restart (an expiration sweep is the real fix).
            // A racing signup that re-creates the entry is harmless (status checks reject it).
            _groupLocks.TryRemove(groupId, out _);
            await UpdateHubAsync(group.GuildId);
            return "Closed the group.";
        }

        // --- hub -------------------------------------------------------------

        /// <summary>
        /// Re-renders the guild's persistent hub card, if one is configured. Best-effort:
        /// failures are logged, a vanished card clears itself so /keys hub can re-post.
        /// </summary>
        public async Task UpdateHubAsync(long guildId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var settings = await db.ServerPushGroupSettings.FindAsync(guildId);
                if (settings?.HubChannelId == null || settings.HubMessageId == null) return;

                var (groups, keys, top) = await LoadHubDataAsync(db, guildId);
                var components = PushGroupStatsCards.BuildHub(guildId, groups, keys, top);

                var channel = _client.GetChannel((ulong)settings.HubChannelId.Value) as IMessageChannel;
                if (channel == null)
                {
                    // Cache miss (startup / guild-unavailable window) — transient, keep the
                    // registration and let a later update catch up.
                    _logger.LogDebug("Hub channel {ChannelId} not resolvable for guild {GuildId}; skipping update",
                        settings.HubChannelId, guildId);
                    return;
                }

                var msg = await channel.GetMessageAsync((ulong)settings.HubMessageId.Value) as IUserMessage;
                if (msg == null)
                {
                    // Channel is fine but the card is gone — someone deleted it. Forget it so
                    // the next /keys hub re-posts.
                    settings.HubMessageId = null;
                    await db.SaveChangesAsync();
                    return;
                }

                await msg.ModifyAsync(p =>
                {
                    p.Components = components.Build();
                    p.Flags = MessageFlags.ComponentsV2;
                    p.AllowedMentions = AllowedMentions.None;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hub update failed for guild {GuildId}", guildId);
            }
        }

        /// <summary>Open groups + current-week key board + weekly top runs for a guild.</summary>
        public async Task<(List<PushGroupStatsCards.OpenGroupRow> Groups, List<PushGroupStatsCards.KeystoneRow> Keys, List<PushGroupStatsCards.LeaderboardRow> Top)>
            LoadHubDataAsync(NinjaBotEntities db, long guildId)
        {
            var open = await db.PushGroups
                .Where(g => g.GuildId == guildId
                    && (g.Status == PushGroupConstants.StatusOpen || g.Status == PushGroupConstants.StatusFull))
                .OrderBy(g => g.ScheduledForUtc ?? g.CreatedAt)
                .ToListAsync();

            var ids = open.Select(g => g.Id).ToList();
            var counts = await db.PushGroupSignups
                .Where(s => ids.Contains(s.PushGroupId) && s.WithdrewAt == null)
                .GroupBy(s => s.PushGroupId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync();
            var capacity = 2 + PushGroupConstants.DefaultDpsSlots;
            var groupRows = open
                .Select(g => new PushGroupStatsCards.OpenGroupRow(
                    g, counts.FirstOrDefault(c => c.Key == g.Id)?.Count ?? 0, capacity))
                .ToList();

            var memberIds = await db.WowCharAssociation
                .Where(a => a.ServerId == guildId && a.IsMain && a.UserId != null)
                .Select(a => a.UserId!.Value)
                .Distinct()
                .ToListAsync();

            var weekFloor = MythicPlusWeekly.CurrentWeekFloorUtc(DateTime.UtcNow);

            // Key board is scoped by Discord guild membership (client cache), NOT by linked
            // character — /keys board set works without /set-main, so the board must too.
            // UserKeystones has no guild key (a key is real in every shared guild), so this
            // reads bot-wide current-week rows; weekly pruning + the Take cap bound it. If
            // registrant volume ever makes this hot, add a per-guild registration key.
            var guild = _client.GetGuild((ulong)guildId);
            var keyRows = guild == null
                ? new List<PushGroupStatsCards.KeystoneRow>()
                : (await db.UserKeystones
                        .Where(k => k.WeekStartUtc > weekFloor)
                        .OrderByDescending(k => k.KeyLevel)
                        .Take(200)
                        .ToListAsync())
                    .Where(k => guild.GetUser((ulong)k.UserId) != null)
                    .Select(k => new PushGroupStatsCards.KeystoneRow(k.UserId, k.DungeonName, k.KeyLevel, k.UpdatedAt))
                    .ToList();

            var top = (await db.WeeklyKeyHistory
                    .Where(h => memberIds.Contains(h.UserId) && h.WeekStartUtc > weekFloor && h.RunCount > 0)
                    .ToListAsync())
                .GroupBy(h => h.UserId)
                .Select(g => g.OrderByDescending(h => h.BestKeyLevel).First())
                .OrderByDescending(h => h.BestKeyLevel)
                .Take(10)
                .Select(h => new PushGroupStatsCards.LeaderboardRow(
                    h.UserId, MythicPlusRotation.FindBySlug(h.DungeonSlug)?.Name ?? h.DungeonSlug, h.BestKeyLevel))
                .ToList();

            return (groupRows, keyRows, top);
        }

        // --- maintenance sweep -------------------------------------------------

        /// <summary>
        /// One pass of scheduled maintenance: T-15min start reminders and auto-closing
        /// stale groups (2h past their start, or 24h old with no schedule).
        /// </summary>
        public async Task RunMaintenanceSweepAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var now = DateTime.UtcNow;

            // Reminders — only for starts still ahead (or barely past) to avoid necro-pings
            // after downtime; ReminderSentAt is set even on failure so we never spam retries.
            var remindCeil = now.AddMinutes(15);
            var remindFloor = now.AddMinutes(-5);
            var toRemind = await db.PushGroups
                .Where(g => (g.Status == PushGroupConstants.StatusOpen || g.Status == PushGroupConstants.StatusFull)
                    && g.ScheduledForUtc != null && g.ReminderSentAt == null
                    && g.ScheduledForUtc <= remindCeil && g.ScheduledForUtc > remindFloor)
                .ToListAsync(ct);

            foreach (var g in toRemind)
            {
                try
                {
                    var signups = await db.PushGroupSignups
                        .Where(s => s.PushGroupId == g.Id && s.WithdrewAt == null)
                        .ToListAsync(ct);
                    var channel = _client.GetChannel((ulong)g.ChannelId) as IMessageChannel;
                    if (channel != null && signups.Count > 0)
                    {
                        var unix = new DateTimeOffset(g.ScheduledForUtc!.Value, TimeSpan.Zero).ToUnixTimeSeconds();
                        var mentions = string.Join(" ", signups.Select(s => $"<@{s.UserId}>"));
                        await channel.SendMessageAsync(
                            $"⏰ **+{g.TargetKeyLevel} {g.DungeonName}** starts <t:{unix}:R> — {mentions}",
                            allowedMentions: new AllowedMentions(AllowedMentionTypes.Users));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Start reminder failed for push group {GroupId}", g.Id);
                }
                g.ReminderSentAt = now;
                g.UpdatedAt = now;
            }
            if (toRemind.Count > 0) await db.SaveChangesAsync(ct);

            // Auto-close stale groups.
            var scheduledCutoff = now.AddHours(-2);
            var unscheduledCutoff = now.AddHours(-24);
            var toClose = await db.PushGroups
                .Where(g => (g.Status == PushGroupConstants.StatusOpen || g.Status == PushGroupConstants.StatusFull)
                    && ((g.ScheduledForUtc != null && g.ScheduledForUtc < scheduledCutoff)
                        || (g.ScheduledForUtc == null && g.CreatedAt < unscheduledCutoff)))
                .ToListAsync(ct);

            var touchedGuilds = new HashSet<long>();
            var closed = 0;
            foreach (var g in toClose)
            {
                if (ct.IsCancellationRequested) return;

                // Serialize against in-flight signups/withdrawals via the same per-group lock,
                // and re-read from a fresh context inside it — the outer query's tracked entity
                // may be stale by the time we get here.
                var sem = _groupLocks.GetOrAdd(g.Id, _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(ct);
                try
                {
                    using var closeScope = _scopeFactory.CreateScope();
                    var closeDb = closeScope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                    var fresh = await closeDb.PushGroups.FirstOrDefaultAsync(x => x.Id == g.Id, ct);
                    if (fresh == null) continue;
                    if (fresh.Status != PushGroupConstants.StatusOpen && fresh.Status != PushGroupConstants.StatusFull) continue;

                    var active = await closeDb.PushGroupSignups
                        .Where(s => s.PushGroupId == fresh.Id && s.WithdrewAt == null)
                        .ToListAsync(ct);
                    fresh.Status = IsRosterFull(active) ? PushGroupConstants.StatusCompleted : PushGroupConstants.StatusCancelled;
                    fresh.ArchivedAt = now;
                    fresh.UpdatedAt = now;
                    await closeDb.SaveChangesAsync(ct);
                    await RebuildLivePostAsync(fresh, active);
                    touchedGuilds.Add(fresh.GuildId);
                    closed++;
                }
                finally
                {
                    sem.Release();
                }
                _groupLocks.TryRemove(g.Id, out _);
            }
            if (closed > 0)
            {
                _logger.LogInformation("Auto-closed {Count} stale push group(s)", closed);
            }
            foreach (var gid in touchedGuilds)
            {
                await UpdateHubAsync(gid);
            }
        }

        // --- internals -----------------------------------------------------

        private async Task RebuildLivePostAsync(PushGroup group, IReadOnlyList<PushGroupSignup> signups)
        {
            try
            {
                var channel = _client.GetChannel((ulong)group.ChannelId) as IMessageChannel;
                if (channel == null) return;

                var msg = await channel.GetMessageAsync((ulong)group.MessageId) as IUserMessage;
                if (msg == null) return;

                var built = PushGroupPostBuilder.Build(group, signups);
                await msg.ModifyAsync(p =>
                {
                    p.Components = built.Components.Build();
                    p.Flags = built.Flags;
                    // Same as the initial send: Notes are user-typed, nothing here may notify.
                    p.AllowedMentions = AllowedMentions.None;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebuild live post for group {GroupId}", group.Id);
            }
        }

        private async Task<ulong?> AutoPingAsync(PushGroup group, NinjaBotEntities db, IMessageChannel channel)
        {
            if (!group.IoRatingMin.HasValue || !group.IoRatingMax.HasValue) return null;

            // Mention guild members with a linked main. Default-on; per-user opt-out via
            // /keys pings (UserPushGroupSettings.DmOnRosterPing). No IO filtering yet, so
            // the message must not claim any — future: filter by cached IO inside the window.
            var userIds = await db.WowCharAssociation
                .Where(a => a.ServerId == group.GuildId && a.IsMain && a.UserId != null && a.UserId != group.CreatorUserId)
                .Select(a => a.UserId!.Value)
                .Distinct()
                .ToListAsync();
            if (userIds.Count == 0) return null;
            var skipSet = (await db.UserPushGroupSettings
                .Where(s => userIds.Contains(s.UserId) && !s.DmOnRosterPing)
                .Select(s => s.UserId)
                .ToListAsync()).ToHashSet();

            var pingable = userIds.Where(uid => !skipSet.Contains(uid)).Take(20).ToList();
            if (pingable.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("📣 Key group up — pinging members with linked characters:");
            sb.AppendLine(string.Join(" ", pingable.Select(uid => $"<@{uid}>")));
            sb.Append("-# Don't want these pings? `/keys pings` turns them off.");

            var msg = await channel.SendMessageAsync(sb.ToString());
            return msg.Id;
        }

        /// <summary>Drops a group's semaphore once the group is gone or terminal.</summary>
        private static void EvictLockIfTerminal(long groupId, PushGroup? group)
        {
            if (group == null
                || group.Status == PushGroupConstants.StatusCancelled
                || group.Status == PushGroupConstants.StatusCompleted)
            {
                _groupLocks.TryRemove(groupId, out _);
            }
        }

        private static bool IsRosterFull(IReadOnlyList<PushGroupSignup> signups)
        {
            return signups.Count(s => s.RoleSlot == PushGroupConstants.RoleTank && s.WithdrewAt == null) >= 1
                && signups.Count(s => s.RoleSlot == PushGroupConstants.RoleHealer && s.WithdrewAt == null) >= 1
                && signups.Count(s => s.RoleSlot == PushGroupConstants.RoleDps && s.WithdrewAt == null) >= PushGroupConstants.DefaultDpsSlots;
        }

        /// <summary>Single raider.io profile fetch; SnapshotFrom/BestKeyThisWeekFrom read off it.</summary>
        private async Task<RaiderIOModels.RioMythicPlusChar?> TryFetchRioCharAsync(string name, string realm, string region)
        {
            try
            {
                return await _rio.GetCharMythicPlusInfoAsync(name, realm, region);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IO fetch failed for {Name}-{Realm}-{Region}", name, realm, region);
                return null;
            }
        }

        private static (string? cls, string? spec, decimal? io) SnapshotFrom(RaiderIOModels.RioMythicPlusChar? info)
        {
            if (info == null) return (null, null, null);
            var io = (decimal?)info.MythicPlusScores?.FirstOrDefault()?.Scores?.All;
            return (info.Class, info.ActiveSpecName, io);
        }

        private static int? BestKeyThisWeekFrom(RaiderIOModels.RioMythicPlusChar? info, string dungeonSlug) =>
            BestKeyFromRuns(info?.MythicPlusWeeklyHighestLevelRuns, dungeonSlug);

        private static int? BestKeyFromRuns(RaiderIOModels.MythicPlusRun[]? runs, string dungeonSlug)
        {
            if (runs == null) return null;
            var dungeon = MythicPlusRotation.FindBySlug(dungeonSlug);
            if (dungeon == null) return null;
            var best = runs
                .Where(r => string.Equals(r.Dungeon, dungeon.Name, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(r.ShortName, dungeon.ShortName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.MythicLevel)
                .FirstOrDefault();
            return best == null ? null : (int)best.MythicLevel;
        }
    }
}
