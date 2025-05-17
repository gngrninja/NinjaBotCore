using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using NinjaBotCore.Database;

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
            return Task.FromResult(AutocompletionResult.FromSuccess(chars.Select(c => new AutocompleteResult(c.CharName, c.Id)))).Result;
        }
    }
}