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

    /// <summary>
    /// Autocomplete handler for WoW realm selection
    /// Shows realms from the appropriate region based on user input
    /// </summary>
    public class RealmAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var logger = services.GetService<ILogger<RealmAutocomplete>>();
                var results = new List<AutocompleteResult>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();

                // Get region parameter value to determine which realm list to use
                var regionParam = autocompleteInteraction.Data.Options.FirstOrDefault(o => o.Name == "region");
                var region = (regionParam?.Value as string ?? "us").ToLower();

                // Select appropriate realm list
                WowRealm.Realm[] realms = region switch
                {
                    "eu" => WowApi.RealmInfoEu?.realms ?? Array.Empty<WowRealm.Realm>(),
                    "ru" => WowApi.RealmInfoRu?.realms ?? Array.Empty<WowRealm.Realm>(),
                    _ => WowApi.RealmInfo?.realms ?? Array.Empty<WowRealm.Realm>()
                };

                if (realms.Length == 0)
                {
                    logger?.LogWarning("No realm data available for region: {Region}", region);
                    return AutocompletionResult.FromSuccess(new[] { new AutocompleteResult("No realms available", "error") });
                }

                // Filter realms by user input
                var filteredRealms = realms
                    .Where(r => string.IsNullOrWhiteSpace(userInput) ||
                                r.name.ToLower().Contains(userInput) ||
                                r.slug.ToLower().Contains(userInput))
                    .OrderBy(r => r.name)
                    .Take(25)
                    .Select(r => new AutocompleteResult(r.name, r.slug))
                    .ToList();

                return await Task.FromResult(AutocompletionResult.FromSuccess(filteredRealms));
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<RealmAutocomplete>>();
                logger?.LogError(ex, "Error in RealmAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete handler for WoW guild search
    /// Searches for guilds by name across all realms in the selected region
    /// Shows results as "GuildName (RealmName)"
    /// </summary>
    public class GuildSearchAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var logger = services.GetService<ILogger<GuildSearchAutocomplete>>();
                var wowApi = services.GetRequiredService<WowApi>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim();

                // Need at least 2 characters to search
                if (string.IsNullOrWhiteSpace(userInput) || userInput.Length < 2)
                {
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult("Type at least 2 characters to search...", "search")
                    });
                }

                // Get region parameter to determine search scope
                var regionParam = autocompleteInteraction.Data.Options.FirstOrDefault(o => o.Name == "region");
                var region = (regionParam?.Value as string ?? "us").ToLower();

                // Get appropriate realm list for the region
                WowRealm.Realm[] realms = region switch
                {
                    "eu" => WowApi.RealmInfoEu?.realms ?? Array.Empty<WowRealm.Realm>(),
                    "ru" => WowApi.RealmInfoRu?.realms ?? Array.Empty<WowRealm.Realm>(),
                    _ => WowApi.RealmInfo?.realms ?? Array.Empty<WowRealm.Realm>()
                };

                if (realms.Length == 0)
                {
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult("No realm data available", "error")
                    });
                }

                var results = new List<AutocompleteResult>();
                var foundGuilds = new HashSet<string>(); // Track unique guild+realm combos

                // Search across all realms to find matches
                // Start with high-pop realms first for better results, then expand to all
                var realmsToSearch = realms
                    .OrderByDescending(r => r.population == "full" ? 4 :
                                           r.population == "high" ? 3 :
                                           r.population == "medium" ? 2 :
                                           r.population == "low" ? 1 : 0)
                    .ThenBy(r => r.name)
                    .Take(100) // Search up to 100 realms for better coverage
                    .ToList();

                foreach (var realm in realmsToSearch)
                {
                    if (results.Count >= 25) break; // Discord autocomplete limit

                    try
                    {
                        // Try to fetch guild info
                        var locale = region switch
                        {
                            "eu" => "en_GB",
                            "ru" => "ru_RU",
                            _ => "en_US"
                        };

                        var guildMembers = wowApi.GetGuildMembers(realm.slug, userInput, locale, region);

                        if (guildMembers?.guild != null)
                        {
                            var guildKey = $"{guildMembers.guild.name}|{guildMembers.guild.realm.slug}";

                            if (!foundGuilds.Contains(guildKey))
                            {
                                foundGuilds.Add(guildKey);
                                results.Add(new AutocompleteResult(
                                    $"{guildMembers.guild.name} ({guildMembers.guild.realm.slug})",
                                    guildKey));
                            }
                        }
                    }
                    catch
                    {
                        // Guild not found on this realm, continue searching
                        continue;
                    }
                }

                if (results.Count == 0)
                {
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult($"No guilds found matching '{userInput}'", "none")
                    });
                }

                return AutocompletionResult.FromSuccess(results);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<GuildSearchAutocomplete>>();
                logger?.LogError(ex, "Error in GuildSearchAutocomplete");
                return AutocompletionResult.FromSuccess(new[]
                {
                    new AutocompleteResult("Search error - please type full guild and realm names", "error")
                });
            }
        }
    }
}