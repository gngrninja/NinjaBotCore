#nullable enable

using System;
using System.Collections.Concurrent;
using NinjaBotCore.Common;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// In-memory wizard state for the /pushgroup guided creation flow.
    /// Keyed by (UserId, ChannelId) so the same user can have one wizard per channel.
    /// Auto-expires after PushGroupConstants.WizardTtlMinutes minutes of inactivity.
    /// </summary>
    public class PushGroupWizardState
    {
        public class State
        {
            public ulong UserId { get; init; }
            public ulong ChannelId { get; init; }
            public ulong? InteractionMessageId { get; set; }

            public string? DungeonSlug { get; set; }
            public string? DungeonName { get; set; }
            public int? KeyLevel { get; set; }
            public string? Role { get; set; }
            public string? CharacterName { get; set; }
            public string? CharacterRealm { get; set; }
            public string? CharacterRegion { get; set; }
            public string? CharacterClass { get; set; }
            public decimal? IoRating { get; set; }
            public DateTime? ScheduledForUtc { get; set; }
            public string? Notes { get; set; }

            public DateTime LastTouchedUtc { get; set; }

            public int CurrentStep { get; set; } = 1;
        }

        private readonly ConcurrentDictionary<(ulong UserId, ulong ChannelId), State> _states = new();

        public State GetOrCreate(ulong userId, ulong channelId)
        {
            Sweep();
            return _states.AddOrUpdate(
                (userId, channelId),
                _ => new State
                {
                    UserId = userId,
                    ChannelId = channelId,
                    LastTouchedUtc = DateTime.UtcNow,
                },
                (_, existing) =>
                {
                    existing.LastTouchedUtc = DateTime.UtcNow;
                    return existing;
                });
        }

        public State? TryGet(ulong userId, ulong channelId)
        {
            Sweep();
            if (_states.TryGetValue((userId, channelId), out var s))
            {
                s.LastTouchedUtc = DateTime.UtcNow;
                return s;
            }
            return null;
        }

        public void Remove(ulong userId, ulong channelId) =>
            _states.TryRemove((userId, channelId), out _);

        private void Sweep()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-PushGroupConstants.WizardTtlMinutes);
            foreach (var kvp in _states)
            {
                if (kvp.Value.LastTouchedUtc < cutoff)
                {
                    _states.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
