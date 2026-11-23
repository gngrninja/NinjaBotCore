#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Models.Wow.Forever
{
    /// <summary>A recipe as the profession lists name it: enough to search by and to ask for the rest.</summary>
    /// <param name="Category">The heading Blizzard files it under in the profession window ("Camping", "Mail Belts").</param>
    public sealed record ForeverRecipeRef(int Id, string Name, int ProfessionId, string ProfessionName, string? Category = null);

    public sealed record ForeverReagent(int ItemId, string Name, int Quantity);

    /// <summary>What a recipe makes and takes. Enchants make no item, so <paramref name="CraftedItemId"/> is null for them.</summary>
    /// <param name="MinQuantity">How many one craft makes at least; equal to <paramref name="MaxQuantity"/> for most recipes.</param>
    public sealed record ForeverRecipe(
        int Id,
        string Name,
        int? CraftedItemId,
        string? CraftedItemName,
        double MinQuantity,
        double MaxQuantity,
        IReadOnlyList<ForeverReagent> Reagents,
        string? Description = null)
    {
        /// <summary>What one craft makes on average, never less than one.</summary>
        public double AverageQuantity => Math.Max(1, (MinQuantity + MaxQuantity) / 2);
    }

    /// <summary>Every recipe Blizzard lists for Forever's professions.</summary>
    public sealed record ForeverRecipeList(IReadOnlyList<ForeverRecipeRef> Recipes)
    {
        private readonly Dictionary<int, ForeverRecipeRef> _byId = Recipes
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First());

        public ForeverRecipeRef? Find(int id) => _byId.TryGetValue(id, out var recipe) ? recipe : null;
    }

    /// <summary>One item's row in TradeSkillMaster's regional file. Prices are in copper.</summary>
    /// <param name="MarketValue">TSM's market value for the region: what the item is listed for, smoothed over recent scans.</param>
    /// <param name="AvgSalePrice">What it actually sold for on average, or 0 when TSM saw no sales.</param>
    /// <param name="SaleRate">The share of listings that sold, from 0 to 1.</param>
    public sealed record ForeverPrice(
        int ItemId, string Name, long MarketValue, long Historical, long AvgSalePrice, double SaleRate, double SoldPerDay);

    /// <summary>A region's prices as of TSM's last scan.</summary>
    public sealed record ForeverPriceSheet(ForeverRegion Region, DateTimeOffset? ScannedAt, IReadOnlyDictionary<int, ForeverPrice> Items)
    {
        public ForeverPrice? Find(int? itemId) => itemId is { } id && Items.TryGetValue(id, out var price) ? price : null;
    }

    /// <summary>A recipe someone knows, as the armory lists it on their professions.</summary>
    public sealed record ForeverKnownRecipe(int Id, string Name);

    /// <summary>One profession of one character in a guild's crafting index.</summary>
    /// <param name="RecipeIds">Sorted, so a guild of hundreds stays small in memory and a lookup is a binary search.</param>
    public sealed record ForeverCrafterProfession(int? Id, string Name, int Skill, int MaxSkill, int[] RecipeIds)
    {
        public static ForeverCrafterProfession From(int? id, string name, int skill, int maxSkill, IEnumerable<int> recipeIds)
            => new(id, name, skill, maxSkill, recipeIds.Where(r => r > 0).Distinct().OrderBy(r => r).ToArray());

        public bool Knows(int recipeId) => recipeId > 0 && Array.BinarySearch(RecipeIds, recipeId) >= 0;
    }

    /// <summary>A character whose professions the bot has read from the armory.</summary>
    public sealed record ForeverCrafter(
        string FirstName,
        string Surname,
        int Level,
        ForeverClass? Class,
        IReadOnlyList<ForeverCrafterProfession> Professions)
    {
        public string FullName => ForeverNames.FullName(FirstName, Surname).Trim();

        /// <summary>The profession this character knows the recipe in, or null when they don't know it.</summary>
        public ForeverCrafterProfession? Knows(int recipeId) => Professions.FirstOrDefault(p => p.Knows(recipeId));
    }

    /// <summary>
    /// Who in a guild knows which recipes, read from the armory one character at a time.
    /// </summary>
    /// <param name="Asked">Characters the armory was asked about.</param>
    /// <param name="Unanswered">Of those, the ones it gave no professions for (not on the armory yet, or it didn't answer).</param>
    /// <param name="Skipped">Characters left out because the index stops at a size the armory can be asked for in one go.</param>
    /// <param name="RosterMissed">The guild's roster didn't come back, so only characters members shared were read.</param>
    public sealed record ForeverCraftIndex(
        IReadOnlyList<ForeverCrafter> Crafters,
        DateTimeOffset BuiltAt,
        int Asked,
        int Unanswered,
        int Skipped = 0,
        bool RosterMissed = false)
    {
        public IReadOnlyList<(ForeverCrafter Crafter, ForeverCrafterProfession Profession)> WhoKnows(int recipeId)
            => Crafters
                .Select(c => (Crafter: c, Profession: c.Knows(recipeId)))
                .Where(e => e.Profession != null)
                .Select(e => (e.Crafter, e.Profession!))
                .OrderByDescending(e => e.Item2.Skill)
                .ThenByDescending(e => e.Crafter.Level)
                .ThenBy(e => e.Crafter.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>How far a crafting index has got, for the "reading the guild" note on a card.</summary>
    public readonly record struct ForeverCraftProgress(int Done, int Total);

    public static class ForeverMoney
    {
        /// <summary>"3g 20s 5c" from copper, leaving out the parts that are zero. "0c" for nothing.</summary>
        public static string Format(long copper)
        {
            if (copper <= 0) return "0c";
            var gold = copper / 10000;
            var silver = copper / 100 % 100;
            var rest = copper % 100;
            var parts = new List<string>(3);
            if (gold > 0) parts.Add($"{gold:N0}g");
            if (silver > 0) parts.Add($"{silver}s");
            // Copper is noise next to gold.
            if (rest > 0 && gold == 0) parts.Add($"{rest}c");
            return parts.Count == 0 ? $"{gold:N0}g" : string.Join(" ", parts);
        }
    }
}
