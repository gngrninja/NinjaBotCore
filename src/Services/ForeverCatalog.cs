#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    public interface IForeverCatalog
    {
        /// <summary>Every recipe of every profession, or null when Blizzard can't be asked right now.</summary>
        Task<ForeverRecipeList?> GetRecipesAsync(CancellationToken cancellationToken = default);

        /// <summary>The list if it is already loaded; never waits. Autocomplete uses this.</summary>
        ForeverRecipeList? RecipesIfLoaded { get; }

        /// <summary>What one recipe makes and takes.</summary>
        Task<ForeverLookup<ForeverRecipe>> GetRecipeAsync(int recipeId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Forever's recipes from Blizzard's game data (the <c>static-forever</c> namespace): the list of
    /// what each profession can make, and what a recipe takes. The game is the same in every region,
    /// so everything is read once, from the US data in English.
    /// </summary>
    public class ForeverCatalog : IForeverCatalog
    {
        private const ForeverRegion Source = ForeverRegion.US;
        private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(24);
        // A list with a profession missing is worth showing, but not for a day.
        private static readonly TimeSpan PartialListLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan RecipeLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan MissingRecipeLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private const int Parallel = 4;

        private readonly IWowApi _wowApi;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ForeverCatalog> _logger;
        private readonly TimeProvider _clock;
        private readonly object _gate = new();
        private Task<ForeverRecipeList?>? _loading;
        private ForeverRecipeList? _list;
        private DateTimeOffset _listExpires = DateTimeOffset.MinValue;
        private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

        public ForeverCatalog(IWowApi wowApi, IMemoryCache cache, ILogger<ForeverCatalog> logger)
            : this(wowApi, cache, logger, TimeProvider.System) { }

        internal ForeverCatalog(IWowApi wowApi, IMemoryCache cache, ILogger<ForeverCatalog> logger, TimeProvider clock)
        {
            _wowApi = wowApi;
            _cache = cache;
            _logger = logger;
            _clock = clock;
        }

        public ForeverRecipeList? RecipesIfLoaded
        {
            get
            {
                lock (_gate) return _list;
            }
        }

        public Task<ForeverRecipeList?> GetRecipesAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (_list != null && now < _listExpires) return Task.FromResult<ForeverRecipeList?>(_list);
                if (_loading != null) return _loading;
                // After a failure the old list, if there is one, serves until the next try.
                if (now < _retryAt) return Task.FromResult(_list);

                // Not tied to the caller's token: the next caller wants the list too.
                return _loading = Task.Run(() => LoadAsync(CancellationToken.None), CancellationToken.None);
            }
        }

        private async Task<ForeverRecipeList?> LoadAsync(CancellationToken cancellationToken)
        {
            ForeverRecipeList? loaded = null;
            var complete = false;
            try
            {
                (loaded, complete) = await ReadListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reading Forever's recipe list failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }

            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                _loading = null;
                if (loaded == null)
                {
                    _retryAt = now + RetryAfterFailure;
                    return _list;
                }
                _list = loaded;
                _listExpires = now + (complete ? ListLifetime : PartialListLifetime);
                return _list;
            }
        }

        private async Task<(ForeverRecipeList? List, bool Complete)> ReadListAsync(CancellationToken cancellationToken)
        {
            var index = await GetAsync("/data/wow/profession/index", cancellationToken);
            if (!index.IsFound || index.Value!["professions"] is not JArray professions) return (null, false);

            var wanted = professions
                .Select(p => (Id: ForeverApi.Int(p["id"]), Name: ForeverApi.Text(p["name"])))
                // Blizzard leaves a "Test Profession [DNT]" in the list.
                .Where(p => p.Id is > 0 && !string.IsNullOrWhiteSpace(p.Name) && !p.Name!.Contains("[DNT]", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Id: p.Id!.Value, Name: p.Name!.Trim()))
                .ToList();
            if (wanted.Count == 0) return (null, false);

            using var gate = new SemaphoreSlim(Parallel);
            var read = await Task.WhenAll(wanted.Select(async p =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await ReadProfessionAsync(p.Id, p.Name, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }));

            var recipes = read.Where(r => r != null).SelectMany(r => r!)
                .GroupBy(r => r.Id).Select(g => g.First())
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipes.Count == 0) return (null, false);

            var complete = read.All(r => r != null);
            _logger.LogInformation("Forever recipe list loaded: {Recipes} recipes from {Read} of {Wanted} professions",
                recipes.Count, read.Count(r => r != null), wanted.Count);
            return (new ForeverRecipeList(recipes), complete);
        }

        /// <summary>A profession's recipes across its skill tiers, or null when any part of it didn't answer.</summary>
        private async Task<List<ForeverRecipeRef>?> ReadProfessionAsync(int id, string name, CancellationToken cancellationToken)
        {
            var profession = await GetAsync($"/data/wow/profession/{id}", cancellationToken);
            // A gathering profession with nothing to craft may have no tiers at all.
            if (profession.Status == ForeverLookupStatus.NotFound) return new List<ForeverRecipeRef>();
            if (!profession.IsFound) return null;

            var recipes = new List<ForeverRecipeRef>();
            foreach (var tier in profession.Value!["skill_tiers"] as JArray ?? new JArray())
            {
                if (ForeverApi.Int(tier["id"]) is not { } tierId) continue;
                var detail = await GetAsync($"/data/wow/profession/{id}/skill-tier/{tierId}", cancellationToken);
                if (detail.Status == ForeverLookupStatus.NotFound) continue;
                if (!detail.IsFound) return null;

                foreach (var category in detail.Value!["categories"] as JArray ?? new JArray())
                {
                    var categoryName = ForeverApi.Text(category["name"])?.Trim();
                    foreach (var recipe in category["recipes"] as JArray ?? new JArray())
                    {
                        var recipeName = ForeverApi.Text(recipe["name"])?.Trim();
                        // The profession's own ranks are listed as recipes ("Mining").
                        if (ForeverApi.Int(recipe["id"]) is not { } recipeId || string.IsNullOrEmpty(recipeName)
                            || string.Equals(recipeName, name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        recipes.Add(new ForeverRecipeRef(recipeId, recipeName, id, name, categoryName));
                    }
                }
            }
            return recipes;
        }

        public async Task<ForeverLookup<ForeverRecipe>> GetRecipeAsync(int recipeId, CancellationToken cancellationToken = default)
        {
            if (recipeId <= 0) return ForeverLookup<ForeverRecipe>.NotFound();
            var key = $"forever:recipe:{recipeId}";
            if (_cache.TryGetValue(key, out ForeverLookup<ForeverRecipe>? cached) && cached != null) return cached;

            var json = await GetAsync($"/data/wow/recipe/{recipeId}", cancellationToken);
            ForeverLookup<ForeverRecipe> result;
            if (!json.IsFound)
            {
                result = new ForeverLookup<ForeverRecipe>(json.Status, null);
            }
            else
            {
                var recipe = Parse(json.Value!, recipeId);
                result = recipe == null ? ForeverLookup<ForeverRecipe>.Unavailable() : ForeverLookup<ForeverRecipe>.Found(recipe);
            }

            if (result.Status != ForeverLookupStatus.Unavailable)
            {
                _cache.Set(key, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = result.IsFound ? RecipeLifetime : MissingRecipeLifetime,
                    Size = 1
                });
            }
            return result;
        }

        private ForeverRecipe? Parse(JObject o, int recipeId)
        {
            try
            {
                var name = ForeverApi.Text(o["name"])?.Trim();
                if (string.IsNullOrEmpty(name)) return null;

                var quantity = o["crafted_quantity"];
                var fixedQuantity = ForeverApi.Number(quantity?["value"]);
                var min = fixedQuantity ?? ForeverApi.Number(quantity?["minimum"]) ?? 1;
                var max = fixedQuantity ?? ForeverApi.Number(quantity?["maximum"]) ?? min;

                var reagents = (o["reagents"] as JArray ?? new JArray())
                    .Select(r => (Id: ForeverApi.Int(r["reagent"]?["id"]), Name: ForeverApi.Text(r["reagent"]?["name"]), Quantity: ForeverApi.Int(r["quantity"])))
                    .Where(r => r.Id is > 0)
                    .Select(r => new ForeverReagent(r.Id!.Value, string.IsNullOrWhiteSpace(r.Name) ? $"Item {r.Id}" : r.Name!.Trim(), Math.Max(1, r.Quantity ?? 1)))
                    .ToList();

                return new ForeverRecipe(
                    recipeId,
                    name,
                    ForeverApi.Int(o["crafted_item"]?["id"]),
                    ForeverApi.Text(o["crafted_item"]?["name"])?.Trim(),
                    Math.Max(1, min),
                    Math.Max(Math.Max(1, min), max),
                    reagents,
                    ForeverApi.Text(o["description"])?.Trim());
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            {
                _logger.LogWarning("Forever recipe {RecipeId} came in a shape the bot doesn't read: {Message}", recipeId, ex.Message);
                return null;
            }
        }

        private Task<ForeverLookup<JObject>> GetAsync(string path, CancellationToken cancellationToken)
            => ForeverApi.GetAsync(_wowApi, _logger, Source, "static", path, Timeout, cancellationToken);
    }
}
