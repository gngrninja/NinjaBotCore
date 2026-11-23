#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// What /forever autocomplete can offer. Blizzard has no search or list for Forever characters
    /// or guilds, so those suggestions come from three places: what the bot has saved, the server
    /// guild's roster on the armory, and asking the armory about the exact name being typed.
    /// Recipes come from Blizzard's game data and items from TradeSkillMaster's price list.
    /// </summary>
    public static class ForeverSuggest
    {
        public const int Max = 25;
        public const string LookupHint = "Type a first name and surname, like Asha Brightvale";

        /// <summary>Discord drops an autocomplete answer after three seconds.</summary>
        public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1800);

        // A character reaches the armory at this level.
        private const int ArmoryMinLevel = 10;
        private const int MinGuildNameLength = 3;

        /// <summary>
        /// The task's result if it arrives in time, otherwise null. A slow lookup keeps running so
        /// it lands in the armory's cache for the next keystroke.
        /// </summary>
        public static async Task<T?> WithinAsync<T>(Task<T> task, TimeSpan budget) where T : class
        {
            if (await Task.WhenAny(task, Task.Delay(budget)) == task)
            {
                try
                {
                    return await task;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }

        public static string Current(IAutocompleteInteraction interaction)
            => (interaction.Data.Current.Value?.ToString() ?? string.Empty).Trim();

        public static string? Option(IAutocompleteInteraction interaction, string name)
        {
            var value = interaction.Data.Options.FirstOrDefault(o => o.Name == name && !o.Focused)?.Value?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static ForeverRegion? Region(IAutocompleteInteraction interaction)
            => Enum.TryParse<ForeverRegion>(Option(interaction, "region"), ignoreCase: true, out var region)
               && Enum.IsDefined(region)
                ? region
                : null;

        public static Func<long, bool> IsMember(IInteractionContext context)
            => id => (context.Guild as SocketGuild)?.GetUser((ulong)id) != null;

        // ===== The caller's own characters =====

        public static IEnumerable<AutocompleteResult> Own(IReadOnlyList<Database.ForeverCharacter> characters, string input, int max)
            => characters
                .Where(c => input.Length == 0 || c.FullName.Contains(input, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(0, max))
                .Select(c => new AutocompleteResult(
                    Clip($"{c.FullName} — {(c.Class?.ToString() ?? "No class")} · {ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)}"),
                    c.Id.ToString()));

        // ===== Anyone, for armory lookups =====

        /// <summary>
        /// The typed name first (with what the armory said about it, when it answered), then the caller's
        /// own characters, then guildmates from Blizzard's roster and characters shared in this server.
        /// A saved character's value is its Id; everyone else's is the name.
        /// </summary>
        public static List<AutocompleteResult> Lookup(
            IReadOnlyList<Database.ForeverCharacter> own,
            string input,
            IReadOnlyList<ForeverArmoryMember>? roster = null,
            IReadOnlyList<Database.ForeverCharacter>? shared = null,
            ForeverLookup<ForeverArmoryCharacter>? live = null)
        {
            var results = new List<AutocompleteResult>();
            var taken = new HashSet<(string, string)>(own.Select(c => (c.NormalizedFirstName, c.NormalizedLastName)));

            if (ForeverNames.TrySplitFullName(input, out var first, out var last, out _)
                && !taken.Contains((ForeverNames.Normalize(first), ForeverNames.Normalize(last))))
            {
                var found = live is { IsFound: true } ? live.Value : null;
                var name = found != null && IsUsable(found.FirstName, found.Surname) ? found.FullName : ForeverNames.FullName(first, last);
                var label = found != null
                    ? Join(name, Kind(found.Level, found.Race, found.ClassName ?? found.Class?.ToString()), found.GuildName, "on the armory")
                    : $"Look up {name} on the armory";
                if (label.Length <= 100)
                {
                    results.Add(new AutocompleteResult(label, name));
                    taken.Add((ForeverNames.Normalize(first), ForeverNames.Normalize(last)));
                }
            }

            results.AddRange(Own(own, input, Max - results.Count));

            // The armory can't show a character below level 10, so there is nothing to look up yet.
            foreach (var m in Ranked(roster, input).Where(m => m.Level >= ArmoryMinLevel))
            {
                if (results.Count >= Max) break;
                if (!taken.Add((ForeverNames.Normalize(m.FirstName), ForeverNames.Normalize(m.Surname)))) continue;
                results.Add(new AutocompleteResult(Clip(Join(m.FullName, Kind(m.Level, null, m.Class?.ToString()), "guild roster")), m.FullName));
            }

            foreach (var c in (shared ?? Array.Empty<Database.ForeverCharacter>())
                         .Where(c => input.Length == 0 || c.FullName.Contains(input, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= Max) break;
                if (!IsUsable(c.FirstName, c.LastName) || !taken.Add((c.NormalizedFirstName, c.NormalizedLastName))) continue;
                results.Add(new AutocompleteResult(Clip(Join(c.FullName, c.Class?.ToString(), "shared in this server")), c.FullName));
            }

            if (results.Count == 0) results.Add(new AutocompleteResult(LookupHint, "0"));
            return results;
        }

        // ===== A character to save =====

        /// <summary>
        /// The typed name first (with what the armory said about it, when it answered), then guildmates
        /// from Blizzard's roster the caller hasn't saved yet. Guildmates below level 10 are offered too:
        /// the armory doesn't have them, but the roster does. Every value is a full name.
        /// </summary>
        public static List<AutocompleteResult> Add(
            IReadOnlyList<Database.ForeverCharacter> own,
            ForeverRegion? region,
            string input,
            IReadOnlyList<ForeverArmoryMember>? roster = null,
            ForeverLookup<ForeverArmoryCharacter>? live = null)
        {
            var results = new List<AutocompleteResult>();
            var taken = own.Where(c => region == null || c.Region == region)
                .Select(c => (c.NormalizedFirstName, c.NormalizedLastName)).ToHashSet();

            if (ForeverNames.TrySplitFullName(input, out var first, out var last, out _))
            {
                var key = (ForeverNames.Normalize(first), ForeverNames.Normalize(last));
                var found = live is { IsFound: true } ? live.Value : null;
                var name = found != null && IsUsable(found.FirstName, found.Surname) ? found.FullName : ForeverNames.FullName(first, last);
                var label = taken.Contains(key)
                    ? $"{name} · already saved"
                    : found != null
                        ? Join(name, Kind(found.Level, found.Race, found.ClassName ?? found.Class?.ToString()), found.GuildName, "on the armory")
                        : $"Save {name}";
                if (label.Length <= 100)
                {
                    results.Add(new AutocompleteResult(label, name));
                    taken.Add(key);
                }
            }

            foreach (var m in Ranked(roster, input))
            {
                if (results.Count >= Max) break;
                if (!taken.Add((ForeverNames.Normalize(m.FirstName), ForeverNames.Normalize(m.Surname)))) continue;
                results.Add(new AutocompleteResult(Clip(Join(m.FullName, Kind(m.Level, null, m.Class?.ToString()), "guild roster")), m.FullName));
            }

            if (results.Count == 0) results.Add(new AutocompleteResult(LookupHint, "0"));
            return results;
        }

        // ===== Recipes and items =====

        public const string RecipeHint = "Type part of a recipe's name, like Linen Bag";
        public const string ItemHint = "Type part of an item's name, like Copper Ore";
        public const string LoadingHint = "Still loading the list. Type another letter in a moment";

        /// <summary>Recipes whose name contains the text, names that start with it first. The value is the recipe's id.</summary>
        public static List<AutocompleteResult> Recipes(ForeverRecipeList? recipes, string input)
        {
            if (recipes == null) return new List<AutocompleteResult> { new(LoadingHint, "0") };
            if (input.Length == 0) return new List<AutocompleteResult> { new(RecipeHint, "0") };

            var results = recipes.Recipes
                .Where(r => r.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.Name.Length)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Max)
                .Select(r => new AutocompleteResult(Clip(Join(r.Name, r.ProfessionName)), r.Id.ToString()))
                .ToList();
            if (results.Count == 0) results.Add(new AutocompleteResult($"No recipe has \"{Clip(input, 60)}\" in its name", "0"));
            return results;
        }

        /// <summary>Priced items whose name contains the text: names that start with it first, then what sells most. The value is the item's id.</summary>
        public static List<AutocompleteResult> Items(ForeverPriceSheet? prices, string input)
        {
            if (prices == null) return new List<AutocompleteResult> { new(LoadingHint, "0") };
            if (input.Length == 0) return new List<AutocompleteResult> { new(ItemHint, "0") };

            var results = prices.Items.Values
                .Where(p => p.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(p => p.SoldPerDay)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Max)
                .Select(p => new AutocompleteResult(Clip(Join(p.Name, ForeverMoney.Format(p.MarketValue))), p.ItemId.ToString()))
                .ToList();
            if (results.Count == 0) results.Add(new AutocompleteResult($"No priced item has \"{Clip(input, 60)}\" in its name", "0"));
            return results;
        }

        // ===== Guild names =====

        /// <summary>
        /// Guilds the armory confirmed for the typed name, then the guilds of the caller's own characters,
        /// then the name as typed when nothing matched it. Every value is a guild name.
        /// </summary>
        /// <param name="checkedEverywhere">Every region asked answered, so "not found" is the armory's answer and not a timeout.</param>
        public static List<AutocompleteResult> Guilds(
            string input,
            IReadOnlyList<ForeverArmoryGuild> found,
            IReadOnlyList<ForeverArmoryCharacter> ownOnArmory,
            bool checkedEverywhere)
        {
            var results = new List<AutocompleteResult>();
            var taken = new HashSet<(ForeverRegion, string)>();

            foreach (var g in found.OrderBy(g => g.Region))
            {
                if (!ForeverNames.TryCleanGuildName(g.Name, out var name, out _) || !taken.Add((g.Region, ForeverNames.Slug(name)))) continue;
                results.Add(new AutocompleteResult(
                    Clip(Join(name, g.Region.ToString(), g.Ruleset?.Name() ?? g.RulesetName, g.Faction?.ToString(),
                        g.MemberCount > 0 ? $"{g.MemberCount:N0} member{(g.MemberCount == 1 ? "" : "s")}" : null)),
                    name));
            }

            foreach (var c in ownOnArmory
                         .Where(c => !string.IsNullOrWhiteSpace(c.GuildName))
                         .Where(c => input.Length == 0 || c.GuildName!.Contains(input, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(c => c.GuildName, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= Max) break;
                if (!ForeverNames.TryCleanGuildName(c.GuildName, out var name, out _) || !taken.Add((c.Region, ForeverNames.Slug(name)))) continue;
                results.Add(new AutocompleteResult(Clip(Join(name, c.Region.ToString(), $"{c.FullName}'s guild")), name));
            }

            if (found.Count == 0 && ForeverNames.TryCleanGuildName(input, out var typed, out _)
                && results.All(r => !string.Equals((string)r.Value, typed, StringComparison.OrdinalIgnoreCase)))
            {
                var note = checkedEverywhere && typed.Length >= MinGuildNameLength ? " · not found on the armory" : "";
                results.Add(new AutocompleteResult(Clip(typed, 100 - note.Length) + note, typed));
            }
            return results;
        }

        /// <summary>Whether a guild name is long enough to be worth asking Blizzard about on every pause in typing.</summary>
        public static bool WorthAsking(string guildName) => guildName.Trim().Length >= MinGuildNameLength;

        // ===== Helpers =====

        /// <summary>Roster members matching the input: names that start with it first, then by rank and level.</summary>
        private static IEnumerable<ForeverArmoryMember> Ranked(IReadOnlyList<ForeverArmoryMember>? roster, string input)
            => (roster ?? Array.Empty<ForeverArmoryMember>())
                .Where(m => IsUsable(m.FirstName, m.Surname))
                .Where(m => input.Length == 0 || m.FullName.Contains(input, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => input.Length > 0
                    && (m.FullName.StartsWith(input, StringComparison.OrdinalIgnoreCase) || m.Surname.StartsWith(input, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(m => m.Rank ?? int.MaxValue)
                .ThenByDescending(m => m.Level)
                .ThenBy(m => m.FullName, StringComparer.OrdinalIgnoreCase);

        /// <summary>A name the commands can read back from "First Surname".</summary>
        private static bool IsUsable(string first, string surname)
            => ForeverNames.TrySplitFullName(ForeverNames.FullName(first, surname), out _, out _, out _);

        private static string? Kind(int level, string? race, string? className)
        {
            var parts = new[] { level > 0 ? level.ToString() : null, race, className }.Where(p => !string.IsNullOrWhiteSpace(p));
            var kind = string.Join(" ", parts);
            return kind.Length == 0 ? null : kind;
        }

        private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        private static string Clip(string label, int max = 100) => label.Length <= max ? label : label[..Math.Max(0, max)];
    }

    /// <summary>
    /// For armory lookups: the caller's own characters (value: the Id), then anyone else by name:
    /// the name being typed, guildmates from Blizzard's roster and characters shared in this server.
    /// </summary>
    public class ForeverLookupAutocomplete : AutocompleteHandler
    {
        internal const string Hint = ForeverSuggest.LookupHint;

        internal static List<AutocompleteResult> Suggestions(IReadOnlyList<Database.ForeverCharacter> characters, string input)
            => ForeverSuggest.Lookup(characters, input);

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var forever = services.GetRequiredService<ForeverService>();
                var armory = services.GetRequiredService<IForeverArmory>();
                var input = ForeverSuggest.Current(autocompleteInteraction);
                var userId = (long)context.User.Id;

                var characters = await forever.GetCharactersAsync(userId);
                var guild = context.Guild == null ? null : await forever.GetGuildAsync((long)context.Guild.Id);
                var region = ForeverSuggest.Region(autocompleteInteraction) ?? guild?.Region;

                // The roster is the server's own guild, so it only fits lookups in that guild's region.
                var rosterTask = guild != null && region == guild.Region
                    ? forever.GetRosterAsync((long)guild.DiscordGuildId, ForeverSuggest.IsMember(context))
                    : Task.FromResult(ForeverResult<ForeverRoster>.Fail("no roster"));
                var liveTask = region != null && ForeverNames.TrySplitFullName(input, out var first, out var last, out _)
                    ? armory.GetCharacterAsync(region.Value, first, last)
                    : Task.FromResult(ForeverLookup<ForeverArmoryCharacter>.NotFound());

                var roster = (await ForeverSuggest.WithinAsync(rosterTask, ForeverSuggest.Budget))?.Value;
                // Both started together, so the second wait is only whatever is left of the first.
                var live = await ForeverSuggest.WithinAsync(liveTask, TimeSpan.FromMilliseconds(200));

                return AutocompletionResult.FromSuccess(ForeverSuggest.Lookup(
                    characters,
                    input,
                    roster?.Armory?.Members,
                    roster?.Members.SelectMany(m => m.Characters).Where(c => c.UserId != userId).ToList(),
                    live));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverLookupAutocomplete>>()?.LogError(ex, "Error in ForeverLookupAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// For /forever char add: the name being typed, checked against the armory, and guildmates from
    /// Blizzard's roster of this server's guild, so a member can pick their character instead of spelling it.
    /// </summary>
    public class ForeverAddAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var forever = services.GetRequiredService<ForeverService>();
                var armory = services.GetRequiredService<IForeverArmory>();
                var input = ForeverSuggest.Current(autocompleteInteraction);
                var guild = context.Guild == null ? null : await forever.GetGuildAsync((long)context.Guild.Id);
                var region = ForeverSuggest.Region(autocompleteInteraction) ?? guild?.Region;
                var own = await forever.GetCharactersAsync((long)context.User.Id);

                // The roster is the server's own guild, so it only fits a character in that guild's region.
                var rosterTask = guild?.ArmorySlug != null && region == guild.Region
                    ? armory.GetRosterAsync(guild.Region, guild.ArmorySlug)
                    : Task.FromResult(ForeverLookup<ForeverArmoryRoster>.NotFound());
                var liveTask = region != null && ForeverNames.TrySplitFullName(input, out var first, out var last, out _)
                    ? armory.GetCharacterAsync(region.Value, first, last)
                    : Task.FromResult(ForeverLookup<ForeverArmoryCharacter>.NotFound());

                var roster = await ForeverSuggest.WithinAsync(rosterTask, ForeverSuggest.Budget);
                // Both started together, so the second wait is only whatever is left of the first.
                var live = await ForeverSuggest.WithinAsync(liveTask, TimeSpan.FromMilliseconds(200));

                return AutocompletionResult.FromSuccess(ForeverSuggest.Add(own, region, input, roster?.Value?.Members, live));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverAddAutocomplete>>()?.LogError(ex, "Error in ForeverAddAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>For /forever craft: recipes by name, from Blizzard's list of what every profession can make.</summary>
    public class ForeverRecipeAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var catalog = services.GetRequiredService<IForeverCatalog>();
                // The first question after a restart starts the load; it rarely finishes inside Discord's three seconds.
                var recipes = catalog.RecipesIfLoaded
                    ?? await ForeverSuggest.WithinAsync<ForeverRecipeList>(catalog.GetRecipesAsync()!, ForeverSuggest.Budget);
                return AutocompletionResult.FromSuccess(ForeverSuggest.Recipes(recipes, ForeverSuggest.Current(autocompleteInteraction)));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverRecipeAutocomplete>>()?.LogError(ex, "Error in ForeverRecipeAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>For /forever price: items by name, from the region's TradeSkillMaster price list.</summary>
    public class ForeverItemAutocomplete : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var forever = services.GetRequiredService<ForeverService>();
                var prices = services.GetRequiredService<IForeverPrices>();
                var guild = context.Guild == null ? null : await forever.GetGuildAsync((long)context.Guild.Id);
                var region = ForeverSuggest.Region(autocompleteInteraction) ?? guild?.Region ?? ForeverRegion.US;

                var sheet = prices.IfLoaded(region)
                    ?? await ForeverSuggest.WithinAsync<ForeverPriceSheet>(prices.GetAsync(region)!, ForeverSuggest.Budget);
                return AutocompletionResult.FromSuccess(ForeverSuggest.Items(sheet, ForeverSuggest.Current(autocompleteInteraction)));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverItemAutocomplete>>()?.LogError(ex, "Error in ForeverItemAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// For /forever guild set: checks the typed name against the armory in the chosen region, or in
    /// every region when none is chosen yet, and offers the guilds of the caller's own characters.
    /// </summary>
    public class ForeverGuildNameAutocomplete : AutocompleteHandler
    {
        private const int MaxOwnCharacters = 5;

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var forever = services.GetRequiredService<ForeverService>();
                var armory = services.GetRequiredService<IForeverArmory>();
                var input = ForeverSuggest.Current(autocompleteInteraction);
                var chosen = ForeverSuggest.Region(autocompleteInteraction);
                var regions = chosen != null ? new[] { chosen.Value } : Enum.GetValues<ForeverRegion>();

                var guildTasks = ForeverSuggest.WorthAsking(input) && ForeverNames.TryCleanGuildName(input, out var name, out _)
                    ? regions.Select(region => armory.GetGuildAsync(region, name)).ToArray()
                    : Array.Empty<Task<ForeverLookup<ForeverArmoryGuild>>>();

                var own = (await forever.GetCharactersAsync((long)context.User.Id))
                    .Where(c => chosen == null || c.Region == chosen)
                    .Take(MaxOwnCharacters)
                    .ToList();
                var ownTasks = own.Select(c => forever.LookUpAsync(c)).ToArray();

                await Task.WhenAny(Task.WhenAll(guildTasks.Cast<Task>().Concat(ownTasks)), Task.Delay(ForeverSuggest.Budget));

                var answered = guildTasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
                return AutocompletionResult.FromSuccess(ForeverSuggest.Guilds(
                    input,
                    answered.Where(g => g.IsFound).Select(g => g.Value!).ToList(),
                    ownTasks.Where(t => t.IsCompletedSuccessfully && t.Result.IsFound).Select(t => t.Result.Value!).ToList(),
                    checkedEverywhere: guildTasks.Length > 0 && answered.Count == guildTasks.Length
                        && answered.All(g => g.Status != ForeverLookupStatus.Unavailable)));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverGuildNameAutocomplete>>()?.LogError(ex, "Error in ForeverGuildNameAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }
}
