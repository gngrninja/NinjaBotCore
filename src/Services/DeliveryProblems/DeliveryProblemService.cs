#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services.DeliveryProblems
{
    /// <summary>
    /// Someone the owner can DM. <see cref="Via"/> says why: they set the feature up, linked the
    /// WoW guild (New log doesn't record who turned it on before this release), or own the server.
    /// </summary>
    public sealed record DeliveryPerson(ulong UserId, string? Name, bool? InServer, string Via);

    /// <summary>A recorded problem with what Discord says about it right now.</summary>
    public sealed record DeliveryProblemView(
        DeliveryProblem Row,
        string? ServerName,
        bool BotPresent,
        string? ChannelName,
        bool? ChannelExists,
        IReadOnlyList<string> MissingPermissions,
        bool FixedNow,
        DeliveryPerson? Setter,
        DeliveryPerson? Owner,
        string State,
        string ProblemText,
        string DmPreview);

    public sealed record DeliveryProblemCounts(int Open, int Notified, int Dismissed, int Resolved, int BotLeft);

    public static class DeliveryDmResult
    {
        public const string Sent = DeliveryNotifyResult.Sent;
        public const string DmsClosed = DeliveryNotifyResult.DmsClosed;
        public const string NotInServer = DeliveryNotifyResult.NotInServer;
        public const string Failed = DeliveryNotifyResult.Failed;
        public const string NoRecipient = "no_recipient";
        public const string BotLeft = "bot_left";
        public const string Resolved = "resolved";
        public const string LooksFixed = "looks_fixed";
        public const string RecentlySent = "recently_sent";

        /// <summary>Discord couldn't say whether they're still in the server; nothing was sent.</summary>
        public const string CheckFailed = "check_failed";
    }

    public static class DeliveryTurnOffStatus
    {
        public const string TurnedOff = "turned_off";
        public const string AlreadyOff = "already_off";
        public const string NotSupported = "not_supported";
        public const string NotFound = "not_found";

        /// <summary>Already resolved, or the server moved or turned the feature off itself: nothing changed.</summary>
        public const string Resolved = "resolved";
    }

    /// <summary>
    /// The bot owner's Actionable Alerts: problems the bot and the helpers recorded, what is
    /// wrong now, and the actions the owner takes after reviewing them. Nothing here runs
    /// unless the owner asks; in particular no DM is ever sent automatically.
    /// </summary>
    public sealed class DeliveryProblemService
    {
        /// <summary>Resolved problems stay on the page this long, so the owner sees what cleared.</summary>
        public static readonly TimeSpan ResolvedWindow = TimeSpan.FromDays(14);

        public const int MaxListed = 500;
        public const int MaxBatch = 200;

        /// <summary>A second DM to the same person about the same problem is refused this long.</summary>
        public static readonly TimeSpan DmCooldown = TimeSpan.FromMinutes(10);

        private readonly IServiceScopeFactory _scopes;
        private readonly IDeliveryProblemDiscord _discord;
        private readonly ILogger<DeliveryProblemService> _logger;
        private readonly Func<DateTimeOffset> _clock;

        // One DM at a time, so a double click or a retry after a web timeout can't send two.
        private readonly SemaphoreSlim _dmLock = new(1, 1);

        public DeliveryProblemService(
            IServiceScopeFactory scopes,
            IDeliveryProblemDiscord discord,
            ILogger<DeliveryProblemService> logger,
            Func<DateTimeOffset>? clock = null)
        {
            _scopes = scopes;
            _discord = discord;
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public async Task<(IReadOnlyList<DeliveryProblemView> Problems, DeliveryProblemCounts Counts)> ListAsync()
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var since = _clock().UtcDateTime - ResolvedWindow;
            var rows = await db.DeliveryProblems
                .Where(p => p.ResolvedAt == null || p.ResolvedAt >= since)
                .OrderByDescending(p => p.LastSeenAt)
                .ThenByDescending(p => p.Id)
                .Take(MaxListed)
                .ToListAsync();

            var context = await SetupContext.LoadAsync(db, rows);
            await ResolveStaleAsync(db, rows, context);
            var views = rows.Select(r => View(r, context)).ToList();
            var unresolved = views.Where(v => v.State != "resolved").ToList();
            var counts = new DeliveryProblemCounts(
                views.Count(v => v.State == "open"),
                views.Count(v => v.State == "notified"),
                views.Count(v => v.State == "dismissed"),
                views.Count(v => v.State == "resolved"),
                unresolved.Count(v => !v.BotPresent));
            return (views, counts);
        }

        public async Task<DeliveryProblemView?> GetAsync(long id)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var row = await db.DeliveryProblems.FirstOrDefaultAsync(p => p.Id == id);
            if (row == null)
            {
                return null;
            }

            var context = await SetupContext.LoadAsync(db, new[] { row });
            await ResolveStaleAsync(db, new[] { row }, context);
            return View(row, context);
        }

        /// <summary>
        /// A problem whose feature the server has since moved to another channel, or turned off
        /// itself, is over: no post will ever resolve it, and the owner must not act on it.
        /// </summary>
        private async Task ResolveStaleAsync(NinjaBotEntities db, IEnumerable<DeliveryProblem> rows, SetupContext context)
        {
            var now = _clock().UtcDateTime;
            var changed = false;
            foreach (var row in rows.Where(r => r.ResolvedAt == null))
            {
                if (context.Stale(row) is { } resolution)
                {
                    row.ResolvedAt = now;
                    row.Resolution = resolution;
                    changed = true;
                }
            }

            if (changed)
            {
                await db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// DMs the person who set the feature up, or the server owner, with the problem and how to
        /// fix it. Refuses when there is nothing left to fix, the bot or the person has left the
        /// server, or the same person was DMed about it moments ago.
        /// </summary>
        public async Task<(string Result, DeliveryProblemView? View)> SendDmAsync(long id, string to)
        {
            await _dmLock.WaitAsync();
            try
            {
                var view = await GetAsync(id);
                if (view == null)
                {
                    return (DeliveryDmResult.Failed, null);
                }

                if (view.Row.ResolvedAt != null)
                {
                    return (DeliveryDmResult.Resolved, view);
                }

                if (!view.BotPresent)
                {
                    return (DeliveryDmResult.BotLeft, view);
                }

                if (view.FixedNow)
                {
                    return (DeliveryDmResult.LooksFixed, view);
                }

                var person = to == DeliveryProblemText.Owner ? view.Owner : view.Setter;
                if (person == null)
                {
                    return (DeliveryDmResult.NoRecipient, view);
                }

                var now = _clock().UtcDateTime;
                if (view.Row.NotifiedAt is { } told && view.Row.NotifiedUserId == (long)person.UserId && now - told < DmCooldown)
                {
                    return (DeliveryDmResult.RecentlySent, view);
                }

                var member = await _discord.IsMemberAsync((ulong)view.Row.ServerId, person.UserId);
                if (member == null)
                {
                    return (DeliveryDmResult.CheckFailed, view);
                }

                string result;
                if (member == false)
                {
                    result = DeliveryDmResult.NotInServer;
                }
                else
                {
                    var text = DeliveryProblemText.Dm(
                        view.Row.Feature,
                        view.Row.Problem,
                        view.ServerName,
                        (ulong)view.Row.ChannelId,
                        view.ChannelName,
                        view.MissingPermissions,
                        person.Via);
                    result = await _discord.SendDmAsync(person.UserId, text) switch
                    {
                        DeliveryDmOutcome.Sent => DeliveryDmResult.Sent,
                        DeliveryDmOutcome.DmsClosed => DeliveryDmResult.DmsClosed,
                        _ => DeliveryDmResult.Failed
                    };
                }

                using (var scope = _scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                    var row = await db.DeliveryProblems.FirstOrDefaultAsync(p => p.Id == id);
                    if (row != null)
                    {
                        row.LastAttemptAt = now;
                        row.LastAttemptRole = to;
                        row.LastAttemptResult = result;

                        // Only a delivered DM counts as told, so a later failure never hides who was.
                        if (result == DeliveryDmResult.Sent)
                        {
                            row.NotifiedAt = now;
                            row.NotifiedUserId = (long)person.UserId;
                            row.NotifiedRole = to;
                        }

                        await db.SaveChangesAsync();
                    }
                }

                _logger.LogInformation(
                    "Delivery problem {Id} ({Feature}) DM to the {Role} of server {ServerId}: {Result}",
                    id, view.Row.Feature, to, view.Row.ServerId, result);
                return (result, await GetAsync(id));
            }
            finally
            {
                _dmLock.Release();
            }
        }

        /// <summary>Checks the bot's permissions again, and resolves the problem when it can post.</summary>
        public async Task<(bool Fixed, DeliveryProblemView? View)> RecheckAsync(long id)
        {
            var view = await GetAsync(id);
            if (view == null)
            {
                return (false, null);
            }

            if (view.Row.ResolvedAt != null)
            {
                return (true, view);
            }

            if (!view.FixedNow)
            {
                return (false, view);
            }

            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                await DeliveryProblemStore.ResolveAsync(
                    db, view.Row.ServerId, view.Row.Feature, view.Row.ChannelId, DeliveryResolution.Fixed, _clock().UtcDateTime);
            }

            return (true, await GetAsync(id));
        }

        /// <summary>
        /// Turns the feature off for the server, the same as its own off command, and resolves
        /// the problem. The word filter has no channel to turn off, so it is left alone.
        /// </summary>
        public async Task<IReadOnlyList<(long Id, string Status)>> TurnOffAsync(IReadOnlyList<long> ids)
        {
            var results = new List<(long, string)>();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            foreach (var id in ids.Distinct().Take(MaxBatch))
            {
                var row = await db.DeliveryProblems.FirstOrDefaultAsync(p => p.Id == id);
                if (row == null)
                {
                    results.Add((id, DeliveryTurnOffStatus.NotFound));
                    continue;
                }

                if (row.ResolvedAt != null)
                {
                    results.Add((id, DeliveryTurnOffStatus.Resolved));
                    continue;
                }

                if (!DeliveryProblemText.CanTurnOff(row.Feature))
                {
                    results.Add((id, DeliveryTurnOffStatus.NotSupported));
                    continue;
                }

                // Never turn off a feature the server has since moved to a working channel.
                var context = await SetupContext.LoadAsync(db, new[] { row });
                if (context.Stale(row) is { } resolution)
                {
                    row.ResolvedAt = _clock().UtcDateTime;
                    row.Resolution = resolution;
                    await db.SaveChangesAsync();
                    results.Add((id, DeliveryTurnOffStatus.Resolved));
                    continue;
                }

                var changed = await TurnOffFeatureAsync(db, row);
                row.ResolvedAt = _clock().UtcDateTime;
                row.Resolution = DeliveryResolution.TurnedOff;
                await db.SaveChangesAsync();
                InvalidateCaches(scope.ServiceProvider, row);
                _logger.LogInformation(
                    "Delivery problem {Id}: turned off {Feature} for server {ServerId} on the web",
                    id, row.Feature, row.ServerId);
                results.Add((id, changed ? DeliveryTurnOffStatus.TurnedOff : DeliveryTurnOffStatus.AlreadyOff));
            }

            return results;
        }

        public async Task<int> DismissAsync(IReadOnlyList<long> ids)
        {
            var wanted = ids.Distinct().Take(MaxBatch).ToList();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            var rows = await db.DeliveryProblems
                .Where(p => wanted.Contains(p.Id) && p.ResolvedAt == null && p.DismissedAt == null)
                .ToListAsync();
            var now = _clock().UtcDateTime;
            foreach (var row in rows)
            {
                row.DismissedAt = now;
            }

            await db.SaveChangesAsync();
            return rows.Count;
        }

        private static async Task<bool> TurnOffFeatureAsync(NinjaBotEntities db, DeliveryProblem row)
        {
            var server = row.ServerId;
            switch (row.Feature)
            {
                case DeliveryFeature.NewLog:
                {
                    var watching = await db.LogMonitoring.Where(m => m.ServerId == server && m.MonitorLogs).ToListAsync();
                    watching.ForEach(m => m.MonitorLogs = false);
                    return watching.Count > 0;
                }
                case DeliveryFeature.Welcome:
                {
                    var greeting = await db.ServerGreetings.FirstOrDefaultAsync(g => g.DiscordGuildId == server && g.GreetUsers == true);
                    if (greeting != null)
                    {
                        greeting.GreetUsers = false;
                    }

                    return greeting != null;
                }
                case DeliveryFeature.Leave:
                {
                    var greeting = await db.ServerGreetings.FirstOrDefaultAsync(g => g.DiscordGuildId == server && g.PartUsers == true);
                    if (greeting != null)
                    {
                        greeting.PartUsers = false;
                    }

                    return greeting != null;
                }
                case DeliveryFeature.ModerationLog:
                {
                    var watcher = await db.ModerationWatcher.FirstOrDefaultAsync(m => m.DiscordGuildId == server);
                    if (watcher == null || !Watching(watcher))
                    {
                        return false;
                    }

                    watcher.WatchVoice = false;
                    watcher.WatchMessages = false;
                    watcher.WatchRoles = false;
                    watcher.WatchBans = false;
                    watcher.WatchNicknames = false;
                    watcher.WatchProfiles = false;
                    watcher.WatchAudit = false;
                    watcher.WatchServer = false;
                    return true;
                }
                case DeliveryFeature.RealmAlert:
                {
                    // Alerts are per person; this removes those posting to the broken channel only.
                    var subscriptions = await db.RealmWatchSubscriptions
                        .Where(s => s.GuildId == server && s.ChannelId == row.ChannelId)
                        .ToListAsync();
                    db.RealmWatchSubscriptions.RemoveRange(subscriptions);
                    return subscriptions.Count > 0;
                }
                default:
                    return false;
            }
        }

        private static bool Watching(ModerationWatcher w) =>
            w.WatchVoice == true || w.WatchMessages == true || w.WatchRoles == true || w.WatchBans == true
            || w.WatchNicknames == true || w.WatchProfiles == true || w.WatchAudit == true || w.WatchServer == true;

        // The same caches the features' own commands clear, so the change takes effect at once.
        private static void InvalidateCaches(IServiceProvider services, DeliveryProblem row)
        {
            switch (row.Feature)
            {
                case DeliveryFeature.Welcome:
                case DeliveryFeature.Leave:
                    services.GetService<WowCacheService>()?.InvalidateServerGreeting(row.ServerId);
                    break;
                case DeliveryFeature.ModerationLog:
                    services.GetService<ModerationWatcherService>()?.InvalidateSettingsCache(row.ServerId);
                    break;
            }
        }

        private DeliveryProblemView View(DeliveryProblem row, SetupContext context)
        {
            var serverId = (ulong)row.ServerId;
            var server = _discord.Server(serverId);
            context.Servers.TryGetValue(row.ServerId, out var known);
            var serverName = server.Name ?? known?.ServerName;

            var channel = _discord.Channel(serverId, (ulong)row.ChannelId, DeliveryProblemText.Needed(row.Feature));
            var channelName = channel?.Name ?? context.ChannelName(row);
            var missing = channel?.MissingPermissions ?? Array.Empty<string>();
            var fixedNow = row.ResolvedAt == null && row.ChannelId > 0 && channel is { Exists: true } && missing.Count == 0;

            DeliveryPerson? owner = null;
            if (server.OwnerId is { } ownerId)
            {
                owner = new DeliveryPerson(ownerId, server.OwnerName, true, DeliveryProblemText.Owner);
            }
            else if (known?.OwnerId is long storedOwner && storedOwner > 0)
            {
                owner = new DeliveryPerson((ulong)storedOwner, known.OwnerName, null, DeliveryProblemText.Owner);
            }

            DeliveryPerson? setter = null;
            if (context.Setter(row) is { } set && set.Id > 0)
            {
                var id = (ulong)set.Id;
                setter = new DeliveryPerson(id, _discord.MemberName(serverId, id) ?? set.Name, _discord.InServer(serverId, id), set.Via);
            }

            var state = row.ResolvedAt != null ? "resolved"
                : row.DismissedAt != null ? "dismissed"
                : row.NotifiedAt != null ? "notified"
                : "open";
            var preview = DeliveryProblemText.Dm(
                row.Feature,
                row.Problem,
                serverName,
                (ulong)row.ChannelId,
                channelName,
                missing,
                setter?.Via ?? DeliveryProblemText.Owner);

            return new DeliveryProblemView(
                row,
                serverName,
                server.Present,
                channelName,
                channel?.Exists,
                missing,
                fixedNow,
                setter,
                owner,
                state,
                DeliveryProblemText.Problem(row.Problem, channelName, row.DiscordCode),
                preview);
        }

        /// <summary>The settings rows behind a page of problems, loaded once for all of them.</summary>
        private sealed class SetupContext
        {
            public Dictionary<long, DiscordServer> Servers { get; private init; } = new();
            private Dictionary<long, LogMonitoring> Logs { get; init; } = new();
            private Dictionary<long, WowGuildAssociations> Guilds { get; init; } = new();
            private Dictionary<long, ServerGreeting> Greetings { get; init; } = new();
            private Dictionary<long, ModerationWatcher> Watchers { get; init; } = new();
            private List<RealmWatchSubscription> Realms { get; init; } = new();
            private Dictionary<long, long> WordSetters { get; init; } = new();
            private HashSet<long> HasWords { get; init; } = new();

            public static async Task<SetupContext> LoadAsync(NinjaBotEntities db, IReadOnlyCollection<DeliveryProblem> rows)
            {
                var ids = rows.Select(r => r.ServerId).Distinct().ToList();
                var nullable = ids.Select(i => (long?)i).ToList();
                return new SetupContext
                {
                    Servers = (await db.DiscordServers.AsNoTracking().Where(s => ids.Contains(s.ServerId)).ToListAsync())
                        .GroupBy(s => s.ServerId).ToDictionary(g => g.Key, g => g.First()),
                    Logs = (await db.LogMonitoring.AsNoTracking().Where(l => ids.Contains(l.ServerId)).ToListAsync())
                        .GroupBy(l => l.ServerId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.MonitorLogs).First()),
                    Guilds = (await db.WowGuildAssociations.AsNoTracking().Where(g => nullable.Contains(g.ServerId)).ToListAsync())
                        .GroupBy(g => g.ServerId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.TimeSet).First()),
                    Greetings = (await db.ServerGreetings.AsNoTracking().Where(g => ids.Contains(g.DiscordGuildId)).ToListAsync())
                        .GroupBy(g => g.DiscordGuildId).ToDictionary(g => g.Key, g => g.First()),
                    Watchers = (await db.ModerationWatcher.AsNoTracking().Where(m => ids.Contains(m.DiscordGuildId)).ToListAsync())
                        .GroupBy(m => m.DiscordGuildId).ToDictionary(g => g.Key, g => g.First()),
                    Realms = await db.RealmWatchSubscriptions.AsNoTracking().Where(s => ids.Contains(s.GuildId)).ToListAsync(),
                    WordSetters = (await db.WordList.AsNoTracking().Where(w => ids.Contains(w.ServerId) && w.SetById > 0).ToListAsync())
                        .GroupBy(w => w.ServerId).ToDictionary(g => g.Key, g => g.First().SetById),
                    HasWords = (await db.WordList.AsNoTracking().Where(w => ids.Contains(w.ServerId)).Select(w => w.ServerId).Distinct().ToListAsync())
                        .ToHashSet()
                };
            }

            /// <summary>Who set the feature up. New log falls back to whoever linked the WoW guild.</summary>
            public (long Id, string? Name, string Via)? Setter(DeliveryProblem row)
            {
                const string setup = DeliveryProblemText.Setter;
                var server = row.ServerId;
                switch (row.Feature)
                {
                    case DeliveryFeature.NewLog:
                        if (Logs.TryGetValue(server, out var log) && log.SetById is long logSetter and > 0)
                        {
                            return (logSetter, log.SetByName, setup);
                        }

                        return Guilds.TryGetValue(server, out var guild) && guild.SetById is long guildSetter and > 0
                            ? (guildSetter, guild.SetBy, DeliveryProblemText.GuildLinker)
                            : null;
                    case DeliveryFeature.Welcome:
                    case DeliveryFeature.Leave:
                        return Greetings.TryGetValue(server, out var greeting) && greeting.SetById is long greetSetter and > 0
                            ? (greetSetter, greeting.SetByName, setup)
                            : null;
                    case DeliveryFeature.ModerationLog:
                        return Watchers.TryGetValue(server, out var watcher) && watcher.SetById is long watchSetter and > 0
                            ? (watchSetter, watcher.SetByName, setup)
                            : null;
                    case DeliveryFeature.RealmAlert:
                        var realm = Realms
                            .Where(s => s.GuildId == server && s.ChannelId == row.ChannelId)
                            .OrderByDescending(s => s.CreatedAt)
                            .FirstOrDefault();
                        return realm == null ? null : (realm.UserId, null, setup);
                    case DeliveryFeature.WordFilter:
                        return WordSetters.TryGetValue(server, out var wordSetter) ? (wordSetter, null, setup) : null;
                    default:
                        return null;
                }
            }

            /// <summary>
            /// How the problem ended without a post, or null while it still applies: the feature is
            /// off now, or posts in another channel than the one that failed.
            /// </summary>
            public string? Stale(DeliveryProblem row)
            {
                var server = row.ServerId;
                long? current;
                switch (row.Feature)
                {
                    case DeliveryFeature.NewLog:
                        if (!Logs.TryGetValue(server, out var log) || !log.MonitorLogs)
                        {
                            return DeliveryResolution.FeatureOff;
                        }

                        current = log.ChannelId;
                        break;
                    case DeliveryFeature.Welcome:
                        if (!Greetings.TryGetValue(server, out var welcome) || welcome.GreetUsers != true)
                        {
                            return DeliveryResolution.FeatureOff;
                        }

                        // No channel set means the server's default channel, which the failure recorded.
                        current = welcome.GreetingChannelId > 0 ? welcome.GreetingChannelId : null;
                        break;
                    case DeliveryFeature.Leave:
                        if (!Greetings.TryGetValue(server, out var leave) || leave.PartUsers != true)
                        {
                            return DeliveryResolution.FeatureOff;
                        }

                        current = leave.PartingChannelId > 0 ? leave.PartingChannelId
                            : leave.GreetingChannelId > 0 ? leave.GreetingChannelId
                            : null;
                        break;
                    case DeliveryFeature.ModerationLog:
                        if (!Watchers.TryGetValue(server, out var watcher) || !Watching(watcher) || watcher.ChannelId is not long watched)
                        {
                            return DeliveryResolution.FeatureOff;
                        }

                        current = watched;
                        break;
                    case DeliveryFeature.RealmAlert:
                        if (Realms.Any(s => s.GuildId == server && s.ChannelId == row.ChannelId))
                        {
                            return null;
                        }

                        return Realms.Any(s => s.GuildId == server && s.ChannelId != null)
                            ? DeliveryResolution.Moved
                            : DeliveryResolution.FeatureOff;
                    case DeliveryFeature.WordFilter:
                        return HasWords.Contains(server) ? null : DeliveryResolution.FeatureOff;
                    default:
                        return null;
                }

                return current != null && current != row.ChannelId ? DeliveryResolution.Moved : null;
            }

            /// <summary>The channel's saved name, for when it no longer exists.</summary>
            public string? ChannelName(DeliveryProblem row)
            {
                var name = row.Feature switch
                {
                    DeliveryFeature.NewLog when Logs.TryGetValue(row.ServerId, out var log) && log.ChannelId == row.ChannelId => log.ChannelName,
                    DeliveryFeature.Welcome when Greetings.TryGetValue(row.ServerId, out var g) && g.GreetingChannelId == row.ChannelId => g.GreetingChannelName,
                    DeliveryFeature.ModerationLog when Watchers.TryGetValue(row.ServerId, out var w) && w.ChannelId == row.ChannelId => w.ChannelName,
                    _ => null
                };
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
    }
}
