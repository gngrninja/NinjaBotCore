#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>A camp card's sign-ups: what it is called, who started it and who is in, in joining order.</summary>
    public sealed record ForeverCampState(string Title, ulong HostId, IReadOnlyList<ulong> Members)
    {
        public const int MaxMembers = 25;
        public const int MaxTitleLength = 80;

        public ForeverCampState With(ulong userId)
            => Members.Contains(userId) ? this : this with { Members = Members.Append(userId).ToList() };

        public ForeverCampState Without(ulong userId) => this with { Members = Members.Where(m => m != userId).ToList() };
    }

    /// <summary>
    /// Camp cards: who signed up, and what each of them can bring. The sign-ups live on the card itself
    /// (the title, the host and one mention per camper), so a card keeps working after a restart and
    /// nothing is stored. A copy is remembered for a while so a click doesn't depend on reading it back.
    /// </summary>
    public class ForeverCamps
    {
        public const string DefaultTitle = "Camp";
        /// <summary>Starts the text block that lists the campers, one mention each. Reading a card back looks for it.</summary>
        public const string WhoHeading = "### 🏕️ Who's in";
        public const string HostLead = "Started by ";

        private static readonly TimeSpan Remembered = TimeSpan.FromHours(12);
        private static readonly TimeSpan ProfessionsLifetime = TimeSpan.FromMinutes(10);
        private static readonly Regex Mention = new(@"<@!?(\d{15,21})>", RegexOptions.Compiled);

        private readonly ForeverService _forever;
        private readonly IForeverArmory _armory;
        private readonly IMemoryCache _cache;
        private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _locks = new();

        public ForeverCamps(ForeverService forever, IForeverArmory armory, IMemoryCache cache)
        {
            _forever = forever;
            _armory = armory;
            _cache = cache;
        }

        /// <summary>A camp's name as it may be shown: one line, no markdown or mentions, not too long.</summary>
        public static string CleanTitle(string? note)
        {
            var title = Regex.Replace(note ?? string.Empty, @"[\r\n\t]+", " ");
            // Nothing that could ping, link or break out of the heading it sits in.
            title = Regex.Replace(title, @"[<>@#`*_~|\[\]()\\]", string.Empty);
            title = Regex.Replace(title, @"\s{2,}", " ").Trim();
            if (title.Length > ForeverCampState.MaxTitleLength) title = title[..ForeverCampState.MaxTitleLength].TrimEnd();
            return title.Length == 0 ? DefaultTitle : title;
        }

        /// <summary>One click at a time per card, so two people joining at once both end up on it.</summary>
        public async Task<IDisposable> LockAsync(ulong messageId, CancellationToken cancellationToken = default)
        {
            var gate = _locks.GetOrAdd(messageId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new Releaser(gate);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _gate;
            public Releaser(SemaphoreSlim gate) => _gate = gate;
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }

        public void Remember(ulong messageId, ForeverCampState state)
            => _cache.Set($"forever:camp:{messageId}", state, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Remembered, Size = 1 });

        /// <summary>
        /// The camp on a card: the remembered copy, or else what the card's own text says.
        /// Null when neither is there, which means the card can't be updated any more.
        /// </summary>
        public ForeverCampState? Recall(ulong messageId, IEnumerable<string> cardTexts)
            => _cache.TryGetValue($"forever:camp:{messageId}", out ForeverCampState? state) && state != null ? state : Read(cardTexts);

        /// <summary>Reads a camp back from the text blocks of its card.</summary>
        public static ForeverCampState? Read(IEnumerable<string> cardTexts)
        {
            var texts = cardTexts.Where(t => !string.IsNullOrEmpty(t)).Select(t => t.Replace("\r\n", "\n")).ToList();
            var header = texts.FirstOrDefault(t => t.Contains("\n" + HostLead, StringComparison.Ordinal));
            if (header == null) return null;

            var lines = header.Split('\n');
            var hostLine = lines.FirstOrDefault(l => l.StartsWith(HostLead, StringComparison.Ordinal));
            var host = hostLine == null ? null : Mention.Match(hostLine) is { Success: true } m ? m.Groups[1].Value : null;
            if (!ulong.TryParse(host, out var hostId)) return null;

            // "# 🔥 Fishing night": everything after the emoji is the title, which was cleaned when the card was made.
            var heading = lines[0].TrimStart('#', ' ');
            var space = heading.IndexOf(' ');
            var title = CleanTitle(space >= 0 ? heading[(space + 1)..] : string.Empty);

            var who = texts.FirstOrDefault(t => t.StartsWith(WhoHeading, StringComparison.Ordinal));
            var members = who == null
                ? new List<ulong>()
                : Mention.Matches(who).Select(x => ulong.TryParse(x.Groups[1].Value, out var id) ? id : 0).Where(id => id != 0).Distinct().ToList();
            return new ForeverCampState(title, hostId, members.Take(ForeverCampState.MaxMembers).ToList());
        }

        /// <summary>
        /// Who a member camps as: their main in this server, or their first character when they have no
        /// main here. Null when they have saved no character. Professions are null when the armory has none for it.
        /// </summary>
        public async Task<ForeverCamper?> CamperAsync(ulong userId, long discordGuildId, CancellationToken cancellationToken = default)
        {
            var characters = await _forever.GetCharactersAsync((long)userId);
            if (characters.Count == 0) return null;
            var mainId = await _forever.GetMainIdAsync((long)userId, discordGuildId);
            var character = characters.FirstOrDefault(c => c.Id == mainId) ?? characters[0];

            var profile = await _armory.GetCharacterAsync(character.Region, character.FirstName, character.LastName, cancellationToken);
            var slug = ForeverNames.CharacterSlug(character.FirstName, character.LastName);
            var key = $"forever:campprof:{character.Region}:{slug}";
            if (!_cache.TryGetValue(key, out ForeverArmoryProfessions? professions) || professions == null)
            {
                var answer = await _armory.GetProfessionsAsync(character.Region, slug, cancellationToken);
                professions = answer.Value;
                if (professions != null)
                    _cache.Set(key, professions, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ProfessionsLifetime, Size = 1 });
            }

            return new ForeverCamper(
                userId,
                (profile.Value?.FullName ?? character.FullName).Trim(),
                profile.Value?.Level ?? 0,
                profile.Value?.Class ?? character.Class,
                professions?.Professions);
        }

        /// <summary>Every camper on the card who still has a character, in joining order.</summary>
        public async Task<List<ForeverCamper>> CampersAsync(ForeverCampState state, long discordGuildId, CancellationToken cancellationToken = default)
        {
            var campers = await Task.WhenAll(state.Members.Select(id => CamperAsync(id, discordGuildId, cancellationToken)));
            return campers.Where(c => c != null).Select(c => c!).ToList();
        }
    }
}
