#nullable enable

using System;
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

                var matches = characters
                    .Where(c => input.Length == 0 || c.FullName.Contains(input, StringComparison.OrdinalIgnoreCase))
                    .Take(25)
                    .Select(c =>
                    {
                        var label = $"{c.FullName} — {(c.Class?.ToString() ?? "No class")} · {ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)}";
                        return new AutocompleteResult(label.Length > 100 ? label[..100] : label, c.Id.ToString());
                    });

                return AutocompletionResult.FromSuccess(matches);
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<ForeverCharacterAutocomplete>>()?.LogError(ex, "Error in ForeverCharacterAutocomplete");
                return AutocompletionResult.FromSuccess(Enumerable.Empty<AutocompleteResult>());
            }
        }
    }
}
