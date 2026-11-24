using Microsoft.Extensions.Logging;
using NinjaBotHelpers.Database;

namespace NinjaBotHelpers.WarcraftLogs;

/// <summary>
/// Which Warcraft Logs server a Forever guild is filed under. Forever has no realms, but Warcraft
/// Logs finds a guild by name, server and region, so its Forever site has servers of its own. Their
/// names aren't written down anywhere this code can rely on, so the site is asked: a region with one
/// server needs no more than that, and in a region with several the guild is looked for on each and
/// used only when exactly one has it. A guild that can't be placed is left alone; nothing is guessed.
/// </summary>
internal sealed class ForeverLogAddresses
{
    /// <summary>More servers than this in a region and looking on each isn't worth the requests.</summary>
    public const int MaxServers = 8;
    /// <summary>Guilds looked for in one request, each on every server of its region.</summary>
    private const int LookupBatch = 20;

    private static readonly TimeSpan ServersLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan ServersRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PlacedLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AmbiguousRetry = TimeSpan.FromHours(6);

    private readonly WarcraftLogsClient _client;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    private Dictionary<string, WclServerList>? _servers;
    private DateTimeOffset _serversUntil = DateTimeOffset.MinValue;
    // Region and guild name → the server it is on (null while unknown) and when to look again.
    private readonly Dictionary<string, (string? Slug, DateTimeOffset Until)> _placed = new(StringComparer.OrdinalIgnoreCase);

    public ForeverLogAddresses(WarcraftLogsClient client, ILogger logger, TimeProvider? clock = null)
    {
        _client = client;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The region slug Warcraft Logs uses for one of NinjaBotCore's ForeverRegion values, or null for a value this code doesn't know.</summary>
    public static string? RegionSlug(int region) => region switch
    {
        0 => "us",
        1 => "eu",
        2 => "kr",
        3 => "tw",
        _ => null
    };

    /// <summary>
    /// The server slug for each guild that can be addressed right now, by Discord server id.
    /// Guilds missing from the answer are skipped this round. Never throws for a site that is
    /// down or answers oddly; only the caller's own cancellation gets out.
    /// </summary>
    public async Task<Dictionary<long, string>> ResolveAsync(IReadOnlyList<ForeverGuild> guilds, CancellationToken cancellationToken)
    {
        var addressed = new Dictionary<long, string>();
        if (guilds.Count == 0) return addressed;

        var servers = await ServersAsync(cancellationToken);
        if (servers == null) return addressed;

        var now = _clock.GetUtcNow();
        var toFind = new List<(ForeverGuild Guild, string Region, IReadOnlyList<string> Slugs, string Key)>();
        foreach (var guild in guilds)
        {
            var region = RegionSlug(guild.Region);
            if (region == null || string.IsNullOrWhiteSpace(guild.Name)) continue;
            if (!servers.TryGetValue(region, out var list) || list.Slugs.Count == 0) continue;
            if (list.Total > list.Slugs.Count || list.Slugs.Count > MaxServers) continue;

            if (list.Slugs.Count == 1)
            {
                addressed[guild.DiscordGuildId] = list.Slugs[0];
                continue;
            }

            var key = $"{region}|{guild.Name.Trim()}";
            if (_placed.TryGetValue(key, out var placed) && now < placed.Until)
            {
                if (placed.Slug != null) addressed[guild.DiscordGuildId] = placed.Slug;
                continue;
            }
            toFind.Add((guild, region, list.Slugs, key));
        }

        // Two Discord servers can be linked to the same guild; it is looked for once.
        var distinct = toFind.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        for (var i = 0; i < distinct.Count; i += LookupBatch)
        {
            var batch = distinct.Skip(i).Take(LookupBatch).ToList();
            Dictionary<string, List<string>> found;
            try
            {
                found = await _client.FindGuildServersAsync(
                    batch.Select(f => (f.Guild.Name.Trim(), f.Region, f.Slugs, f.Key)).ToList(),
                    WowGameVersion.Forever,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("[LogMonitoring] Looking for Forever guilds on Warcraft Logs failed ({ErrorType})", ex.GetType().Name);
                break;
            }

            foreach (var (_, _, _, key) in batch)
            {
                var on = found.TryGetValue(key, out var slugs) ? slugs : new List<string>();
                if (on.Count == 1)
                {
                    _placed[key] = (on[0], now + PlacedLifetime);
                }
                else if (on.Count == 0)
                {
                    // Not uploaded to yet, most likely. Worth another look soon.
                    _placed[key] = (null, now + NotFoundRetry);
                }
                else
                {
                    _placed[key] = (null, now + AmbiguousRetry);
                    _logger.LogWarning(
                        "[LogMonitoring] A Forever guild name is on {Count} Warcraft Logs servers ({Servers}); not watching it",
                        on.Count, string.Join(", ", on));
                }
            }
        }

        foreach (var (guild, _, _, key) in toFind)
        {
            if (_placed.TryGetValue(key, out var placed) && placed.Slug != null)
                addressed[guild.DiscordGuildId] = placed.Slug;
        }
        return addressed;
    }

    private async Task<Dictionary<string, WclServerList>?> ServersAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (now < _serversUntil) return _servers;

        try
        {
            // One more than the cap, so a region over it is seen as over it.
            var servers = await _client.GetServersByRegionAsync(WowGameVersion.Forever, MaxServers + 1, cancellationToken);
            _servers = servers;
            _serversUntil = now + (servers.Values.Any(s => s.Slugs.Count > 0) ? ServersLifetime : ServersRetry);
            // The one line that says how Warcraft Logs lays Forever out; everything above follows from it.
            _logger.LogInformation("[LogMonitoring] Warcraft Logs Forever servers: {Servers}",
                servers.Count == 0
                    ? "none"
                    : string.Join("; ", servers.OrderBy(s => s.Key).Select(s => $"{s.Key}=[{string.Join(", ", s.Value.Slugs)}] of {s.Value.Total}")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Keep what was known, if anything, and don't ask again every cycle.
            _serversUntil = now + ServersRetry;
            _logger.LogWarning("[LogMonitoring] Warcraft Logs' Forever site gave no server list ({ErrorType}); trying again in {Minutes} minutes",
                ex.GetType().Name, ServersRetry.TotalMinutes);
        }
        return _servers;
    }
}
