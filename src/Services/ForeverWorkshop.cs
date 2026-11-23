#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    /// <summary>Everything the craft card says about a recipe itself. Only the name is certain; the rest may not have answered.</summary>
    /// <param name="Recipe">What it makes and takes, or null when Blizzard didn't send it.</param>
    /// <param name="Prices">The region's prices, or null when TradeSkillMaster didn't send them.</param>
    public sealed record ForeverCraftSheet(ForeverRecipeRef Ref, ForeverRecipe? Recipe, ForeverPriceSheet? Prices, ForeverRegion Region);

    /// <summary>A guild's crafting index, with who shared which character so their Discord name can sit next to it.</summary>
    /// <param name="SharedBy">Display names by normalized (first name, surname), only for characters members put on this server's roster.</param>
    public sealed record ForeverCraftWho(
        ForeverCraftIndex Index, IReadOnlyDictionary<(string First, string Last), string> SharedBy);

    /// <summary>
    /// Puts the craft card's parts together: the recipe from Blizzard's game data, prices from
    /// TradeSkillMaster and who knows the recipe from the guild's crafting index.
    /// </summary>
    public class ForeverWorkshop
    {
        private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(2.5);
        // A deferred reply can be edited for fifteen minutes; a read that takes longer than this is given up on.
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(8);

        private readonly ForeverService _forever;
        private readonly IForeverCatalog _catalog;
        private readonly IForeverPrices _prices;
        private readonly IForeverCrafters _crafters;

        public ForeverWorkshop(ForeverService forever, IForeverCatalog catalog, IForeverPrices prices, IForeverCrafters crafters)
        {
            _forever = forever;
            _catalog = catalog;
            _prices = prices;
            _crafters = crafters;
        }

        /// <summary>The recipe a command was given: an id from the suggestions, an exact name, or the only name that contains the text.</summary>
        public static ForeverRecipeRef? FindRecipe(ForeverRecipeList recipes, string? text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return recipes.Find(id);

            var exact = recipes.Recipes.FirstOrDefault(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var partial = recipes.Recipes.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }

        /// <summary>The priced item a command was given: an id from the suggestions, an exact name, or the only name that contains the text.</summary>
        public static ForeverPrice? FindItem(ForeverPriceSheet prices, string? text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return prices.Find(id);

            var exact = prices.Items.Values.FirstOrDefault(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var partial = prices.Items.Values.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }

        public async Task<ForeverCraftSheet> SheetAsync(ForeverRecipeRef recipe, ForeverRegion region)
        {
            var detail = _catalog.GetRecipeAsync(recipe.Id);
            var prices = _prices.GetAsync(region);
            await Task.WhenAll(detail, prices);
            return new ForeverCraftSheet(recipe, detail.Result.Value, prices.Result, region);
        }

        /// <summary>
        /// The guild's crafting index, read now if it isn't recent. <paramref name="onProgress"/> is called
        /// every few seconds while the armory is being asked, so the card can say how far it is.
        /// Null when the read didn't finish in time.
        /// </summary>
        public async Task<ForeverCraftWho?> CraftersAsync(
            Database.ForeverGuild guild, long discordGuildId, Func<long, bool> isMember, Func<long, string?> displayName,
            Func<ForeverCraftProgress, Task>? onProgress = null)
        {
            var roster = await _forever.GetRosterAsync(discordGuildId, isMember);
            var shared = (roster.Value?.Members ?? Array.Empty<ForeverRosterMember>())
                .SelectMany(m => m.Characters.Select(c => (Character: c, m.UserId, IsMain: c.Id == m.MainId)))
                .ToList();

            var sharedBy = shared
                .GroupBy(s => (s.Character.NormalizedFirstName, s.Character.NormalizedLastName))
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(", ", g.Select(s => (s.IsMain ? "★ " : "") + (displayName(s.UserId) ?? "Unknown member")).Distinct()));

            var source = new ForeverCraftSource(
                guild.Region,
                guild.ArmorySlug,
                shared.Select(s => new ForeverCraftCandidate(
                        s.Character.FirstName, s.Character.LastName,
                        ForeverNames.CharacterSlug(s.Character.FirstName, s.Character.LastName), 0, s.Character.Class))
                    .ToList());

            var index = _crafters.Current(discordGuildId, source);
            if (index == null)
            {
                var build = _crafters.Build(discordGuildId, source);
                var deadline = Task.Delay(MaxWait);
                while (!build.Task.IsCompleted)
                {
                    var first = await Task.WhenAny(build.Task, Task.Delay(ProgressEvery), deadline);
                    if (first == deadline) return null;
                    if (build.Task.IsCompleted || onProgress == null) continue;
                    try
                    {
                        await onProgress(new ForeverCraftProgress(build.Done, build.Total));
                    }
                    catch (Exception)
                    {
                        // A progress note that didn't land is not worth losing the answer over.
                    }
                }
                index = await build.Task;
            }
            return new ForeverCraftWho(index, sharedBy);
        }
    }
}
