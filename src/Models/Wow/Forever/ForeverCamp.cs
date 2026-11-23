#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Models.Wow.Forever
{
    /// <summary>One thing a profession can add to a camp.</summary>
    /// <param name="RecipeId">Blizzard's recipe id, or 0 when the bot only knows the recipe by name.</param>
    /// <param name="ReplacedBuff">The class buff it doesn't stack with, or null when it stacks with everything.</param>
    /// <param name="BuffClass">The class that casts <paramref name="ReplacedBuff"/>.</param>
    public sealed record ForeverCampFeature(
        int RecipeId, int ProfessionId, string Profession, string Name, string Effect,
        string? ReplacedBuff = null, ForeverClass? BuffClass = null);

    /// <summary>Someone at the camp and what the armory says they can craft.</summary>
    /// <param name="Professions">Null when the armory gave no professions for the character, so nothing is known about what they can bring.</param>
    public sealed record ForeverCamper(
        ulong UserId, string CharacterName, int Level, ForeverClass? Class, IReadOnlyList<ForeverArmoryProfession>? Professions);

    /// <param name="Makers">Campers who know a recipe for it, with the recipe each knows (an upgrade has its own name).</param>
    /// <param name="CoveredBy">Campers whose class casts the buff this doesn't stack with.</param>
    public sealed record ForeverCampOffer(
        ForeverCampFeature Feature,
        IReadOnlyList<(ForeverCamper Camper, string Recipe)> Makers,
        IReadOnlyList<ForeverCamper> CoveredBy);

    /// <param name="Picks">Who should bring what, best first: one thing per camper and no feature twice.</param>
    /// <param name="FireStarters">Campers who know a campfire recipe, with the recipe.</param>
    /// <param name="UpgradedFire">Someone knows a campfire above the basic one, which holds more than three objects.</param>
    public sealed record ForeverCampPlan(
        IReadOnlyList<ForeverCampOffer> Offers,
        IReadOnlyList<(ForeverCampFeature Feature, ForeverCamper Camper, string Recipe)> Picks,
        IReadOnlyList<(ForeverCamper Camper, string Recipe)> FireStarters,
        bool UpgradedFire);

    /// <summary>
    /// Forever's camps, from Blizzard's "Gather Around the Campfire" article: a cook lights a campfire,
    /// nearby players each add one crafted object, and everyone who sits there for a minute gets the
    /// objects' buffs. A Basic Campfire holds three objects; better campfires hold five or ten. Most
    /// objects are a class buff in another form and don't stack with it.
    /// </summary>
    public static class ForeverCamp
    {
        /// <summary>Objects a Basic Campfire holds.</summary>
        public const int BasicCapacity = 3;
        /// <summary>Objects the best campfire holds, and so the most a plan lists.</summary>
        public const int MaxCapacity = 10;

        public const int CookingId = 185;
        public const int BasicCampfireRecipe = 52640;
        public const string BasicCampfireName = "Basic Campfire";

        /// <summary>The heading Blizzard files camp recipes under in every profession.</summary>
        public const string Category = "Camping";

        // Best first: what a plan picks when there are more makers than room. Buffs everyone uses lead,
        // then the ones only some classes want. Recipe ids are from recorded armory answers; the
        // Reagent Bot's wasn't in them, so it is matched by name.
        public static readonly IReadOnlyList<ForeverCampFeature> Features = new[]
        {
            new ForeverCampFeature(52639, 356, "Fishing", "Fish Bowl", "+8% to all stats", "Blessing of Kings", ForeverClass.Paladin),
            new ForeverCampFeature(52647, 165, "Leatherworking", "Camp Tent", "rested XP worth 5% of a level"),
            new ForeverCampFeature(52644, 393, "Skinning", "Camp Chair", "+2% critical strike", "Moonkin Aura", ForeverClass.Druid),
            new ForeverCampFeature(52641, 129, "First Aid", "First Aid Kit", "Stamina", "Power Word: Fortitude", ForeverClass.Priest),
            new ForeverCampFeature(52649, 333, "Enchanting", "Enchanted Lute", "armor, stats and resistances", "Mark of the Wild", ForeverClass.Druid),
            new ForeverCampFeature(52645, 164, "Blacksmithing", "Sharpening Wheel", "Strength", "Strength of Earth Totem", ForeverClass.Shaman),
            new ForeverCampFeature(52642, 182, "Herbalism", "Incense Candle", "Intellect", "Arcane Intellect", ForeverClass.Mage),
            new ForeverCampFeature(52646, 171, "Alchemy", "Mana Well", "mana every 5 seconds", "Blessing of Wisdom", ForeverClass.Paladin),
            new ForeverCampFeature(52648, 197, "Tailoring", "Faction Banner", "Spirit for your faction", "Divine Spirit", ForeverClass.Priest),
            new ForeverCampFeature(0, 202, "Engineering", "Reagent Bot", "sells reagents")
        };

        /// <summary>
        /// The recipe a character knows for a feature: the basic one, or an upgrade Blizzard files under
        /// Camping in the same profession when the recipe list is at hand. Null when they know neither.
        /// </summary>
        public static string? KnownRecipe(ForeverCampFeature feature, IReadOnlyList<ForeverArmoryProfession>? professions, ForeverRecipeList? recipes = null)
        {
            var known = (professions ?? Array.Empty<ForeverArmoryProfession>())
                .Where(p => p.Id == feature.ProfessionId || (p.Id == null && string.Equals(p.Name, feature.Profession, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(p => p.Known ?? Array.Empty<ForeverKnownRecipe>())
                .ToList();

            // An upgrade replaces the basic object, so it is the one worth naming.
            var upgrade = known.LastOrDefault(r => !IsBasic(feature, r) && recipes?.Find(r.Id) is { } listed
                && listed.ProfessionId == feature.ProfessionId && IsCamping(listed));
            if (upgrade != null) return upgrade.Name;
            return known.FirstOrDefault(r => IsBasic(feature, r))?.Name;
        }

        private static bool IsBasic(ForeverCampFeature feature, ForeverKnownRecipe recipe)
            => (feature.RecipeId > 0 && recipe.Id == feature.RecipeId) || string.Equals(recipe.Name, feature.Name, StringComparison.OrdinalIgnoreCase);

        private static bool IsCamping(ForeverRecipeRef recipe) => string.Equals(recipe.Category, Category, StringComparison.OrdinalIgnoreCase);

        /// <summary>The campfire a character can light, or null. An upgraded one needs the recipe list to be recognised.</summary>
        public static (string Recipe, bool Upgraded)? Campfire(IReadOnlyList<ForeverArmoryProfession>? professions, ForeverRecipeList? recipes = null)
        {
            var known = (professions ?? Array.Empty<ForeverArmoryProfession>())
                .Where(p => p.Id == CookingId || (p.Id == null && string.Equals(p.Name, "Cooking", StringComparison.OrdinalIgnoreCase)))
                .SelectMany(p => p.Known ?? Array.Empty<ForeverKnownRecipe>())
                .ToList();
            bool Basic(ForeverKnownRecipe r) => r.Id == BasicCampfireRecipe || string.Equals(r.Name, BasicCampfireName, StringComparison.OrdinalIgnoreCase);

            var upgrade = known.LastOrDefault(r => !Basic(r) && recipes?.Find(r.Id) is { } listed && listed.ProfessionId == CookingId && IsCamping(listed));
            if (upgrade != null) return (upgrade.Name, true);
            return known.FirstOrDefault(Basic) is { } basic ? (basic.Name, false) : null;
        }

        /// <summary>
        /// What these campers can set up between them. Picks go to the most useful features first and
        /// leave the ones a class here already buffs for last, since those add nothing while that buff is up.
        /// </summary>
        public static ForeverCampPlan Plan(IReadOnlyList<ForeverCamper> campers, ForeverRecipeList? recipes = null)
        {
            var offers = Features
                .Select(feature => new ForeverCampOffer(
                    feature,
                    campers.Select(c => (Camper: c, Recipe: KnownRecipe(feature, c.Professions, recipes)))
                        .Where(m => m.Recipe != null)
                        .Select(m => (m.Camper, m.Recipe!))
                        .ToList(),
                    feature.BuffClass is { } buffClass ? campers.Where(c => c.Class == buffClass).ToList() : new List<ForeverCamper>()))
                .ToList();

            var fires = campers
                .Select(c => (Camper: c, Fire: Campfire(c.Professions, recipes)))
                .Where(f => f.Fire != null)
                .OrderByDescending(f => f.Fire!.Value.Upgraded)
                .Select(f => (f.Camper, f.Fire!.Value.Recipe, f.Fire.Value.Upgraded))
                .ToList();

            // Stable: within "not covered" and "covered" the order of Features decides.
            var ranked = offers.Where(o => o.Makers.Count > 0).OrderBy(o => o.CoveredBy.Count > 0).ToList();
            var picks = Match(ranked, campers)
                .Select(p => (p.Offer.Feature, p.Camper, p.Offer.Makers.First(m => ReferenceEquals(m.Camper, p.Camper)).Recipe))
                .ToList();

            return new ForeverCampPlan(
                offers,
                picks,
                fires.Select(f => (f.Camper, f.Recipe)).ToList(),
                fires.Any(f => f.Upgraded));
        }

        /// <summary>
        /// One feature per camper and one camper per feature, taking features in the order given and
        /// moving an earlier pick to another camper when that makes room (augmenting paths), so a
        /// feature is only left out when nobody is free to bring it.
        /// </summary>
        private static List<(ForeverCampOffer Offer, ForeverCamper Camper)> Match(IReadOnlyList<ForeverCampOffer> offers, IReadOnlyList<ForeverCamper> campers)
        {
            var bringing = new Dictionary<ForeverCamper, int>(ReferenceEqualityComparer.Instance);
            var matched = new List<int>();

            bool Place(int offer, HashSet<ForeverCamper> seen)
            {
                foreach (var (camper, _) in offers[offer].Makers)
                {
                    if (!seen.Add(camper)) continue;
                    if (!bringing.TryGetValue(camper, out var current) || Place(current, seen))
                    {
                        bringing[camper] = offer;
                        return true;
                    }
                }
                return false;
            }

            for (var i = 0; i < offers.Count && matched.Count < MaxCapacity; i++)
            {
                if (Place(i, new HashSet<ForeverCamper>(ReferenceEqualityComparer.Instance))) matched.Add(i);
            }

            return matched
                .Select(i => (Offer: offers[i], Camper: bringing.First(b => b.Value == i).Key))
                .ToList();
        }
    }
}
