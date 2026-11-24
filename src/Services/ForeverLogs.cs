#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NinjaBot.WarcraftLogs;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services
{
    public enum ForeverLogStatus
    {
        /// <summary>The guild is on Warcraft Logs; it may still have no logs.</summary>
        Found,
        /// <summary>Warcraft Logs has no guild of that name in the region.</summary>
        NotOnSite,
        /// <summary>Warcraft Logs listed no Forever servers for the region.</summary>
        NoServers,
        /// <summary>The region has more servers than are worth looking through one by one.</summary>
        TooManyServers,
        /// <summary>The name is on more than one server, and nothing says which is this guild.</summary>
        Ambiguous,
        /// <summary>The Forever site didn't answer, or not in a way that could be read.</summary>
        Unreachable
    }

    public sealed record ForeverLogReport(string Code, string Title, string? Zone, string? Owner, DateTimeOffset Start, DateTimeOffset End)
    {
        public string Url => ForeverLogs.ReportUrl(Code);
    }

    /// <param name="Server">The Warcraft Logs server the guild was found on, or the only one that was looked at.</param>
    /// <param name="Servers">The servers in question: the ones listed when there are too many, the ones with the name when it is ambiguous.</param>
    /// <param name="ServerTotal">How many servers Warcraft Logs says the region has.</param>
    /// <param name="Detail">Why the site couldn't be read, in words that are safe to show.</param>
    public sealed record ForeverLogAnswer(
        ForeverLogStatus Status,
        IReadOnlyList<ForeverLogReport> Reports,
        string? Server = null,
        IReadOnlyList<string>? Servers = null,
        int ServerTotal = 0,
        string? Detail = null);

    /// <summary>Warcraft Logs' Forever site, as far as <see cref="ForeverLogs"/> needs it: one query, its data back.</summary>
    public interface IForeverLogSite
    {
        /// <summary>Throws when the site doesn't answer. Fields it couldn't fill are null in the data.</summary>
        Task<JToken?> AskAsync(string query, object? variables, CancellationToken cancellationToken = default);
    }

    public sealed class WclForeverLogSite : IForeverLogSite
    {
        private readonly WarcraftLogsV2Client _client;
        public WclForeverLogSite(WarcraftLogsV2Client client) => _client = client;

        public async Task<JToken?> AskAsync(string query, object? variables, CancellationToken cancellationToken = default)
            => await _client.QueryAsync(query, variables!, WowGameVersion.Forever, cancellationToken);
    }

    /// <summary>
    /// A Forever guild's logs on Warcraft Logs. Forever has no realms, but Warcraft Logs finds a guild by
    /// name, server and region, so its Forever site has servers of its own. Which ones isn't written
    /// down anywhere this code can rely on, so the site is asked: the guild is looked for on every
    /// server of its region (usually one) and used only when exactly one has it. The log watcher in
    /// NinjaBotHelpers places guilds the same way.
    /// </summary>
    public class ForeverLogs
    {
        /// <summary>More servers than this in a region and looking on each isn't worth the requests.</summary>
        public const int MaxServers = 8;
        public const int DefaultLimit = 5;

        private const string ServersKey = "forever:wcl:servers";
        private static readonly TimeSpan ServersLifetime = TimeSpan.FromHours(6);
        private static readonly TimeSpan EmptyServersLifetime = TimeSpan.FromMinutes(10);

        private readonly IForeverLogSite _site;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ForeverLogs> _logger;

        public ForeverLogs(IForeverLogSite site, IMemoryCache cache, ILogger<ForeverLogs> logger)
        {
            _site = site;
            _cache = cache;
            _logger = logger;
        }

        public static string ReportUrl(string code) => $"https://forever.warcraftlogs.com/reports/{Uri.EscapeDataString(code)}";

        /// <summary>The guild's newest logs, newest first. Never throws for a site that is down or answers oddly.</summary>
        public async Task<ForeverLogAnswer> LatestAsync(
            string guildName, ForeverRegion region, int limit = DefaultLimit, CancellationToken cancellationToken = default)
        {
            var none = Array.Empty<ForeverLogReport>();
            var regionSlug = region.ToString().ToLowerInvariant();
            try
            {
                var servers = await ServersAsync(cancellationToken);
                if (!servers.TryGetValue(regionSlug, out var listed) || listed.Slugs.Count == 0)
                    return new ForeverLogAnswer(ForeverLogStatus.NoServers, none);
                if (listed.Total > listed.Slugs.Count || listed.Slugs.Count > MaxServers)
                    return new ForeverLogAnswer(ForeverLogStatus.TooManyServers, none, Servers: listed.Slugs, ServerTotal: listed.Total);

                var slugs = listed.Slugs;
                var data = await _site.AskAsync(GuildQuery(slugs.Count), GuildVariables(guildName.Trim(), regionSlug, limit, slugs), cancellationToken);

                var on = new List<(string Slug, List<ForeverLogReport> Reports)>();
                for (var i = 0; i < slugs.Count; i++)
                {
                    var reports = Reports(At(At(At(data, $"r{i}"), "reports"), "data"));
                    var known = At(At(At(data, $"g{i}"), "guild"), "id")?.Type == JTokenType.Integer;
                    // Either is proof the guild is there; a log can't belong to a guild that isn't.
                    if (known || reports.Count > 0) on.Add((slugs[i], reports));
                }

                if (on.Count == 0)
                    return new ForeverLogAnswer(ForeverLogStatus.NotOnSite, none, Server: slugs.Count == 1 ? slugs[0] : null, ServerTotal: slugs.Count);
                if (on.Count > 1)
                    return new ForeverLogAnswer(ForeverLogStatus.Ambiguous, none, Servers: on.Select(o => o.Slug).ToList(), ServerTotal: slugs.Count);
                return new ForeverLogAnswer(ForeverLogStatus.Found, on[0].Reports, Server: on[0].Slug, ServerTotal: slugs.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Forever logs lookup failed ({ErrorType})", ex.GetType().Name);
                return new ForeverLogAnswer(ForeverLogStatus.Unreachable, none, Detail: Describe(ex));
            }
        }

        private sealed record ServerList(IReadOnlyList<string> Slugs, int Total);

        private async Task<Dictionary<string, ServerList>> ServersAsync(CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(ServersKey, out Dictionary<string, ServerList>? cached) && cached != null) return cached;

            // One more than the cap, so a region over it is seen as over it.
            var data = await _site.AskAsync(
                $"query {{ worldData {{ regions {{ slug servers(limit: {MaxServers + 1}) {{ total data {{ slug }} }} }} }} }}", null, cancellationToken);
            if (At(At(data, "worldData"), "regions") is not JArray regions)
                throw new InvalidOperationException("Warcraft Logs listed no regions.");

            var byRegion = new Dictionary<string, ServerList>(StringComparer.OrdinalIgnoreCase);
            foreach (var region in regions.OfType<JObject>())
            {
                var slug = Text(region["slug"]);
                if (slug == null) continue;
                var servers = At(region, "servers");
                var slugs = (At(servers, "data") as JArray ?? new JArray())
                    .Select(s => Text(At(s, "slug")))
                    .Where(s => s != null)
                    .Select(s => s!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var total = At(servers, "total") is { Type: JTokenType.Integer } count ? count.Value<int>() : slugs.Count;
                byRegion[slug.ToLowerInvariant()] = new ServerList(slugs, Math.Max(total, slugs.Count));
            }

            // What everything else here follows from, so it is worth a line in the log.
            _logger.LogInformation("Warcraft Logs Forever servers: {Servers}", byRegion.Count == 0
                ? "none"
                : string.Join("; ", byRegion.OrderBy(r => r.Key).Select(r => $"{r.Key}=[{string.Join(", ", r.Value.Slugs)}] of {r.Value.Total}")));

            _cache.Set(ServersKey, byRegion, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = byRegion.Values.Any(s => s.Slugs.Count > 0) ? ServersLifetime : EmptyServersLifetime,
                Size = 1
            });
            return byRegion;
        }

        /// <summary>One request that asks every server of the region for the guild and for its newest logs.</summary>
        internal static string GuildQuery(int servers)
        {
            var sb = new StringBuilder("query($name: String!, $region: String!, $limit: Int!");
            for (var i = 0; i < servers; i++) sb.Append($", $s{i}: String!");
            sb.Append(") {");
            for (var i = 0; i < servers; i++)
            {
                sb.Append($" g{i}: guildData {{ guild(name: $name, serverSlug: $s{i}, serverRegion: $region) {{ id }} }}");
                sb.Append($" r{i}: reportData {{ reports(guildName: $name, guildServerSlug: $s{i}, guildServerRegion: $region, limit: $limit)");
                sb.Append(" { data { code title startTime endTime zone { id name } owner { name } } } }");
            }
            return sb.Append(" }").ToString();
        }

        private static Dictionary<string, object> GuildVariables(string name, string region, int limit, IReadOnlyList<string> slugs)
        {
            var variables = new Dictionary<string, object> { ["name"] = name, ["region"] = region, ["limit"] = limit };
            for (var i = 0; i < slugs.Count; i++) variables[$"s{i}"] = slugs[i];
            return variables;
        }

        private static List<ForeverLogReport> Reports(JToken? data)
        {
            var reports = new List<ForeverLogReport>();
            foreach (var report in (data as JArray ?? new JArray()).OfType<JObject>())
            {
                var code = Text(report["code"]);
                if (code == null) continue;
                reports.Add(new ForeverLogReport(
                    code,
                    Text(report["title"]) ?? "Untitled",
                    Text(At(At(report, "zone"), "name")),
                    Text(At(At(report, "owner"), "name")),
                    Time(report["startTime"]),
                    Time(report["endTime"])));
            }
            return reports.OrderByDescending(r => r.Start).ToList();
        }

        /// <summary>A child of an object, or null for anything else: an answer may hold null where an object was asked for.</summary>
        private static JToken? At(JToken? token, string key) => token is JObject o ? o[key] : null;

        private static string? Text(JToken? token)
            => token is { Type: JTokenType.String } && !string.IsNullOrWhiteSpace(token.Value<string>()) ? token.Value<string>()!.Trim() : null;

        private static DateTimeOffset Time(JToken? token)
        {
            if (token?.Type is not (JTokenType.Integer or JTokenType.Float)) return DateTimeOffset.UnixEpoch;
            var ms = token.Value<double>();
            // Outside what a timestamp can be: shown as unknown rather than thrown on.
            return ms is >= 0 and < 253402300800000d ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : DateTimeOffset.UnixEpoch;
        }

        /// <summary>What went wrong, without anything the site itself sent back.</summary>
        private static string Describe(Exception ex) => ex switch
        {
            WclAuthenticationUnavailableException => "NinjaBot's Warcraft Logs sign-in isn't working right now",
            HttpRequestException { StatusCode: { } status } => $"it answered HTTP {(int)status}",
            HttpRequestException => "it couldn't be reached",
            OperationCanceledException => "it didn't answer in time",
            InvalidOperationException invalid when invalid.Message.StartsWith("Warcraft Logs quota", StringComparison.Ordinal)
                => "NinjaBot is out of Warcraft Logs requests for the moment",
            InvalidOperationException => "it sent an answer NinjaBot couldn't read",
            _ => "something went wrong on the way"
        };
    }
}
