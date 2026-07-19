#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Common;

namespace NinjaBotCore.Services
{
    /// <summary>
    /// Autocomplete for the current M+ dungeon rotation. Returns the dungeon slug as the value.
    /// </summary>
    public class DungeonAutocomplete : AutocompleteHandler
    {
        public override Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            var input = autocompleteInteraction.Data.Current.Value?.ToString()?.ToLowerInvariant() ?? string.Empty;

            var matches = MythicPlusRotation.Current
                .Where(d => string.IsNullOrWhiteSpace(input) ||
                            d.Name.ToLowerInvariant().Contains(input) ||
                            d.ShortName.ToLowerInvariant().Contains(input) ||
                            d.Slug.Contains(input))
                .Take(20)
                .Select(d => new AutocompleteResult($"{d.Name} [{d.ShortName}]", d.Slug));

            return Task.FromResult(AutocompletionResult.FromSuccess(matches));
        }
    }
}
