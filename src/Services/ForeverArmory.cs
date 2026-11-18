#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    public interface IForeverArmory
    {
        Task<ForeverLookup<ForeverArmoryCharacter>> GetCharacterAsync(
            ForeverRegion region, string firstName, string surname, CancellationToken cancellationToken = default);

        Task<ForeverLookup<ForeverArmoryCard>> GetCardAsync(
            ForeverRegion region, string firstName, string surname, CancellationToken cancellationToken = default);

        Task<ForeverLookup<ForeverArmoryGuild>> GetGuildAsync(
            ForeverRegion region, string guildName, CancellationToken cancellationToken = default);

        Task<ForeverLookup<ForeverArmoryRoster>> GetRosterAsync(
            ForeverRegion region, string guildSlug, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Reads WoW Forever characters and guilds from Blizzard's armory. Blizzard hasn't documented
    /// these routes: the <c>profile-forever-{region}</c> namespace, realmless slugs and field names
    /// are what players found after launch, so every lookup can fail and callers must cope.
    /// A character only exists here from level 10, after it has logged out once.
    /// </summary>
    public class ForeverArmory : IForeverArmory
    {
        private static readonly TimeSpan FoundLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NotFoundLifetime = TimeSpan.FromMinutes(1);
        // Blizzard caches rosters for about three hours, so asking more often gains nothing.
        private static readonly TimeSpan RosterLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ProfileTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(10);

        // Achievement statistic ids, the same as Retail's.
        private const long DeathsStat = 60;
        private const long QuestsStat = 98;
        private const long KillsStat = 1197;
        private const long DungeonsStat = 932;

        private readonly IWowApi _wowApi;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ForeverArmory> _logger;

        public ForeverArmory(IWowApi wowApi, IMemoryCache cache, ILogger<ForeverArmory> logger)
        {
            _wowApi = wowApi;
            _cache = cache;
            _logger = logger;
        }

        public Task<ForeverLookup<ForeverArmoryCharacter>> GetCharacterAsync(
            ForeverRegion region, string firstName, string surname, CancellationToken cancellationToken = default)
        {
            var slug = ForeverNames.CharacterSlug(firstName, surname);
            return CachedAsync($"forever:char:{region}:{slug}", FoundLifetime, async () =>
            {
                var json = await GetAsync(region, $"/profile/wow/character/{Uri.EscapeDataString(slug)}", ProfileTimeout, cancellationToken);
                return Map(json, o => ParseCharacter(o, region, slug));
            });
        }

        public Task<ForeverLookup<ForeverArmoryCard>> GetCardAsync(
            ForeverRegion region, string firstName, string surname, CancellationToken cancellationToken = default)
        {
            var slug = ForeverNames.CharacterSlug(firstName, surname);
            return CachedAsync($"forever:card:{region}:{slug}", FoundLifetime, async () =>
            {
                var profile = await GetCharacterAsync(region, firstName, surname, cancellationToken);
                if (!profile.IsFound) return new ForeverLookup<ForeverArmoryCard>(profile.Status, null);

                // The profile is the card; each detail is a bonus that may lag or be missing.
                var path = $"/profile/wow/character/{Uri.EscapeDataString(profile.Value!.Slug)}";
                var equipment = GetAsync(region, path + "/equipment", DetailTimeout, cancellationToken);
                var media = GetAsync(region, path + "/character-media", DetailTimeout, cancellationToken);
                var professions = GetAsync(region, path + "/professions", DetailTimeout, cancellationToken);
                var talents = GetAsync(region, path + "/specializations", DetailTimeout, cancellationToken);
                var statistics = GetAsync(region, path + "/achievements/statistics", DetailTimeout, cancellationToken);
                await Task.WhenAll(equipment, media, professions, talents, statistics);

                return ForeverLookup<ForeverArmoryCard>.Found(new ForeverArmoryCard(
                    profile.Value,
                    Detail(media.Result, ParseAvatar),
                    Detail(equipment.Result, ParseGear) ?? Array.Empty<ForeverArmoryItem>(),
                    Detail(professions.Result, ParseProfessions) ?? Array.Empty<ForeverArmoryProfession>(),
                    Detail(talents.Result, ParseTalents) ?? Array.Empty<ForeverArmoryTalentTree>(),
                    Detail(statistics.Result, ParseStats)));
            });
        }

        public async Task<ForeverLookup<ForeverArmoryGuild>> GetGuildAsync(
            ForeverRegion region, string guildName, CancellationToken cancellationToken = default)
        {
            var slug = ForeverNames.Slug(guildName);
            if (slug.Length == 0) return ForeverLookup<ForeverArmoryGuild>.NotFound();

            var json = await GetAsync(region, $"/data/wow/guild/{Uri.EscapeDataString(slug)}", ProfileTimeout, cancellationToken);
            return Map(json, o => ParseGuild(o, region, slug));
        }

        public Task<ForeverLookup<ForeverArmoryRoster>> GetRosterAsync(
            ForeverRegion region, string guildSlug, CancellationToken cancellationToken = default)
            => CachedAsync($"forever:roster:{region}:{guildSlug}", RosterLifetime, async () =>
            {
                var json = await GetAsync(region, $"/data/wow/guild/{Uri.EscapeDataString(guildSlug)}/roster", ProfileTimeout, cancellationToken);
                return Map(json, ParseRoster);
            });

        // ===== Requests =====

        private static string Locale(ForeverRegion region) => region == ForeverRegion.EU ? "en_GB" : "en_US";

        private async Task<ForeverLookup<JObject>> GetAsync(ForeverRegion region, string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var regionName = region.ToString().ToLowerInvariant();
            var url = $"{path}?namespace=profile-forever-{regionName}&locale={Locale(region)}";
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                var response = await _wowApi.TryGetAPIRequestAsync(url, regionName, timeoutSource.Token);
                if (response.Status == HttpStatusCode.NotFound) return ForeverLookup<JObject>.NotFound();
                if (response.Status != HttpStatusCode.OK)
                {
                    _logger.LogWarning("Forever armory returned {Status} for {Path} in {Region}", (int)response.Status, path, region);
                    return ForeverLookup<JObject>.Unavailable();
                }

                return ForeverLookup<JObject>.Found(JObject.Parse(response.Body));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Message only: a timeout or outage here is routine and shouldn't read as a crash.
                _logger.LogWarning("Forever armory request for {Path} in {Region} failed: {Type}: {Message}",
                    path, region, ex.GetType().Name, ex.Message);
                return ForeverLookup<JObject>.Unavailable();
            }
        }

        private ForeverLookup<T> Map<T>(ForeverLookup<JObject> json, Func<JObject, T?> parse) where T : class
        {
            if (!json.IsFound) return new ForeverLookup<T>(json.Status, null);
            var value = Detail(json, parse);
            return value == null ? ForeverLookup<T>.Unavailable() : ForeverLookup<T>.Found(value);
        }

        private T? Detail<T>(ForeverLookup<JObject> json, Func<JObject, T?> parse) where T : class
        {
            if (!json.IsFound) return null;
            try
            {
                return parse(json.Value!);
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
                _logger.LogWarning("Forever armory sent {Kind} in a shape the bot doesn't read: {Message}", typeof(T).Name, ex.Message);
                return null;
            }
        }

        private async Task<ForeverLookup<T>> CachedAsync<T>(string key, TimeSpan foundLifetime, Func<Task<ForeverLookup<T>>> load) where T : class
        {
            if (_cache.TryGetValue(key, out ForeverLookup<T>? cached) && cached != null) return cached;

            var result = await load();
            if (result.Status != ForeverLookupStatus.Unavailable)
            {
                _cache.Set(key, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = result.IsFound ? foundLifetime : NotFoundLifetime,
                    Size = 1
                });
            }
            return result;
        }

        // ===== Parsing =====

        private static ForeverArmoryCharacter? ParseCharacter(JObject o, ForeverRegion region, string requestedSlug)
        {
            var name = Text(o["name"]);
            if (string.IsNullOrWhiteSpace(name)) return null;

            var surname = Text(o["surname"]) ?? string.Empty;
            var classId = Int(o["character_class"]?["id"]);
            var lastLogin = Long(o["last_login_timestamp"]);
            return new ForeverArmoryCharacter(
                Id: Long(o["id"]) ?? 0,
                FirstName: name,
                Surname: surname,
                Slug: requestedSlug,
                Region: region,
                Level: Int(o["level"]) ?? 0,
                Race: Text(o["race"]?["name"]),
                Class: ForeverArmoryIds.Class(classId),
                ClassName: Text(o["character_class"]?["name"]),
                Faction: ForeverArmoryIds.Faction(Text(o["faction"]?["type"])),
                Ruleset: ForeverArmoryIds.Ruleset(Int(o["ruleset"]?["id"])),
                RulesetName: Text(o["ruleset"]?["name"]),
                GuildName: Text(o["guild"]?["name"]),
                GuildSlug: Text(o["guild"]?["slug"]),
                ItemLevel: Int(o["equipped_item_level"]) ?? Int(o["average_item_level"]),
                LastSeen: lastLogin is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastLogin.Value) : null);
        }

        private static ForeverArmoryGuild? ParseGuild(JObject o, ForeverRegion region, string requestedSlug)
        {
            var name = Text(o["name"]);
            if (string.IsNullOrWhiteSpace(name)) return null;

            return new ForeverArmoryGuild(
                Id: Long(o["id"]) ?? 0,
                Name: name,
                Slug: Text(o["slug"]) is { Length: > 0 } slug ? slug : requestedSlug,
                Region: region,
                Faction: ForeverArmoryIds.Faction(Text(o["faction"]?["type"])),
                Ruleset: ForeverArmoryIds.Ruleset(Int(o["ruleset"]?["id"])),
                RulesetName: Text(o["ruleset"]?["name"]),
                MemberCount: Int(o["member_count"]) ?? 0);
        }

        private static ForeverArmoryRoster? ParseRoster(JObject o)
        {
            if (o["members"] is not JArray members) return null;

            var list = new List<ForeverArmoryMember>(members.Count);
            foreach (var member in members)
            {
                var character = member["character"];
                var name = Text(character?["name"]);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var surname = Text(character?["surname"]) ?? string.Empty;
                list.Add(new ForeverArmoryMember(
                    Id: Long(character?["id"]) ?? 0,
                    FirstName: name,
                    Surname: surname,
                    Slug: Text(character?["slug"]) is { Length: > 0 } slug ? slug : ForeverNames.CharacterSlug(name, surname),
                    Level: Int(character?["level"]) ?? 0,
                    Class: ForeverArmoryIds.Class(Int(character?["playable_class"]?["id"])),
                    Rank: Int(member["rank"])));
            }
            return new ForeverArmoryRoster(list);
        }

        private static string? ParseAvatar(JObject o)
        {
            if (o["assets"] is not JArray assets) return null;
            var avatar = assets.FirstOrDefault(a => Text(a["key"]) == "avatar") ?? assets.FirstOrDefault(a => Text(a["key"]) == "avatar-sd");
            var url = Text(avatar?["value"]);
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
        }

        private static IReadOnlyList<ForeverArmoryItem>? ParseGear(JObject o)
        {
            if (o["equipped_items"] is not JArray items) return null;
            return items
                .Select(i => new ForeverArmoryItem(
                    Slot: Text(i["slot"]?["type"]) ?? string.Empty,
                    SlotName: Text(i["slot"]?["name"]) ?? string.Empty,
                    Name: Text(i["name"]) ?? string.Empty,
                    Quality: Text(i["quality"]?["type"]),
                    ItemLevel: Int(i["level"]?["value"])))
                .Where(i => i.Name.Length > 0)
                .ToList();
        }

        private static IReadOnlyList<ForeverArmoryProfession>? ParseProfessions(JObject o)
        {
            var list = new List<ForeverArmoryProfession>();
            foreach (var group in new[] { "primaries", "secondaries" })
            {
                if (o[group] is not JArray professions) continue;
                foreach (var p in professions)
                {
                    var name = Text(p["profession"]?["name"]);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Skill sits on the profession's highest tier; older shapes put it on the profession.
                    var tier = (p["tiers"] as JArray)?.OrderByDescending(t => Int(t["max_skill_points"]) ?? 0).FirstOrDefault() ?? p;
                    list.Add(new ForeverArmoryProfession(name, Int(tier["skill_points"]) ?? 0, Int(tier["max_skill_points"]) ?? 0));
                }
            }
            return list;
        }

        private static IReadOnlyList<ForeverArmoryTalentTree>? ParseTalents(JObject o)
        {
            if (o["specialization_groups"] is not JArray groups || groups.Count == 0) return null;
            var active = groups.FirstOrDefault(g => g["is_active"]?.Type == JTokenType.Boolean && g["is_active"]!.Value<bool>()) ?? groups[0];
            if (active["specializations"] is not JArray trees) return null;

            return trees
                .Select(t => new ForeverArmoryTalentTree(
                    Text(t["specialization_name"]) ?? Text(t["specialization"]?["name"]) ?? string.Empty,
                    Int(t["spent_points"]) ?? 0))
                .Where(t => t.Name.Length > 0 && t.Points > 0)
                .OrderByDescending(t => t.Points)
                .ToList();
        }

        private static ForeverArmoryStats? ParseStats(JObject o)
        {
            if (o["categories"] is not JArray categories) return null;

            var found = new Dictionary<long, long>();
            void Walk(JArray list)
            {
                foreach (var category in list)
                {
                    if (category["statistics"] is JArray statistics)
                    {
                        foreach (var s in statistics)
                        {
                            if (Long(s["id"]) is { } id && s["quantity"] is { } quantity
                                && quantity.Type is JTokenType.Integer or JTokenType.Float)
                            {
                                found[id] = (long)quantity.Value<double>();
                            }
                        }
                    }
                    if (category["sub_categories"] is JArray sub) Walk(sub);
                }
            }
            Walk(categories);

            long? Stat(long id) => found.TryGetValue(id, out var value) ? value : null;
            var stats = new ForeverArmoryStats(Stat(DeathsStat), Stat(QuestsStat), Stat(KillsStat), Stat(DungeonsStat));
            return stats is { Deaths: null, Quests: null, Kills: null, Dungeons: null } ? null : stats;
        }

        // Names are plain strings when a locale is sent; without one Blizzard sends a map per locale.
        private static string? Text(JToken? token) => token?.Type switch
        {
            JTokenType.String => token.Value<string>(),
            JTokenType.Object => Text(token["en_US"] ?? token["en_GB"] ?? token.First?.First),
            _ => null
        };

        private static int? Int(JToken? token) => Long(token) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

        private static long? Long(JToken? token) => token?.Type switch
        {
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => (long)token.Value<double>(),
            _ => null
        };
    }
}
