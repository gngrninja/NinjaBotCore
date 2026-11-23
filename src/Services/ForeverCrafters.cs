#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>Someone whose professions are worth asking the armory for.</summary>
    public sealed record ForeverCraftCandidate(string FirstName, string Surname, string Slug, int Level, ForeverClass? Class);

    /// <summary>Whose professions a server's index covers: its guild on the armory, plus characters members shared.</summary>
    public sealed record ForeverCraftSource(ForeverRegion Region, string? GuildSlug, IReadOnlyList<ForeverCraftCandidate> Shared);

    /// <summary>An index being read. <see cref="Done"/> of <see cref="Total"/> can be shown while <see cref="Task"/> runs.</summary>
    public sealed class ForeverCraftBuild
    {
        private int _done;
        private int _total;

        public Task<ForeverCraftIndex> Task { get; internal set; } = System.Threading.Tasks.Task.FromResult(
            new ForeverCraftIndex(Array.Empty<ForeverCrafter>(), DateTimeOffset.MinValue, 0, 0));

        public int Done => Volatile.Read(ref _done);
        public int Total => Volatile.Read(ref _total);

        internal void Start(int total) => Volatile.Write(ref _total, total);
        internal void Step() => Interlocked.Increment(ref _done);
    }

    public interface IForeverCrafters
    {
        /// <summary>The server's index when it is recent and covers this source; never waits.</summary>
        ForeverCraftIndex? Current(long discordGuildId, ForeverCraftSource source);

        /// <summary>Starts reading the server's index, or joins a read already under way.</summary>
        ForeverCraftBuild Build(long discordGuildId, ForeverCraftSource source);
    }

    /// <summary>
    /// Who in a server's Forever guild knows which recipes. Blizzard has no "who can craft this", so the
    /// bot reads each member's professions from the armory, a few at a time, and remembers the answers
    /// for some hours. Nothing is stored: a restart forgets it and the next question reads it again.
    /// </summary>
    public class ForeverCrafters : IForeverCrafters
    {
        // Professions change slowly and the armory only updates when a character logs out.
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
        // One read asks the armory once per character; this keeps the largest guild to a minute or so.
        public const int MaxCharacters = 600;
        // A character reaches the armory at this level.
        public const int ArmoryMinLevel = 10;
        private const int Parallel = 6;
        private const int MaxServers = 200;

        private sealed record Entry(ForeverRegion Region, string? GuildSlug, ForeverCraftIndex Index, IReadOnlyDictionary<string, ForeverCrafter?> BySlug);

        private readonly IForeverArmory _armory;
        private readonly ILogger<ForeverCrafters> _logger;
        private readonly TimeProvider _clock;
        private readonly ConcurrentDictionary<long, Entry> _entries = new();
        private readonly Dictionary<long, ForeverCraftBuild> _builds = new();
        private readonly object _gate = new();

        public ForeverCrafters(IForeverArmory armory, ILogger<ForeverCrafters> logger)
            : this(armory, logger, TimeProvider.System) { }

        internal ForeverCrafters(IForeverArmory armory, ILogger<ForeverCrafters> logger, TimeProvider clock)
        {
            _armory = armory;
            _logger = logger;
            _clock = clock;
        }

        public ForeverCraftIndex? Current(long discordGuildId, ForeverCraftSource source)
            => Usable(discordGuildId, source) is { } entry && source.Shared.All(c => entry.BySlug.ContainsKey(c.Slug)) ? entry.Index : null;

        /// <summary>The server's last read while it is for the same guild and still recent.</summary>
        private Entry? Usable(long discordGuildId, ForeverCraftSource source)
            => _entries.TryGetValue(discordGuildId, out var entry)
               && entry.Region == source.Region
               && string.Equals(entry.GuildSlug, source.GuildSlug, StringComparison.Ordinal)
               && _clock.GetUtcNow() - entry.Index.BuiltAt < Lifetime
                ? entry
                : null;

        public ForeverCraftBuild Build(long discordGuildId, ForeverCraftSource source)
        {
            lock (_gate)
            {
                if (_builds.TryGetValue(discordGuildId, out var running)) return running;

                var build = new ForeverCraftBuild();
                _builds[discordGuildId] = build;
                // Not tied to the caller: whoever asks next joins this read instead of starting another.
                build.Task = Task.Run(async () =>
                {
                    try
                    {
                        return await ReadAsync(discordGuildId, source, build);
                    }
                    finally
                    {
                        lock (_gate) _builds.Remove(discordGuildId);
                    }
                });
                return build;
            }
        }

        private async Task<ForeverCraftIndex> ReadAsync(long discordGuildId, ForeverCraftSource source, ForeverCraftBuild build)
        {
            var rosterMissed = false;
            var candidates = new List<ForeverCraftCandidate>();
            if (source.GuildSlug != null)
            {
                var roster = await _armory.GetRosterAsync(source.Region, source.GuildSlug);
                rosterMissed = !roster.IsFound;
                if (roster.IsFound)
                {
                    candidates.AddRange(roster.Value!.Members
                        .Where(m => m.Level >= ArmoryMinLevel && !string.IsNullOrWhiteSpace(m.Slug))
                        .OrderByDescending(m => m.Level)
                        .ThenBy(m => m.Rank ?? int.MaxValue)
                        .Select(m => new ForeverCraftCandidate(m.FirstName, m.Surname, m.Slug, m.Level, m.Class)));
                }
            }

            // Shared characters come first so the cap never drops someone who asked to be listed.
            var wanted = source.Shared.Concat(candidates)
                .Where(c => !string.IsNullOrWhiteSpace(c.Slug))
                .GroupBy(c => c.Slug, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(c => c.Level).First())
                .ToList();
            var skipped = Math.Max(0, wanted.Count - MaxCharacters);
            wanted = wanted.Take(MaxCharacters).ToList();
            build.Start(wanted.Count);

            // What an earlier read of the same guild learned is still good while that read is recent.
            var earlier = Usable(discordGuildId, source)?.BySlug;
            var bySlug = new ConcurrentDictionary<string, ForeverCrafter?>(StringComparer.Ordinal);
            using var gate = new SemaphoreSlim(Parallel);
            await Task.WhenAll(wanted.Select(async candidate =>
            {
                await gate.WaitAsync();
                try
                {
                    bySlug[candidate.Slug] = earlier != null && earlier.TryGetValue(candidate.Slug, out var known) && known != null
                        ? known
                        : await AskAsync(source.Region, candidate);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Reading professions for a Forever character failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
                    bySlug[candidate.Slug] = null;
                }
                finally
                {
                    gate.Release();
                    build.Step();
                }
            }));

            var crafters = wanted
                .Select(c => bySlug.TryGetValue(c.Slug, out var crafter) ? crafter : null)
                .Where(c => c != null)
                .Select(c => c!)
                .ToList();
            var index = new ForeverCraftIndex(crafters, _clock.GetUtcNow(), wanted.Count, wanted.Count - crafters.Count, skipped, rosterMissed);

            _entries[discordGuildId] = new Entry(source.Region, source.GuildSlug, index, bySlug);
            Trim();
            _logger.LogInformation("Forever crafting index for {GuildId}: {Crafters} of {Asked} characters answered, {Skipped} skipped",
                discordGuildId, crafters.Count, wanted.Count, skipped);
            return index;
        }

        private async Task<ForeverCrafter?> AskAsync(ForeverRegion region, ForeverCraftCandidate candidate)
        {
            var answer = await _armory.GetProfessionsAsync(region, candidate.Slug);
            if (!answer.IsFound) return null;

            var professions = answer.Value!.Professions
                .Select(p => ForeverCrafterProfession.From(
                    p.Id, p.Name, p.Skill, p.MaxSkill, (p.Known ?? Array.Empty<ForeverKnownRecipe>()).Select(r => r.Id)))
                .ToList();
            return new ForeverCrafter(candidate.FirstName, candidate.Surname, candidate.Level, candidate.Class, professions);
        }

        /// <summary>Forgets the oldest reads once more servers are remembered than the bot should hold.</summary>
        private void Trim()
        {
            if (_entries.Count <= MaxServers) return;
            foreach (var old in _entries.OrderBy(e => e.Value.Index.BuiltAt).Take(_entries.Count - MaxServers).ToList())
                _entries.TryRemove(old.Key, out _);
        }
    }
}
