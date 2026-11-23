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
    public sealed class ForeverResult<T> where T : class
    {
        public T? Value { get; private init; }
        public string? Error { get; private init; }
        public bool Ok => Error == null;

        public static ForeverResult<T> Success(T value) => new() { Value = value };
        public static ForeverResult<T> Fail(string error) => new() { Error = error };
    }

    public sealed record ForeverRosterMember(long UserId, IReadOnlyList<ForeverCharacter> Characters, long? MainId);

    /// <summary>
    /// <paramref name="Armory"/> is Blizzard's roster when the guild is matched to the armory and it answered.
    /// <paramref name="ArmoryMissed"/> means it was asked and didn't, so only member-reported characters are known.
    /// </summary>
    public sealed record ForeverRoster(
        ForeverGuild Guild,
        IReadOnlyList<ForeverRosterMember> Members,
        ForeverArmoryRoster? Armory = null,
        bool ArmoryMissed = false);

    /// <summary>A saved character with what the armory said about it, if it was asked.</summary>
    public sealed record ForeverSavedCharacter(ForeverCharacter Character, ForeverArmoryCharacter? Armory, ForeverLookupStatus? Lookup);

    /// <summary>A saved guild with what the armory said about it.</summary>
    public sealed record ForeverSavedGuild(ForeverGuild Guild, ForeverArmoryGuild? Armory, ForeverLookupStatus Lookup);

    /// <summary>
    /// WoW Forever guild and character associations. Members say which characters are theirs;
    /// everything else (class, level, ruleset, faction, the guild roster) comes from Blizzard's armory.
    /// Nothing proves ownership, and nothing touches the Retail WowCharAssociation table.
    /// </summary>
    public class ForeverService
    {
        public const string NoGuildMessage = "This server has no Forever guild yet. An officer can set one with `/forever guild set`.";
        public const string NotOnArmoryHint = "Characters appear on the armory at level 10, after they log out.";
        public const string ArmoryDownMessage = "Blizzard's armory didn't answer. Try again in a few minutes.";
        private const string NotFoundMessage = "Character not found. Pick one of yours from the list.";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IForeverArmory _armory;
        private readonly ILogger<ForeverService> _logger;

        public ForeverService(IServiceScopeFactory scopeFactory, IForeverArmory armory, ILogger<ForeverService> logger)
        {
            _scopeFactory = scopeFactory;
            _armory = armory;
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

        /// <summary>
        /// Saves the server's guild as the armory has it: its spelling, ruleset and faction.
        /// A guild the armory doesn't have can't be saved, because nothing else would work for it.
        /// </summary>
        public async Task<ForeverResult<ForeverSavedGuild>> SetGuildAsync(
            long guildId, string? name, ForeverRegion region, long setById, string? setByName)
        {
            if (!ForeverNames.TryCleanGuildName(name, out var cleanName, out var error))
                return ForeverResult<ForeverSavedGuild>.Fail(error);

            var lookup = await _armory.GetGuildAsync(region, cleanName);
            var armory = lookup.Value;
            if (!lookup.IsFound)
            {
                return ForeverResult<ForeverSavedGuild>.Fail(lookup.Status == ForeverLookupStatus.NotFound
                    ? $"I couldn't find **{cleanName}** on the {region} armory. Check the spelling and the region; a new guild can take a few hours to show up."
                    : ArmoryDownMessage);
            }
            var savedRuleset = armory!.Ruleset;
            var savedFaction = armory.Faction;
            if (savedRuleset == null || savedFaction == null)
            {
                return ForeverResult<ForeverSavedGuild>.Fail(
                    $"**{armory.Name}** is on the armory, but with a ruleset or faction I don't know yet. Tell the bot's owner so it can be added.");
            }

            var savedName = ForeverNames.TryCleanGuildName(armory.Name, out var armoryName, out _) ? armoryName : cleanName;

            async Task<ForeverGuild> Upsert(NinjaBotEntities db)
            {
                var guild = await db.ForeverGuilds.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
                if (guild == null)
                {
                    guild = new ForeverGuild { DiscordGuildId = guildId };
                    db.ForeverGuilds.Add(guild);
                }
                guild.Name = savedName;
                guild.Region = region;
                guild.Ruleset = savedRuleset.Value;
                guild.Faction = savedFaction.Value;
                guild.ArmorySlug = Truncate(armory.Slug, ForeverGuild.MaxArmorySlugLength);
                // The roster used to need switching on. It is always there now; the column stays for older builds.
                guild.RosterEnabled = true;
                guild.SetById = setById;
                guild.SetByName = Truncate(setByName, 100);
                guild.TimeSet = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return guild;
            }

            ForeverGuild saved;
            try
            {
                saved = await WithDbAsync(Upsert);
            }
            catch (DbUpdateException)
            {
                // Two officers created it at the same moment; the row exists now, so update it.
                saved = await WithDbAsync(Upsert);
            }
            return ForeverResult<ForeverSavedGuild>.Success(new ForeverSavedGuild(saved, armory, lookup.Status));
        }

        /// <summary>Removes the association. What members put on the roster stays, unseen until a guild is set again.</summary>
        public Task<bool> UnsetGuildAsync(long guildId) => WithDbAsync(async db =>
        {
            var guild = await db.ForeverGuilds.FirstOrDefaultAsync(g => g.DiscordGuildId == guildId);
            if (guild == null) return false;
            db.ForeverGuilds.Remove(guild);
            await db.SaveChangesAsync();
            return true;
        });

        // ===== Characters =====

        public Task<IReadOnlyList<ForeverCharacter>> GetCharactersAsync(long userId) => WithDbAsync(async db =>
            (IReadOnlyList<ForeverCharacter>)await db.ForeverCharacters.AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderBy(c => c.FirstName).ThenBy(c => c.LastName)
                .ToListAsync());

        public Task<ForeverCharacter?> GetCharacterAsync(long userId, long characterId) => WithDbAsync(db =>
            db.ForeverCharacters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId));

        /// <summary>
        /// Adds a character as the armory has it. One below level 10 isn't there yet, but the guild's
        /// roster lists it with its class, so a guildmate that young is saved from the roster instead.
        /// The caller's first character becomes their main in this server.
        /// </summary>
        public async Task<ForeverResult<ForeverSavedCharacter>> AddCharacterAsync(
            long userId, long guildId, string? firstName, string? surname, ForeverRegion? chosenRegion = null)
        {
            if (!ForeverNames.TryCleanPart(firstName, "First name", out var first, out var error)
                || !ForeverNames.TryCleanPart(surname, "Surname", out var last, out error))
                return ForeverResult<ForeverSavedCharacter>.Fail(error);

            var guild = await GetGuildAsync(guildId);
            var region = chosenRegion ?? guild?.Region;
            if (region == null)
                return ForeverResult<ForeverSavedCharacter>.Fail("This server has no Forever guild to take the region from, so please also pick a region.");

            var lookup = await _armory.GetCharacterAsync(region.Value, first, last);
            var armory = lookup.Value;
            ForeverRuleset? ruleset = armory?.Ruleset;
            ForeverFaction? faction = armory?.Faction;
            ForeverClass? characterClass = armory?.Class;

            if (!lookup.IsFound)
            {
                var member = guild != null && guild.Region == region ? await FindOnRosterAsync(guild, first, last) : null;
                if (member == null)
                {
                    return ForeverResult<ForeverSavedCharacter>.Fail(lookup.Status == ForeverLookupStatus.NotFound
                        ? $"**{ForeverNames.FullName(first, last)}** isn't on the {region} armory. {NotOnArmoryHint} Check the spelling and the region too."
                        : ArmoryDownMessage);
                }
                // In this server's guild, so it shares the guild's ruleset and faction.
                (first, last) = CleanOr(member.FirstName, member.Surname, first, last);
                ruleset = guild!.Ruleset;
                faction = guild.Faction;
                characterClass = member.Class;
            }
            else
            {
                // The armory's spelling is the character's real one (capitals, accents).
                (first, last) = CleanOr(armory!.FirstName, armory.Surname, first, last);
            }

            if (ruleset == null || faction == null)
            {
                return ForeverResult<ForeverSavedCharacter>.Fail(
                    $"**{ForeverNames.FullName(first, last)}** is on the armory, but with a ruleset or faction I don't know yet. Tell the bot's owner so it can be added.");
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
                Class = characterClass,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            return await WithDbAsync(async db =>
            {
                if (await IsDuplicateAsync(db, character, exceptId: null))
                    return ForeverResult<ForeverSavedCharacter>.Fail(DuplicateMessage(character));

                var isFirst = !await db.ForeverCharacters.AnyAsync(c => c.UserId == userId);
                db.ForeverCharacters.Add(character);
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Lost a race with a double-submitted add; the unique index caught it.
                    return ForeverResult<ForeverSavedCharacter>.Fail(DuplicateMessage(character));
                }

                if (isFirst || !await db.ForeverMains.AnyAsync(m => m.DiscordGuildId == guildId && m.UserId == userId))
                {
                    db.ForeverMains.Add(new ForeverMain { DiscordGuildId = guildId, UserId = userId, CharacterId = character.Id, SetAt = DateTime.UtcNow });
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (DbUpdateException)
                    {
                        // A main appeared in the meantime; the character is saved either way.
                    }
                }
                return ForeverResult<ForeverSavedCharacter>.Success(new ForeverSavedCharacter(character, armory, lookup.Status));
            });
        }

        private async Task<ForeverArmoryMember?> FindOnRosterAsync(ForeverGuild guild, string first, string last)
        {
            if (guild.ArmorySlug == null) return null;
            var roster = await _armory.GetRosterAsync(guild.Region, guild.ArmorySlug);
            var (wantedFirst, wantedLast) = (ForeverNames.Normalize(first), ForeverNames.Normalize(last));
            return roster.Value?.Members.FirstOrDefault(m =>
                ForeverNames.Normalize(m.FirstName) == wantedFirst && ForeverNames.Normalize(m.Surname) == wantedLast);
        }

        private static (string First, string Last) CleanOr(string first, string last, string fallbackFirst, string fallbackLast)
            => ForeverNames.TryCleanPart(first, "First name", out var cleanFirst, out _)
               && ForeverNames.TryCleanPart(last, "Surname", out var cleanLast, out _)
                ? (cleanFirst, cleanLast)
                : (fallbackFirst, fallbackLast);

        /// <summary>What the armory says about a saved character now. Cached for a few minutes.</summary>
        public Task<ForeverLookup<ForeverArmoryCharacter>> LookUpAsync(ForeverCharacter character)
            => _armory.GetCharacterAsync(character.Region, character.FirstName, character.LastName);

        /// <summary>
        /// Brings a saved character up to date with the armory: its class, ruleset, faction and spelling.
        /// When the armory has nothing for it, the character is returned as it is saved.
        /// </summary>
        public async Task<ForeverResult<ForeverSavedCharacter>> RefreshCharacterAsync(long userId, long characterId)
        {
            var current = await GetCharacterAsync(userId, characterId);
            if (current == null) return ForeverResult<ForeverSavedCharacter>.Fail(NotFoundMessage);

            var lookup = await LookUpAsync(current);
            var armory = lookup.Value;
            if (armory == null) return ForeverResult<ForeverSavedCharacter>.Success(new ForeverSavedCharacter(current, null, lookup.Status));

            var (first, last) = CleanOr(armory.FirstName, armory.Surname, current.FirstName, current.LastName);
            if (first == current.FirstName && last == current.LastName
                && (armory.Ruleset ?? current.Ruleset) == current.Ruleset
                && (armory.Faction ?? current.Faction) == current.Faction
                && (armory.Class ?? current.Class) == current.Class)
                return ForeverResult<ForeverSavedCharacter>.Success(new ForeverSavedCharacter(current, armory, lookup.Status));

            return await WithDbAsync(async db =>
            {
                var character = await db.ForeverCharacters.FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId);
                if (character == null) return ForeverResult<ForeverSavedCharacter>.Fail(NotFoundMessage);

                character.FirstName = first;
                character.NormalizedFirstName = ForeverNames.Normalize(first);
                character.LastName = last;
                character.NormalizedLastName = ForeverNames.Normalize(last);
                character.Ruleset = armory.Ruleset ?? character.Ruleset;
                character.Faction = armory.Faction ?? character.Faction;
                character.Class = armory.Class ?? character.Class;
                character.UpdatedAt = DateTime.UtcNow;
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // The armory's spelling collides with another of the member's characters; keep what is saved.
                    return ForeverResult<ForeverSavedCharacter>.Success(new ForeverSavedCharacter(current, armory, lookup.Status));
                }
                return ForeverResult<ForeverSavedCharacter>.Success(new ForeverSavedCharacter(character, armory, lookup.Status));
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
        /// Shared characters of current members that match the guild's region/ruleset/faction, plus
        /// Blizzard's roster when the guild is matched to the armory.
        /// <paramref name="isMember"/> must fail closed: unknown users are left out.
        /// </summary>
        public async Task<ForeverResult<ForeverRoster>> GetRosterAsync(long guildId, Func<long, bool> isMember)
        {
            var guild = await GetGuildAsync(guildId);
            if (guild == null) return ForeverResult<ForeverRoster>.Fail(NoGuildMessage);

            var roster = await WithDbAsync(async db =>
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

                return new ForeverRoster(guild, members);
            });

            if (roster.Guild.ArmorySlug == null) return ForeverResult<ForeverRoster>.Success(roster);

            var armory = await _armory.GetRosterAsync(roster.Guild.Region, roster.Guild.ArmorySlug);
            return ForeverResult<ForeverRoster>.Success(roster with { Armory = armory.Value, ArmoryMissed = !armory.IsFound });
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
