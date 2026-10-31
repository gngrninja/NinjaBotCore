#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>Optional fields; null means "not given" (add: use the server default, edit: keep).</summary>
    public sealed record ForeverCharacterInput(
        string? FirstName = null,
        string? LastName = null,
        ForeverRegion? Region = null,
        ForeverRuleset? Ruleset = null,
        ForeverFaction? Faction = null,
        ForeverClass? Class = null);

    public sealed class ForeverResult<T> where T : class
    {
        public T? Value { get; private init; }
        public string? Error { get; private init; }
        public bool Ok => Error == null;

        public static ForeverResult<T> Success(T value) => new() { Value = value };
        public static ForeverResult<T> Fail(string error) => new() { Error = error };
    }

    public sealed record ForeverRosterMember(long UserId, IReadOnlyList<ForeverCharacter> Characters, long? MainId);

    public sealed record ForeverRoster(ForeverGuild Guild, IReadOnlyList<ForeverRosterMember> Members);

    /// <summary>
    /// WoW Forever guild and character associations. Everything here is self-reported and
    /// manual: no Blizzard/Raider.IO/WCL calls, and nothing touches the Retail WowCharAssociation table.
    /// </summary>
    public class ForeverService
    {
        public const string NoGuildMessage = "This server has no Forever guild yet. An officer can set one with `/forever guild set`.";
        public const string RosterOffMessage = "The Forever roster is turned off in this server. An officer can turn it on with `/forever guild roster`.";
        private const string NotFoundMessage = "Character not found. Pick one of yours from the list.";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ForeverService> _logger;

        public ForeverService(IServiceScopeFactory scopeFactory, ILogger<ForeverService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>Drops a departing member's shares and main for that server; rejoining doesn't restore them.</summary>
        public void Attach(DiscordShardedClient client)
        {
            client.UserLeft += async (guild, user) =>
            {
                try
                {
                    await ForgetMemberAsync((long)guild.Id, (long)user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to clear Forever roster entries for departing member in {GuildId}", guild.Id);
                }
            };
        }

        public static bool Matches(ForeverCharacter character, ForeverGuild guild)
            => character.Region == guild.Region
               && character.Ruleset == guild.Ruleset
               && character.Faction == guild.Faction;

        // ===== Guild =====

        public Task<ForeverGuild?> GetGuildAsync(long guildId)
            => WithDbAsync(db => db.ForeverGuilds.AsNoTracking().FirstOrDefaultAsync(g => g.DiscordGuildId == guildId));

        public async Task<ForeverResult<ForeverGuild>> SetGuildAsync(
            long guildId, string? name, ForeverRegion region, ForeverRuleset ruleset, ForeverFaction faction,
            long setById, string? setByName)
        {
            if (!ForeverNames.TryCleanGuildName(name, out var cleanName, out var error))
                return ForeverResult<ForeverGuild>.Fail(error);

            async Task<ForeverGuild> Upsert(NinjaBotEntities db)
            {
                var guild = await db.ForeverGuilds.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
                if (guild == null)
                {
                    guild = new ForeverGuild { DiscordGuildId = guildId };
                    db.ForeverGuilds.Add(guild);
                }
                guild.Name = cleanName;
                guild.Region = region;
                guild.Ruleset = ruleset;
                guild.Faction = faction;
                guild.SetById = setById;
                guild.SetByName = Truncate(setByName, 100);
                guild.TimeSet = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return guild;
            }

            try
            {
                return ForeverResult<ForeverGuild>.Success(await WithDbAsync(Upsert));
            }
            catch (DbUpdateException)
            {
                // Two officers created it at the same moment; the row exists now, so update it.
                return ForeverResult<ForeverGuild>.Success(await WithDbAsync(Upsert));
            }
        }

        /// <summary>Removes the association. Shares stay but are invisible until a guild is set again.</summary>
        public Task<bool> UnsetGuildAsync(long guildId) => WithDbAsync(async db =>
        {
            var guild = await db.ForeverGuilds.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            if (guild == null) return false;
            db.ForeverGuilds.Remove(guild);
            await db.SaveChangesAsync();
            return true;
        });

        public Task<ForeverResult<ForeverGuild>> SetRosterEnabledAsync(long guildId, bool enabled, long setById, string? setByName)
            => WithDbAsync(async db =>
            {
                var guild = await db.ForeverGuilds.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
                if (guild == null) return ForeverResult<ForeverGuild>.Fail(NoGuildMessage);
                guild.RosterEnabled = enabled;
                guild.SetById = setById;
                guild.SetByName = Truncate(setByName, 100);
                guild.TimeSet = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return ForeverResult<ForeverGuild>.Success(guild);
            });

        // ===== Characters =====

        public Task<IReadOnlyList<ForeverCharacter>> GetCharactersAsync(long userId) => WithDbAsync(async db =>
            (IReadOnlyList<ForeverCharacter>)await db.ForeverCharacters.AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderBy(c => c.FirstName).ThenBy(c => c.LastName)
                .ToListAsync());

        public Task<ForeverCharacter?> GetCharacterAsync(long userId, long characterId) => WithDbAsync(db =>
            db.ForeverCharacters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId));

        public async Task<ForeverResult<ForeverCharacter>> AddCharacterAsync(long userId, long guildId, ForeverCharacterInput input)
        {
            if (!ForeverNames.TryCleanPart(input.FirstName, "First name", out var first, out var error)
                || !ForeverNames.TryCleanPart(input.LastName, "Surname", out var last, out error))
                return ForeverResult<ForeverCharacter>.Fail(error);

            var guild = await GetGuildAsync(guildId);
            var region = input.Region ?? guild?.Region;
            var ruleset = input.Ruleset ?? guild?.Ruleset;
            var faction = input.Faction ?? guild?.Faction;
            if (region == null || ruleset == null || faction == null)
            {
                var missing = new[] { region == null ? "region" : null, ruleset == null ? "ruleset" : null, faction == null ? "faction" : null }
                    .Where(m => m != null);
                return ForeverResult<ForeverCharacter>.Fail(
                    $"This server has no Forever guild to fill in defaults, so please also pick: {string.Join(", ", missing)}.");
            }

            var character = new ForeverCharacter
            {
                UserId = userId,
                FirstName = first,
                LastName = last,
                NormalizedFirstName = ForeverNames.Normalize(first),
                NormalizedLastName = ForeverNames.Normalize(last),
                Region = region.Value,
                Ruleset = ruleset.Value,
                Faction = faction.Value,
                Class = input.Class,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            return await WithDbAsync(async db =>
            {
                if (await IsDuplicateAsync(db, character, exceptId: null))
                    return ForeverResult<ForeverCharacter>.Fail(DuplicateMessage(character));

                db.ForeverCharacters.Add(character);
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Lost a race with a double-submitted add; the unique index caught it.
                    return ForeverResult<ForeverCharacter>.Fail(DuplicateMessage(character));
                }
                return ForeverResult<ForeverCharacter>.Success(character);
            });
        }

        public async Task<ForeverResult<ForeverCharacter>> EditCharacterAsync(long userId, long characterId, ForeverCharacterInput changes)
        {
            string? first = null, last = null, error = string.Empty;
            if (changes.FirstName != null && !ForeverNames.TryCleanPart(changes.FirstName, "First name", out first, out error))
                return ForeverResult<ForeverCharacter>.Fail(error);
            if (changes.LastName != null && !ForeverNames.TryCleanPart(changes.LastName, "Surname", out last, out error))
                return ForeverResult<ForeverCharacter>.Fail(error);

            return await WithDbAsync(async db =>
            {
                var character = await db.ForeverCharacters.FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId);
                if (character == null) return ForeverResult<ForeverCharacter>.Fail(NotFoundMessage);

                if (first != null)
                {
                    character.FirstName = first;
                    character.NormalizedFirstName = ForeverNames.Normalize(first);
                }
                if (last != null)
                {
                    character.LastName = last;
                    character.NormalizedLastName = ForeverNames.Normalize(last);
                }
                character.Region = changes.Region ?? character.Region;
                character.Ruleset = changes.Ruleset ?? character.Ruleset;
                character.Faction = changes.Faction ?? character.Faction;
                character.Class = changes.Class ?? character.Class;
                character.UpdatedAt = DateTime.UtcNow;

                if (await IsDuplicateAsync(db, character, exceptId: character.Id))
                    return ForeverResult<ForeverCharacter>.Fail(DuplicateMessage(character));

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    return ForeverResult<ForeverCharacter>.Fail(DuplicateMessage(character));
                }
                return ForeverResult<ForeverCharacter>.Success(character);
            });
        }

        /// <summary>Deletes the character plus its mains and shares in every server.</summary>
        public Task<ForeverCharacter?> RemoveCharacterAsync(long userId, long characterId) => WithDbAsync(async db =>
        {
            var character = await db.ForeverCharacters.FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId);
            if (character == null) return null;

            // One SaveChanges is one transaction; the FK cascade is only a backstop.
            db.ForeverMains.RemoveRange(await db.ForeverMains.Where(m => m.CharacterId == characterId).ToListAsync());
            db.ForeverRosterShares.RemoveRange(await db.ForeverRosterShares.Where(s => s.CharacterId == characterId).ToListAsync());
            db.ForeverCharacters.Remove(character);
            await db.SaveChangesAsync();
            return character;
        });

        // ===== Main =====

        public Task<long?> GetMainIdAsync(long userId, long guildId) => WithDbAsync(db =>
            db.ForeverMains.AsNoTracking()
                .Where(m => m.DiscordGuildId == guildId && m.UserId == userId)
                .Select(m => (long?)m.CharacterId)
                .FirstOrDefaultAsync());

        public async Task<ForeverResult<ForeverCharacter>> SetMainAsync(long userId, long guildId, long characterId)
        {
            async Task<ForeverResult<ForeverCharacter>> Upsert(NinjaBotEntities db)
            {
                var character = await db.ForeverCharacters.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId);
                if (character == null) return ForeverResult<ForeverCharacter>.Fail(NotFoundMessage);

                var main = await db.ForeverMains.FirstOrDefaultAsync(m => m.DiscordGuildId == guildId && m.UserId == userId);
                if (main == null)
                {
                    main = new ForeverMain { DiscordGuildId = guildId, UserId = userId };
                    db.ForeverMains.Add(main);
                }
                main.CharacterId = character.Id;
                main.SetAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return ForeverResult<ForeverCharacter>.Success(character);
            }

            try
            {
                return await WithDbAsync(Upsert);
            }
            catch (DbUpdateException)
            {
                // A concurrent click created the row first; update it instead.
                return await WithDbAsync(Upsert);
            }
        }

        // ===== Roster =====

        public Task<HashSet<long>> GetSharedIdsAsync(long userId, long guildId) => WithDbAsync(async db =>
            (await db.ForeverRosterShares.AsNoTracking()
                .Where(s => s.DiscordGuildId == guildId && s.UserId == userId)
                .Select(s => s.CharacterId)
                .ToListAsync()).ToHashSet());

        public async Task<ForeverResult<ForeverCharacter>> ShareAsync(long userId, long guildId, long characterId)
        {
            var guild = await GetGuildAsync(guildId);
            if (guild == null) return ForeverResult<ForeverCharacter>.Fail(NoGuildMessage);
            if (!guild.RosterEnabled) return ForeverResult<ForeverCharacter>.Fail(RosterOffMessage);

            return await WithDbAsync(async db =>
            {
                var character = await db.ForeverCharacters.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId);
                if (character == null) return ForeverResult<ForeverCharacter>.Fail(NotFoundMessage);

                if (!Matches(character, guild))
                {
                    return ForeverResult<ForeverCharacter>.Fail(
                        $"**{character.FullName}** is {ForeverDisplay.Context(character.Region, character.Ruleset, character.Faction)}, " +
                        $"but this server's guild is {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}. " +
                        "Only matching characters can go on the roster.");
                }

                var exists = await db.ForeverRosterShares.AnyAsync(s => s.DiscordGuildId == guildId && s.CharacterId == characterId);
                if (!exists)
                {
                    db.ForeverRosterShares.Add(new ForeverRosterShare
                    {
                        DiscordGuildId = guildId,
                        UserId = userId,
                        CharacterId = characterId,
                        SharedAt = DateTime.UtcNow
                    });
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (DbUpdateException)
                    {
                        // Double click: it's shared either way.
                    }
                }
                return ForeverResult<ForeverCharacter>.Success(character);
            });
        }

        public Task<bool> UnshareAsync(long userId, long guildId, long characterId) => WithDbAsync(async db =>
        {
            var shares = await db.ForeverRosterShares
                .Where(s => s.DiscordGuildId == guildId && s.UserId == userId && s.CharacterId == characterId)
                .ToListAsync();
            if (shares.Count == 0) return false;
            db.ForeverRosterShares.RemoveRange(shares);
            await db.SaveChangesAsync();
            return true;
        });

        /// <summary>
        /// Shared characters of current members that match the guild's region/ruleset/faction.
        /// <paramref name="isMember"/> must fail closed: unknown users are left out.
        /// </summary>
        public async Task<ForeverResult<ForeverRoster>> GetRosterAsync(long guildId, Func<long, bool> isMember)
        {
            var guild = await GetGuildAsync(guildId);
            if (guild == null) return ForeverResult<ForeverRoster>.Fail(NoGuildMessage);
            if (!guild.RosterEnabled) return ForeverResult<ForeverRoster>.Fail(RosterOffMessage);

            return await WithDbAsync(async db =>
            {
                var shared = await db.ForeverRosterShares.AsNoTracking()
                    .Where(s => s.DiscordGuildId == guildId)
                    .Join(db.ForeverCharacters.AsNoTracking(),
                        s => new { Id = s.CharacterId, s.UserId },
                        c => new { c.Id, c.UserId },
                        (s, c) => c)
                    .ToListAsync();

                var mains = await db.ForeverMains.AsNoTracking()
                    .Where(m => m.DiscordGuildId == guildId)
                    .ToDictionaryAsync(m => m.UserId, m => m.CharacterId);

                var members = shared
                    .Where(c => Matches(c, guild) && isMember(c.UserId))
                    .GroupBy(c => c.UserId)
                    .Select(g =>
                    {
                        long? mainId = mains.TryGetValue(g.Key, out var id) ? id : null;
                        var characters = g
                            .OrderByDescending(c => c.Id == mainId)
                            .ThenBy(c => c.FirstName).ThenBy(c => c.LastName)
                            .ToList();
                        return new ForeverRosterMember(g.Key, characters, mainId);
                    })
                    .ToList();

                return ForeverResult<ForeverRoster>.Success(new ForeverRoster(guild, members));
            });
        }

        public Task<int> ForgetMemberAsync(long guildId, long userId) => WithDbAsync(async db =>
        {
            var shares = await db.ForeverRosterShares.Where(s => s.DiscordGuildId == guildId && s.UserId == userId).ToListAsync();
            var mains = await db.ForeverMains.Where(m => m.DiscordGuildId == guildId && m.UserId == userId).ToListAsync();
            if (shares.Count == 0 && mains.Count == 0) return 0;
            db.ForeverRosterShares.RemoveRange(shares);
            db.ForeverMains.RemoveRange(mains);
            await db.SaveChangesAsync();
            return shares.Count + mains.Count;
        });

        // ===== Helpers =====

        private static Task<bool> IsDuplicateAsync(NinjaBotEntities db, ForeverCharacter character, long? exceptId)
            => db.ForeverCharacters.AnyAsync(c =>
                c.UserId == character.UserId
                && c.Region == character.Region
                && c.NormalizedFirstName == character.NormalizedFirstName
                && c.NormalizedLastName == character.NormalizedLastName
                && (exceptId == null || c.Id != exceptId));

        private static string DuplicateMessage(ForeverCharacter character)
            => $"You already have **{character.FullName}** in {character.Region}.";

        private static string? Truncate(string? value, int max)
            => value == null || value.Length <= max ? value : value[..max];

        private async Task<T> WithDbAsync<T>(Func<NinjaBotEntities, Task<T>> func)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            return await func(db);
        }
    }
}
