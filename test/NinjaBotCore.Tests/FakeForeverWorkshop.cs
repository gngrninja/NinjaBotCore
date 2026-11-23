using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Tests;

/// <summary>A recipe list that holds only what a test puts in it. Every recipe is SYNTHETIC or a well-known low-level one.</summary>
internal sealed class FakeForeverCatalog : IForeverCatalog
{
    private readonly List<ForeverRecipeRef> _refs = new();
    private readonly Dictionary<int, ForeverRecipe> _recipes = new();

    /// <summary>Blizzard isn't answering: no list, and every recipe is Unavailable.</summary>
    public bool Down { get; set; }

    public ForeverRecipeRef Add(int id, string name, int professionId, string profession, string category = null,
        int? craftedItemId = null, double quantity = 1, params ForeverReagent[] reagents)
    {
        var reference = new ForeverRecipeRef(id, name, professionId, profession, category);
        _refs.Add(reference);
        _recipes[id] = new ForeverRecipe(id, name, craftedItemId, craftedItemId == null ? null : name, quantity, quantity, reagents);
        return reference;
    }

    public ForeverRecipeList RecipesIfLoaded => Down || _refs.Count == 0 ? null : new ForeverRecipeList(_refs.ToList());

    public Task<ForeverRecipeList> GetRecipesAsync(CancellationToken cancellationToken = default) => Task.FromResult(RecipesIfLoaded);

    public Task<ForeverLookup<ForeverRecipe>> GetRecipeAsync(int recipeId, CancellationToken cancellationToken = default)
        => Task.FromResult(Down
            ? ForeverLookup<ForeverRecipe>.Unavailable()
            : _recipes.TryGetValue(recipeId, out var recipe) ? ForeverLookup<ForeverRecipe>.Found(recipe) : ForeverLookup<ForeverRecipe>.NotFound());
}

/// <summary>Prices a test sets by hand, in copper.</summary>
internal sealed class FakeForeverPrices : IForeverPrices
{
    private readonly Dictionary<ForeverRegion, Dictionary<int, ForeverPrice>> _items = new();

    public static readonly DateTimeOffset Scanned = new(2026, 11, 22, 8, 58, 35, TimeSpan.Zero);

    public FakeForeverPrices Set(ForeverRegion region, int itemId, string name, long marketValue,
        long historical = 0, long avgSalePrice = 0, double saleRate = 0, double soldPerDay = 0)
    {
        if (!_items.TryGetValue(region, out var items)) _items[region] = items = new Dictionary<int, ForeverPrice>();
        items[itemId] = new ForeverPrice(itemId, name, marketValue, historical, avgSalePrice, saleRate, soldPerDay);
        return this;
    }

    public ForeverPriceSheet IfLoaded(ForeverRegion region)
        => _items.TryGetValue(region, out var items) ? new ForeverPriceSheet(region, Scanned, items) : null;

    public Task<ForeverPriceSheet> GetAsync(ForeverRegion region, CancellationToken cancellationToken = default) => Task.FromResult(IfLoaded(region));
}
