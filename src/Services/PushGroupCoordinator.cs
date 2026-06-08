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
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Heavy-lifting service for the /pushgroup feature. Owns:
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
                var (cls, io) = await TryFetchCharSnapshotAsync(main.CharName, slug, state.CharacterRegion);
                state.CharacterClass = cls;
                state.IoRating = io;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefill character lookup failed for user {UserId}", userId);
            }
        }

        /// <summary>
        /// Returns a Raider.IO-compatible realm slug. If the input already looks like a slug
        /// (lowercase + dashes), pass through; otherwise lowercase and replace whitespace with `-`.
        /// </summary>
        private static string SlugForRio(string? realm)
        {
            if (string.IsNullOrWhiteSpace(realm)) return string.Empty;
            var trimmed = realm.Trim();
            if (trimmed.All(ch => char.IsLower(ch) || ch == '-' || char.IsDigit(ch)))
                return trimmed;
            return new string(trimmed.ToLowerInvariant()
                .Select(ch => ch switch { ' ' => '-', '\'' => '\0', _ => ch })
                .Where(ch => ch != '\0').ToArray());
        }

        /// <summary>Persists a new push group, posts the live message, runs the auto-ping follow-up.</summary>
        public async Task<PushGroup?> PostGroupAsync(PushGroupWizardState.State state, ulong guildId, ulong channelId, ulong creatorUserId, string creatorUserName, int ioWindow)
        {
            if (string.IsNullOrWhiteSpace(state.DungeonSlug) || string.IsNullOrWhiteSpace(state.DungeonName) || state.KeyLevel == null)
            {
                return null;
            }

            var channel = _client.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                _logger.LogWarning("PostGroup: channel {ChannelId} not resolvable", channelId);
                return null;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var now = DateTime.UtcNow;
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

            db.PushGroups.Add(group);
            await db.SaveChangesAsync();

            // Add the creator as the first signup in their chosen role.
            int? creatorBestThisWeek = null;
            if (!string.IsNullOrWhiteSpace(state.CharacterName) && !string.IsNullOrWhiteSpace(state.CharacterRealm))
            {
                creatorBestThisWeek = await TryFetchBestKeyThisWeekAsync(
                    state.CharacterName!, SlugForRio(state.CharacterRealm!), state.CharacterRegion ?? "us", state.DungeonSlug!);
            }

            var creatorSignup = new PushGroupSignup
            {
                PushGroupId = group.Id,
                UserId = (long)creatorUserId,
                UserName = creatorUserName,
                RoleSlot = state.Role ?? PushGroupConstants.RoleDps,
                SlotIndex = 0,
                WowCharacterName = state.CharacterName,
                WowCharacterRealm = state.CharacterRealm,
                WowClass = state.CharacterClass,
                IoRating = state.IoRating,
                IoBestThisWeek = creatorBestThisWeek,
                SignedUpAt = now,
            };
            db.PushGroupSignups.Add(creatorSignup);
            await db.SaveChangesAsync();

            var built = PushGroupPostBuilder.Build(group, new[] { creatorSignup });
            var posted = await channel.SendMessageAsync(components: built.Components.Build(), flags: built.Flags);

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

            return group;
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
                decimal? io = null;
                int? bestThisWeek = null;
                string? charName = main?.CharName;
                string? charRealm = main?.WowRealm;
                if (main != null)
                {
                    var slug = SlugForRio(main.LocalRealmSlug ?? main.WowRealm);
                    var region = main.WowRegion ?? "us";
                    var (c, i) = await TryFetchCharSnapshotAsync(main.CharName, slug, region);
                    cls = c;
                    io = i;
                    bestThisWeek = await TryFetchBestKeyThisWeekAsync(main.CharName, slug, region, group.DungeonSlug);
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
            if (group == null) return "That group no longer exists.";

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
            return $"Set you as key holder with +{keyLevel} {group.DungeonName}.";
        }

        public async Task<string> CloseAsync(long groupId, ulong actorUserId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var group = await db.PushGroups.FirstOrDefaultAsync(g => g.Id == groupId);
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
            return "Closed the group.";
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

            // Mention guild members with a linked main, default-on (per-user opt-out via
            // UserPushGroupSettings.DmOnRosterPing). Future: filter by cached IO inside the window.
            var assocs = await db.WowCharAssociation
                .Where(a => a.ServerId == group.GuildId && a.IsMain && a.UserId != null && a.UserId != group.CreatorUserId)
                .ToListAsync();
            if (assocs.Count == 0) return null;

            var userIds = assocs.Select(a => a.UserId!.Value).ToList();
            var skipSet = (await db.UserPushGroupSettings
                .Where(s => userIds.Contains(s.UserId) && !s.DmOnRosterPing)
                .Select(s => s.UserId)
                .ToListAsync()).ToHashSet();

            var pingable = userIds.Where(uid => !skipSet.Contains(uid)).Take(20).ToList();
            if (pingable.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("📣 Push group up — pinging roster (IO-window auto-ping):");
            sb.Append(string.Join(" ", pingable.Select(uid => $"<@{uid}>")));

            var msg = await channel.SendMessageAsync(sb.ToString());
            return msg.Id;
        }

        private static bool IsRosterFull(IReadOnlyList<PushGroupSignup> signups)
        {
            return signups.Count(s => s.RoleSlot == PushGroupConstants.RoleTank && s.WithdrewAt == null) >= 1
                && signups.Count(s => s.RoleSlot == PushGroupConstants.RoleHealer && s.WithdrewAt == null) >= 1
                && signups.Count(s => s.RoleSlot == PushGroupConstants.RoleDps && s.WithdrewAt == null) >= PushGroupConstants.DefaultDpsSlots;
        }

        private async Task<(string? cls, decimal? io)> TryFetchCharSnapshotAsync(string name, string realm, string region)
        {
            try
            {
                var info = await _rio.GetCharMythicPlusInfoAsync(name, realm, region);
                if (info == null) return (null, null);
                var io = (decimal?)info.MythicPlusScores?.FirstOrDefault()?.Scores?.All;
                return (info.Class, io);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IO fetch failed for {Name}-{Realm}-{Region}", name, realm, region);
                return (null, null);
            }
        }

        private async Task<int?> TryFetchBestKeyThisWeekAsync(string name, string realm, string region, string dungeonSlug)
        {
            try
            {
                var info = await _rio.GetCharMythicPlusInfoAsync(name, realm, region);
                if (info?.MythicPlusWeeklyHighestLevelRuns == null) return null;
                var dungeon = MythicPlusRotation.FindBySlug(dungeonSlug);
                if (dungeon == null) return null;
                var runs = info.MythicPlusWeeklyHighestLevelRuns
                    .Where(r => string.Equals(r.Dungeon, dungeon.Name, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(r.ShortName, dungeon.ShortName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.MythicLevel)
                    .FirstOrDefault();
                return runs == null ? null : (int)runs.MythicLevel;
            }
            catch
            {
                return null;
            }
        }
    }
}
