using System.Collections.Concurrent;

namespace NinjaBotHelpers.Discord;

internal sealed class DiscordDeliveryWarningPolicy
{
    private const int UnknownChannelCode = 10003;
    private const int MissingAccessCode = 50001;
    private const int MissingPermissionsCode = 50013;

    private readonly TimeSpan _logInterval;
    private readonly ConcurrentDictionary<FailureKey, DateTimeOffset> _nextLogAt = new();

    public DiscordDeliveryWarningPolicy(TimeSpan logInterval)
    {
        if (logInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(logInterval));
        }

        _logInterval = logInterval;
    }

    public static bool IsExpectedConfigurationFailure(int? discordCode) =>
        discordCode is UnknownChannelCode or MissingAccessCode or MissingPermissionsCode;

    public bool ShouldLog(ulong channelId, int discordCode, DateTimeOffset observedAt)
    {
        PruneExpiredEntries(observedAt);
        var key = new FailureKey(channelId, discordCode);

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

    private void PruneExpiredEntries(DateTimeOffset observedAt)
    {
        var cutoff = observedAt.Subtract(_logInterval);
        foreach (var entry in _nextLogAt)
        {
            if (entry.Value <= cutoff)
            {
                ((ICollection<KeyValuePair<FailureKey, DateTimeOffset>>)_nextLogAt).Remove(entry);
            }
        }
    }

    private readonly record struct FailureKey(ulong ChannelId, int DiscordCode);
}
