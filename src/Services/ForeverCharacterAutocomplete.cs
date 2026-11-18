#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// The caller's own Forever characters only. Returns the character Id as the value.
    /// </summary>
    public class ForeverCharacterAutocomplete : AutocompleteHandler
    {
        internal static IEnumerable<AutocompleteResult> Matches(IReadOnlyList<Database.ForeverCharacter> characters, string input, int max)
            => characters
                .Where(c => input.Length == 0 || c.FullName.Contains(input, StringComparison.OrdinalIgnoreCase))
                .Take(max)
                .Select(c =>
                {
                    var label = $"{c.FullName} — {(c.Class?.ToString() ?? "No class")} · {ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)}";
                    return new AutocompleteResult(label.Length > 100 ? label[..100] : label, c.Id.ToString());
                });

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var forever = services.GetRequiredService<ForeverService>();
                var input = (autocompleteInteraction.Data.Current.Value?.ToString() ?? string.Empty).Trim();
                var characters = await forever.GetCharactersAsync((long)context.User.Id);

                if (characters.Count == 0)
                {
                    return AutocompletionResult.FromSuccess(
                        new[] { new AutocompleteResult("No Forever characters yet — use /forever char add", "0") });
                }

                return AutocompletionResult.FromSuccess(Matches(characters, input, 25));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverCharacterAutocomplete>>()?.LogError(ex, "Error in ForeverCharacterAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }

    /// <summary>
    /// For armory lookups: the caller's own characters (value: the Id), plus the name they typed
    /// when it's a first name and surname (value: the name), since anyone can be looked up.
    /// </summary>
    public class ForeverLookupAutocomplete : AutocompleteHandler
    {
        internal const string Hint = "Type a first name and surname, like Asha Brightvale";

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            try
            {
                var forever = services.GetRequiredService<ForeverService>();
                var input = (autocompleteInteraction.Data.Current.Value?.ToString() ?? string.Empty).Trim();
                var characters = await forever.GetCharactersAsync((long)context.User.Id);
                return AutocompletionResult.FromSuccess(Suggestions(characters, input));
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverLookupAutocomplete>>()?.LogError(ex, "Error in ForeverLookupAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }

        internal static List<AutocompleteResult> Suggestions(IReadOnlyList<Database.ForeverCharacter> characters, string input)
        {
            var results = new List<AutocompleteResult>();
            if (ForeverNames.TrySplitFullName(input, out var first, out var last, out _))
            {
                var name = ForeverNames.FullName(first, last);
                var label = $"Look up {name} on the armory";
                var isOwn = characters.Any(c => c.NormalizedFirstName == ForeverNames.Normalize(first) && c.NormalizedLastName == ForeverNames.Normalize(last));
                if (!isOwn && label.Length <= 100) results.Add(new AutocompleteResult(label, name));
            }

            results.AddRange(ForeverCharacterAutocomplete.Matches(characters, input, 25 - results.Count));
            if (results.Count == 0) results.Add(new AutocompleteResult(Hint, "0"));
            return results;
        }
    }
}
