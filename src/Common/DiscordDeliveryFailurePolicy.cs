using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Classifies expected Discord delivery failures and rate-limits the
    /// corresponding configuration warnings by guild, channel, and reason.
    /// </summary>
    internal sealed class DiscordDeliveryFailurePolicy
    {
        private const int UnknownChannelCode = 10003;
        private const int MissingAccessCode = 50001;
        private const int MissingPermissionsCode = 50013;

        private readonly TimeSpan _logInterval;
        private readonly ConcurrentDictionary<FailureKey, DateTimeOffset> _nextLogAt = new();

        internal int RetainedKeyCount => _nextLogAt.Count;

        public DiscordDeliveryFailurePolicy(TimeSpan logInterval)
        {
            if (logInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(logInterval));
            }

            _logInterval = logInterval;
        }

        public static bool IsExpectedConfigurationFailure(int? discordCode)
        {
            return discordCode is UnknownChannelCode or MissingAccessCode or MissingPermissionsCode;
        }

        public bool ShouldLog(
            ulong guildId,
            ulong channelId,
            string reason,
            DateTimeOffset observedAt)
        {
            PruneExpired(observedAt);
            var key = new FailureKey(guildId, channelId, reason ?? string.Empty);

            while (true)
            {
                if (!_nextLogAt.TryGetValue(key, out var nextLogAt))
                {
                    if (_nextLogAt.TryAdd(key, observedAt.Add(_logInterval)))
                    {
                        return true;
                    }

                    continue;
                }

                if (observedAt < nextLogAt)
                {
                    return false;
                }

                if (_nextLogAt.TryUpdate(key, observedAt.Add(_logInterval), nextLogAt))
                {
                    return true;
                }
            }
        }

        private void PruneExpired(DateTimeOffset observedAt)
        {
            var retentionCutoff = observedAt.Subtract(_logInterval);
            var entries = (ICollection<KeyValuePair<FailureKey, DateTimeOffset>>)_nextLogAt;

            foreach (var entry in _nextLogAt)
            {
                if (entry.Value <= retentionCutoff)
                {
                    // ICollection.Remove is key-and-value conditional for
                    // ConcurrentDictionary, so a concurrent refresh is preserved.
                    entries.Remove(entry);
                }
            }
        }

        private readonly record struct FailureKey(ulong GuildId, ulong ChannelId, string Reason);
    }
}
