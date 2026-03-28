using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;

namespace NinjaBotCore.Services.Api
{
    public static class CraftableItemEndpoints
    {
        public static void MapCraftableItemEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // Search craftable items
            group.MapGet("/api/craftable-items", async (HttpContext context) =>
            {
                var search = context.Request.Query["search"].FirstOrDefault() ?? "";
                var profession = context.Request.Query["profession"].FirstOrDefault();
                var pageStr = context.Request.Query["page"].FirstOrDefault();
                var pageSizeStr = context.Request.Query["page_size"].FirstOrDefault();

                var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
                var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Clamp(ps, 1, 100) : 25;

                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var query = db.CraftableItems.AsQueryable();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                    query = query.Where(c => EF.Functions.ILike(c.RecipeName, $"%{escaped}%", "\\"));
                }

                if (!string.IsNullOrWhiteSpace(profession))
                    query = query.Where(c => c.Profession == profession);

                var total = await query.CountAsync();

                var items = await query
                    .OrderBy(c => c.Profession)
                    .ThenBy(c => c.RecipeName)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(c => new
                    {
                        recipe_id = c.Id,
                        recipe_name = c.RecipeName,
                        crafted_item_name = c.CraftedItemName,
                        crafted_item_id = c.CraftedItemId,
                        profession = c.Profession,
                        skill_tier = c.SkillTier,
                        category = c.Category,
                        last_updated = c.LastUpdated
                    })
                    .ToListAsync();

                return Results.Ok(new
                {
                    items,
                    total,
                    page,
                    page_size = pageSize,
                    total_pages = (int)Math.Ceiling((double)total / pageSize)
                });
            });

            // List distinct professions
            group.MapGet("/api/craftable-items/professions", async (HttpContext context) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var professions = await db.CraftableItems
                    .Select(c => c.Profession)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToListAsync();

                return Results.Ok(new { professions });
            });

            // Stats by profession
            group.MapGet("/api/craftable-items/stats", async (HttpContext context) =>
            {
                using var scope = deps.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

                var stats = await db.CraftableItems
                    .GroupBy(c => c.Profession)
                    .Select(g => new { profession = g.Key, count = g.Count() })
                    .OrderBy(s => s.profession)
                    .ToListAsync();

                var total = stats.Sum(s => s.count);

                return Results.Ok(new { total, by_profession = stats });
            });
        }
    }
}
