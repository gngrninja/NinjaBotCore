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
    /// class, ruleset, faction and the guild roster come from Blizzard's armory when it has them.
    /// Nothing proves ownership, and nothing touches the Retail WowCharAssociation table.
    /// </summary>
    public class ForeverService
    {
        public const string NoGuildMessage = "This server has no Forever guild yet. An officer can set one with `/forever guild set`.";
        public const string RosterOffMessage = "The Forever roster is turned off in this server. An officer can turn it on with `/forever guild roster`.";
        public const string NotOnArmoryHint = "Characters appear on the armory at level 10, after they log out.";
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
        /// Saves the server's guild. The armory fills in the ruleset and faction and overrides what
        /// was typed; a guild it doesn't have is saved as typed, which then needs both.
        /// </summary>
        public async Task<ForeverResult<ForeverSavedGuild>> SetGuildAsync(
            long guildId, string? name, ForeverRegion region, ForeverRuleset? ruleset, ForeverFaction? faction,
            long setById, string? setByName)
        {
            if (!ForeverNames.TryCleanGuildName(name, out var cleanName, out var error))
                return ForeverResult<ForeverSavedGuild>.Fail(error);

            var lookup = await _armory.GetGuildAsync(region, cleanName);
            var armory = lookup.Value;
            var savedRuleset = armory?.Ruleset ?? ruleset;
            var savedFaction = armory?.Faction ?? faction;
            if (savedRuleset == null || savedFaction == null)
            {
                var missing = string.Join(" and ", new[] { savedRuleset == null ? "ruleset" : null, savedFaction == null ? "faction" : null }
                    .Where(m => m != null));
                return ForeverResult<ForeverSavedGuild>.Fail(lookup.Status switch
                {
                    ForeverLookupStatus.Found =>
                        $"**{armory!.Name}** is on the armory, but with a {missing} I don't know yet. Please also pick: {missing}.",
                    ForeverLookupStatus.NotFound =>
                        $"I couldn't find **{cleanName}** on the {region} armory. Check the spelling and region, or also pick a {missing} to save it as typed.",
                    _ =>
                        $"Blizzard's armory didn't answer, so I can't fill in the details. Try again, or also pick a {missing} to save it as typed."
                });
            }

            var savedName = armory != null && ForeverNames.TryCleanGuildName(armory.Name, out var armoryName, out _) ? armoryName : cleanName;

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
                guild.ArmorySlug = Truncate(armory?.Slug, ForeverGuild.MaxArmorySlugLength);
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

        /// <summary>
        /// Adds a character. The armory fills in the class, ruleset and faction and overrides what was
        /// typed; a character it doesn't have yet is saved as typed, with the server's guild as the default.
        /// </summary>
        public async Task<ForeverResult<ForeverSavedCharacter>> AddCharacterAsync(long userId, long guildId, ForeverCharacterInput input)
        {
            if (!ForeverNames.TryCleanPart(input.FirstName, "First name", out var first, out var error)
                || !ForeverNames.TryCleanPart(input.LastName, "Surname", out var last, out error))
                return ForeverResult<ForeverSavedCharacter>.Fail(error);

            var guild = await GetGuildAsync(guildId);
            var region = input.Region ?? guild?.Region;
            if (region == null)
                return ForeverResult<ForeverSavedCharacter>.Fail("This server has no Forever guild to take the region from, so please also pick a region.");

            var lookup = await _armory.GetCharacterAsync(region.Value, first, last);
            var armory = lookup.Value;
            var ruleset = armory?.Ruleset ?? input.Ruleset ?? guild?.Ruleset;
            var faction = armory?.Faction ?? input.Faction ?? guild?.Faction;
            if (ruleset == null || faction == null)
            {
                var missing = string.Join(" and ", new[] { ruleset == null ? "ruleset" : null, faction == null ? "faction" : null }
                    .Where(m => m != null));
                return ForeverResult<ForeverSavedCharacter>.Fail(lookup.Status switch
                {
                    ForeverLookupStatus.Found =>
                        $"**{armory!.FullName}** is on the armory, but with a {missing} I don't know yet. Please also pick: {missing}.",
                    ForeverLookupStatus.NotFound =>
                        $"**{ForeverNames.FullName(first, last)}** isn't on the {region} armory yet. {NotOnArmoryHint} To add it now, also pick a {missing}.",
                    _ =>
                        $"Blizzard's armory didn't answer, so I can't fill in the details. Try again, or also pick a {missing}."
                });
            }

            // The armory's spelling is the character's real one (capitals, accents).
            if (armory != null
                && ForeverNames.TryCleanPart(armory.FirstName, "First name", out var armoryFirst, out _)
                && ForeverNames.TryCleanPart(armory.Surname, "Surname", out var armoryLast, out _))
            {
                first = armoryFirst;
                last = armoryLast;
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
                Class = armory?.Class ?? input.Class,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            return await WithDbAsync(async db =>
            {
                if (await IsDuplicateAsync(db, character, exceptId: null))
                    return ForeverResult<ForeverSavedCharacter>.Fail(DuplicateMessage(character));

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
                return ForeverResult<ForeverSavedCharacter>.Success(new ForeverSavedCharacter(character, armory, lookup.Status));
            });
        }

        /// <summary>What the armory says about a saved character now. Cached for a few minutes.</summary>
        public Task<ForeverLookup<ForeverArmoryCharacter>> LookUpAsync(ForeverCharacter character)
            => _armory.GetCharacterAsync(character.Region, character.FirstName, character.LastName);

        /// <summary>
        /// Edits a character, then checks the armory under its new name and region. As with adding,
        /// the armory's class, ruleset, faction and spelling override what was typed, so an edit with
        /// no changes brings a character added before it reached the armory up to date.
        /// </summary>
        public async Task<ForeverResult<ForeverSavedCharacter>> EditCharacterAsync(long userId, long characterId, ForeverCharacterInput changes)
        {
            string? first = null, last = null, error = string.Empty;
            if (changes.FirstName != null && !ForeverNames.TryCleanPart(changes.FirstName, "First name", out first, out error))
                return ForeverResult<ForeverSavedCharacter>.Fail(error);
            if (changes.LastName != null && !ForeverNames.TryCleanPart(changes.LastName, "Surname", out last, out error))
                return ForeverResult<ForeverSavedCharacter>.Fail(error);

            var current = await GetCharacterAsync(userId, characterId);
            if (current == null) return ForeverResult<ForeverSavedCharacter>.Fail(NotFoundMessage);

            first ??= current.FirstName;
            last ??= current.LastName;
            var region = changes.Region ?? current.Region;
            var lookup = await _armory.GetCharacterAsync(region, first, last);
            var armory = lookup.Value;
            if (armory != null
                && ForeverNames.TryCleanPart(armory.FirstName, "First name", out var armoryFirst, out _)
                && ForeverNames.TryCleanPart(armory.Surname, "Surname", out var armoryLast, out _))
            {
                first = armoryFirst;
                last = armoryLast;
            }

            return await WithDbAsync(async db =>
            {
                var character = await db.ForeverCharacters.FirstOrDefaultAsync(c => c.Id == characterId && c.UserId == userId);
                if (character == null) return ForeverResult<ForeverSavedCharacter>.Fail(NotFoundMessage);

                character.FirstName = first;
                character.NormalizedFirstName = ForeverNames.Normalize(first);
                character.LastName = last;
                character.NormalizedLastName = ForeverNames.Normalize(last);
                character.Region = region;
                character.Ruleset = armory?.Ruleset ?? changes.Ruleset ?? character.Ruleset;
                character.Faction = armory?.Faction ?? changes.Faction ?? character.Faction;
                character.Class = armory?.Class ?? changes.Class ?? character.Class;
                character.UpdatedAt = DateTime.UtcNow;

                if (await IsDuplicateAsync(db, character, exceptId: character.Id))
                    return ForeverResult<ForeverSavedCharacter>.Fail(DuplicateMessage(character));

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    return ForeverResult<ForeverSavedCharacter>.Fail(DuplicateMessage(character));
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
        /// Shared characters of current members that match the guild's region/ruleset/faction, plus
        /// Blizzard's roster when the guild is matched to the armory.
        /// <paramref name="isMember"/> must fail closed: unknown users are left out.
        /// </summary>
        public async Task<ForeverResult<ForeverRoster>> GetRosterAsync(long guildId, Func<long, bool> isMember)
        {
            var guild = await GetGuildAsync(guildId);
            if (guild == null) return ForeverResult<ForeverRoster>.Fail(NoGuildMessage);
            if (!guild.RosterEnabled) return ForeverResult<ForeverRoster>.Fail(RosterOffMessage);

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
