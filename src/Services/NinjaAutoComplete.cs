using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace NinjaBotCore.Services
{
    public class NinjaAutoComplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            List<WowCharAssociation> chars = new List<WowCharAssociation>();
            List<string> foundChars = new List<string>();
            using (var db = new NinjaBotEntities())
            {
                chars = db.WowCharAssociation.Where(a => a.UserId == (long)context.User.Id).ToList();
            }
            var result = Task.FromResult(AutocompletionResult.FromSuccess(chars.Select(c => new AutocompleteResult(c.CharName, c.Id.ToString())))).Result;
            return result;
        }
    }

    /// <summary>
    /// Enhanced autocomplete that shows user's saved characters + guild roster characters
    /// Supports manual text entry (user can type anything) or selection from suggestions
    /// Uses in-memory caching to avoid hammering WoW API on every keystroke
    /// </summary>
    public class GuildCharAutocomplete : AutocompleteHandler
    {
        private static readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 100 // Limit cache to ~100 guild rosters
        });

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            var logger = services.GetService<ILogger<GuildCharAutocomplete>>();
            var wowApi = services.GetService<WowApi>();
            var results = new List<AutocompleteResult>();

            try
            {
                // Get user's typed input (empty string if nothing typed yet)
                var userInput = autocompleteInteraction.Data.Current.Value?.ToString()?.ToLower() ?? string.Empty;

                // Step 1: Get user's saved characters (always prioritize these)
                List<WowCharAssociation> savedChars;
                using (var db = new NinjaBotEntities())
                {
                    savedChars = db.WowCharAssociation
                        .Where(a => a.UserId == (long)context.User.Id)
                        .ToList();
                }

                // Filter saved chars by user input if provided
                var filteredSavedChars = string.IsNullOrWhiteSpace(userInput)
                    ? savedChars
                    : savedChars.Where(c => c.CharName.ToLower().Contains(userInput)).ToList();

                // Add saved characters with ★ marker
                foreach (var savedChar in filteredSavedChars.Take(10)) // Limit saved to 10
                {
                    var displayName = savedChar.IsMain
                        ? $"★ {savedChar.CharName} ({savedChar.WowRealm}) [MAIN]"
                        : $"★ {savedChar.CharName} ({savedChar.WowRealm})";

                    // Return the character name as value (for backward compatibility with existing parsing)
                    var value = string.IsNullOrEmpty(savedChar.WowRealm)
                        ? savedChar.CharName
                        : $"{savedChar.CharName} {savedChar.WowRealm}";

                    results.Add(new AutocompleteResult(displayName, value));
                }

                // Step 2: Get guild roster characters (if guild is associated)
                try
                {
                    // Get guild association directly from database
                    NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
                    string discordGuildName = context.Guild?.Name ?? context.User.Username;

                    using (var db = new NinjaBotEntities())
                    {
                        var foundGuild = db.WowGuildAssociations.FirstOrDefault(g => g.ServerName == discordGuildName);
                        if (foundGuild != null)
                        {
                            guildObject.guildName = foundGuild.WowGuild;
                            guildObject.realmName = foundGuild.WowRealm;
                            guildObject.realmSlug = foundGuild.LocalRealmSlug;
                            guildObject.regionName = foundGuild.WowRegion;
                            guildObject.locale = foundGuild.Locale;
                        }
                    }

                    if (!string.IsNullOrEmpty(guildObject.guildName) && !string.IsNullOrEmpty(guildObject.realmName))
                    {
                        // Create cache key based on guild+realm+region
                        var cacheKey = $"guild_roster_{guildObject.regionName}_{guildObject.realmName}_{guildObject.guildName}".ToLower();

                        // Try to get from cache first
                        GuildMembers guildMembers = _cache.Get<GuildMembers>(cacheKey);

                        if (guildMembers == null)
                        {
                            // Cache miss - fetch from API
                            logger?.LogInformation("Cache miss for guild roster: {CacheKey}. Fetching from WoW API.", cacheKey);

                            // Fetch guild members based on locale/region
                            if (!string.IsNullOrEmpty(guildObject.locale))
                            {
                                guildMembers = wowApi.GetGuildMembersBySlug(
                                    guildObject.realmName,
                                    guildObject.guildName,
                                    locale: guildObject.locale,
                                    regionName: guildObject.regionName);
                            }
                            else
                            {
                                guildMembers = wowApi.GetGuildMembersBySlug(
                                    guildObject.realmName,
                                    guildObject.guildName,
                                    regionName: guildObject.regionName);
                            }

                            // Cache for 15 minutes
                            if (guildMembers != null)
                            {
                                var cacheOptions = new MemoryCacheEntryOptions
                                {
                                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
                                    Size = 1 // Count each entry as size 1 for the size limit
                                };
                                _cache.Set(cacheKey, guildMembers, cacheOptions);
                                logger?.LogInformation("Cached guild roster: {CacheKey} (expires in 15 minutes)", cacheKey);
                            }
                        }
                        else
                        {
                            logger?.LogDebug("Cache hit for guild roster: {CacheKey}", cacheKey);
                        }

                        if (guildMembers?.members != null)
                        {
                            // Filter guild members by user input and exclude already-saved characters
                            var savedCharNames = savedChars.Select(c => c.CharName.ToLower()).ToHashSet();

                            var guildCharResults = guildMembers.members
                                .Where(m =>
                                    m.character != null &&
                                    m.character.realm != null &&
                                    !savedCharNames.Contains(m.character.name.ToLower()) && // Not already saved
                                    (string.IsNullOrWhiteSpace(userInput) || m.character.name.ToLower().Contains(userInput))) // Matches input
                                .OrderByDescending(m => m.character.level) // Prioritize max level chars
                                .ThenBy(m => m.character.name) // Then alphabetical
                                .Take(25 - results.Count) // Fill remaining slots up to Discord's 25 limit
                                .Select(m => new AutocompleteResult(
                                    $"{m.character.name} ({m.character.realm.slug})",
                                    $"{m.character.name} {m.character.realm.slug}"))
                                .ToList();

                            results.AddRange(guildCharResults);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error fetching guild roster for autocomplete. Showing saved characters only.");
                    // Continue with just saved characters
                }

                // Return results (empty list is valid - allows free text entry)
                return AutocompletionResult.FromSuccess(results.Take(25));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error in GuildCharAutocomplete");
                // Return empty result to allow free text entry
                return AutocompletionResult.FromSuccess(results);
            }
        }
    }
}