#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services.Api
{
    /// <summary>
    /// A server's WoW Forever guild, for its officers on the web dashboard. The same rules as
    /// <c>/forever guild set</c>, <c>view</c> and <c>unset</c>. The website checks that the user
    /// has Kick Members in the server, as the commands do.
    /// </summary>
    public static class ForeverEndpoints
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public static void MapForeverEndpoints(this WebApplication app,
            ApiDependencies deps, ApiKeyEndpointFilter apiKeyFilter)
        {
            var group = app.MapGroup("").AddEndpointFilter(apiKeyFilter);

            // The saved guild, or null. The armory isn't asked, so a page can call this on every load.
            group.MapGet("/api/guilds/{guildId}/forever-guild", async (string guildId) =>
            {
                var forever = deps.ServiceProvider.GetService<ForeverService>();
                if (forever == null)
                {
                    return NotRunning();
                }

                if (!TryGuildId(guildId, out var id))
                {
                    return Bad("Invalid guild_id format");
                }

                var guild = await forever.GetSavedGuildAsync(id);
                return Results.Json(new
                {
                    success = true,
                    guild = guild == null ? null : Guild(guild, null)
                }, Json);
            });

            // Sets the guild as the armory has it: its spelling, ruleset and faction.
            group.MapPut("/api/guilds/{guildId}/forever-guild", async (HttpContext context, string guildId) =>
            {
                var forever = deps.ServiceProvider.GetService<ForeverService>();
                if (forever == null)
                {
                    return NotRunning();
                }

                if (!TryGuildId(guildId, out var id))
                {
                    return Bad("Invalid guild_id format");
                }

                var body = await ReadAsync(context);
                if (body == null || !long.TryParse(body.UserId, out var userId) || userId <= 0)
                {
                    return Bad("name, region and user_id are required");
                }

                if (!TryRegion(body.Region, out var region))
                {
                    return Bad("region must be US, EU, KR or TW");
                }

                var result = await forever.SetGuildAsync(id, body.Name, region, userId, body.Username);
                if (!result.Ok)
                {
                    // An armory outage is nobody's mistake, so it isn't a bad request.
                    var armoryDown = result.Error == ForeverService.ArmoryDownMessage;
                    return Results.Json(new { success = false, error = Plain(result.Error!) }, Json,
                        statusCode: armoryDown ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status400BadRequest);
                }

                var saved = result.Value!;
                deps.Logger.LogInformation("Forever guild set for server {GuildId} on the web by {UserId}", id, userId);
                return Results.Json(new
                {
                    success = true,
                    guild = Guild(saved.Guild, saved.Armory)
                }, Json);
            });

            // Removes the guild. Members keep their characters, as with /forever guild unset.
            group.MapDelete("/api/guilds/{guildId}/forever-guild", async (string guildId, [FromQuery(Name = "user_id")] string? userId) =>
            {
                var forever = deps.ServiceProvider.GetService<ForeverService>();
                if (forever == null)
                {
                    return NotRunning();
                }

                if (!TryGuildId(guildId, out var id))
                {
                    return Bad("Invalid guild_id format");
                }

                var removed = await forever.UnsetGuildAsync(id);
                if (removed)
                {
                    deps.Logger.LogInformation("Forever guild removed for server {GuildId} on the web by {UserId}",
                        id, long.TryParse(userId, out var who) ? who : 0);
                }

                return Results.Json(new { success = true, removed }, Json);
            });
        }

        /// <param name="armory">What the armory said when the guild was just set; null when it wasn't asked.</param>
        private static object Guild(ForeverGuild guild, ForeverArmoryGuild? armory) => new
        {
            guild_id = guild.DiscordGuildId.ToString(),
            name = guild.Name,
            region = guild.Region.ToString(),
            ruleset = guild.Ruleset.ToString(),
            ruleset_name = guild.Ruleset.Name(),
            faction = guild.Faction.ToString(),
            member_count = armory?.MemberCount,
            set_by_id = guild.SetById?.ToString(),
            set_by_name = guild.SetByName,
            // Saved in UTC; said so, or a browser reads it as local time.
            time_set = guild.TimeSet is { } set ? DateTime.SpecifyKind(set, DateTimeKind.Utc) : (DateTime?)null
        };

        private static bool TryGuildId(string? value, out long id)
            => long.TryParse(value, out id) && id > 0;

        // Names only: Enum.TryParse would also take "1" for EU.
        private static bool TryRegion(string? value, out ForeverRegion region)
        {
            region = default;
            var name = value?.Trim();
            return !string.IsNullOrEmpty(name)
                && name.All(char.IsLetter)
                && Enum.TryParse(name, ignoreCase: true, out region)
                && Enum.IsDefined(region);
        }

        /// <summary>The service words its refusals for Discord. A web page shows them as plain text.</summary>
        private static string Plain(string message) => message.Replace("**", "").Replace("`", "");

        private static async Task<UpdateForeverGuildRequest?> ReadAsync(HttpContext context)
        {
            try
            {
                return await context.Request.ReadFromJsonAsync<UpdateForeverGuildRequest>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Bad JSON, or no JSON body at all.
                return null;
            }
        }

        private static IResult Bad(string error) => Results.BadRequest(new { success = false, error });

        private static IResult NotRunning() =>
            Results.NotFound(new { success = false, error = "WoW Forever is not running" });
    }
}
