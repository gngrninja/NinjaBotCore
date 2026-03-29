using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Repositories;

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
            var charRepo = services.GetRequiredService<IRepository<WowCharAssociation>>();
            var chars = await charRepo.WhereAsync(a => a.UserId == (long)context.User.Id);
            return AutocompletionResult.FromSuccess(chars.Select(c => new AutocompleteResult(c.CharName, c.Id.ToString())));
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
                var charRepo = services.GetRequiredService<IRepository<WowCharAssociation>>();
                var savedChars = await charRepo.WhereAsync(a => a.UserId == (long)context.User.Id);

                // Filter saved chars by user input if provided
                var filteredSavedChars = string.IsNullOrWhiteSpace(userInput)
                    ? savedChars
                    : savedChars.Where(c => c.CharName.ToLower().Contains(userInput)).ToList();

                // Add saved characters with ★ marker
                foreach (var savedChar in filteredSavedChars.Take(8)) // Limit saved to 8 to leave room for history
                {
                    var displayName = savedChar.IsMain
                        ? $"★ {savedChar.CharName} ({savedChar.WowRealm}) [MAIN]"
                        : $"★ {savedChar.CharName} ({savedChar.WowRealm})";

                    // Include region in value for cross-region support (use ~ delimiter to handle realms with spaces)
                    var value = string.IsNullOrEmpty(savedChar.WowRealm)
                        ? savedChar.CharName
                        : $"{savedChar.CharName}~{savedChar.WowRealm}~{savedChar.WowRegion ?? "us"}";

                    results.Add(new AutocompleteResult(displayName, value));
                }

                // Step 1.5: Get search history (recent/frequent searches not already in saved chars)
                try
                {
                    var wowCache = services.GetRequiredService<WowCacheService>();
                    var searchHistory = await wowCache.GetRioSearchHistoryAsync((long)context.User.Id);

                    // Take top 5
                    searchHistory = searchHistory
                        .Take(5)
                        .ToList();

                    // Filter by user input and exclude saved characters
                    var filteredHistory = searchHistory
                        .Where(h => !savedChars.Any(c =>
                            c.CharName.Equals(h.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                            c.WowRealm.Equals(h.RealmName, StringComparison.OrdinalIgnoreCase)))
                        .Where(h => string.IsNullOrWhiteSpace(userInput) || h.CharacterName.ToLower().Contains(userInput))
                        .Take(3) // Limit to 3 history items
                        .ToList();

                    foreach (var history in filteredHistory)
                    {
                        var displayName = $"🕐 {history.CharacterName} ({history.RealmName}) [{history.Region.ToUpper()}]";
                        var value = $"{history.CharacterName}~{history.RealmName}~{history.Region}";
                        results.Add(new AutocompleteResult(displayName, value));
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error fetching search history for autocomplete");
                    // Continue without history
                }

                // Step 2: Get guild roster characters (if guild is associated)
                try
                {
                    // Get guild association directly from database
                    NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
                    string discordGuildName = context.Guild?.Name ?? context.User.Username;

                    var guildRepo = services.GetRequiredService<IRepository<WowGuildAssociations>>();
                    var foundGuild = await guildRepo.FirstOrDefaultAsync(g => g.ServerName == discordGuildName);
                    if (foundGuild != null)
                    {
                        guildObject.guildName = foundGuild.WowGuild;
                        guildObject.realmName = foundGuild.WowRealm;
                        guildObject.realmSlug = foundGuild.LocalRealmSlug;
                        guildObject.regionName = foundGuild.WowRegion;
                        guildObject.locale = foundGuild.Locale;
                    }

                    if (!string.IsNullOrEmpty(guildObject.guildName) && !string.IsNullOrEmpty(guildObject.realmName))
                    {
                        // Ensure we have a valid realm slug (fallback to slugifying realmName if not set)
                        if (string.IsNullOrEmpty(guildObject.realmSlug))
                        {
                            guildObject.realmSlug = guildObject.realmName.ToLower().Replace(" ", "-").Replace("'", "");
                        }

                        // Create cache key based on guild+realm+region
                        var cacheKey = $"guild_roster_{guildObject.regionName}_{guildObject.realmSlug}_{guildObject.guildName}".ToLower();
                        var staleCacheKey = $"{cacheKey}_stale";

                        // Try to get from cache first (primary cache)
                        GuildMembers guildMembers = _cache.Get<GuildMembers>(cacheKey);

                        if (guildMembers == null)
                        {
                            // Primary cache miss - try stale cache (allows serving expired data)
                            guildMembers = _cache.Get<GuildMembers>(staleCacheKey);

                            if (guildMembers == null)
                            {
                                // No cache at all (cold start) - must fetch synchronously
                                logger?.LogInformation("Cold cache miss for guild roster: {CacheKey}. Fetching from WoW API.", cacheKey);

                                try
                                {
                                    // Fetch guild members based on locale/region
                                    if (!string.IsNullOrEmpty(guildObject.locale))
                                    {
                                        guildMembers = await wowApi.GetGuildMembersBySlugAsync(
                                            guildObject.realmSlug,
                                            guildObject.guildName,
                                            locale: guildObject.locale,
                                            regionName: guildObject.regionName);
                                    }
                                    else
                                    {
                                        guildMembers = await wowApi.GetGuildMembersBySlugAsync(
                                            guildObject.realmSlug,
                                            guildObject.guildName,
                                            regionName: guildObject.regionName);
                                    }

                                    // Cache for 12 hours in both primary and stale caches
                                    if (guildMembers != null)
                                    {
                                        var primaryCacheOptions = new MemoryCacheEntryOptions
                                        {
                                            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12),
                                            Size = 1
                                        };
                                        var staleCacheOptions = new MemoryCacheEntryOptions
                                        {
                                            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7), // Keep stale data for 7 days
                                            Size = 1
                                        };

                                        _cache.Set(cacheKey, guildMembers, primaryCacheOptions);
                                        _cache.Set(staleCacheKey, guildMembers, staleCacheOptions);
                                        logger?.LogInformation("Cached guild roster: {CacheKey} (expires in 12 hours)", cacheKey);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.LogWarning(ex, "Failed to fetch guild roster from API for {CacheKey}", cacheKey);
                                    // Continue without guild roster data
                                }
                            }
                            else
                            {
                                // Serving stale data while we refresh in the background
                                logger?.LogInformation("Serving stale cache for {CacheKey}, refreshing in background", cacheKey);

                                // Trigger background refresh (fire and forget)
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        GuildMembers freshData = null;

                                        if (!string.IsNullOrEmpty(guildObject.locale))
                                        {
                                            freshData = await wowApi.GetGuildMembersBySlugAsync(
                                                guildObject.realmSlug,
                                                guildObject.guildName,
                                                locale: guildObject.locale,
                                                regionName: guildObject.regionName);
                                        }
                                        else
                                        {
                                            freshData = await wowApi.GetGuildMembersBySlugAsync(
                                                guildObject.realmSlug,
                                                guildObject.guildName,
                                                regionName: guildObject.regionName);
                                        }

                                        if (freshData != null)
                                        {
                                            var primaryCacheOptions = new MemoryCacheEntryOptions
                                            {
                                                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12),
                                                Size = 1
                                            };
                                            var staleCacheOptions = new MemoryCacheEntryOptions
                                            {
                                                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7),
                                                Size = 1
                                            };

                                            _cache.Set(cacheKey, freshData, primaryCacheOptions);
                                            _cache.Set(staleCacheKey, freshData, staleCacheOptions);
                                            logger?.LogInformation("Background refresh complete for {CacheKey}", cacheKey);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogWarning(ex, "Background refresh failed for {CacheKey}", cacheKey);
                                    }
                                });
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
                                    $"{m.character.name}~{m.character.realm.slug}~{guildObject.regionName ?? "us"}"))
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
    /// Uses database-backed realm data from WowStaticDataService for reliable results
    /// Falls back to WowApi static data if database is empty
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
                var staticDataService = services.GetService<WowStaticDataService>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();

                // Get region parameter value to determine which realm list to use
                var regionParam = autocompleteInteraction.Data.Options.FirstOrDefault(o => o.Name == "region");
                var region = (regionParam?.Value as string ?? "us").ToLower();

                // Map region to uppercase for database query
                var dbRegion = region.ToUpper();

                // Try to get realms from database first (more reliable, retail only)
                if (staticDataService != null)
                {
                    var dbRealms = await staticDataService.GetRetailRealmsByRegionAsync(dbRegion);

                    if (dbRealms.Count > 0)
                    {
                        var filteredRealms = dbRealms
                            .Where(r => string.IsNullOrWhiteSpace(userInput) ||
                                        r.Name.ToLower().Contains(userInput) ||
                                        r.Slug.ToLower().Contains(userInput))
                            .OrderBy(r => r.Name)
                            .Take(25)
                            .Select(r => new AutocompleteResult(r.Name, r.Slug))
                            .ToList();

                        return AutocompletionResult.FromSuccess(filteredRealms);
                    }

                    logger?.LogDebug("No realms in database for region {Region}, falling back to WowApi static data", region);
                }

                // Fallback to WowApi static data (startup-loaded)
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
                var filteredStaticRealms = realms
                    .Where(r => string.IsNullOrWhiteSpace(userInput) ||
                                r.name.ToLower().Contains(userInput) ||
                                r.slug.ToLower().Contains(userInput))
                    .OrderBy(r => r.name)
                    .Take(25)
                    .Select(r => new AutocompleteResult(r.name, r.slug))
                    .ToList();

                return AutocompletionResult.FromSuccess(filteredStaticRealms);
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
    /// Autocomplete handler for Classic WoW realms.
    /// Queries database-backed Classic realm list (GameVersion == "Classic").
    /// </summary>
    public class ClassicRealmAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var logger = services.GetService<ILogger<ClassicRealmAutocomplete>>();
                var staticDataService = services.GetService<WowStaticDataService>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();

                // Get region parameter value (default to US)
                var regionParam = autocompleteInteraction.Data.Options.FirstOrDefault(o => o.Name == "region");
                var region = (regionParam?.Value as string ?? "us").ToUpper();

                if (staticDataService == null)
                {
                    logger?.LogWarning("WowStaticDataService not available for ClassicRealmAutocomplete");
                    return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
                }

                var classicRealms = await staticDataService.GetClassicRealmsByRegionAsync(region);

                var filteredRealms = classicRealms
                    .Where(r => string.IsNullOrWhiteSpace(userInput) ||
                                r.Name.ToLower().Contains(userInput) ||
                                r.Slug.ToLower().Contains(userInput))
                    .OrderBy(r => r.Name)
                    .Take(25)
                    .Select(r => new AutocompleteResult(r.Name, r.Slug))
                    .ToList();

                return AutocompletionResult.FromSuccess(filteredRealms);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<ClassicRealmAutocomplete>>();
                logger?.LogError(ex, "Error in ClassicRealmAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete handler for Classic WoW character names.
    /// Shows the user's recent Classic character lookups from search history.
    /// </summary>
    public class ClassicCharAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var wowCache = services.GetService<WowCacheService>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();

                if (wowCache == null)
                {
                    return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
                }

                var history = await wowCache.GetClassicSearchHistoryAsync((long)context.User.Id);

                var suggestions = history
                    .Where(h => string.IsNullOrWhiteSpace(userInput) ||
                                h.CharacterName.ToLower().Contains(userInput))
                    .Take(25)
                    .Select(h => new AutocompleteResult(
                        $"\U0001F552 {h.CharacterName} ({h.RealmName}) [{(h.Region?.ToUpper() ?? "?")}]",
                        $"{h.CharacterName}~{h.RealmName}~{h.Region ?? "us"}"))
                    .ToList();

                return AutocompletionResult.FromSuccess(suggestions);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<ClassicCharAutocomplete>>();
                logger?.LogError(ex, "Error in ClassicCharAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete handler for user's watched realms
    /// Shows only realms the user has active watch subscriptions for
    /// </summary>
    public class WatchedRealmAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();

                // Get region parameter if provided
                var regionParam = autocompleteInteraction.Data.Options.FirstOrDefault(o => o.Name == "region");
                var region = (regionParam?.Value as string ?? "").ToLower();

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotCore.Database.NinjaBotEntities>();

                // Query user's watched realms
                var query = db.RealmWatchSubscriptions
                    .Where(s => s.UserId == (long)context.User.Id);

                // Filter by region if specified
                if (!string.IsNullOrEmpty(region))
                {
                    query = query.Where(s => s.Region.ToLower() == region);
                }

                var subscriptions = await query
                    .OrderBy(s => s.RealmName)
                    .ToListAsync();

                if (!subscriptions.Any())
                {
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult("No watched realms - use /realm-watch add first", "none")
                    });
                }

                // Filter by user input
                var filtered = subscriptions
                    .Where(s => string.IsNullOrWhiteSpace(userInput) ||
                                s.RealmName.ToLower().Contains(userInput) ||
                                s.RealmSlug.ToLower().Contains(userInput))
                    .Take(25)
                    .Select(s => new AutocompleteResult($"{s.RealmName} ({s.Region.ToUpper()})", s.RealmSlug))
                    .ToList();

                return AutocompletionResult.FromSuccess(filtered);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<WatchedRealmAutocomplete>>();
                logger?.LogError(ex, "Error in WatchedRealmAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete handler for removing realm watches.
    /// Shows both user's DM watches and guild channel watches (for admins) with clear labels.
    /// Value format: "type~realmSlug~region" (e.g., "dm~area-52~us" or "channel~area-52~us")
    /// </summary>
    public class WatchedRealmRemoveAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();
                var isAdmin = (context.User as IGuildUser)?.GuildPermissions.Administrator ?? false;

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotCore.Database.NinjaBotEntities>();

                var results = new List<AutocompleteResult>();

                // Get user's DM watches (ChannelId is null for DM watches)
                var dmWatches = await db.RealmWatchSubscriptions
                    .Where(s => s.UserId == (long)context.User.Id && !s.ChannelId.HasValue)
                    .OrderBy(s => s.RealmName)
                    .ToListAsync();

                // Get guild's channel watches (admins only)
                var channelWatches = isAdmin
                    ? await db.RealmWatchSubscriptions
                        .Where(s => s.GuildId == (long)context.Guild.Id && s.ChannelId.HasValue)
                        .OrderBy(s => s.RealmName)
                        .ToListAsync()
                    : new List<NinjaBotCore.Database.RealmWatchSubscription>();

                // Add channel watches first (server-wide)
                foreach (var sub in channelWatches)
                {
                    var label = $"{sub.RealmName} ({sub.Region.ToUpper()}) - Channel";
                    if (string.IsNullOrWhiteSpace(userInput) ||
                        sub.RealmName.ToLower().Contains(userInput) ||
                        sub.RealmSlug.ToLower().Contains(userInput))
                    {
                        // Encode type~realmSlug~region in value
                        results.Add(new AutocompleteResult(label, $"channel~{sub.RealmSlug}~{sub.Region}"));
                    }
                }

                // Add DM watches
                foreach (var sub in dmWatches)
                {
                    var label = $"{sub.RealmName} ({sub.Region.ToUpper()}) - DM";
                    if (string.IsNullOrWhiteSpace(userInput) ||
                        sub.RealmName.ToLower().Contains(userInput) ||
                        sub.RealmSlug.ToLower().Contains(userInput))
                    {
                        results.Add(new AutocompleteResult(label, $"dm~{sub.RealmSlug}~{sub.Region}"));
                    }
                }

                if (!results.Any())
                {
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult("No watches found - use /realm-watch add first", "none")
                    });
                }

                return AutocompletionResult.FromSuccess(results.Take(25));
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<WatchedRealmRemoveAutocomplete>>();
                logger?.LogError(ex, "Error in WatchedRealmRemoveAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete handler for WoW guild search
    /// Searches for guilds by name across all realms in the selected region
    /// Uses database-backed realm data with fallback to WowApi static data
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
                var staticDataService = services.GetService<WowStaticDataService>();
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
                var dbRegion = region.ToUpper();

                var results = new List<AutocompleteResult>();
                var foundGuilds = new HashSet<string>(); // Track unique guild+realm combos

                // Try to get realms from database first
                List<(string slug, string name, string population)> realmsToSearch = new();

                if (staticDataService != null)
                {
                    var dbRealms = await staticDataService.GetRetailRealmsByRegionAsync(dbRegion);

                    if (dbRealms.Count > 0)
                    {
                        realmsToSearch = dbRealms
                            .OrderByDescending(r => r.Population == "full" ? 4 :
                                                    r.Population == "high" ? 3 :
                                                    r.Population == "medium" ? 2 :
                                                    r.Population == "low" ? 1 : 0)
                            .ThenBy(r => r.Name)
                            .Take(100)
                            .Select(r => (r.Slug, r.Name, r.Population))
                            .ToList();
                    }
                }

                // Fallback to WowApi static data if no database realms
                if (realmsToSearch.Count == 0)
                {
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

                    realmsToSearch = realms
                        .OrderByDescending(r => r.population == "full" ? 4 :
                                               r.population == "high" ? 3 :
                                               r.population == "medium" ? 2 :
                                               r.population == "low" ? 1 : 0)
                        .ThenBy(r => r.name)
                        .Take(100)
                        .Select(r => (r.slug, r.name, r.population))
                        .ToList();
                }

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

                        var guildMembers = await wowApi.GetGuildMembersBySlugAsync(realm.slug, userInput, locale, region);

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

    /// <summary>
    /// Autocomplete handler for WarcraftLogs encounter/boss names.
    /// Fetches the current raid tier and returns matching encounters.
    /// Uses WarcraftLogsV2Client's internal cache (10hr TTL).
    /// </summary>
    public class EncounterAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var logger = services.GetService<ILogger<EncounterAutocomplete>>();
                var logsApi = services.GetRequiredService<WarcraftLogsV2Client>();
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").ToLower().Trim();

                // GetCurrentRaidTierAsync has its own 10hr cache in WarcraftLogsV2Client
                WclV2ZoneDetail currentTier;
                try
                {
                    currentTier = await logsApi.GetCurrentRaidTierAsync();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to fetch current raid tier for autocomplete");
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult("Unable to load encounters - try again", "error")
                    });
                }

                if (currentTier?.Encounters == null || !currentTier.Encounters.Any())
                {
                    return AutocompletionResult.FromSuccess(new[]
                    {
                        new AutocompleteResult("No encounters available", "error")
                    });
                }

                // Filter encounters by user input (fuzzy match)
                var encounters = currentTier.Encounters
                    .Select((e, index) => new { Encounter = e, Index = index + 1 })
                    .Where(e => string.IsNullOrWhiteSpace(userInput) ||
                                e.Encounter.Name.ToLower().Contains(userInput) ||
                                e.Index.ToString() == userInput)
                    .Take(25)
                    .Select(e => new AutocompleteResult(
                        $"{e.Index}. {e.Encounter.Name}",
                        e.Encounter.Id.ToString()))
                    .ToList();

                // If no input, show all encounters with raid name header
                if (string.IsNullOrWhiteSpace(userInput) && encounters.Any())
                {
                    // Insert raid name as first non-selectable hint
                    encounters.Insert(0, new AutocompleteResult(
                        $"── {currentTier.Name} ──",
                        currentTier.Encounters.First().Id.ToString()));
                }

                return AutocompletionResult.FromSuccess(encounters);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<EncounterAutocomplete>>();
                logger?.LogError(ex, "Error in EncounterAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete for craftable items from synced Blizzard profession data.
    /// Queries the CraftableItems table with case-insensitive search.
    /// Falls back to typed text if no matches found (preserves free-text entry).
    /// </summary>
    public class CraftableItemAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim();

                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                if (userInput.Length < 2)
                {
                    // Show popular professions as hints when user hasn't typed yet
                    var recentItems = await db.CraftableItems
                        .OrderBy(c => c.RecipeName)
                        .Take(24)
                        .Select(c => new { c.RecipeName, c.Profession })
                        .ToListAsync();

                    var hints = new List<AutocompleteResult>
                    {
                        new("Start typing an item name, or enter any name", string.IsNullOrEmpty(userInput) ? " " : userInput)
                    };
                    hints.AddRange(recentItems.Select(m =>
                    {
                        var displayName = $"{m.RecipeName} ({m.Profession})";
                        if (displayName.Length > 100) displayName = displayName[..100];
                        var value = m.RecipeName.Length > 100 ? m.RecipeName[..100] : m.RecipeName;
                        return new AutocompleteResult(displayName, value);
                    }));

                    return AutocompletionResult.FromSuccess(hints);
                }

                // Escape LIKE wildcards in user input
                var escaped = userInput.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

                var matches = await db.CraftableItems
                    .Where(c => EF.Functions.ILike(c.RecipeName, $"%{escaped}%", "\\"))
                    .OrderBy(c => c.RecipeName)
                    .Take(25)
                    .Select(c => new { c.RecipeName, c.Profession })
                    .ToListAsync();

                if (!matches.Any())
                {
                    return AutocompletionResult.FromSuccess(
                        new[] { new AutocompleteResult($"{userInput} (custom item)", userInput) });
                }

                var results = matches.Select(m =>
                {
                    // Discord autocomplete: name max 100 chars, value max 100 chars
                    var displayName = $"{m.RecipeName} ({m.Profession})";
                    if (displayName.Length > 100) displayName = $"{m.RecipeName[..Math.Min(m.RecipeName.Length, 90)]}... ({m.Profession})";
                    if (displayName.Length > 100) displayName = displayName[..100];
                    var value = m.RecipeName.Length > 100 ? m.RecipeName[..100] : m.RecipeName;
                    return new AutocompleteResult(displayName, value);
                });

                return AutocompletionResult.FromSuccess(results);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<CraftableItemAutocomplete>>();
                logger?.LogError(ex, "Error in CraftableItemAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// Autocomplete for a user's own active craft tickets (for /craft cancel).
    /// Shows tickets the user can cancel, with item name and status.
    /// </summary>
    public class CraftTicketAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var userId = (long)context.User.Id;
                var guildId = (long)context.Guild.Id;
                var userInput = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim();

                var query = db.CraftTickets
                    .Where(t => t.RequesterId == userId
                                && t.GuildId == guildId
                                && CraftConstants.ActiveStatuses.Contains(t.Status));

                if (!string.IsNullOrEmpty(userInput))
                {
                    var escaped = userInput.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                    query = query.Where(t => EF.Functions.ILike(t.ItemName, $"%{escaped}%", "\\"));
                }

                var tickets = await query
                    .OrderBy(t => t.CreatedAt)
                    .Take(25)
                    .Select(t => new { t.Id, t.ItemName, t.Status })
                    .ToListAsync();

                if (!tickets.Any())
                {
                    return AutocompletionResult.FromSuccess(
                        new[] { new AutocompleteResult("No active tickets to cancel", "0") });
                }

                var results = tickets.Select(t =>
                {
                    var display = $"#{t.Id} — {t.ItemName} ({t.Status})";
                    if (display.Length > 100) display = display[..100];
                    return new AutocompleteResult(display, t.Id.ToString());
                });

                return AutocompletionResult.FromSuccess(results);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<CraftTicketAutocomplete>>();
                logger?.LogError(ex, "Error in CraftTicketAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }
}